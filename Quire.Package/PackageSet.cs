using Quire.Package.Internal;
using System.Buffers;
using System.Security.Cryptography;

namespace Quire.Package;
/// <summary>A fixed, read-only view of packages discovered in a directory. Later packages override paths.</summary>
public sealed class PackageSet : IDisposable, IAsyncDisposable
{
    private readonly PackageReader[] _packages;
    private readonly ReadOptions _options;
    private MergedIndex? _index;
    private int _references = 1, _closed;
    private readonly object _cacheSync = new();
    private readonly Dictionary<(PackageReader, long), Metadata> _metadata = new();
    private readonly Queue<(PackageReader, long)> _metadataOrder = new();
    private long _metadataBytes;
    public IReadOnlyList<AssetPackageInfo> Packages
    {
        get;
    }
    /// <summary>自动索引的实际状态、统一索引大小和有效文件/目录计数。</summary>
    public PackageIndexInfo IndexInfo
    {
        get;
    }
    // Callers must hold a package reference while accessing the index.
    internal MergedIndex? MergedIndex => _index;

    private PackageSet(PackageReader[] packages, ReadOptions options, MergedIndex? index)
    {
        _packages = packages;
        _options = options;
        _index = index;
        Packages = Array.AsReadOnly(packages.Select(p => p.Info).ToArray());
        IndexInfo = index is null
            ? new(false, 0, null, null, options.IndexMode == AssetIndexMode.Auto)
            : new(true, index.MemoryBytes, index.Files.Length, index.Directories.Length - 1, false);
    }

    public static PackageSet Open(string directory, ReadOptions? options = null) => OpenCoreAsync(directory, options ?? new(), false, default).GetAwaiter().GetResult();
    public static ValueTask<PackageSet> OpenAsync(string directory, ReadOptions? options = null, CancellationToken cancellationToken = default) => OpenCoreAsync(directory, options ?? new(), true, cancellationToken);
    private static async ValueTask<PackageSet> OpenCoreAsync(string directory, ReadOptions options, bool asynchronous, CancellationToken token)
    {
        options.Validate();
        options.Progress?.StartStage("Opening packages");
        if (!BitConverter.IsLittleEndian)
            throw new PlatformNotSupportedException("Asset packages currently support little-endian .NET platforms.");
        string root = Path.GetFullPath(directory);
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException(root);
        var readers = new List<PackageReader>();
        try
        {
            foreach (string path in Discover(root, options.Recursive).OrderBy(p => Path.GetRelativePath(root, p).Replace('\\', '/'), StringComparer.Ordinal))
                readers.Add(await PackageReader.OpenAsync(path, options, asynchronous, token).ConfigureAwait(false));
            var packages = readers.OrderBy(p => p.Header.BuildTicks).ToArray();
            MergedIndex? index = null;
            if (options.IndexMode != AssetIndexMode.PerPackage)
            {
                options.Progress?.StartStage("Building merged index");
                token.ThrowIfCancellationRequested();
                index = Internal.MergedIndex.TryBuild(packages, options.MaxMergedIndexBytes, token, options.Progress);
                if (index is null && options.IndexMode == AssetIndexMode.Merged)
                    throw new InvalidOperationException("The merged index exceeds MaxMergedIndexBytes or the supported array capacity. Increase the budget or use Auto/PerPackage indexing.");
            }

            token.ThrowIfCancellationRequested();
            return new(packages, options, index);
        }
        catch
        {
            foreach (var p in readers)
                p.Dispose();
            throw;
        }
    }

