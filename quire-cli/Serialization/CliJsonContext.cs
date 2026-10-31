using System.Text.Json.Serialization;

namespace Quire.Cli.Serialization;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(long[]))]
[JsonSerializable(typeof(bool[]))]
[JsonSerializable(typeof(TagResult))]
[JsonSerializable(typeof(ChangeResult))]
[JsonSerializable(typeof(ChangesResult))]
[JsonSerializable(typeof(AssetResult))]
[JsonSerializable(typeof(KeyResult))]
[JsonSerializable(typeof(PathResult))]
[JsonSerializable(typeof(NameResult))]
[JsonSerializable(typeof(CountResult))]
[JsonSerializable(typeof(InspectionResult))]
[JsonSerializable(typeof(ExtractionResult))]
[JsonSerializable(typeof(AssetPackageInfo))]
internal partial class CliJsonContext : JsonSerializerContext;
