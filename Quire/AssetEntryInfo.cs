namespace Quire;

/// <summary>Identifies a visible file or virtual directory.</summary>
public enum AssetEntryKind
{
    File,
    Directory
}

/// <summary>A visible file or directory, with a complete package-relative path.</summary>
/// <remarks>Directories have Length = 0 and HasMetadata = false. Paths have no trailing slash.
/// A file and a directory can share a path; use Kind to distinguish them.
/// File queries, file enumeration and content readers return Kind = File.</remarks>
public readonly record struct AssetEntryInfo
{
    // Keep the two eight-byte fields together to avoid padding between Kind and Length.
    public string Path { get; init; }
    public long Length { get; init; }
    public AssetEntryKind Kind { get; init; }
    public bool HasMetadata { get; init; }

    public AssetEntryInfo(string Path, AssetEntryKind Kind, long Length, bool HasMetadata)
    {
        this.Path = Path;
        this.Length = Length;
        this.Kind = Kind;
        this.HasMetadata = HasMetadata;
    }

    public bool IsDirectory => Kind == AssetEntryKind.Directory;
    public bool IsFile => Kind == AssetEntryKind.File;

    public void Deconstruct(out string Path, out AssetEntryKind Kind, out long Length, out bool HasMetadata)
    {
        Path = this.Path;
        Kind = this.Kind;
        Length = this.Length;
        HasMetadata = this.HasMetadata;
    }
}
