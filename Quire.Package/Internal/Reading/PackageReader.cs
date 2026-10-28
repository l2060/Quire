using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Quire.Package.Internal;

internal sealed class PackageReader : IDisposable
{
    internal FileStorage File
    {
        get;
    }
    internal PackageHeader Header
    {
        get;
    }
    internal AssetPackageInfo Info
    {
        get;
    }
    internal byte[] Key
    {
        get;
    }
    internal ProtectedSection Directory
    {
        get;
    }
    internal ProtectedSection Metadata
    {
        get;
    }
    internal ProtectedSection Blocks
    {
        get;
    }
    internal CipherPool Ciphers
    {
        get;
    }

    private PackageReader(FileStorage file, PackageHeader h, AssetPackageInfo info, byte[] key, ReadOptions options)
    {
        File = file;
        Header = h;
        Info = info;
        Key = key;
        Ciphers = new(key, h.TagId);
        Directory = new(file, h, key, 1, h.DirectoryOffset, h.DirectoryLength, options.PageCacheBytes / 3);
        Metadata = new(file, h, key, 2, h.MetadataOffset, h.MetadataLength, options.PageCacheBytes / 3);
        Blocks = new(file, h, key, 3, h.BlocksOffset, h.BlocksLength, options.PageCacheBytes / 3);
    }

    internal static async ValueTask<PackageReader> OpenAsync(string path, ReadOptions options, bool asynchronous, CancellationToken token)
    {
        FileStorage? file = null;
        PackageReader? reader = null;
        byte[]? key = null;
        try
        {
            token.ThrowIfCancellationRequested();
            file = new(path, options.ReadMode == AssetReadMode.MemoryMapped, options.Progress);
            byte[] bytes = new byte[PackageHeader.Size];
            if (asynchronous)
            {
                await file.ReadAsync(bytes, 0, token).ConfigureAwait(false);
            }
            else
            {
                file.Read(bytes, 0);
            }

            var h = PackageHeader.Decode(bytes, file.Length);
            var info = new AssetPackageInfo(path, h.TagId, new DateTimeOffset(h.BuildTicks, TimeSpan.Zero), h.EntryCount, h.Encrypted);
            key = BlockCipher.DeriveKey(h, h.Encrypted ? options.PasswordProvider?.Invoke(info) ?? options.Password : null);
            byte[] mac = HMACSHA256.HashData(key, bytes.AsSpan(0, 128));
            if (!CryptographicOperations.FixedTimeEquals(mac, bytes.AsSpan(128, 32)))
            {
                throw new CryptographicException("Incorrect password or unauthenticated package header.");
            }

            reader = new(file, h, info, key, options);
            await reader.Directory.LoadResidentAsync(options.MaxResidentIndexBytes, asynchronous, token).ConfigureAwait(false);
            reader.ValidateDirectory(token);
            return reader;
        }
        catch (Exception e)
        {
            if (reader is not null)
            {
                reader.Dispose();
            }
            else
            {
                if (key is not null)
                {
                    CryptographicOperations.ZeroMemory(key);
                }

                file?.Dispose();
            }

            if (e is OperationCanceledException)
            {
                throw;
            }

            throw new InvalidDataException($"Cannot open asset package '{path}': {e.Message}", e);
        }
    }

    internal EntryRecord ReadEntry(long index)
    {
        if (!Directory.Resident.IsEmpty)
        {
            return EntryRecord.Decode(Directory.Resident.Slice(checked((int)(index * PackageHeader.EntrySize)), PackageHeader.EntrySize), Header);
        }

        Span<byte> b = stackalloc byte[PackageHeader.EntrySize];
        Directory.Read(b, checked(index * PackageHeader.EntrySize));
        return EntryRecord.Decode(b, Header);
    }

    private (long Offset, int Length) ReadPathLocation(long index)
    {
        if (!Directory.Resident.IsEmpty)
        {
            var location = Directory.Resident.Slice(checked((int)(index * PackageHeader.EntrySize)), 12);
            return (BinaryPrimitives.ReadInt64LittleEndian(location), BinaryPrimitives.ReadInt32LittleEndian(location[8..]));
        }

        Span<byte> bytes = stackalloc byte[12];
        Directory.Read(bytes, checked(index * PackageHeader.EntrySize));
        return (BinaryPrimitives.ReadInt64LittleEndian(bytes), BinaryPrimitives.ReadInt32LittleEndian(bytes[8..]));
    }

