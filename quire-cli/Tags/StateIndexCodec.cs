using System.Buffers;
using System.Buffers.Binary;
using Quire.Cli.Configuration;
using Quire.Internal;

namespace Quire.Cli.Tags;

// Numeric records are decoded in one buffered read. Only paths are prefix-compressed;
// package and encoding strings are shared by records referring to the same table entry.
internal static class StateIndexCodec
{
    internal const int FileRecordSize = 49, KeyRecordSize = 9, MaxStringBytes = 16384;
    private const int MaxPathBytes = 4096;

    internal static int Compare(string leftPackage, string leftPath, string rightPackage, string rightPath)
    {
        int order = string.CompareOrdinal(leftPackage, rightPackage);
        return order != 0 ? order : AssetPath.Comparer.Compare(leftPath, rightPath);
    }

    internal static void ValidateKey(string package, string path)
    {
        ValidatePackage(package);
        AssetPath.Validate(path);
    }

    internal static FileState[] SortFiles(IEnumerable<FileState> source, CancellationToken token)
    {
        var files = source.ToArray();
        foreach (var file in files)
        {
            token.ThrowIfCancellationRequested();
            if (file is null || file.Length < 0 || string.IsNullOrEmpty(file.Encoding) || AssetPath.Utf8.GetByteCount(file.Encoding) > MaxStringBytes)
                throw new ArgumentException("Invalid file state.", nameof(source));
            ValidateKey(file.Package, file.Path);
        }
        Array.Sort(files, static (a, b) => Compare(a.Package, a.Path, b.Package, b.Path));
        for (int i = 1; i < files.Length; i++)
            if (files[i - 1].Package == files[i].Package && files[i - 1].Path == files[i].Path)
                throw new InvalidDataException("Duplicate file state.");
        return files;
    }

    internal static Dictionary<(string Package, string Encoding), int> CreateFileTable(FileState[] files)
    {
        var table = new Dictionary<(string Package, string Encoding), int>();
        foreach (var file in files) table.TryAdd((file.Package, file.Encoding), table.Count);
        return table;
    }

    internal static void WriteFiles(BinaryWriter writer, FileState[] files,
        Dictionary<(string Package, string Encoding), int> table, CancellationToken token)
    {
        byte[] text = ArrayPool<byte>.Shared.Rent(MaxStringBytes);
        try
        {
            foreach (var pair in table)
            { WriteString(writer, pair.Key.Package, text); WriteString(writer, pair.Key.Encoding, text); }
        }
        finally { ArrayPool<byte>.Shared.Return(text); }
        Span<byte> record = stackalloc byte[FileRecordSize + 32];
        Span<byte> previous = stackalloc byte[MaxPathBytes], current = stackalloc byte[MaxPathBytes];
        int previousLength = 0;
        string? previousPackage = null;
        foreach (var file in files)
        {
            token.ThrowIfCancellationRequested();
            if (file.Package != previousPackage) previousLength = 0;
            int length = AssetPath.Utf8.GetBytes(file.Path.AsSpan(), current);
            int prefix = CommonPrefix(previous[..previousLength], current[..length]);
            BinaryPrimitives.WriteInt32LittleEndian(record, table[(file.Package, file.Encoding)]);
            BinaryPrimitives.WriteUInt16LittleEndian(record[4..], (ushort)prefix);
            BinaryPrimitives.WriteUInt16LittleEndian(record[6..], (ushort)(length - prefix));
            BinaryPrimitives.WriteInt64LittleEndian(record[8..], file.Length);
            record[16] = file.MetadataHash.HasValue ? (byte)1 : (byte)0;
            file.ContentHash.WriteTo(record.Slice(17, 32));
            int size = FileRecordSize;
            if (file.MetadataHash is { } metadata) { metadata.WriteTo(record.Slice(size, 32)); size += 32; }
            writer.Write(record[..size]); writer.Write(current.Slice(prefix, length - prefix));
            Span<byte> swap = previous; previous = current; current = swap;
            previousLength = length; previousPackage = file.Package;
        }
    }

