using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Quire.Internal;

// Opening-only state: parse one manifest per Tag, but authenticate the bytes in every package.
// No resource paths or content blocks participate in package completeness checks.
internal sealed class PackageManifestCatalog(bool verifyCompleteness)
{
    private sealed record TagManifest(PackageHeader Header, AssetTagInfo Info, PackageManifest Manifest);
    private readonly Dictionary<Guid, TagManifest> _tags = new();
    private readonly Dictionary<Guid, AssetPackageInfo>? _packages = verifyCompleteness ? new() : null;
    internal AssetTagInfo? LatestTag { get; private set; }

    internal async ValueTask AddAsync(PackageReader package, bool asynchronous, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var header = package.Header;
        if (_packages is not null && !_packages.TryAdd(header.PackageUid, package.Info))
            throw new InvalidDataException($"Duplicate PackageUid {header.PackageUid}: '{_packages[header.PackageUid].FilePath}' and '{package.Info.FilePath}'.");

        if (_tags.TryGetValue(header.TagUid, out var tag))
        {
            if (tag.Header.TagId != header.TagId || tag.Header.TagName != header.TagName ||
                tag.Header.BuildTicks != header.BuildTicks || tag.Header.ManifestLength != header.ManifestLength ||
                tag.Header.ManifestHash != header.ManifestHash)
                throw new InvalidDataException($"Tag '{header.TagName}' ({header.TagUid}) has inconsistent package metadata in '{package.Info.FilePath}'.");

            await VerifyManifestHashAsync(package, asynchronous, token).ConfigureAwait(false);
            if (!tag.Manifest.Contains(header.PackageUid))
                throw new InvalidDataException($"Package '{package.Info.FilePath}' ({header.PackageUid}) is absent from its own Tag manifest.");
            return;
        }

        byte[] bytes = GC.AllocateUninitializedArray<byte>(header.ManifestLength);
        if (asynchronous)
            await package.File.ReadAsync(bytes, PackageHeader.Size, token).ConfigureAwait(false);
        else
            package.File.Read(bytes, PackageHeader.Size);
        token.ThrowIfCancellationRequested();
        var manifest = PackageManifest.Decode(bytes, header.PackageUid, header.ManifestHash);
        var info = new AssetTagInfo(header.TagUid, header.TagName, package.Info.BuildTimeUtc);
        _tags.Add(header.TagUid, new(header, info, manifest));
        if (LatestTag is null || info.BuildTimeUtc > LatestTag.BuildTimeUtc ||
            (info.BuildTimeUtc == LatestTag.BuildTimeUtc && info.TagUid.CompareTo(LatestTag.TagUid) > 0))
            LatestTag = info;
    }

    internal void ValidateCompleteness(CancellationToken token)
    {
        if (_packages is null)
            return;

        StringBuilder? errors = null;
        foreach (var tag in _tags.Values)
        {
            foreach (var entry in tag.Manifest.Entries)
            {
                token.ThrowIfCancellationRequested();
                if (_packages.TryGetValue(entry.PackageUid, out var actual) && actual.TagUid == tag.Info.TagUid)
                    continue;

                errors ??= new("Incomplete asset package set:");
                errors.AppendLine().Append("Tag '").Append(tag.Info.Name).Append("' (").Append(tag.Info.TagUid)
                    .Append(") is missing package '").Append(entry.FileName).Append("' (PackageUid: ")
                    .Append(entry.PackageUid).Append(").");
                if (actual is not null)
                    errors.Append(" This UID belongs to Tag '").Append(actual.TagName).Append("' in '").Append(actual.FilePath).Append("'.");
            }
        }

        if (errors is not null)
            throw new InvalidDataException(errors.ToString());
    }

    private static async ValueTask VerifyManifestHashAsync(PackageReader package, bool asynchronous, CancellationToken token)
    {
        int length = package.Header.ManifestLength;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(Math.Min(length, PackageHeader.PageSize));
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            for (int offset = 0; offset < length;)
            {
                token.ThrowIfCancellationRequested();
                var chunk = buffer.AsMemory(0, Math.Min(buffer.Length, length - offset));
                if (asynchronous)
                    await package.File.ReadAsync(chunk, PackageHeader.Size + (long)offset, token).ConfigureAwait(false);
                else
                    package.File.Read(chunk.Span, PackageHeader.Size + (long)offset);
                hash.AppendData(chunk.Span);
                offset += chunk.Length;
            }

            Span<byte> digest = stackalloc byte[32];
            hash.GetHashAndReset(digest);
            if (!package.Header.ManifestHash.Matches(digest))
                throw new InvalidDataException($"Invalid package manifest checksum in '{package.Info.FilePath}'.");
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }
}
