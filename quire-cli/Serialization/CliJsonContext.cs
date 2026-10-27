using System.Text.Json.Serialization;
using Quire.Cli.Tags;

namespace Quire.Cli.Serialization;

[JsonSerializable(typeof(BuildTag))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(long[]))]
[JsonSerializable(typeof(bool[]))]
internal partial class CliJsonContext : JsonSerializerContext;