    internal static void ReadFiles(BinaryReader reader, int count, int tableCount,
        Dictionary<(string, string), FileState>? destination, FileState[]? array, CancellationToken token, long? payloadEnd = null)
    {
        var stream = reader.BaseStream;
        long end = payloadEnd ?? stream.Length;
        if (count < 0 || tableCount < 0 || tableCount > count || (count == 0) != (tableCount == 0) ||
            (long)count * FileRecordSize + (long)tableCount * 10 > end - stream.Position)
            throw new InvalidDataException("Invalid file state dimensions.");
        var table = new (string Package, string Encoding)[tableCount];
        var unique = new HashSet<(string, string)>();
        byte[] text = ArrayPool<byte>.Shared.Rent(MaxStringBytes);
        try
        {
            for (int i = 0; i < table.Length; i++)
            {
                string package = ReadString(reader, text, PackageName.MaxUtf8Bytes), encoding = ReadString(reader, text, MaxStringBytes);
                ValidatePackage(package);
                if (!unique.Add((package, encoding))) throw new InvalidDataException("Duplicate file encoding table entry.");
                table[i] = (package, encoding);
            }
        }
        finally { ArrayPool<byte>.Shared.Return(text); }
        if ((long)count * FileRecordSize > end - stream.Position) throw new InvalidDataException("Truncated file state records.");
        Span<byte> record = stackalloc byte[FileRecordSize + 32];
        Span<byte> previous = stackalloc byte[MaxPathBytes], current = stackalloc byte[MaxPathBytes];
        int previousLength = 0;
        string? previousPackage = null;
        for (int i = 0; i < count; i++)
        {
            token.ThrowIfCancellationRequested();
            stream.ReadExactly(record[..FileRecordSize]);
            int tableId = BinaryPrimitives.ReadInt32LittleEndian(record);
            int prefix = BinaryPrimitives.ReadUInt16LittleEndian(record[4..]), suffix = BinaryPrimitives.ReadUInt16LittleEndian(record[6..]);
            long length = BinaryPrimitives.ReadInt64LittleEndian(record[8..]);
            if ((uint)tableId >= table.Length || length < 0 || record[16] > 1) throw new InvalidDataException("Invalid file state record.");
            var (package, encoding) = table[tableId];
            var content = new ContentDigest(record.Slice(17, 32));
            ContentDigest? metadata = null;
            if (record[16] != 0) { stream.ReadExactly(record[..32]); metadata = new ContentDigest(record[..32]); }
            int pathLength = ReadPath(stream, end, previous, current, previousLength, previousPackage, package, prefix, suffix);
            string path = AssetPath.Utf8.GetString(current[..pathLength]);
            AssetPath.Validate(path);
            var file = new FileState(package, path, length, content, metadata, encoding);
            if (destination is not null) destination.Add((package, path), file);
            else array![i] = file;
            Span<byte> swap = previous; previous = current; current = swap;
            previousLength = pathLength; previousPackage = package;
        }
    }

    internal static List<string> CreateKeyTable(FileChange[]? changes, FileKey[]? removed)
    {
        int count = changes?.Length ?? removed!.Length;
        var packages = new List<string>();
        string? previous = null;
        for (int i = 0; i < count; i++)
        {
            string package = changes is not null ? changes[i].Package : removed![i].Package;
            if (package != previous) { packages.Add(package); previous = package; }
        }
        return packages;
    }

    internal static void WriteKeys(BinaryWriter writer, FileChange[]? changes, FileKey[]? removed, List<string> packages, CancellationToken token)
    {
        byte[] text = ArrayPool<byte>.Shared.Rent(PackageName.MaxUtf8Bytes);
        try { foreach (string package in packages) WriteString(writer, package, text); }
        finally { ArrayPool<byte>.Shared.Return(text); }
        Span<byte> record = stackalloc byte[KeyRecordSize];
        Span<byte> previous = stackalloc byte[MaxPathBytes], current = stackalloc byte[MaxPathBytes];
        int count = changes?.Length ?? removed!.Length, previousLength = 0, packageId = -1;
        string? previousPackage = null;
        for (int i = 0; i < count; i++)
        {
            token.ThrowIfCancellationRequested();
            string package = changes is not null ? changes[i].Package : removed![i].Package;
            string path = changes is not null ? changes[i].Path : removed![i].Path;
            if (package != previousPackage) { previousLength = 0; packageId++; }
            int length = AssetPath.Utf8.GetBytes(path.AsSpan(), current);
            int prefix = CommonPrefix(previous[..previousLength], current[..length]);
            BinaryPrimitives.WriteInt32LittleEndian(record, packageId);
            BinaryPrimitives.WriteUInt16LittleEndian(record[4..], (ushort)prefix);
            BinaryPrimitives.WriteUInt16LittleEndian(record[6..], (ushort)(length - prefix));
            record[8] = (byte)(changes is not null ? changes[i].Kind : ChangeKind.Deleted);
            writer.Write(record); writer.Write(current.Slice(prefix, length - prefix));
            Span<byte> swap = previous; previous = current; current = swap;
            previousLength = length; previousPackage = package;
        }
    }

