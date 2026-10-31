using System.Buffers;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Threading.Channels;
using Quire.Cli.Sources;
using Quire.Internal;

namespace Quire.Cli.Build;

internal sealed class BuildEntry(SourceAsset source, EntryKind kind)
{
    internal SourceAsset Source { get; } = source;
    internal EntryKind Kind { get; } = kind;
    internal bool MetadataOnly => Kind == EntryKind.MetadataOnly;
    internal bool Deleted => Kind == EntryKind.Deleted;
    internal bool Blocked;
    internal long FirstBlock, BlockCount, MetadataOffset, DataOffset, StoredLength;
}

internal static class PackageWriter
{
    private sealed record BlockJob(BuildEntry Entry, byte[] Bytes, int Length, long Sequence);

    internal static Task WriteAsync(string destination, string scratch, string tag, long buildTicks,
        BuildEntry[] entries, PublisherKey? publisherKey, BuildOptions options, CancellationToken token, IOperationProgress? progress = null)
    {
        token.ThrowIfCancellationRequested();
        Guid packageUid = Guid.NewGuid();
        var manifest = PackageManifest.Create([new(packageUid, Path.GetFileName(destination))]);
        return WriteAsync(destination, scratch, tag, buildTicks, Guid.NewGuid(), tag, packageUid, manifest,
            entries, publisherKey, publisherKey is not null, options, token, progress);
    }

