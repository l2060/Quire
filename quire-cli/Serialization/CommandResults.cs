using Quire.Cli.Tags;

namespace Quire.Cli.Serialization;

internal readonly record struct TagResult(string TagId, string TagName, string? ParentTagId, string Status,
    DateTimeOffset BuildTimeUtc, int ContentChanges, int MetadataChanges, int DeletedChanges,
    int AddedFiles, int UpdatedFiles, int RemovedFiles, string[] Outputs)
{
    internal static TagResult From(BuildTag tag, string status) => new(tag.TagId, tag.Name, tag.BaseTag, status,
        tag.BuildTimeUtc, tag.ContentChanges, tag.MetadataChanges, tag.DeletedChanges,
        tag.AddedFiles, tag.UpdatedFiles, tag.RemovedFiles, tag.Outputs);
}

internal readonly record struct ChangeResult(string Package, string Path, string Kind);
internal readonly record struct ChangesResult(int Added, int Updated, int Deleted);
internal readonly record struct AssetResult(string Path, string Kind, long Length, bool HasMetadata);
internal readonly record struct KeyResult(string Fingerprint, string Name, string? ClientKey);
internal readonly record struct PathResult(string Path);
internal readonly record struct NameResult(string Name);
internal readonly record struct CountResult(long Count);
internal readonly record struct InspectionResult(long Packages, long Assets);
internal readonly record struct ExtractionResult(string Destination, long Assets, long MetadataFiles);