    internal static void ReadKeys(BinaryReader reader, int count, int packageCount, FileChange[]? changes, FileKey[]? removed, CancellationToken token, long? payloadEnd = null)
    {
        var stream = reader.BaseStream;
        long end = payloadEnd ?? stream.Length;
        if (packageCount < 0 || packageCount > count || (count == 0) != (packageCount == 0) ||
            (long)packageCount * 5 + (long)count * KeyRecordSize > end - stream.Position)
            throw new InvalidDataException("Invalid change dimensions.");
        var packages = new string[packageCount];
        byte[] text = ArrayPool<byte>.Shared.Rent(PackageName.MaxUtf8Bytes);
        try
        {
            for (int i = 0; i < packages.Length; i++)
            {
                string package = ReadString(reader, text, PackageName.MaxUtf8Bytes);
                ValidatePackage(package);
                if (i != 0 && string.CompareOrdinal(packages[i - 1], package) >= 0) throw new InvalidDataException("Package table must be unique and sorted.");
                packages[i] = package;
            }
        }
        finally { ArrayPool<byte>.Shared.Return(text); }
        if ((long)count * KeyRecordSize > end - stream.Position) throw new InvalidDataException("Truncated change records.");
        Span<byte> record = stackalloc byte[KeyRecordSize];
        Span<byte> previous = stackalloc byte[MaxPathBytes], current = stackalloc byte[MaxPathBytes];
        int previousLength = 0;
        string? previousPackage = null;
        for (int i = 0; i < count; i++)
        {
            token.ThrowIfCancellationRequested();
            stream.ReadExactly(record);
            int packageId = BinaryPrimitives.ReadInt32LittleEndian(record);
            int prefix = BinaryPrimitives.ReadUInt16LittleEndian(record[4..]), suffix = BinaryPrimitives.ReadUInt16LittleEndian(record[6..]);
            var kind = (ChangeKind)record[8];
            if ((uint)packageId >= packages.Length || record[8] is < 1 or > 3 || (removed is not null && kind != ChangeKind.Deleted))
                throw new InvalidDataException("Invalid change record.");
            string package = packages[packageId];
            int length = ReadPath(stream, end, previous, current, previousLength, previousPackage, package, prefix, suffix);
            string path = AssetPath.Utf8.GetString(current[..length]);
            AssetPath.Validate(path);
            if (changes is not null) changes[i] = new(package, path, kind); else removed![i] = new(package, path);
            Span<byte> swap = previous; previous = current; current = swap;
            previousLength = length; previousPackage = package;
        }
    }

    private static int ReadPath(Stream stream, long payloadEnd, ReadOnlySpan<byte> previous, Span<byte> current, int previousLength,
        string? previousPackage, string package, int prefix, int suffix)
    {
        int packageOrder = previousPackage is null ? 1 : string.CompareOrdinal(package, previousPackage);
        int length = prefix + suffix;
        if (packageOrder < 0 || prefix > (packageOrder == 0 ? previousLength : 0) || length is < 1 or > MaxPathBytes ||
            suffix > payloadEnd - stream.Position) throw new InvalidDataException("Invalid path prefix or length.");
        previous[..prefix].CopyTo(current);
        stream.ReadExactly(current.Slice(prefix, suffix));
        if (packageOrder == 0 && previous[..previousLength].SequenceCompareTo(current[..length]) >= 0)
            throw new InvalidDataException("Paths must be unique and sorted within each package.");
        return length;
    }

    private static int CommonPrefix(ReadOnlySpan<byte> previous, ReadOnlySpan<byte> current)
    {
        int limit = Math.Min(previous.Length, current.Length), prefix = 0;
        while (prefix < limit && previous[prefix] == current[prefix]) prefix++;
        return prefix;
    }

    private static void ValidatePackage(string package)
    {
        try { PackageName.Validate(package); }
        catch (ArgumentException error) { throw new InvalidDataException("Invalid state package name.", error); }
    }

    internal static string ReadString(BinaryReader reader, byte[] buffer, int maximum)
    {
        int length = reader.ReadInt32();
        if (length <= 0 || length > maximum || length > reader.BaseStream.Length - reader.BaseStream.Position)
            throw new InvalidDataException("Invalid state string length.");
        reader.BaseStream.ReadExactly(buffer.AsSpan(0, length));
        return AssetPath.Utf8.GetString(buffer.AsSpan(0, length));
    }

    internal static void WriteString(BinaryWriter writer, string value, byte[] buffer)
    {
        int count = AssetPath.Utf8.GetBytes(value.AsSpan(), buffer);
        writer.Write(count); writer.Write(buffer.AsSpan(0, count));
    }
}
