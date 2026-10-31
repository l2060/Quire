using System;

namespace Quire;

public sealed record AssetPackageInfo(string FilePath, string TagId, DateTimeOffset BuildTimeUtc, long EntryCount, bool IsEncrypted)
{
    /// <summary>Whether the package authenticates its contents with a publisher signature.</summary>
    public bool IsSigned { get; init; }
    /// <summary>Stable package identity, independent of its current file name.</summary>
    public Guid PackageUid { get; init; }
    /// <summary>Identity shared by all packages produced in the same build.</summary>
    public Guid TagUid { get; init; }
    /// <summary>The user-supplied Tag name, or its generated name.</summary>
    public string TagName { get; init; } = TagId;
}