    internal string ReadPath(long index)
    {
        var p = ReadPathLocation(index);
        if (!Directory.Resident.IsEmpty)
        {
            return AssetPath.Utf8.GetString(Directory.Resident.Slice((int)p.Offset, p.Length));
        }

        Span<byte> bytes = stackalloc byte[p.Length];
        Directory.Read(bytes, p.Offset);
        return AssetPath.Utf8.GetString(bytes);
    }

    internal ReadOnlySpan<byte> ReadPathBytes(long index, Span<byte> scratch)
    {
        var location = ReadPathLocation(index);
        if (!Directory.Resident.IsEmpty)
        {
            return Directory.Resident.Slice((int)location.Offset, location.Length);
        }

        Directory.Read(scratch[..location.Length], location.Offset);
        return scratch[..location.Length];
    }

    internal AssetInfo ReadFileInfo(long index, string? path, bool metadataOverride)
    {
        // OpenAsync validates every complete record before publishing this immutable reader.
        // Enumeration only needs the path location, metadata flag and length in its first 24 bytes.
        long position = checked(index * PackageHeader.EntrySize);
        Span<byte> scratch = stackalloc byte[24];
        scoped ReadOnlySpan<byte> fields;
        if (!Directory.Resident.IsEmpty)
        {
            fields = Directory.Resident.Slice(checked((int)position), 24);
        }
        else
        {
            Directory.Read(scratch, position);
            fields = scratch;
        }
        long length = BinaryPrimitives.ReadInt64LittleEndian(fields[16..]);
        bool hasMetadata = metadataOverride || (fields[13] & 1) != 0;
        if (path is null)
        {
            long pathOffset = BinaryPrimitives.ReadInt64LittleEndian(fields);
            int pathLength = BinaryPrimitives.ReadInt32LittleEndian(fields[8..]);
            if (!Directory.Resident.IsEmpty)
            {
                path = AssetPath.Utf8.GetString(Directory.Resident.Slice((int)pathOffset, pathLength));
            }
            else
            {
                Span<byte> pathBytes = stackalloc byte[4096];
                Directory.Read(pathBytes[..pathLength], pathOffset);
                path = AssetPath.Utf8.GetString(pathBytes[..pathLength]);
            }
        }
        return new AssetInfo(path, length, hasMetadata);
    }

    internal EntryRecord? Find(string path)
    {
        int n = AssetPath.Utf8.GetByteCount(path);
        Span<byte> wanted = stackalloc byte[n];
        AssetPath.Utf8.GetBytes(path, wanted);
        return Find(wanted);
    }

