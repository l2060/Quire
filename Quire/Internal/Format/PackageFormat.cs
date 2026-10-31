using System;
using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;

namespace Quire.Internal;

internal sealed record PackageHeader(string TagId, long BuildTicks, bool Encrypted, int BlockSize,
    int KeyVersion, long FileLength, long EntryCount, long DirectoryLength, long MetadataLength,
    long BlocksLength, long DataOffset, byte[] Salt)
{
    internal const int Version = 6;
    internal const int Size = 4096, PageSize = 65536, EntrySize = 128, BlockEntrySize = 32;
    internal const int MaxMetadataBytes = 1024 * 1024;
    internal const int MacOffset = 4032, MacLength = 32, ChecksumOffset = 4064, ChecksumLength = 32;
    internal const int TagNameOffset = 176, MaxTagNameBytes = 255;
    internal const int SignatureOffset = 480, SignatureLength = 64;
    internal Guid PackageUid { get; init; }
    internal Guid TagUid { get; init; }
    internal string TagName { get; init; } = TagId;
    internal int ManifestLength { get; init; }
    internal ContentDigest ManifestHash { get; init; }
    internal bool Signed { get; init; }
    internal long AuthenticationOffset { get; init; }
    internal ContentDigest AuthenticationHash { get; init; }
    internal long AuthenticationCount => Signed ? checked(Pages(DirectoryLength, PageSize) + Pages(MetadataLength, PageSize) +
        Pages(BlocksLength, PageSize) + BlocksLength / BlockEntrySize) : 0;
    internal long AuthenticationLength => checked(AuthenticationCount * 32);
    internal long PayloadEnd => Signed ? AuthenticationOffset : FileLength;
    internal long DirectoryOffset => checked((long)Size + ManifestLength);
    internal long MetadataOffset => checked(DirectoryOffset + ProtectedLength(DirectoryLength));
    internal long BlocksOffset => checked(MetadataOffset + ProtectedLength(MetadataLength));
    internal static long Pages(long length, int size) => length == 0 ? 0 : (length - 1) / size + 1;
    internal static long ProtectedLength(long length) => checked(length + Pages(length, PageSize) * 16);
    internal static long Align(long value) => checked((value + 7) & ~7L);

    internal byte[] Encode(byte[] key, PublisherKey? signer = null)
    {
        if (Signed != (signer is not null)) throw new ArgumentException("A signed package requires its publisher's private key.", nameof(signer));
        byte[] bytes = new byte[Size];
        "PK.QUIRE"u8.CopyTo(bytes);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), Version);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(12), (Encrypted ? 1 : 0) | (Signed ? 2 : 0));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), PackageTag.Parse(TagId));
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(20), ManifestLength);
        int nameLength = ValidateTagName(TagName);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(24), nameLength);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(32), BuildTicks);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(40), BlockSize);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(44), KeyVersion);
        long[] fields = [FileLength, EntryCount, DirectoryLength, MetadataLength, BlocksLength, DataOffset];
        for (int i = 0; i < fields.Length; i++)
            BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(48 + i * 8), fields[i]);
        Salt.CopyTo(bytes, 96);
        PackageUid.TryWriteBytes(bytes.AsSpan(112, 16));
        TagUid.TryWriteBytes(bytes.AsSpan(128, 16));
        ManifestHash.WriteTo(bytes.AsSpan(144, 32));
        AssetPath.Utf8.GetBytes(TagName.AsSpan(), bytes.AsSpan(TagNameOffset, nameLength));
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(432), AuthenticationOffset);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(440), AuthenticationCount);
        AuthenticationHash.WriteTo(bytes.AsSpan(448, 32));
        if (signer is not null)
        {
            Span<byte> digest = stackalloc byte[32];
            SHA256.HashData(bytes.AsSpan(0, SignatureOffset), digest);
            signer.Sign(digest, bytes.AsSpan(SignatureOffset, SignatureLength));
        }
        HMACSHA256.HashData(key, bytes.AsSpan(0, MacOffset), bytes.AsSpan(MacOffset, MacLength));
        SHA256.HashData(bytes.AsSpan(0, ChecksumOffset), bytes.AsSpan(ChecksumOffset, ChecksumLength));
        return bytes;
    }

    internal static PackageHeader Decode(ReadOnlySpan<byte> bytes, long actualLength)
    {
        if (bytes.Length < Size) throw new InvalidDataException("Truncated asset header.");
        bytes = bytes[..Size];
        Span<byte> digest = stackalloc byte[32];
        SHA256.HashData(bytes[..ChecksumOffset], digest);
        if (!bytes[..8].SequenceEqual("PK.QUIRE"u8) || BinaryPrimitives.ReadInt32LittleEndian(bytes[8..]) != Version ||
            !CryptographicOperations.FixedTimeEquals(digest, bytes.Slice(ChecksumOffset, ChecksumLength)))
            throw new InvalidDataException("Invalid or unsupported asset header.");
        int nameLength = BinaryPrimitives.ReadInt32LittleEndian(bytes[24..]);
        if (nameLength is < 1 or > MaxTagNameBytes || bytes.Slice(28, 4).IndexOfAnyExcept((byte)0) >= 0 ||
            bytes.Slice(TagNameOffset + nameLength, 432 - TagNameOffset - nameLength).IndexOfAnyExcept((byte)0) >= 0 ||
            bytes.Slice(SignatureOffset + SignatureLength, MacOffset - SignatureOffset - SignatureLength).IndexOfAnyExcept((byte)0) >= 0)
            throw new InvalidDataException("Invalid package Tag name or reserved header bytes.");
        string tagName;
        try
        {
            tagName = AssetPath.Utf8.GetString(bytes.Slice(TagNameOffset, nameLength));
            ValidateTagName(tagName);
        }
        catch (ArgumentException error) { throw new InvalidDataException("Invalid package Tag name.", error); }
        int flags = BinaryPrimitives.ReadInt32LittleEndian(bytes[12..]);
        if (flags is not (0 or 2 or 3))
            throw new InvalidDataException("Unknown package flags.");
        var h = new PackageHeader(PackageTag.Format(BinaryPrimitives.ReadUInt32LittleEndian(bytes[16..])), BinaryPrimitives.ReadInt64LittleEndian(bytes[32..]), (flags & 1) != 0,
            BinaryPrimitives.ReadInt32LittleEndian(bytes[40..]), BinaryPrimitives.ReadInt32LittleEndian(bytes[44..]),
            BinaryPrimitives.ReadInt64LittleEndian(bytes[48..]), BinaryPrimitives.ReadInt64LittleEndian(bytes[56..]),
            BinaryPrimitives.ReadInt64LittleEndian(bytes[64..]), BinaryPrimitives.ReadInt64LittleEndian(bytes[72..]),
            BinaryPrimitives.ReadInt64LittleEndian(bytes[80..]), BinaryPrimitives.ReadInt64LittleEndian(bytes[88..]), bytes.Slice(96, 16).ToArray())
        {
            PackageUid = new Guid(bytes.Slice(112, 16)),
            TagUid = new Guid(bytes.Slice(128, 16)),
            TagName = tagName,
            ManifestLength = BinaryPrimitives.ReadInt32LittleEndian(bytes[20..]),
            ManifestHash = new ContentDigest(bytes.Slice(144, 32)),
            Signed = (flags & 2) != 0,
            AuthenticationOffset = BinaryPrimitives.ReadInt64LittleEndian(bytes[432..]),
            AuthenticationHash = new ContentDigest(bytes.Slice(448, 32))
        };
        if (h.BuildTicks <= 0 || h.BuildTicks > DateTime.MaxValue.Ticks ||
            h.PackageUid == Guid.Empty || h.TagUid == Guid.Empty ||
            h.ManifestLength is < PackageManifest.MinEncodedLength or > PackageManifest.MaxEncodedLength ||
            h.BlockSize is < 65536 or > 1048576 || (h.BlockSize & (h.BlockSize - 1)) != 0 ||
            h.FileLength != actualLength || h.EntryCount < 0 || h.DirectoryLength < 0 || h.MetadataLength < 0 || h.BlocksLength < 0 ||
            h.KeyVersion != (h.Signed ? PublisherKey.ProfileVersion : 0))
            throw new InvalidDataException("Invalid package dimensions or encryption parameters.");
        try
        {
            if (checked(h.EntryCount * EntrySize) > h.DirectoryLength || h.BlocksLength % BlockEntrySize != 0 ||
                h.DataOffset != Align(checked(h.BlocksOffset + ProtectedLength(h.BlocksLength))) || h.DataOffset > actualLength ||
                BinaryPrimitives.ReadInt64LittleEndian(bytes[440..]) != h.AuthenticationCount ||
                (h.Signed ? h.AuthenticationLength > Array.MaxLength || h.AuthenticationOffset < h.DataOffset ||
                    checked(h.AuthenticationOffset + h.AuthenticationLength) != actualLength :
                    h.AuthenticationOffset != 0 || !h.AuthenticationHash.IsZero || bytes.Slice(SignatureOffset, SignatureLength).IndexOfAnyExcept((byte)0) >= 0))
                throw new InvalidDataException("Invalid package section layout.");
        }
        catch (OverflowException e) { throw new InvalidDataException("Package dimensions overflow.", e); }
        return h;
    }

    private static int ValidateTagName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        foreach (char character in name)
            if (char.IsControl(character)) throw new ArgumentException("Package Tag names cannot contain control characters.", nameof(name));
        int length = AssetPath.Utf8.GetByteCount(name);
        if (length > MaxTagNameBytes) throw new ArgumentException("Package Tag names exceed 255 UTF-8 bytes.", nameof(name));
        return length;
    }
}