    private static IEnumerable<string> Discover(string directory, bool recursive)
    {
        foreach (var item in new DirectoryInfo(directory).EnumerateFileSystemInfos())
        {
            if ((item.Attributes & FileAttributes.ReparsePoint) != 0)
                continue;
            if (item is DirectoryInfo)
            {
                if (recursive && item.Name != ".quire" && !item.Name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
                    foreach (var path in Discover(item.FullName, true))
                        yield return path;
            }
            else if (item.Extension.Equals(".asset", StringComparison.OrdinalIgnoreCase))
                yield return item.FullName;
        }
    }

    private readonly record struct Resolved(PackageReader ContentPackage, EntryRecord Content, PackageReader MetadataPackage, EntryRecord Metadata);
    private Resolved? Resolve(string path)
    {
        Span<byte> wanted = stackalloc byte[AssetPath.Utf8.GetByteCount(path)];
        AssetPath.Utf8.GetBytes(path, wanted);
        if (_index is { } index)
        {
            int file = index.FindFile(wanted);
            if (file < 0)
                return null;
            var resolved = index.ReadFile(file);
            return new(resolved.ContentPackage, resolved.Content, resolved.MetadataPackage, resolved.Metadata);
        }

        (PackageReader Package, EntryRecord Entry)? meta = null;
        for (int i = _packages.Length - 1; i >= 0; i--)
        {
            var found = _packages[i].Find(wanted);
            if (found is not { } e)
                continue;
            if (e.MetadataOnly)
            {
                meta ??= (_packages[i], e);
                continue;
            }

            return new(_packages[i], e, meta?.Package ?? _packages[i], meta?.Entry ?? e);
        }

        return null;
    }

    private Resolved Require(string path) => Resolve(path) ?? throw new FileNotFoundException("Asset content was not found in the loaded packages.", path);
    private static AssetInfo Describe(string path, Resolved r) => new(path, r.Content.Length, r.Metadata.HasMetadata);
    public bool FileExists(string path)
    {
        using var lease = Acquire();
        AssetPath.Validate(path);
        if (_index is not { } index)
            return Resolve(path) is not null;
        return FindIndexedFile(index, path) >= 0;
    }

    private static int FindIndexedFile(MergedIndex index, string path)
    {
        Span<byte> wanted = stackalloc byte[AssetPath.Utf8.GetByteCount(path)];
        AssetPath.Utf8.GetBytes(path, wanted);
        return index.FindFile(wanted);
    }

    public bool TryGetFileInfo(string path, out AssetInfo info)
    {
        using var lease = Acquire();
        AssetPath.Validate(path);
        if (_index is { } index)
        {
            int file = FindIndexedFile(index, path);
            info = file < 0 ? default : index.GetFileInfo(file, path);
            return file >= 0;
        }

        var r = Resolve(path);
        info = r is { } resolved ? Describe(path, resolved) : default;
        return r is not null;
    }

    public AssetInfo GetFileInfo(string path)
    {
        using var lease = Acquire();
        AssetPath.Validate(path);
        if (_index is { } index)
        {
            int file = FindIndexedFile(index, path);
            if (file < 0)
                throw new FileNotFoundException("Asset content was not found in the loaded packages.", path);
            return index.GetFileInfo(file, path);
        }

        return Describe(path, Require(path));
    }

    /// <summary>Enumerates visible files in UTF-8 path order. The empty path denotes the package root.</summary>
    public FileEnumerable EnumerateFiles(string directory = "", bool recursive = true)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _closed) != 0, this);
        AssetPath.Validate(directory, true);
        return new(this, _packages, directory, recursive);
    }

    /// <summary>Enumerates distinct descendant directory paths. Set recursive to false for direct child directories only.</summary>
    public DirectoryEnumerable EnumerateDirectories(string directory = "", bool recursive = true)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _closed) != 0, this);
        AssetPath.Validate(directory, true);
        return new(this, _packages, directory, recursive);
    }

    /// <summary>Tests whether a virtual directory contains visible files. The root always exists.</summary>
    public bool DirectoryExists(string directory)
    {
        using var lease = Acquire();
        AssetPath.Validate(directory, true);
        if (directory.Length == 0)
            return true;
        int length = AssetPath.Utf8.GetByteCount(directory);
        Span<byte> prefix = stackalloc byte[length + 1];
        AssetPath.Utf8.GetBytes(directory, prefix);
        if (_index is { } index)
            return index.FindDirectory(prefix[..length]) >= 0;
        prefix[length] = (byte)'/';
        for (int i = _packages.Length - 1; i >= 0; i--)
        {
            if (_packages[i].DirectoryExists(prefix))
                return true;
        }

        return false;
    }

    public Metadata ReadMetadata(string path) => ReadMetadataCoreAsync(path, false, default).GetAwaiter().GetResult();
    public ValueTask<Metadata> ReadMetadataAsync(string path, CancellationToken cancellationToken = default) => ReadMetadataCoreAsync(path, true, cancellationToken);
    private async ValueTask<Metadata> ReadMetadataCoreAsync(string path, bool asynchronous, CancellationToken token)
    {
        using var lease = Acquire();
        AssetPath.Validate(path);
        token.ThrowIfCancellationRequested();
        return await MetadataAsync(Require(path), asynchronous, token).ConfigureAwait(false);
    }

    private async ValueTask<Metadata> MetadataAsync(Resolved r, bool asynchronous, CancellationToken token)
    {
        if (!r.Metadata.HasMetadata)
            return Metadata.Empty;
        var key = (r.MetadataPackage, r.Metadata.MetadataOffset);
        lock (_cacheSync)
            if (_metadata.TryGetValue(key, out var existing))
                return existing;
        var result = await r.MetadataPackage.ReadMetadataAsync(r.Metadata, asynchronous, token).ConfigureAwait(false);
        if (result.ByteLength <= _options.MetadataCacheBytes)
        {
            lock (_cacheSync)
            {
                if (_metadata.TryGetValue(key, out var existing))
                    return existing;
                while (_metadata.Count > 0 && _metadataBytes + result.ByteLength > _options.MetadataCacheBytes)
                {
                    var old = _metadataOrder.Dequeue();
                    _metadataBytes -= _metadata[old].ByteLength;
                    _metadata.Remove(old);
                }

                _metadata.Add(key, result);
                _metadataOrder.Enqueue(key);
                _metadataBytes += result.ByteLength;
            }
        }

        return result;
    }

    /// <summary>Opens a reusable reader with sequential and positional reads. Dispose it to release buffers and the package lease.</summary>
    public AssetReader OpenRead(string path)
    {
        AcquireReference();
        try
        {
            AssetPath.Validate(path);
            var r = Require(path);
            return new AssetReader(r.ContentPackage, r.Content, this, Describe(path, r));
        }
        catch
        {
            ReleaseReference();
            throw;
        }
    }

    public int ReadAt(string path, long offset, Span<byte> destination)
    {
        using var lease = Acquire();
        AssetPath.Validate(path);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        var r = Require(path);
        if (r.Content.Blocked)
        {
            using var reader = new AssetReader(r.ContentPackage, r.Content, null);
            return reader.ReadAt(offset, destination);
        }

        int count = (int)Math.Min(destination.Length, Math.Max(0, r.Content.Length - offset));
        if (count > 0)
            r.ContentPackage.File.Read(destination[..count], checked(r.Content.DataOffset + offset));
        return count;
    }

    public async ValueTask<int> ReadAtAsync(string path, long offset, Memory<byte> destination, CancellationToken cancellationToken = default)
    {
        using var lease = Acquire();
        AssetPath.Validate(path);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        cancellationToken.ThrowIfCancellationRequested();
        var r = Require(path);
        if (r.Content.Blocked)
        {
            using var reader = new AssetReader(r.ContentPackage, r.Content, null);
            return await reader.ReadAtAsync(offset, destination, cancellationToken).ConfigureAwait(false);
        }

        int count = (int)Math.Min(destination.Length, Math.Max(0, r.Content.Length - offset));
        if (count > 0)
            await r.ContentPackage.File.ReadAsync(destination[..count], checked(r.Content.DataOffset + offset), cancellationToken).ConfigureAwait(false);
        return count;
    }

    public void CopyTo(string path, Stream destination, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var stream = OpenRead(path);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(65536);
        try
        {
            int n;
            while ((n = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                destination.Write(buffer, 0, n);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, true);
        }
    }

    public async Task CopyToAsync(string path, Stream destination, CancellationToken cancellationToken = default)
    {
        using var stream = OpenRead(path);
        await stream.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
    }

    internal async Task CopyToVerifiedAsync(string path, Stream destination, CancellationToken token, IOperationProgress? progress = null)
    {
        using var lease = Acquire();
        AssetPath.Validate(path);
        token.ThrowIfCancellationRequested();
        var resolved = Require(path);
        using var stream = new AssetReader(resolved.ContentPackage, resolved.Content, null);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(65536);
        try
        {
            while (true)
            {
                int read = await stream.ReadAsync(buffer, token).ConfigureAwait(false);
                if (read == 0)
                    break;
                hash.AppendData(buffer, 0, read);
                await destination.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                progress?.AddWritten(read);
                progress?.Advance(read);
            }

            Span<byte> digest = stackalloc byte[32];
            hash.GetHashAndReset(digest);
            if (!resolved.Content.ContentHash.Matches(digest))
                throw new InvalidDataException($"Asset checksum mismatch: {path}");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, resolved.ContentPackage.Header.Encrypted);
        }
    }

    public AssetData Read(string path, CancellationToken cancellationToken = default) => ReadCoreAsync(path, false, cancellationToken).GetAwaiter().GetResult();
    public ValueTask<AssetData> ReadAsync(string path, CancellationToken cancellationToken = default) => ReadCoreAsync(path, true, cancellationToken);
    private async ValueTask<AssetData> ReadCoreAsync(string path, bool asynchronous, CancellationToken token)
    {
        using var lease = Acquire();
        byte[]? bytes = null;
        bool clear = false;
        try
        {
            AssetPath.Validate(path);
            token.ThrowIfCancellationRequested();
            var r = Require(path);
            if (r.Content.Length > _options.MaxBufferedAssetBytes)
                throw new InvalidOperationException("Asset exceeds MaxBufferedAssetBytes; use OpenRead or ReadAt.");
            int length = checked((int)r.Content.Length);
            clear = r.ContentPackage.Header.Encrypted;
            var metadata = await MetadataAsync(r, asynchronous, token).ConfigureAwait(false);
            bytes = ArrayPool<byte>.Shared.Rent(length);
            if (r.Content.Blocked)
            {
                using var reader = new AssetReader(r.ContentPackage, r.Content, null);
                if (asynchronous)
                    await reader.ReadExactlyAsync(bytes.AsMemory(0, length), token).ConfigureAwait(false);
                else
                {
                    int pos = 0;
                    while (pos < length)
                    {
                        token.ThrowIfCancellationRequested();
                        int n = Math.Min(length - pos, r.ContentPackage.Header.BlockSize);
                        reader.ReadExactly(bytes.AsSpan(pos, n));
                        pos += n;
                    }
                }
            }
            else if (asynchronous)
            {
                if (length > 0)
                    await r.ContentPackage.File.ReadAsync(bytes.AsMemory(0, length), r.Content.DataOffset, token).ConfigureAwait(false);
            }
            else
            {
                int pos = 0;
                while (pos < length)
                {
                    token.ThrowIfCancellationRequested();
                    int n = Math.Min(length - pos, 1024 * 1024);
                    r.ContentPackage.File.Read(bytes.AsSpan(pos, n), checked(r.Content.DataOffset + pos));
                    pos += n;
                }
            }

            Span<byte> hash = stackalloc byte[32];
            SHA256.HashData(bytes.AsSpan(0, length), hash);
            if (!r.Content.ContentHash.Matches(hash))
                throw new InvalidDataException($"Asset checksum mismatch: {path}");
            var result = new AssetData(Describe(path, r), metadata, bytes, length, clear);
            bytes = null;
            return result;
        }
        finally
        {
            if (bytes is not null)
                ArrayPool<byte>.Shared.Return(bytes, clear);
        }
    }

    public void Verify(CancellationToken cancellationToken = default) => VerifyCoreAsync(false, cancellationToken).GetAwaiter().GetResult();
    public Task VerifyAsync(CancellationToken cancellationToken = default) => VerifyCoreAsync(true, cancellationToken);
    private async Task VerifyCoreAsync(bool asynchronous, CancellationToken token)
    {
        using var lease = Acquire();
        byte[] buffer = ArrayPool<byte>.Shared.Rent(65536);
        try
        {
            var progress = _options.Progress;
            if (progress is not null)
            {
                progress.StartStage("Preparing verification");
                long totalBytes = 0, totalFiles = 0;
                foreach (var package in _packages)
                    for (long i = 0; i < package.Header.EntryCount; i++)
                    {
                        token.ThrowIfCancellationRequested();
                        var entry = package.ReadEntry(i);
                        totalBytes = checked(totalBytes + entry.Length + entry.MetadataLength);
                        totalFiles = checked(totalFiles + 1);
                    }

                progress.StartStage("Verifying", totalBytes, totalFiles);
            }

            foreach (var package in _packages)
                for (long i = 0; i < package.Header.EntryCount; i++)
                {
                    token.ThrowIfCancellationRequested();
                    var entry = package.ReadEntry(i);
                    _ = await package.ReadMetadataAsync(entry, asynchronous, token).ConfigureAwait(false);
                    progress?.Advance(entry.MetadataLength);
                    if (entry.MetadataOnly)
                    {
                        progress?.Advance(files: 1);
                        continue;
                    }

                    using var stream = new AssetReader(package, entry, null);
                    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    while (true)
                    {
                        token.ThrowIfCancellationRequested();
                        int n = asynchronous ? await stream.ReadAsync(buffer, token).ConfigureAwait(false) : stream.Read(buffer);
                        if (n == 0)
                            break;
                        hash.AppendData(buffer.AsSpan(0, n));
                        progress?.Advance(n);
                    }

                    if (!MatchesHash(hash, entry.ContentHash))
                        throw new InvalidDataException($"Asset checksum mismatch: {package.ReadPath(i)}");
                    progress?.Advance(files: 1);
                }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, true);
        }
    }

    private static bool MatchesHash(IncrementalHash hash, ContentDigest expected)
    {
        Span<byte> digest = stackalloc byte[32];
        hash.GetHashAndReset(digest);
        return expected.Matches(digest);
    }

    private ScopeLease Acquire()
    {
        AcquireReference();
        return new(this);
    }

    internal void AcquireReference()
    {
        while (true)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _closed) != 0, this);
            int count = Volatile.Read(ref _references);
            if (count == 0)
                throw new ObjectDisposedException(nameof(PackageSet));
            if (Interlocked.CompareExchange(ref _references, checked(count + 1), count) != count)
                continue;
            if (Volatile.Read(ref _closed) == 0)
                return;
            ReleaseReference();
            throw new ObjectDisposedException(nameof(PackageSet));
        }
    }

    internal void ReleaseReference()
    {
        if (Interlocked.Decrement(ref _references) != 0)
            return;
        _index = null;
        lock (_cacheSync)
        {
            _metadata.Clear();
            _metadataOrder.Clear();
        }

        foreach (var p in _packages)
            p.Dispose();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _closed, 1) == 0)
            ReleaseReference();
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    // Private scopes are never copied or boxed and release exactly once, including across awaits.
    private readonly struct ScopeLease(PackageSet owner) : IDisposable
    {
        public void Dispose() => owner.ReleaseReference();
    }
}