    internal EntryRecord? Find(ReadOnlySpan<byte> wanted)
    {
        Span<byte> scratch = Directory.Resident.IsEmpty ? stackalloc byte[4096] : Span<byte>.Empty;
        long lo = 0, hi = Header.EntryCount;
        while (lo < hi)
        {
            long mid = lo + (hi - lo) / 2;
            var p = ReadPathLocation(mid);
            int cmp;
            if (!Directory.Resident.IsEmpty)
            {
                cmp = Directory.Resident.Slice((int)p.Offset, p.Length).SequenceCompareTo(wanted);
            }
            else
            {
                Directory.Read(scratch[..p.Length], p.Offset);
                cmp = scratch[..p.Length].SequenceCompareTo(wanted);
            }

            if (cmp == 0)
            {
                return ReadEntry(mid);
            }

            if (cmp < 0)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        return null;
    }

    internal long LowerBound(ReadOnlySpan<byte> wanted, long start, long end)
    {
        Span<byte> scratch = Directory.Resident.IsEmpty ? stackalloc byte[4096] : Span<byte>.Empty;
        while (start < end)
        {
            long mid = start + (end - start) / 2;
            var location = ReadPathLocation(mid);
            int comparison;
            if (!Directory.Resident.IsEmpty)
            {
                comparison = Directory.Resident.Slice((int)location.Offset, location.Length).SequenceCompareTo(wanted);
            }
            else
            {
                Directory.Read(scratch[..location.Length], location.Offset);
                comparison = scratch[..location.Length].SequenceCompareTo(wanted);
            }

            if (comparison < 0)
            {
                start = mid + 1;
            }
            else
            {
                end = mid;
            }
        }

        return start;
    }

    internal bool DirectoryExists(ReadOnlySpan<byte> prefix)
    {
        Span<byte> scratch = Directory.Resident.IsEmpty ? stackalloc byte[4096] : Span<byte>.Empty;
        long start = prefix.IsEmpty ? 0 : LowerBound(prefix, 0, Header.EntryCount);
        for (long index = start; index < Header.EntryCount; index++)
        {
            var entry = ReadEntry(index);
            scoped ReadOnlySpan<byte> path;
            if (!Directory.Resident.IsEmpty)
            {
                path = Directory.Resident.Slice((int)entry.PathOffset, entry.PathLength);
            }
            else
            {
                Directory.Read(scratch[..entry.PathLength], entry.PathOffset);
                path = scratch[..entry.PathLength];
            }

            if (!path.StartsWith(prefix))
            {
                return false;
            }

            if (!entry.MetadataOnly)
            {
                return true;
            }
        }

        return false;
    }

    private void ValidateDirectory(CancellationToken token)
    {
        Span<byte> first = Directory.Resident.IsEmpty ? stackalloc byte[4096] : Span<byte>.Empty;
        Span<byte> second = Directory.Resident.IsEmpty ? stackalloc byte[4096] : Span<byte>.Empty;
        int previousLength = 0;
        long previousOffset = 0;
        long expectedPath = checked(Header.EntryCount * PackageHeader.EntrySize);
        for (long index = 0; index < Header.EntryCount; index++)
        {
            token.ThrowIfCancellationRequested();
            var entry = ReadEntry(index);
            if (entry.PathOffset != expectedPath)
            {
                throw new InvalidDataException("Noncanonical path pool.");
            }

            scoped ReadOnlySpan<byte> path;
            if (!Directory.Resident.IsEmpty)
            {
                path = Directory.Resident.Slice((int)entry.PathOffset, entry.PathLength);
            }
            else
            {
                Span<byte> target = (index & 1) == 0 ? first : second;
                Directory.Read(target[..entry.PathLength], entry.PathOffset);
                path = target[..entry.PathLength];
            }

            scoped ReadOnlySpan<byte> previous = !Directory.Resident.IsEmpty ? Directory.Resident.Slice((int)previousOffset, previousLength) : ((index & 1) == 0 ? second : first)[..previousLength];
            ValidatePath(path);
            if (index > 0 && previous.SequenceCompareTo(path) >= 0)
            {
                throw new InvalidDataException("Asset paths must be unique and sorted.");
            }

            previousLength = entry.PathLength;
            previousOffset = entry.PathOffset;
            expectedPath += entry.PathLength;
        }

        if (expectedPath != Header.DirectoryLength)
        {
            throw new InvalidDataException("Untracked path data.");
        }
    }

    private static void ValidatePath(ReadOnlySpan<byte> path)
    {
        // Strict UTF-8 validation does not require constructing a temporary string.
        _ = AssetPath.Utf8.GetCharCount(path);
        int segmentStart = 0;
        for (int index = 0; index <= path.Length; index++)
        {
            if (index == path.Length || path[index] == (byte)'/')
            {
                var segment = path[segmentStart..index];
                if (segment.IsEmpty || segment.SequenceEqual("."u8) || segment.SequenceEqual(".."u8))
                {
                    throw new InvalidDataException("Empty, '.' and '..' path segments are not allowed.");
                }

                segmentStart = index + 1;
                continue;
            }

            byte value = path[index];
            if (value < 32 || value == 127 || value is (byte)'\\' or (byte)':' or (byte)'*' or (byte)'?' or (byte)'"' or (byte)'<' or (byte)'>' or (byte)'|' || (value == 0xc2 && index + 1 < path.Length && path[index + 1] is >= 0x80 and <= 0x9f))
            {
                throw new InvalidDataException("Invalid asset path character.");
            }
        }
    }

    internal async ValueTask<Metadata> ReadMetadataAsync(EntryRecord e, bool asynchronous, CancellationToken token)
    {
        if (!e.HasMetadata)
        {
            return Package.Metadata.Empty;
        }

        byte[] bytes = new byte[e.MetadataLength];
        if (asynchronous)
        {
            await Metadata.ReadAsync(bytes, e.MetadataOffset, token).ConfigureAwait(false);
        }
        else
        {
            Metadata.Read(bytes, e.MetadataOffset);
        }

        return new Metadata(bytes);
    }

    public void Dispose()
    {
        Directory.Clear();
        Metadata.Clear();
        Blocks.Clear();
        Ciphers.Dispose();
        CryptographicOperations.ZeroMemory(Key);
        File.Dispose();
    }
}
