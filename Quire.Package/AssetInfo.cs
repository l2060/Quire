namespace Quire.Package;

public readonly record struct AssetInfo(string Path, long Length, bool HasMetadata);

public sealed record AssetPackageInfo(string FilePath, string TagId, DateTimeOffset BuildTimeUtc, long EntryCount, bool IsEncrypted);