internal enum EntryKind : byte
{
    Content = 0,
    MetadataOnly = 1,
    Deleted = 2
}

internal readonly record struct EntryRecord(long PathOffset, int PathLength, EntryKind Kind, bool HasMetadata,
    bool Blocked, long Length, long DataOffset, long StoredLength, long MetadataOffset, int MetadataLength,
    long FirstBlock, long BlockCount, ContentDigest ContentHash)
{
    internal bool MetadataOnly => Kind == EntryKind.MetadataOnly;
    internal bool Deleted => Kind == EntryKind.Deleted;

    internal byte[] Encode()
    {
        byte[] b = new byte[PackageHeader.EntrySize];
        BinaryPrimitives.WriteInt64LittleEndian(b, PathOffset);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(8), PathLength);
        b[12] = (byte)Kind;
        b[13] = (byte)((HasMetadata ? 1 : 0) | (Blocked ? 2 : 0));
        BinaryPrimitives.WriteInt64LittleEndian(b.AsSpan(16), Length);
        BinaryPrimitives.WriteInt64LittleEndian(b.AsSpan(24), DataOffset);
        BinaryPrimitives.WriteInt64LittleEndian(b.AsSpan(32), StoredLength);
        BinaryPrimitives.WriteInt64LittleEndian(b.AsSpan(40), MetadataOffset);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(48), MetadataLength);
        BinaryPrimitives.WriteInt64LittleEndian(b.AsSpan(56), FirstBlock);
        BinaryPrimitives.WriteInt64LittleEndian(b.AsSpan(64), BlockCount);
        ContentHash.WriteTo(b.AsSpan(72));
        return b;
    }

    internal static EntryRecord Decode(ReadOnlySpan<byte> b, PackageHeader h)
    {
        if (b[12] > (byte)EntryKind.Deleted || (b[13] & ~3) != 0 || b.Slice(14, 2).IndexOfAnyExcept((byte)0) >= 0 ||
            b.Slice(52, 4).IndexOfAnyExcept((byte)0) >= 0 || b[104..].IndexOfAnyExcept((byte)0) >= 0)
            throw new InvalidDataException("Invalid asset entry flags.");
        var e = new EntryRecord(BinaryPrimitives.ReadInt64LittleEndian(b), BinaryPrimitives.ReadInt32LittleEndian(b[8..]),
            (EntryKind)b[12], (b[13] & 1) != 0, (b[13] & 2) != 0,
            BinaryPrimitives.ReadInt64LittleEndian(b[16..]), BinaryPrimitives.ReadInt64LittleEndian(b[24..]),
            BinaryPrimitives.ReadInt64LittleEndian(b[32..]), BinaryPrimitives.ReadInt64LittleEndian(b[40..]),
            BinaryPrimitives.ReadInt32LittleEndian(b[48..]), BinaryPrimitives.ReadInt64LittleEndian(b[56..]),
            BinaryPrimitives.ReadInt64LittleEndian(b[64..]), new ContentDigest(b.Slice(72, 32)));
        if (e.PathLength is <= 0 or > 4096 || e.PathOffset < h.EntryCount * PackageHeader.EntrySize ||
            !Range(e.PathOffset, e.PathLength, h.DirectoryLength) || e.Length < 0 || e.StoredLength < 0 ||
            e.MetadataLength is < 0 or > PackageHeader.MaxMetadataBytes ||
            (e.HasMetadata ? e.MetadataLength < 8 || !Range(e.MetadataOffset, e.MetadataLength, h.MetadataLength) : e.MetadataLength != 0 || e.MetadataOffset != 0))
            throw new InvalidDataException("Invalid asset entry range.");
        if (e.Deleted)
        {
            if (e.HasMetadata || e.Blocked || e.Length != 0 || e.DataOffset != 0 || e.StoredLength != 0 ||
                e.MetadataOffset != 0 || e.MetadataLength != 0 || e.FirstBlock != 0 || e.BlockCount != 0 || !e.ContentHash.IsZero)
                throw new InvalidDataException("Invalid deletion entry.");
        }
        else if (e.MetadataOnly)
        {
            if (!e.HasMetadata || e.Length != 0 || e.DataOffset != 0 || e.StoredLength != 0 || e.Blocked || e.BlockCount != 0 || e.FirstBlock != 0 || !e.ContentHash.IsZero)
                throw new InvalidDataException("Invalid metadata-only entry.");
        }
        else if (e.Blocked)
        {
            if (e.DataOffset != 0 || e.BlockCount != PackageHeader.Pages(e.Length, h.BlockSize) ||
                !Range(e.FirstBlock, e.BlockCount, h.BlocksLength / PackageHeader.BlockEntrySize))
                throw new InvalidDataException("Invalid block table reference.");
        }
        else if (h.Encrypted || (h.Signed && e.Length != 0) || e.BlockCount != 0 || e.FirstBlock != 0 || e.StoredLength != e.Length ||
            (e.Length == 0 ? e.DataOffset != 0 : e.DataOffset < h.DataOffset || !Range(e.DataOffset, e.Length, h.PayloadEnd)))
            throw new InvalidDataException("Invalid contiguous content range.");
        return e;
    }

    internal static bool Range(long offset, long length, long limit) => offset >= 0 && length >= 0 && offset <= limit && length <= limit - offset;
}

