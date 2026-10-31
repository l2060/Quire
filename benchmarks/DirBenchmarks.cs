using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Quire;
using Quire.Cli.Build;
using Quire.Cli.Configuration;
using Quire.Cli.Tags;

internal static class DirBenchmarks
{
    private static long _sink;

    internal static async Task RunAsync(string resultPath)
    {
        resultPath = Path.GetFullPath(resultPath);
        string root = Path.Combine(Path.GetTempPath(), "quire-dir-benchmark-" + Guid.NewGuid().ToString("N"));
        string input = Path.Combine(root, "input");
        var results = new List<Measurement>();
        string[] apis = ["TwoEnumerationsToArray", "DirArray", "DirArrayAsync", "TwoEnumerations", "EnumerateEntries", "EnumerateEntriesAsync"];
        try
        {
            for (int i = 0; i < 256; i++)
                Put(input, $"files/f{i:D3}.bin", i);
            for (int i = 0; i < 128; i++)
                Put(input, $"directories/d{i:D3}/file.bin", i);
            for (int i = 0; i < 8; i++)
            {
                Put(input, $"deep/direct{i}.bin", i);
                for (int j = 0; j < 64; j++)
                    Put(input, $"deep/d{i}/nested/child/f{j:D2}.bin", j);
            }

            File.WriteAllText(Path.Combine(input, "files/f000.bin.meta"), "revision=1\n");
            string yaml = Path.Combine(root, "quire.project.yml");
            File.WriteAllText(yaml, "version: 1\nproject: benchmark\noutput: ./dist/{tag}\ncompression: none\npackages:\n  - name: data\n    sources:\n      - directory: ./input\n        prefix: data\n");
            var config = ProjectConfig.Load(yaml);
            var buildOptions = new BuildOptions { Jobs = 4, IoConcurrency = 4 };
            Console.WriteLine("Building 904 small files and an overlay package...");
            var baseline = await ProjectBuilder.BuildAsync(config, null, buildOptions, default);
            TagStore.Commit(config.StateDirectory, baseline.TagId);
            for (int i = 1; i <= 8; i++)
                Put(input, $"files/f{i:D3}.bin", -i);
            File.WriteAllText(Path.Combine(input, "files/f000.bin.meta"), "revision=2\n");
            await ProjectBuilder.BuildAsync(config, null, buildOptions, default);

            foreach (string storage in new[] { "Resident", "PagedWarm", "PagedUncached" })
            {
                foreach (var mode in new[] { AssetIndexMode.Merged, AssetIndexMode.PerPackage })
                {
                    var options = new ReadOptions
                    {
                        IndexMode = mode,
                        ReadMode = AssetReadMode.RandomAccess,
                        MaxResidentIndexBytes = storage == "Resident" ? 64 * 1024 * 1024 : 0,
                        PageCacheBytes = storage == "PagedUncached" ? 0 : 8 * 1024 * 1024
                    };
                    using var bundle = QuireBundle.Open(Path.Combine(root, "dist"), options);
                    int iterations = storage == "PagedUncached" ? 1 : 30;
                    int rounds = storage == "PagedUncached" ? 1 : 3;
                    foreach (string scope in new[] { "data/files", "data/directories", "data/deep" })
                    {
                        long expected = EnumerateSeparately(bundle, scope);
                        foreach (string api in apis)
                        {
                            if (await RunListingAsync(bundle, scope, api) != expected)
                                throw new InvalidDataException($"Directory listing benchmark results disagree: {api}.");
                        }

                        for (int round = 0; round < rounds; round++)
                        {
                            // Rotate the order to reduce a fixed first/last measurement bias.
                            for (int position = 0; position < apis.Length; position++)
                            {
                                string api = apis[(position + round) % apis.Length];
                                long allocated = GC.GetTotalAllocatedBytes(precise: true);
                                long started = Stopwatch.GetTimestamp();
                                for (int iteration = 0; iteration < iterations; iteration++)
                                {
                                    _sink = await RunListingAsync(bundle, scope, api);
                                    if (_sink != expected)
                                        throw new InvalidDataException("Directory listing benchmark changed during measurement.");
                                }
                                double milliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                                allocated = GC.GetTotalAllocatedBytes(precise: true) - allocated;
                                string consumption = api is "TwoEnumerationsToArray" or "DirArray" or "DirArrayAsync" ? "Array" : "Streaming";
                                var measurement = new Measurement(storage, mode.ToString(), scope, consumption, api, round + 1,
                                    iterations, milliseconds / iterations, (double)allocated / iterations);
                                results.Add(measurement);
                                Console.WriteLine($"{storage,-13} {mode,-10} {scope,-17} {api,-24} {measurement.MillisecondsPerListing:F4} ms/listing; {measurement.AllocatedBytesPerListing:F0} B/listing");
                            }
                        }
                    }
                }
            }

            var report = new
            {
                GeneratedUtc = DateTimeOffset.UtcNow,
                Runtime = RuntimeInformation.FrameworkDescription,
                OS = RuntimeInformation.OSDescription,
                Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                LibrarySha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(QuireBundle).Assembly.Location))),
                FileCount = 904,
                PackageCount = 2,
                EntrySizeBytes = System.Runtime.CompilerServices.Unsafe.SizeOf<AssetEntryInfo>(),
                Notes = "Each listing returns direct children. Compare Array and Streaming consumption separately: DirArray/DirArrayAsync obtain and consume native complete arrays; TwoEnumerationsToArray builds a List<AssetEntryInfo> from both existing APIs and calls ToArray; EnumerateEntries/EnumerateEntriesAsync/TwoEnumerations consume streams without materializing. Both two-enumeration baselines omit sorting, favoring the baseline compared with native mixed-order results. Resident and PagedWarm are warmed; PagedUncached disables the application page cache but does not flush the OS cache. Allocation uses process-wide GC.GetTotalAllocatedBytes so asynchronous thread switches are included; background allocations can add noise. Directory indexing/opening and package construction are excluded. Timing is descriptive and has no pass/fail threshold.",
                Results = results
            };
            Directory.CreateDirectory(Path.GetDirectoryName(resultPath)!);
            await File.WriteAllTextAsync(resultPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"Saved {resultPath}");
        }
        finally
        {
            string resolved = Path.GetFullPath(root);
            if (!ProjectConfig.Within(resolved, Path.GetFullPath(Path.GetTempPath())) ||
                !Path.GetFileName(resolved).StartsWith("quire-dir-benchmark-", StringComparison.Ordinal))
                throw new InvalidOperationException("Unsafe benchmark cleanup path.");
            if (Directory.Exists(resolved))
                Directory.Delete(resolved, recursive: true);
        }
    }

    private static void Put(string input, string relative, int value)
    {
        string path = Path.Combine(input, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, BitConverter.GetBytes(value));
    }

    private static long EnumerateSeparately(QuireBundle bundle, string directory)
    {
        long checksum = 0;
        foreach (string path in bundle.EnumerateDirectories(directory, recursive: false))
            checksum += path.Length + 1;
        foreach (AssetEntryInfo file in bundle.EnumerateFiles(directory, recursive: false))
            checksum += file.Path.Length + file.Length + (file.HasMetadata ? 1 : 0);
        return checksum;
    }

    private static ValueTask<long> RunListingAsync(QuireBundle bundle, string directory, string api) => api switch
    {
        "TwoEnumerationsToArray" => new ValueTask<long>(ConsumeArray(MaterializeSeparately(bundle, directory))),
        "DirArray" => new ValueTask<long>(ConsumeArray(bundle.Dir(directory))),
        "DirArrayAsync" => ConsumeDirArrayAsync(bundle, directory),
        "TwoEnumerations" => new ValueTask<long>(EnumerateSeparately(bundle, directory)),
        "EnumerateEntries" => new ValueTask<long>(EnumerateEntries(bundle, directory)),
        "EnumerateEntriesAsync" => EnumerateEntriesAsync(bundle, directory),
        _ => throw new ArgumentOutOfRangeException(nameof(api))
    };

    private static AssetEntryInfo[] MaterializeSeparately(QuireBundle bundle, string directory)
    {
        var entries = new List<AssetEntryInfo>();
        foreach (string path in bundle.EnumerateDirectories(directory, recursive: false))
            entries.Add(new AssetEntryInfo(path, AssetEntryKind.Directory, 0, false));
        foreach (AssetEntryInfo file in bundle.EnumerateFiles(directory, recursive: false))
            entries.Add(file);
        return entries.ToArray();
    }

    private static long ConsumeArray(AssetEntryInfo[] entries)
    {
        long checksum = 0;
        foreach (AssetEntryInfo entry in entries)
            checksum += entry.Path.Length + entry.Length + (entry.IsDirectory || entry.HasMetadata ? 1 : 0);
        return checksum;
    }

    private static async ValueTask<long> ConsumeDirArrayAsync(QuireBundle bundle, string directory)
    {
        AssetEntryInfo[] entries = await bundle.DirAsync(directory);
        return ConsumeArray(entries);
    }

    private static long EnumerateEntries(QuireBundle bundle, string directory)
    {
        long checksum = 0;
        foreach (AssetEntryInfo entry in bundle.EnumerateEntries(directory))
            checksum += entry.Path.Length + entry.Length + (entry.IsDirectory || entry.HasMetadata ? 1 : 0);
        return checksum;
    }

    private static async ValueTask<long> EnumerateEntriesAsync(QuireBundle bundle, string directory)
    {
        long checksum = 0;
        await foreach (AssetEntryInfo entry in bundle.EnumerateEntriesAsync(directory))
            checksum += entry.Path.Length + entry.Length + (entry.IsDirectory || entry.HasMetadata ? 1 : 0);
        return checksum;
    }

    private sealed record Measurement(string Storage, string IndexMode, string Directory, string Consumption, string Api,
        int Round, int Iterations, double MillisecondsPerListing, double AllocatedBytesPerListing);
}
