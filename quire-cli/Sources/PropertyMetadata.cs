using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Quire.Cli.Serialization;
using Quire;
using Quire.Internal;

namespace Quire.Cli.Sources;

internal static class PropertyMetadata
{
    private static readonly CliJsonContext TextJson = new(new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    private sealed record Value(MetadataType Type, object Data, int Count);
    internal static string Format(Metadata metadata)
    {
        var text = new StringBuilder();
        foreach (string key in metadata.Keys)
        {
            if (key != key.Trim() || key.StartsWith('#') || key.Contains('=') || key.Any(char.IsControl))
                throw new InvalidDataException($"Metadata key cannot be represented as property=value: '{key}'.");
            string type, literal;
            switch (metadata.GetValueType(key))
            {
                case MetadataType.String: type = "string"; literal = JsonSerializer.Serialize(metadata.GetString(key), TextJson.String); break;
                case MetadataType.Int64: type = "int64"; literal = metadata.GetInt64(key).ToString(CultureInfo.InvariantCulture); break;
                case MetadataType.Double: type = "double"; literal = FormatDouble(metadata.GetDouble(key)); break;
                case MetadataType.Boolean: type = "bool"; literal = metadata.GetBoolean(key) ? "true" : "false"; break;
                case MetadataType.StringArray: type = "string[]"; literal = JsonSerializer.Serialize(metadata.GetStringArray(key).ToArray(), TextJson.StringArray); break;
                case MetadataType.Int64Array: type = "int64[]"; literal = JsonSerializer.Serialize(metadata.GetInt64Array(key).ToArray(), TextJson.Int64Array); break;
                case MetadataType.DoubleArray:
                    type = "double[]"; literal = "[" + string.Join(',', metadata.GetDoubleArray(key).ToArray().Select(FormatDouble)) + "]"; break;
                case MetadataType.BooleanArray: type = "bool[]"; literal = JsonSerializer.Serialize(metadata.GetBooleanArray(key).ToArray(), TextJson.BooleanArray); break;
                default: throw new InvalidDataException("Unsupported metadata type.");
            }
            // A leading BOM in a property name must not become the sidecar's BOM.
            if (text.Length == 0 && key.StartsWith('\uFEFF')) text.Append("# Metadata\n");
            text.Append(key).Append(':').Append(type).Append('=').Append(literal).Append('\n');
        }
        return text.ToString();
    }
    private static string FormatDouble(double value)
    {
        string text = value.ToString("R", CultureInfo.InvariantCulture);
        return text.Contains('.') || text.Contains('E') ? text : text + ".0";
    }
    internal static byte[] Parse(string text, string source = "metadata")
    {
        var fields = new Dictionary<string, Value>(StringComparer.Ordinal);
        int lineNumber = 0;
        foreach (string raw in text.TrimStart('\uFEFF').Split('\n'))
        {
            lineNumber++; string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            try
            {
                int equal = line.IndexOf('=');
                if (equal <= 0) throw new FormatException("Expected property=value.");
                string key = line[..equal].Trim(), literal = line[(equal + 1)..].Trim();
                string? type = null; int colon = key.LastIndexOf(':');
                if (colon >= 0) { type = key[(colon + 1)..].Trim(); key = key[..colon].Trim(); }
                if (key.Length == 0 || AssetPath.Utf8.GetByteCount(key) > 1024 || key.Any(char.IsControl)) throw new FormatException("Invalid property name.");
                if (!fields.TryAdd(key, ParseValue(literal, type))) throw new FormatException($"Duplicate property '{key}'.");
            }
            catch (Exception e) when (e is FormatException or JsonException or OverflowException or InvalidOperationException)
            { throw new InvalidDataException($"{source}:{lineNumber}: {e.Message}", e); }
        }
        var sorted = fields.OrderBy(p => p.Key, AssetPath.Comparer).ToArray();
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream, AssetPath.Utf8, true);
        writer.Write("AMTA"u8); writer.Write(sorted.Length); stream.SetLength(8L + sorted.Length * 24L); stream.Position = stream.Length;
        var descriptors = new byte[sorted.Length][];
        for (int i = 0; i < sorted.Length; i++)
        {
            byte[] key = AssetPath.Utf8.GetBytes(sorted[i].Key), d = new byte[24];
            BinaryPrimitives.WriteInt32LittleEndian(d, checked((int)stream.Position));
            BinaryPrimitives.WriteInt32LittleEndian(d.AsSpan(4), key.Length);
            writer.Write(key); descriptors[i] = d;
        }
        for (int i = 0; i < sorted.Length; i++)
        {
            while (stream.Position % 8 != 0) writer.Write((byte)0);
            var v = sorted[i].Value; var d = descriptors[i]; long start = stream.Position;
            WriteValue(writer, v);
            if (stream.Length > PackageHeader.MaxMetadataBytes) throw new InvalidDataException($"{source}: encoded metadata exceeds 1 MiB.");
            BinaryPrimitives.WriteInt32LittleEndian(d.AsSpan(8), checked((int)start));
            BinaryPrimitives.WriteInt32LittleEndian(d.AsSpan(12), checked((int)(stream.Position - start)));
            BinaryPrimitives.WriteInt32LittleEndian(d.AsSpan(16), v.Count); d[20] = (byte)v.Type;
        }
        stream.Position = 8; foreach (var d in descriptors) writer.Write(d);
        var result = stream.ToArray(); _ = new Metadata(result); return result;
    }
    private static Value ParseValue(string text, string? type)
    {
        bool array = type?.EndsWith("[]", StringComparison.Ordinal) == true || (type is null && text.StartsWith('['));
        if (array)
        {
            using var doc = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 2 });
            if (doc.RootElement.ValueKind != JsonValueKind.Array) throw new FormatException("Expected an array.");
            var items = doc.RootElement.EnumerateArray().ToArray();
            string element = type is not null ? type[..^2] : InferArray(items);
            return element switch
            {
                "string" => new(MetadataType.StringArray, items.Select(x => x.ValueKind == JsonValueKind.String ? x.GetString()! : throw new FormatException("Expected string array.")).ToArray(), items.Length),
                "int64" => new(MetadataType.Int64Array, items.Select(x => x.TryGetInt64(out long n) ? n : throw new FormatException("Expected int64 array.")).ToArray(), items.Length),
                "double" => new(MetadataType.DoubleArray, items.Select(ReadDouble).ToArray(), items.Length),
                "bool" => new(MetadataType.BooleanArray, items.Select(x => x.ValueKind is JsonValueKind.True or JsonValueKind.False ? x.GetBoolean() : throw new FormatException("Expected bool array.")).ToArray(), items.Length),
                _ => throw new FormatException($"Unsupported metadata type '{type}'.")
            };
        }
        if (type is null)
        {
            if (text.StartsWith('"')) type = "string";
            else if (bool.TryParse(text, out _)) type = "bool";
            else if (IsInteger(text)) type = "int64";
            else if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out _)) type = "double";
            else type = "string";
        }
        return type switch
        {
            "string" => new(MetadataType.String, text.StartsWith('"') ? JsonSerializer.Deserialize(text, TextJson.String) ?? throw new FormatException("Null is not supported.") : text, 1),
            "int64" => new(MetadataType.Int64, long.Parse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture), 1),
            "double" => new(MetadataType.Double, Finite(double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture)), 1),
            "bool" => new(MetadataType.Boolean, bool.Parse(text), 1),
            _ => throw new FormatException($"Unsupported metadata type '{type}'.")
        };
    }
    private static bool IsInteger(string text)
    {
        var span = text.AsSpan(); if (span.Length > 0 && span[0] is '+' or '-') span = span[1..];
        return span.Length > 0 && span.IndexOfAnyExceptInRange('0', '9') < 0;
    }
    private static string InferArray(JsonElement[] items)
    {
        if (items.Length == 0) return "string";
        if (items.All(x => x.ValueKind == JsonValueKind.Number))
            return items.All(x => IsInteger(x.GetRawText())) ? "int64" : "double";
        if (items.All(x => x.ValueKind == JsonValueKind.String)) return "string";
        if (items.All(x => x.ValueKind is JsonValueKind.True or JsonValueKind.False)) return "bool";
        throw new FormatException("Arrays must contain one supported scalar type.");
    }
    private static double ReadDouble(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Number) throw new FormatException("Expected double array.");
        double result = Finite(value.GetDouble());
        if (IsInteger(value.GetRawText()))
        {
            long integer = value.GetInt64();
            if ((System.Numerics.BigInteger)result != integer) throw new FormatException("Integer cannot be converted to double without loss; use int64[].");
        }
        return result;
    }
    private static double Finite(double value) => double.IsFinite(value) ? value : throw new FormatException("NaN and infinity are not supported.");
    private static void WriteValue(BinaryWriter w, Value value)
    {
        switch (value.Type)
        {
            case MetadataType.String: w.Write(AssetPath.Utf8.GetBytes((string)value.Data)); break;
            case MetadataType.Int64: w.Write((long)value.Data); break;
            case MetadataType.Double: w.Write((double)value.Data); break;
            case MetadataType.Boolean: w.Write((bool)value.Data); break;
            case MetadataType.Int64Array: foreach (long v in (long[])value.Data) w.Write(v); break;
            case MetadataType.DoubleArray: foreach (double v in (double[])value.Data) w.Write(v); break;
            case MetadataType.BooleanArray: foreach (bool v in (bool[])value.Data) w.Write(v); break;
            case MetadataType.StringArray:
                var strings = ((string[])value.Data).Select(AssetPath.Utf8.GetBytes).ToArray();
                int offset = checked((strings.Length + 1) * 4);
                w.Write(offset); foreach (var s in strings) { offset = checked(offset + s.Length); w.Write(offset); }
                foreach (var s in strings) w.Write(s);
                break;
        }
    }
}