internal readonly record struct BlockRecord(long Offset, int StoredLength, int Length, long Sequence, byte Codec)
{
    internal byte[] Encode()
    {
        byte[] b = new byte[32];
        Encode(b);
        return b;
    }
    internal void Encode(Span<byte> b)
    {
        b = b[..PackageHeader.BlockEntrySize];
        b.Clear();
        BinaryPrimitives.WriteInt64LittleEndian(b, Offset);
        BinaryPrimitives.WriteInt32LittleEndian(b[8..], StoredLength);
        BinaryPrimitives.WriteInt32LittleEndian(b[12..], Length);
        BinaryPrimitives.WriteInt64LittleEndian(b[16..], Sequence);
        b[24] = Codec;
    }
    internal static BlockRecord Decode(ReadOnlySpan<byte> b, long sequence, int expectedLength, PackageHeader h)
    {
        var r = new BlockRecord(BinaryPrimitives.ReadInt64LittleEndian(b), BinaryPrimitives.ReadInt32LittleEndian(b[8..]),
            BinaryPrimitives.ReadInt32LittleEndian(b[12..]), BinaryPrimitives.ReadInt64LittleEndian(b[16..]), b[24]);
        int overhead = h.Encrypted ? 16 : 0;
        if (r.Codec > (byte)CompressionAlgorithm.Zlib || b[25..].IndexOfAnyExcept((byte)0) >= 0 ||
            r.Sequence != sequence || r.Length != expectedLength || expectedLength is <= 0 || expectedLength > h.BlockSize ||
            r.StoredLength <= overhead || r.StoredLength > BlockCompression.GetMaxCompressedLength((CompressionAlgorithm)r.Codec, expectedLength) + overhead ||
            (r.Codec == 0 && r.StoredLength != expectedLength + overhead) ||
            r.Offset < h.DataOffset || !EntryRecord.Range(r.Offset, r.StoredLength, h.PayloadEnd))
            throw new InvalidDataException("Invalid content block.");
        return r;
    }
}
