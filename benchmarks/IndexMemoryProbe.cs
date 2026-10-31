using Quire.Internal;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;

internal static class IndexMemoryProbe
{
    private const int Count = 100_000;
    private const int PathCharacters = 48;
    private const int Repetitions = 3;

    internal static void Run(string resultPath)
    {
        string[] keys = MakeKeys(Count, PathCharacters);
        string[] warmKeys = MakeKeys(1, PathCharacters);
        _ = GC.GetAllocatedBytesForCurrentThread();
        _ = MakeDictionary<int>(warmKeys);
        _ = MakeDictionary<MergedRow>(warmKeys);
        _ = MakeDictionary<ResolvedRow>(warmKeys);

        var measurements = new[]
        {
            Measure("MergedRow array", () => new MergedRow[Count]),
            Measure("DirectoryNode32 array", () => new DirectoryNode32[Count]),
            Measure("DirectoryNode40 array", () => new DirectoryNode40[Count]),
            Measure("EntryRecord array", () => new EntryRecord[Count]),
            Measure("ResolvedRow array", () => new ResolvedRow[Count]),
            Measure("int adjacency IDs", () => new int[Count]),
            Measure("48-character ASCII keys with reference array", () => MakeKeys(Count, PathCharacters)),
            Measure("Dictionary<string,int>, excluding existing keys", () => MakeDictionary<int>(keys)),
            Measure("Dictionary<string,MergedRow>, excluding existing keys", () => MakeDictionary<MergedRow>(keys)),
            Measure("Dictionary<string,ResolvedRow>, excluding existing keys", () => MakeDictionary<ResolvedRow>(keys))
        };
        var result = new
        {
            RecordedAtUtc = DateTimeOffset.UtcNow,
            Runtime = RuntimeInformation.FrameworkDescription,
            Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            Count,
            PathCharacters,
            Repetitions,
            EntryRecordBytes = Unsafe.SizeOf<EntryRecord>(),
            EquivalentResolvedRowBytes = Unsafe.SizeOf<ResolvedRow>(),
            ProposedMergedRowBytes = Unsafe.SizeOf<MergedRow>(),
            ProposedDirectoryNode32Bytes = Unsafe.SizeOf<DirectoryNode32>(),
            ProposedDirectoryNode40Bytes = Unsafe.SizeOf<DirectoryNode40>(),
            PreallocatedDictionaryCapacity = new Dictionary<string, int>(Count).EnsureCapacity(0),
            Measurements = measurements,
            Notes = new[]
            {
                "Minimum managed allocated bytes of three repetitions on the current thread; not retained heap size, process RSS, lookup speed or a production-index implementation.",
                "Dictionaries are preallocated and reuse existing keys. Key-string allocation is reported separately with its reference array.",
                "Every sample path has 48 ASCII characters. Unicode UTF-8 byte lengths and string lengths can differ.",
                "Array measurements include their managed object headers. Dictionary capacities and overhead change with element count and runtime.",
                "Only EntryRecord is an existing runtime index representation. ResolvedRow reproduces the existing private resolved value's field types. MergedRow and directory nodes are hypothetical compact layouts.",
                "Compact references assume existing package indexes remain available. Direct-file adjacency IDs and any optional hash table are additional storage. No full path-string pool is included in compact-row sizes."
            }
        };
        resultPath = Path.GetFullPath(resultPath);
        Directory.CreateDirectory(Path.GetDirectoryName(resultPath)!);
        File.WriteAllText(resultPath, JsonSerializer.Serialize(result, new JsonSerializerOptions
        {
            WriteIndented = true
        }));
        Console.WriteLine($"Index representation allocation probe saved to {resultPath}");
    }

    private static string[] MakeKeys(int count, int characters)
    {
        var result = new string[count];
        for (int index = 0; index < count; index++)
        {
            result[index] = string.Create(characters, index, static (span, value) =>
            {
                span.Fill('a');
                value.TryFormat(span, out _, "D10");
            });
        }
        return result;
    }

    private static Dictionary<string, T> MakeDictionary<T>(string[] keys)
    {
        var result = new Dictionary<string, T>(keys.Length, StringComparer.Ordinal);
        foreach (string key in keys)
        {
            result.Add(key, default!);
        }
        return result;
    }

    private static Measurement Measure<T>(string name, Func<T> create)
    {
        long allocated = long.MaxValue;
        for (int repeat = 0; repeat < Repetitions; repeat++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            T value = create();
            allocated = Math.Min(allocated, GC.GetAllocatedBytesForCurrentThread() - before);
            GC.KeepAlive(value);
        }
        return new Measurement(name, allocated, allocated / (double)Count);
    }

    private readonly record struct Measurement(string Name, long AllocatedBytes, double BytesPerItem);

    // Long physical entry IDs preserve access to very large package indexes.
    private readonly record struct MergedRow(long ContentEntry, long MetadataEntry, int ContentPackage, int MetadataPackage);

    private readonly record struct ResolvedRow(PackageReader ContentPackage, EntryRecord Content, PackageReader MetadataPackage, EntryRecord Metadata);

    // Both layouts refer to an existing UTF-8 path prefix instead of retaining a string.
    private readonly record struct DirectoryNode32(long SourceEntry, int SourcePackage, int PrefixBytes,
        int Parent, int FirstChild, int NextSibling, int FirstFile);

    private readonly record struct DirectoryNode40(int SourceFile, int PrefixBytes, int Parent, int FirstChild,
        int ChildCount, int FileEdgeStart, int FileEdgeCount, int SubtreeStart, int SubtreeCount, int Flags);
}