    internal static async Task WriteAsync(string destination, string scratch, string tag, long buildTicks,
        Guid tagUid, string tagName, Guid packageUid, PackageManifest manifest, BuildEntry[] entries,
        PublisherKey? publisherKey, bool encrypted, BuildOptions options, CancellationToken token, IOperationProgress? progress = null)
    {
        token.ThrowIfCancellationRequested();
        options.Validate();
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentException.ThrowIfNullOrWhiteSpace(tagName);
        if (tagUid == Guid.Empty || packageUid == Guid.Empty || !manifest.Contains(packageUid))
            throw new ArgumentException("The package and Tag UIDs must be nonempty, and the manifest must contain this package.", nameof(manifest));
        int jobs = options.EffectiveJobs;
        Directory.CreateDirectory(scratch);
        if (encrypted && publisherKey is null) throw new ArgumentException("Encryption requires publisher keys.", nameof(publisherKey));
        int compressedCapacity = 0;
        long directoryLength = checked(entries.LongLength * PackageHeader.EntrySize), metaLength = 0, blockCount = 0;
        foreach (var e in entries)
        {
            if (!Enum.IsDefined(e.Kind) || (e.Deleted && (e.Source.Length != 0 || e.Source.Metadata is not null)))
                throw new ArgumentException("Deletion entries must not contain content or metadata.", nameof(entries));
            if (!Enum.IsDefined(e.Source.Compression) || !Enum.IsDefined(e.Source.CompressionPolicy))
                throw new ArgumentException("Unsupported compression algorithm or policy.", nameof(entries));
            directoryLength = checked(directoryLength + AssetPath.Utf8.GetByteCount(e.Source.Path));
            if (e.Source.Metadata is { } metadata) { e.MetadataOffset = metaLength; metaLength = checked(metaLength + metadata.Length); }
            e.Blocked = e.Kind == EntryKind.Content && (e.Source.Compression != CompressionAlgorithm.None || publisherKey is not null);
            if (e.Blocked)
            {
                e.FirstBlock = blockCount; e.BlockCount = PackageHeader.Pages(e.Source.Length, options.BlockSize);
                blockCount = checked(blockCount + e.BlockCount);
                if (e.Source.Compression != CompressionAlgorithm.None)
                    compressedCapacity = Math.Max(compressedCapacity, BlockCompression.GetMaxCompressedLength(e.Source.Compression, options.BlockSize));
            }
        }
        long blocksLength = checked(blockCount * PackageHeader.BlockEntrySize);
        long dataOffset = PackageHeader.Align(checked((long)PackageHeader.Size + manifest.Encoded.Length + PackageHeader.ProtectedLength(directoryLength) +
            PackageHeader.ProtectedLength(metaLength) + PackageHeader.ProtectedLength(blocksLength)));
        var header = new PackageHeader(tag, buildTicks, encrypted, options.BlockSize, publisherKey is null ? 0 : PublisherKey.ProfileVersion,
            0, entries.LongLength, directoryLength, metaLength, blocksLength, dataOffset, RandomNumberGenerator.GetBytes(16))
        {
            PackageUid = packageUid,
            TagUid = tagUid,
            TagName = tagName,
            ManifestLength = manifest.Encoded.Length,
            ManifestHash = manifest.Hash,
            Signed = publisherKey is not null
        };
        if (header.AuthenticationLength > Array.MaxLength)
            throw new InvalidDataException("Package authentication table exceeds the supported size. Split resources into smaller packages.");
        byte[] key = BlockCipher.DeriveKey(header, publisherKey?.ClientKey);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        var ct = cancellation.Token;
        // Keep staging names independent of the final name, which may already use
        // all 255 UTF-8 bytes allowed by the filesystem for one path component.
        string temporary = Path.Combine(Path.GetDirectoryName(destination) ?? string.Empty, ".quire-package-" + Guid.NewGuid().ToString("N") + ".tmp");
        bool temporaryOwned = false;
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.Asynchronous | FileOptions.RandomAccess))
            {
                temporaryOwned = true;
                await using var blockTable = new FileStream(Path.Combine(scratch, "blocks.bin"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.Asynchronous | FileOptions.RandomAccess);
                await using var authentication = publisherKey is null ? null : new FileStream(Path.Combine(scratch, "authentication.bin"),
                    FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.Asynchronous | FileOptions.RandomAccess);
                authentication?.SetLength(header.AuthenticationLength);
                long dataHashBase = PackageHeader.Pages(directoryLength, PackageHeader.PageSize) + PackageHeader.Pages(metaLength, PackageHeader.PageSize) +
                    PackageHeader.Pages(blocksLength, PackageHeader.PageSize);
                output.SetLength(dataOffset); blockTable.SetLength(blocksLength);
                long end = dataOffset;
                using var writeGate = new SemaphoreSlim(options.WriteConcurrency);
                var channel = Channel.CreateBounded<BlockJob>(new BoundedChannelOptions(jobs * 2) { FullMode = BoundedChannelFullMode.Wait });
                ExceptionDispatchInfo? pipelineFailure = null;

                void FailPipeline(Exception error)
                {
                    // Preserve the cause before waking peers with cancellation or a closed channel.
                    Interlocked.CompareExchange(ref pipelineFailure, ExceptionDispatchInfo.Capture(error), null);
                    channel.Writer.TryComplete(error);
                    cancellation.Cancel();
                }

                async ValueTask WriteAt(ReadOnlyMemory<byte> bytes, long position)
                {
                    await writeGate.WaitAsync(ct).ConfigureAwait(false);
                    try
                    {
                        await RandomAccess.WriteAsync(output.SafeFileHandle, bytes, position, ct).ConfigureAwait(false);
                        progress?.AddWritten(bytes.Length);
                    }
                    finally { writeGate.Release(); }
                }
                long Reserve(long size)
                {
                    // A CAS loop preserves checked 64-bit address arithmetic.
                    while (true)
                    {
                        long before = Volatile.Read(ref end), after = checked(before + size);
                        if (Interlocked.CompareExchange(ref end, after, before) == before) return before;
                    }
                }
                var workers = Enumerable.Range(0, jobs).Select(_ => Task.Run(async () =>
                {
                    byte[] compressed = [], secured = [];
                    try
                    {
                        // Initialization failures must also stop producers waiting on the bounded queue.
                        compressed = compressedCapacity > 0 ? ArrayPool<byte>.Shared.Rent(compressedCapacity) : [];
                        secured = encrypted ? ArrayPool<byte>.Shared.Rent(checked(Math.Max(options.BlockSize, compressedCapacity) + 16)) : [];
                        byte[] descriptor = new byte[PackageHeader.BlockEntrySize];
                        byte[] digest = authentication is null ? [] : new byte[32];
                        using var cipher = encrypted ? new BlockCipher(key, tag, 4) : null;
                        await foreach (var job in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                        {
                            try
                            {
                                byte codec = 0; ReadOnlyMemory<byte> payload = job.Bytes.AsMemory(0, job.Length);
                                if (job.Entry.Source.Compression != CompressionAlgorithm.None)
                                {
                                    int size = BlockCompression.Encode(job.Entry.Source.Compression, payload.Span, compressed);
                                    if (size <= 0) throw new InvalidDataException("Block compression failed.");
                                    if (job.Entry.Source.CompressionPolicy == CompressionPolicy.Always || size < job.Length)
                                    {
                                        codec = (byte)job.Entry.Source.Compression;
                                        payload = compressed.AsMemory(0, size);
                                    }
                                }
                                if (cipher is not null)
                                { cipher.Encrypt(job.Sequence, payload.Span, secured.AsSpan(0, payload.Length + 16), job.Length, codec); payload = secured.AsMemory(0, payload.Length + 16); }
                                long position = Reserve(payload.Length);
                                await WriteAt(payload, position).ConfigureAwait(false);
                                if (authentication is not null)
                                {
                                    SHA256.HashData(payload.Span, digest);
                                    await RandomAccess.WriteAsync(authentication.SafeFileHandle, digest, checked((dataHashBase + job.Sequence) * 32), ct).ConfigureAwait(false);
                                    progress?.AddWritten(digest.Length);
                                }
                                var block = new BlockRecord(position, payload.Length, job.Length, job.Sequence, codec);
                                block.Encode(descriptor);
                                await RandomAccess.WriteAsync(blockTable.SafeFileHandle, descriptor, checked(job.Sequence * PackageHeader.BlockEntrySize), ct).ConfigureAwait(false);
                                progress?.AddWritten(PackageHeader.BlockEntrySize);
                                progress?.Advance(job.Length);
                                Interlocked.Add(ref job.Entry.StoredLength, payload.Length);
                            }
                            finally { ArrayPool<byte>.Shared.Return(job.Bytes, encrypted); }
                        }
                    }
                    catch (Exception error) { FailPipeline(error); throw; }
                    finally
                    {
                        if (compressed.Length != 0) ArrayPool<byte>.Shared.Return(compressed, encrypted);
                        if (secured.Length != 0) ArrayPool<byte>.Shared.Return(secured, true);
                    }
                }, CancellationToken.None)).ToArray();

                async Task Produce()
                {
                    try
                    {
                        await Parallel.ForEachAsync(entries.Where(e => e.Kind == EntryKind.Content), new ParallelOptions { MaxDegreeOfParallelism = options.IoConcurrency, CancellationToken = ct }, async (e, entryToken) =>
                        {
                            await using var source = new FileStream(e.Source.SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.Asynchronous | FileOptions.SequentialScan);
                            if (source.Length != e.Source.Length) throw new IOException($"Source length changed: {e.Source.SourcePath}");
                            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                            if (!e.Blocked && e.Source.Length > 0) e.DataOffset = Reserve(e.Source.Length);
                            long position = 0, block = 0;
                            while (position < e.Source.Length)
                            {
                                entryToken.ThrowIfCancellationRequested();
                                int count = (int)Math.Min(options.BlockSize, e.Source.Length - position);
                                byte[]? bytes = ArrayPool<byte>.Shared.Rent(options.BlockSize);
                                try
                                {
                                    await source.ReadExactlyAsync(bytes.AsMemory(0, count), entryToken).ConfigureAwait(false);
                                    progress?.AddRead(count);
                                    hash.AppendData(bytes.AsSpan(0, count));
                                    if (e.Blocked)
                                    { await channel.Writer.WriteAsync(new(e, bytes, count, e.FirstBlock + block), entryToken).ConfigureAwait(false); bytes = null; }
                                    else
                                    {
                                        await WriteAt(bytes.AsMemory(0, count), checked(e.DataOffset + position)).ConfigureAwait(false);
                                        progress?.Advance(count);
                                    }
                                }
                                finally { if (bytes is not null) ArrayPool<byte>.Shared.Return(bytes, encrypted); }
                                position += count; block++;
                            }
                            if (source.Length != e.Source.Length || !CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), e.Source.ContentHash))
                                throw new IOException($"Source content changed after scanning: {e.Source.SourcePath}");
                            if (!e.Blocked) e.StoredLength = e.Source.Length;
                        }).ConfigureAwait(false);
                        channel.Writer.TryComplete();
                    }
                    catch (Exception error) { FailPipeline(error); throw; }
                }
                try { await Task.WhenAll(workers.Append(Produce())).ConfigureAwait(false); }
                catch
                {
                    pipelineFailure?.Throw();
                    throw;
                }
                finally
                {
                    cancellation.Cancel();
                    while (channel.Reader.TryRead(out var remaining)) ArrayPool<byte>.Shared.Return(remaining.Bytes, encrypted);
                }
                token.ThrowIfCancellationRequested();
                header = header with { FileLength = end };
                output.SetLength(end);

                string indexPath = Path.Combine(scratch, "directory.bin"), metaPath = Path.Combine(scratch, "metadata.bin");
                await using (var index = new FileStream(indexPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
                await using (var meta = new FileStream(metaPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
                {
                    long nameOffset = checked(entries.LongLength * PackageHeader.EntrySize);
                    foreach (var e in entries)
                    {
                        var record = new EntryRecord(nameOffset, AssetPath.Utf8.GetByteCount(e.Source.Path), e.Kind,
                            e.Source.Metadata is not null, e.Blocked, e.Kind == EntryKind.Content ? e.Source.Length : 0, e.DataOffset, e.StoredLength,
                            e.MetadataOffset, e.Source.Metadata?.Length ?? 0, e.FirstBlock, e.BlockCount,
                            e.Kind == EntryKind.Content ? new ContentDigest(e.Source.ContentHash) : default);
                        await index.WriteAsync(record.Encode(), token).ConfigureAwait(false); nameOffset += record.PathLength;
                        progress?.AddWritten(PackageHeader.EntrySize);
                        if (e.Source.Metadata is { } bytes)
                        {
                            await meta.WriteAsync(bytes, token).ConfigureAwait(false);
                            progress?.AddWritten(bytes.Length);
                        }
                    }
                    foreach (var e in entries)
                    {
                        byte[] path = AssetPath.Utf8.GetBytes(e.Source.Path);
                        await index.WriteAsync(path, token).ConfigureAwait(false);
                        progress?.AddWritten(path.Length);
                    }
                    await index.FlushAsync(token).ConfigureAwait(false); await meta.FlushAsync(token).ConfigureAwait(false);
                    await ProtectSectionAsync(index, output, header.DirectoryOffset, header, key, 1, authentication, 0, token, progress).ConfigureAwait(false);
                    await ProtectSectionAsync(meta, output, header.MetadataOffset, header, key, 2, authentication,
                        PackageHeader.Pages(directoryLength, PackageHeader.PageSize), token, progress).ConfigureAwait(false);
                }
                await blockTable.FlushAsync(token).ConfigureAwait(false);
                await ProtectSectionAsync(blockTable, output, header.BlocksOffset, header, key, 3, authentication,
                    PackageHeader.Pages(directoryLength, PackageHeader.PageSize) + PackageHeader.Pages(metaLength, PackageHeader.PageSize), token, progress).ConfigureAwait(false);
                await RandomAccess.WriteAsync(output.SafeFileHandle, manifest.Encoded, PackageHeader.Size, token).ConfigureAwait(false);
                progress?.AddWritten(manifest.Encoded.Length);
                if (authentication is not null)
                {
                    await authentication.FlushAsync(token).ConfigureAwait(false);
                    authentication.Position = 0;
                    using var tableHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    byte[] buffer = ArrayPool<byte>.Shared.Rent(PackageHeader.PageSize);
                    long tablePosition = end;
                    try
                    {
                        int count;
                        while ((count = await authentication.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
                        {
                            progress?.AddRead(count);
                            tableHash.AppendData(buffer, 0, count);
                            await RandomAccess.WriteAsync(output.SafeFileHandle, buffer.AsMemory(0, count), tablePosition, token).ConfigureAwait(false);
                            progress?.AddWritten(count);
                            tablePosition = checked(tablePosition + count);
                        }
                    }
                    finally { ArrayPool<byte>.Shared.Return(buffer); }
                    header = header with { FileLength = tablePosition, AuthenticationOffset = end, AuthenticationHash = new ContentDigest(tableHash.GetHashAndReset()) };
                }
                await RandomAccess.WriteAsync(output.SafeFileHandle, header.Encode(key, publisherKey), 0, token).ConfigureAwait(false);
                progress?.AddWritten(PackageHeader.Size);
                output.Flush(true);
            }
            token.ThrowIfCancellationRequested();
            File.Move(temporary, destination);
            temporaryOwned = false;
            progress?.Advance(files: entries.LongLength);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            if (temporaryOwned) File.Delete(temporary);
        }
    }
    private static async Task ProtectSectionAsync(FileStream source, FileStream destination, long offset, PackageHeader header,
        byte[] key, int domain, FileStream? authentication, long hashBase, CancellationToken token, IOperationProgress? progress)
    {
        source.Position = 0;
        byte[] raw = ArrayPool<byte>.Shared.Rent(PackageHeader.PageSize), encoded = ArrayPool<byte>.Shared.Rent(PackageHeader.PageSize + 16);
        using var cipher = new BlockCipher(key, header.TagId, domain);
        byte[] digest = authentication is null ? [] : new byte[32];
        try
        {
            long page = 0;
            while (source.Position < source.Length)
            {
                int size = (int)Math.Min(PackageHeader.PageSize, source.Length - source.Position);
                await source.ReadExactlyAsync(raw.AsMemory(0, size), token).ConfigureAwait(false);
                progress?.AddRead(size);
                cipher.Encrypt(page, raw.AsSpan(0, size), encoded.AsSpan(0, size + 16), size);
                await RandomAccess.WriteAsync(destination.SafeFileHandle, encoded.AsMemory(0, size + 16), offset, token).ConfigureAwait(false);
                progress?.AddWritten(size + 16);
                if (authentication is not null)
                {
                    SHA256.HashData(encoded.AsSpan(0, size + 16), digest);
                    await RandomAccess.WriteAsync(authentication.SafeFileHandle, digest, checked((hashBase + page) * 32), token).ConfigureAwait(false);
                    progress?.AddWritten(digest.Length);
                }
                if (domain == 2) progress?.Advance(size);
                offset += size + 16; page++;
            }
        }
        finally { ArrayPool<byte>.Shared.Return(raw, true); ArrayPool<byte>.Shared.Return(encoded, true); }
    }
}
