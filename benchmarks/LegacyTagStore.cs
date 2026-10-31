using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;

using Quire.Internal;

namespace Quire.Cli.Tags;

// Frozen ASTAG002 reference implementation used only by --build-state.
// Keep its full-index parsing/writing behavior independent of current storage changes.
internal sealed record LegacyBuildTag(string TagId, DateTimeOffset BuildTimeUtc, string? BaseTag,
    string ConfigHash, string[] Outputs, int ContentChanges, int MetadataChanges, int DeletedChanges);


internal static class LegacyTagStore
{
    private const int HeaderSize = 20, RecordSize = 49, MaxPathBytes = 4096, MaxStringBytes = 16384;
    private static readonly LegacyJsonContext Json = new(new JsonSerializerOptions { WriteIndented = true });
    internal static LegacyBuildTag Load(string state, string id)
    {
        id = PackageTag.Format(PackageTag.Parse(id));
        string path = Path.Combine(state, "tags", id, "tag.json");
        if (!File.Exists(path)) throw new FileNotFoundException($"Build Tag '{id}' was not found in .quire/tags.", path);
        var tag = JsonSerializer.Deserialize(File.ReadAllText(path), Json.LegacyBuildTag) ?? throw new InvalidDataException("Invalid Tag record.");
        if (tag.TagId != id) throw new InvalidDataException("Tag identifier does not match its directory.");
        if (tag.BaseTag is { } baseline && (!PackageTag.IsValid(baseline) || baseline != baseline.ToLowerInvariant()))
            throw new InvalidDataException("Invalid base Tag identifier.");
        return tag;
    }
    internal static IEnumerable<LegacyBuildTag> List(string state)
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
        using var stream = new FileStream(Path.Combine(state, "tags", id, "files.idx"), FileMode.Open, FileAccess.Read,
            FileShare.Read, 65536, FileOptions.SequentialScan);
        using var reader = new BinaryReader(stream, AssetPath.Utf8);
        try
        {
            Span<byte> digest = stackalloc byte[32];
            stream.ReadExactly(digest[..8]);
            if (!digest[..8].SequenceEqual("ASTAG002"u8))
                throw new InvalidDataException("Invalid or unsupported Tag index. Create a full package without -base.");
            long count = reader.ReadInt64();
            int tableCount = reader.ReadInt32();
            // Each table entry needs two length prefixes and at least one byte per string.
            // Validate dimensions against physical bytes before allocating arrays or the dictionary.
            if (count < 0 || count > Array.MaxLength || tableCount < 0 || tableCount > count ||
                (count == 0) != (tableCount == 0) ||
                count * RecordSize + (long)tableCount * 10 > stream.Length - HeaderSize)
                throw new InvalidDataException("Invalid Tag index dimensions.");

            var table = new (string Package, string Encoding)[tableCount];
            var unique = new HashSet<(string, string)>();
            byte[] textBuffer = ArrayPool<byte>.Shared.Rent(MaxStringBytes);
            try
            {
                for (int i = 0; i < table.Length; i++)
                {
                    string package = ReadString(reader, textBuffer, 80, count * RecordSize);
                    string encoding = ReadString(reader, textBuffer, MaxStringBytes, count * RecordSize);
                    if (!ValidPackage(package)) throw new InvalidDataException("Invalid Tag package name.");
                    if (!unique.Add((package, encoding))) throw new InvalidDataException("Duplicate Tag encoding table entry.");
                    table[i] = (package, encoding);
                }
            }
            finally { ArrayPool<byte>.Shared.Return(textBuffer); }

            var result = new Dictionary<(string, string), FileState>((int)count);
            Span<byte> previousPath = stackalloc byte[MaxPathBytes];
            Span<byte> currentPath = stackalloc byte[MaxPathBytes];
            int previousLength = 0;
            string? previousPackage = null;
            for (long i = 0; i < count; i++)
            {
                int tableId = reader.ReadInt32();
                int prefixLength = reader.ReadUInt16(), suffixLength = reader.ReadUInt16();
                long length = reader.ReadInt64();
                byte flags = reader.ReadByte();
                int pathLength = prefixLength + suffixLength;
                if ((uint)tableId >= (uint)table.Length || length < 0 || (flags & ~1) != 0 ||
                    prefixLength > previousLength || pathLength is < 1 or > MaxPathBytes)
                    throw new InvalidDataException("Invalid Tag record.");
                var (package, encoding) = table[tableId];
                int packageOrder = previousPackage is null ? 1 : string.CompareOrdinal(package, previousPackage);
                if (packageOrder < 0 || (packageOrder != 0 && prefixLength != 0))
                    throw new InvalidDataException("Invalid Tag package order or path prefix.");

                stream.ReadExactly(digest);
                var content = new ContentDigest(digest);
                ContentDigest? metadata = null;
                if ((flags & 1) != 0)
                {
                    stream.ReadExactly(digest);
                    metadata = new ContentDigest(digest);
                }
                previousPath[..prefixLength].CopyTo(currentPath);
                stream.ReadExactly(currentPath.Slice(prefixLength, suffixLength));
                var pathBytes = currentPath[..pathLength];
                if (packageOrder == 0 && previousPath[..previousLength].SequenceCompareTo(pathBytes) >= 0)
                    throw new InvalidDataException("Tag paths must be unique and sorted within each package.");
                string path = AssetPath.Utf8.GetString(pathBytes);
                AssetPath.Validate(path);
                var file = new FileState(package, path, length, content, metadata, encoding);
                if (!result.TryAdd((package, path), file)) throw new InvalidDataException("Duplicate Tag path.");

                Span<byte> swap = previousPath;
                previousPath = currentPath;
                currentPath = swap;
                previousLength = pathLength;
                previousPackage = package;
            }
            if (stream.Position != stream.Length) throw new InvalidDataException("Trailing Tag index bytes.");
            return result;
        }
        catch (Exception e) when (e is EndOfStreamException or ArgumentException or OverflowException)
        { throw new InvalidDataException("Invalid or truncated Tag index.", e); }
    }
    private static string ReadString(BinaryReader reader, byte[] buffer, int maximum, long reservedBytes)
    {
        int length = reader.ReadInt32();
        if (length <= 0 || length > maximum || length > reader.BaseStream.Length - reader.BaseStream.Position - reservedBytes)
            throw new InvalidDataException("Invalid Tag string length.");
        reader.BaseStream.ReadExactly(buffer.AsSpan(0, length));
        return AssetPath.Utf8.GetString(buffer.AsSpan(0, length));
    }
    private static void WriteString(BinaryWriter writer, string value, byte[] buffer)
    {
        int length = AssetPath.Utf8.GetBytes(value.AsSpan(), buffer);
        writer.Write(length);
        writer.Write(buffer.AsSpan(0, length));
    }
    private static bool ValidPackage(string? package) => package is { Length: > 0 and <= 80 } &&
        !package.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_'));

    internal static void Write(string directory, LegacyBuildTag tag, IEnumerable<FileState> files)
    {
        if (!PackageTag.IsValid(tag.TagId) || tag.TagId != tag.TagId.ToLowerInvariant() ||
            (tag.BaseTag is { } baseline && (!PackageTag.IsValid(baseline) || baseline != baseline.ToLowerInvariant())))
            throw new ArgumentException("Build Tag IDs must contain exactly 8 lowercase hexadecimal characters.");
        var sorted = files.ToArray();
        foreach (var file in sorted)
        {
            if (file is null || !ValidPackage(file.Package) || file.Length < 0 ||
                string.IsNullOrEmpty(file.Encoding) || AssetPath.Utf8.GetByteCount(file.Encoding) > MaxStringBytes)
                throw new ArgumentException("Invalid Tag file record.", nameof(files));
            AssetPath.Validate(file.Path);
        }
        Array.Sort(sorted, static (left, right) =>
        {
            int order = string.CompareOrdinal(left.Package, right.Package);
            return order != 0 ? order : AssetPath.Comparer.Compare(left.Path, right.Path);
        });
        var table = new List<(string Package, string Encoding)>();
        var tableIds = new Dictionary<(string, string), int>();
        for (int i = 0; i < sorted.Length; i++)
        {
            var file = sorted[i];
            if (i != 0 && file.Package == sorted[i - 1].Package && file.Path == sorted[i - 1].Path)
                throw new ArgumentException("Duplicate Tag path.", nameof(files));
            var key = (file.Package, file.Encoding);
            if (tableIds.TryAdd(key, table.Count)) table.Add(key);
        }

        Directory.CreateDirectory(directory);
        using (var stream = new FileStream(Path.Combine(directory, "files.idx"), FileMode.CreateNew, FileAccess.Write,
            FileShare.None, 65536, FileOptions.SequentialScan))
        {
            using var w = new BinaryWriter(stream, AssetPath.Utf8, true);
            w.Write("ASTAG002"u8); w.Write((long)sorted.Length); w.Write(table.Count);
            byte[] textBuffer = ArrayPool<byte>.Shared.Rent(MaxStringBytes);
            try
            {
                foreach (var (package, encoding) in table)
                { WriteString(w, package, textBuffer); WriteString(w, encoding, textBuffer); }
            }
            finally { ArrayPool<byte>.Shared.Return(textBuffer); }

            Span<byte> digest = stackalloc byte[32];
            Span<byte> previousPath = stackalloc byte[MaxPathBytes];
            Span<byte> currentPath = stackalloc byte[MaxPathBytes];
            int previousLength = 0;
            string? previousPackage = null;
            foreach (var file in sorted)
            {
                int pathLength = AssetPath.Utf8.GetBytes(file.Path.AsSpan(), currentPath);
                int prefixLength = 0;
                if (file.Package == previousPackage)
                    while (prefixLength < Math.Min(previousLength, pathLength) && previousPath[prefixLength] == currentPath[prefixLength]) prefixLength++;
                int suffixLength = pathLength - prefixLength;
                w.Write(tableIds[(file.Package, file.Encoding)]);
                w.Write((ushort)prefixLength); w.Write((ushort)suffixLength);
                w.Write(file.Length); w.Write(file.MetadataHash.HasValue ? (byte)1 : (byte)0);
                file.ContentHash.WriteTo(digest); w.Write(digest);
                if (file.MetadataHash is { } metadata) { metadata.WriteTo(digest); w.Write(digest); }
                w.Write(currentPath.Slice(prefixLength, suffixLength));

                Span<byte> swap = previousPath;
                previousPath = currentPath;
                currentPath = swap;
                previousLength = pathLength;
                previousPackage = file.Package;
            }
            w.Flush(); stream.Flush(true);
        }
        using var tagFile = new FileStream(Path.Combine(directory, "tag.json"), FileMode.CreateNew, FileAccess.Write, FileShare.None);
        JsonSerializer.Serialize(tagFile, tag, Json.LegacyBuildTag); tagFile.Flush(true);
    }
}
