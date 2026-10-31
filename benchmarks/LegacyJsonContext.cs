using System.Text.Json.Serialization;

namespace Quire.Cli.Tags;

[JsonSerializable(typeof(LegacyBuildTag))]
internal partial class LegacyJsonContext : JsonSerializerContext;
