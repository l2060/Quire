using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using Quire.Cli.Serialization;
using Quire.Package.Internal;

namespace Quire.Cli.Tags;

internal sealed record FileState(string Package, string Path, long Length, string ContentHash, string? MetadataHash, string Encoding);
internal sealed record BuildTag(string TagId, DateTimeOffset BuildTimeUtc, string? BaseTag, string ConfigHash, string[] Outputs, int ContentChanges, int MetadataChanges);

internal static class TagStore
{
    private static readonly CliJsonContext Json = new(new JsonSerializerOptions { WriteIndented = true });
    internal static BuildTag Load(string state, string id)
    {
        id = PackageTag.Format(PackageTag.Parse(id));
        string path = Path.Combine(state, "tags", id, "tag.json");
        if (!File.Exists(path)) throw new FileNotFoundException($"Build Tag '{id}' was not found in .quire/tags.", path);
        var tag = JsonSerializer.Deserialize(File.ReadAllText(path), Json.BuildTag) ?? throw new InvalidDataException("Invalid Tag record.");
        if (tag.TagId != id) throw new InvalidDataException("Tag identifier does not match its directory.");
        if (tag.BaseTag is { } baseline && (!PackageTag.IsValid(baseline) || baseline != baseline.ToLowerInvariant()))
            throw new InvalidDataException("Invalid base Tag identifier.");
        return tag;
    }
    internal static IEnumerable<BuildTag> List(string state)
    {
        string root = Path.Combine(state, "tags"); if (!Directory.Exists(root)) yield break;
        foreach (string path in Directory.EnumerateDirectories(root))
        {
            string name = Path.GetFileName(path);
            if (name.StartsWith('.') && name.EndsWith(".tmp", StringComparison.Ordinal))
                continue;
            if (!PackageTag.IsValid(name))
                throw new InvalidDataException("Invalid build Tag ID. Expected exactly 8 hexadecimal characters.");
            yield return Load(state, name);
        }
    }
    internal static string Resolve(string state, string value)
    {
        if (value.Length is < 1 or > 8 || !value.All(char.IsAsciiHexDigit))
            throw new ArgumentException("-base must contain 1 to 8 hexadecimal characters: a complete Tag ID or a unique prefix.");

        string[] matches = List(state)
            .Where(tag => tag.TagId.StartsWith(value, StringComparison.OrdinalIgnoreCase))
            .Select(tag => tag.TagId)
            .Take(2)
            .ToArray();
        return matches.Length switch
        {
            0 => throw new FileNotFoundException($"No build Tag matches prefix '{value}' in .quire/tags."),
            1 => matches[0],
            _ => throw new ArgumentException($"Tag prefix '{value}' is ambiguous (matches {matches[0]} and {matches[1]}). Specify more characters with -base.")
        };
    }
    internal static string CreateId(IEnumerable<string> existing, Func<uint>? generate = null)
    {
        var used = existing.ToHashSet(StringComparer.OrdinalIgnoreCase);
        Span<byte> random = stackalloc byte[sizeof(uint)];
        while (true)
        {
            uint value;
            if (generate is not null)
                value = generate();
            else
            {
                RandomNumberGenerator.Fill(random);
                value = BinaryPrimitives.ReadUInt32LittleEndian(random);
            }
            string id = PackageTag.Format(value);
            if (!used.Contains(id))
                return id;
        }
    }
    internal static Dictionary<(string Package, string Path), FileState> LoadFiles(string state, string id)
    {
        id = Load(state, id).TagId;
        using var stream = File.OpenRead(Path.Combine(state, "tags", id, "files.idx"));
        using var reader = new BinaryReader(stream, AssetPath.Utf8);
        if (!reader.ReadBytes(8).AsSpan().SequenceEqual("ASTAG001"u8)) throw new InvalidDataException("Invalid Tag index.");
        long count = reader.ReadInt64(); if (count < 0 || count > (stream.Length - 16) / 20) throw new InvalidDataException("Invalid Tag record count.");
        var result = new Dictionary<(string, string), FileState>();
        for (long i = 0; i < count; i++)
        {
            string package = ReadString(reader), path = ReadString(reader); long length = reader.ReadInt64();
            string content = ReadString(reader), meta = ReadString(reader), encoding = ReadString(reader);
            AssetPath.Validate(path);
            if (length < 0 || content.Length != 64 || (meta.Length != 0 && meta.Length != 64)) throw new InvalidDataException("Invalid Tag fingerprint.");
            var f = new FileState(package, path, length, content, meta.Length == 0 ? null : meta, encoding);
            if (!result.TryAdd((package, path), f)) throw new InvalidDataException("Duplicate Tag path.");
        }
        if (stream.Position != stream.Length) throw new InvalidDataException("Trailing Tag index bytes.");
        return result;
    }
    private static string ReadString(BinaryReader reader)
    {
        int length = reader.ReadInt32(); if (length < 0 || length > 16384) throw new InvalidDataException("Invalid Tag string length.");
        byte[] b = reader.ReadBytes(length); if (b.Length != length) throw new EndOfStreamException(); return AssetPath.Utf8.GetString(b);
    }
    private static void WriteString(BinaryWriter writer, string value) { byte[] b = AssetPath.Utf8.GetBytes(value); writer.Write(b.Length); writer.Write(b); }
    internal static void Write(string directory, BuildTag tag, IEnumerable<FileState> files)
    {
        if (!PackageTag.IsValid(tag.TagId) || tag.TagId != tag.TagId.ToLowerInvariant() ||
            (tag.BaseTag is { } baseline && (!PackageTag.IsValid(baseline) || baseline != baseline.ToLowerInvariant())))
            throw new ArgumentException("Build Tag IDs must contain exactly 8 lowercase hexadecimal characters.");
        Directory.CreateDirectory(directory);
        var sorted = files.OrderBy(f => f.Package, StringComparer.Ordinal).ThenBy(f => f.Path, AssetPath.Comparer).ToArray();
        using (var stream = new FileStream(Path.Combine(directory, "files.idx"), FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            using var w = new BinaryWriter(stream, AssetPath.Utf8, true); w.Write("ASTAG001"u8); w.Write((long)sorted.Length);
            foreach (var f in sorted)
            { WriteString(w, f.Package); WriteString(w, f.Path); w.Write(f.Length); WriteString(w, f.ContentHash); WriteString(w, f.MetadataHash ?? ""); WriteString(w, f.Encoding); }
            w.Flush(); stream.Flush(true);
        }
        using var tagFile = new FileStream(Path.Combine(directory, "tag.json"), FileMode.CreateNew, FileAccess.Write, FileShare.None);
        JsonSerializer.Serialize(tagFile, tag, Json.BuildTag); tagFile.Flush(true);
    }
}
