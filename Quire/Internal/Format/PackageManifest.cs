using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;

namespace Quire.Internal;

internal readonly record struct PackageManifestEntry(Guid PackageUid, string FileName);

// A Tag's manifest describes only the packages emitted by that build. Filenames are diagnostic
// hints; identity and completeness checks always use the package UIDs.
internal sealed class PackageManifest
{
    internal const int MaxEntries = 65536, MaxFileNameBytes = 255;
    internal const int MinEncodedLength = 4 + 16 + 2 + 1;
    internal const int MaxEncodedLength = 4 + MaxEntries * (16 + 2 + MaxFileNameBytes);
    internal PackageManifestEntry[] Entries { get; }
    internal ReadOnlyMemory<byte> Encoded { get; }
    internal ContentDigest Hash { get; }
    private readonly HashSet<Guid> _uids;

    private PackageManifest(PackageManifestEntry[] entries, ReadOnlyMemory<byte> encoded, ContentDigest hash, HashSet<Guid> uids)
    {
        Entries = entries;
        Encoded = encoded;
        Hash = hash;
        _uids = uids;
    }

    internal bool Contains(Guid packageUid) => _uids.Contains(packageUid);

    internal static PackageManifest Create(PackageManifestEntry[] entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Length is < 1 or > MaxEntries)
            throw new ArgumentException("A package manifest must contain 1-65536 packages.", nameof(entries));
        var owned = (PackageManifestEntry[])entries.Clone();
        var unique = new HashSet<Guid>(owned.Length);
        int length = sizeof(int);
        foreach (var entry in owned)
        {
            if (entry.PackageUid == Guid.Empty || !unique.Add(entry.PackageUid))
                throw new ArgumentException("Package manifest UIDs must be nonempty and unique.", nameof(entries));
            length = checked(length + 18 + ValidateFileName(entry.FileName));
        }
        byte[] bytes = new byte[length];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, owned.Length);
        int offset = sizeof(int);
        foreach (var entry in owned)
        {
            entry.PackageUid.TryWriteBytes(bytes.AsSpan(offset, 16));
            int count = AssetPath.Utf8.GetBytes(entry.FileName.AsSpan(), bytes.AsSpan(offset + 18));
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset + 16), (ushort)count);
            offset += 18 + count;
        }
        Span<byte> digest = stackalloc byte[32];
        SHA256.HashData(bytes, digest);
        return new(owned, bytes, new ContentDigest(digest), unique);
    }

    internal static PackageManifest Decode(ReadOnlyMemory<byte> encoded, Guid ownPackageUid, ContentDigest expectedHash)
    {
        if (encoded.Length is < MinEncodedLength or > MaxEncodedLength || ownPackageUid == Guid.Empty)
            throw new InvalidDataException("Invalid package manifest dimensions or identity.");
        ReadOnlySpan<byte> bytes = encoded.Span;
        Span<byte> digest = stackalloc byte[32];
        SHA256.HashData(bytes, digest);
        if (!expectedHash.Matches(digest)) throw new InvalidDataException("Package manifest checksum mismatch.");
        int count = BinaryPrimitives.ReadInt32LittleEndian(bytes);
        if (count is < 1 or > MaxEntries || (long)count * 19 > bytes.Length - sizeof(int))
            throw new InvalidDataException("Invalid package manifest entry count.");
        var entries = new PackageManifestEntry[count];
        var unique = new HashSet<Guid>(count);
        int offset = sizeof(int);
        bool foundSelf = false;
        try
        {
            for (int i = 0; i < count; i++)
            {
                if (bytes.Length - offset < 18) throw new InvalidDataException("Truncated package manifest record.");
                var uid = new Guid(bytes.Slice(offset, 16));
                int nameLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(offset + 16, 2));
                offset += 18;
                if (uid == Guid.Empty || !unique.Add(uid) || nameLength is < 1 or > MaxFileNameBytes || nameLength > bytes.Length - offset)
                    throw new InvalidDataException("Invalid package manifest record.");
                string name = AssetPath.Utf8.GetString(bytes.Slice(offset, nameLength));
                ValidateFileName(name);
                entries[i] = new(uid, name);
                foundSelf |= uid == ownPackageUid;
                offset += nameLength;
            }
        }
        catch (ArgumentException error) { throw new InvalidDataException("Invalid package manifest filename.", error); }
        if (offset != bytes.Length) throw new InvalidDataException("Trailing package manifest bytes.");
        if (!foundSelf) throw new InvalidDataException("The package manifest does not contain this package UID.");
        return new(entries, encoded, expectedHash, unique);
    }

    private static int ValidateFileName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (name.Contains('/')) throw new ArgumentException("Manifest filenames cannot contain directory paths.", nameof(name));
        AssetPath.Validate(name);
        int length = AssetPath.Utf8.GetByteCount(name);
        if (length > MaxFileNameBytes) throw new ArgumentException("Manifest filenames exceed 255 UTF-8 bytes.", nameof(name));
        return length;
    }
}
