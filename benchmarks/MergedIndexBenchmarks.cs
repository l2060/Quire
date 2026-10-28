using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Quire.Cli.Build;
using Quire.Cli.Configuration;
using Quire.Package;

internal static class MergedIndexBenchmarks
{
    private const int FileCount = 8192;
    private const int DirectoryCount = 513;
    private const int FileLength = 128;
    private const int PackageCount = 8;
    private static long _sink;
    internal static async Task RunAsync(string resultPath)
    {
        resultPath = Path.GetFullPath(resultPath);
        string root = Path.Combine(Path.GetTempPath(), "quire-merged-index-" + Guid.NewGuid().ToString("N"));
        string input = Path.Combine(root, "input");
        Directory.CreateDirectory(input);
        var measurements = new List<IndexMeasurement>();
        var openings = new List<IndexOpening>();
        var diagnostics = new List<object>();
        using var process = Process.GetCurrentProcess();
        try
        {
            string[] paths = new string[FileCount];
            byte[] content = new byte[FileLength];
            for (int i = 0; i < FileCount; i++)
            {
                string relative = $"d{i / 32:D3}/" + (i % 32 < 16 ? "" : "child/") + $"f{i:D4}.bin";
                paths[i] = "data/" + relative;
                string path = Path.Combine(input, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                BitConverter.TryWriteBytes(content, i);
                File.WriteAllBytes(path, content);
                File.WriteAllText(path + ".meta", $"id={i}\nrevision=0\n");
            }

            string yaml = Path.Combine(root, "quire.project.yml");
            File.WriteAllText(yaml, "version: 1\noutput: ./dist\ncompression: false\npackages:\n  - name: data\n    sources:\n      - directory: ./input\n        prefix: data\n");
            var config = ProjectConfig.Load(yaml);
            var buildOptions = new BuildOptions
            {
                Jobs = 4,
                IoConcurrency = 4
            };
            Console.WriteLine($"Building {FileCount} files and {PackageCount} package layers...");
            var tag = await ProjectBuilder.BuildAsync(config, null, buildOptions, default);
            for (int layer = 1; layer < PackageCount; layer++)
            {
                for (int j = 0; j < 128; j++)
                {
                    // Half of the overrides repeat across all layers; half are unique to each patch.
                    int contentIndex = j < 64 ? j : layer * 512 + j;
                    BitConverter.TryWriteBytes(content, contentIndex);
                    BitConverter.TryWriteBytes(content.AsSpan(4), layer);
                    File.WriteAllBytes(Path.Combine(input, paths[contentIndex][5..]), content);
                    int metadataIndex = j < 64 ? 128 + j : layer * 512 + 128 + j;
                    File.WriteAllText(Path.Combine(input, paths[metadataIndex][5..]) + ".meta", $"id={metadataIndex}\nrevision={layer}\n");
                }

                tag = await ProjectBuilder.BuildAsync(config, tag.TagId, buildOptions, default);
                if (tag.ContentChanges != 128 || tag.MetadataChanges != 128)
                {
                    throw new InvalidDataException("The index benchmark patch must contain both content and metadata-only overrides.");
                }
            }

            string output = Path.Combine(root, "dist");
            string[] missingPaths = Enumerable.Range(0, 256).Select(i => $"data/d{i:D3}/missing.bin").ToArray();
            string[] directories = Enumerable.Range(0, 256).Select(i => $"data/d{i:D3}").ToArray();
            string[] missingDirectories = Enumerable.Range(0, 256).Select(i => $"data/missing{i:D3}").ToArray();
            foreach (bool resident in new[]
            {
                true,
                false
            }

            )
            {
                // Reverse index order between the resident and paged cases.
                var modes = resident ? new[]
                {
                    AssetIndexMode.PerPackage,
                    AssetIndexMode.Merged
                }

                : new[]
                {
                    AssetIndexMode.Merged,
                    AssetIndexMode.PerPackage
                };
                foreach (var mode in modes)
                {
                    string storage = resident ? "Resident" : "Paged";
                    var options = new ReadOptions
                    {
                        IndexMode = mode,
                        MaxMergedIndexBytes = 64L * 1024 * 1024,
                        MaxResidentIndexBytes = resident ? 64 * 1024 * 1024 : 0,
                        PageCacheBytes = 8 * 1024 * 1024,
                        ReadMode = AssetReadMode.MemoryMapped
                    };
                    using (var warm = PackageSet.Open(output, options))
                    {
                        _sink = warm.Packages.Count;
                    }

                    for (int round = 0; round < 3; round++)
                    {
                        GC.Collect();
                        GC.WaitForPendingFinalizers();
                        GC.Collect();
                        long cpuStart = process.TotalProcessorTime.Ticks;
                        long start = Stopwatch.GetTimestamp();
                        long allocated = GC.GetAllocatedBytesForCurrentThread();
                        using var opened = PackageSet.Open(output, options);
                        allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
                        double milliseconds = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                        double cpuMilliseconds = (process.TotalProcessorTime.Ticks - cpuStart) / (double)TimeSpan.TicksPerMillisecond;
                        openings.Add(new IndexOpening(storage, mode.ToString(), round, milliseconds, cpuMilliseconds, allocated, opened.IndexInfo.MemoryBytes, opened.IndexInfo.FileCount, opened.IndexInfo.DirectoryCount, opened.IndexInfo.IsFallback));
                        Console.WriteLine($"{storage}/{mode} open #{round + 1}: {milliseconds:F3} ms, {allocated:N0} B allocated, {opened.IndexInfo.MemoryBytes:N0} B index");
                    }

                    using var set = PackageSet.Open(output, options);
                    if (set.Packages.Count != PackageCount || set.IndexInfo.IsMerged != (mode == AssetIndexMode.Merged) || set.IndexInfo.IsFallback)
                    {
                        throw new InvalidDataException("Unexpected package count or effective index mode.");
                    }

                    if (mode == AssetIndexMode.Merged && (set.IndexInfo.FileCount != FileCount || set.IndexInfo.DirectoryCount != DirectoryCount))
                    {
                        throw new InvalidDataException("Merged index diagnostics do not match the fixture.");
                    }

                    if (set.ReadMetadata(paths[128]).GetInt64("revision") != PackageCount - 1)
                    {
                        throw new InvalidDataException("Metadata overlay is incorrect.");
                    }

                    using (var latest = set.Read(paths[0]))
                    {
                        if (BitConverter.ToInt32(latest.Data.Span[4..]) != PackageCount - 1)
                        {
                            throw new InvalidDataException("Content overlay is incorrect.");
                        }
                    }

                    diagnostics.Add(new
                    {
                        Storage = storage,
                        RequestedMode = mode.ToString(),
                        EffectiveMode = set.IndexInfo.IsMerged ? "Merged" : "PerPackage",
                        set.IndexInfo.MemoryBytes,
                        set.IndexInfo.FileCount,
                        set.IndexInfo.DirectoryCount,
                        set.IndexInfo.IsFallback
                    });
                    void Measure(string workload, int operations, Func<long> body)
                    {
                        _sink = body();
                        for (int round = 0; round < 3; round++)
                        {
                            GC.Collect();
                            GC.WaitForPendingFinalizers();
                            GC.Collect();
                            long cpuStart = process.TotalProcessorTime.Ticks;
                            long start = Stopwatch.GetTimestamp();
                            long allocated = GC.GetAllocatedBytesForCurrentThread();
                            long checksum = body();
                            allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
                            double milliseconds = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                            double cpuMilliseconds = (process.TotalProcessorTime.Ticks - cpuStart) / (double)TimeSpan.TicksPerMillisecond;
                            _sink = checksum;
                            measurements.Add(new IndexMeasurement(storage, mode.ToString(), workload, round, operations, milliseconds, cpuMilliseconds, allocated, allocated / (double)operations, checksum));
                            Console.WriteLine($"{storage}/{mode} {workload} #{round + 1}: {milliseconds:F3} ms, {allocated / (double)operations:F2} B/op");
                        }
                    }

                    Measure("lookup-hit", 20000, () =>
                    {
                        long sum = 0;
                        for (int i = 0; i < 20000; i++)
                        {
                            sum += set.GetFileInfo(paths[i * 104729 & (FileCount - 1)]).Length;
                        }

                        if (sum != 20000L * FileLength)
                        {
                            throw new InvalidDataException("Lookup result mismatch.");
                        }

                        return sum;
                    });
                    Measure("lookup-miss", 20000, () =>
                    {
                        long count = 0;
                        for (int i = 0; i < 20000; i++)
                        {
                            count += set.FileExists(missingPaths[i & 255]) ? 1 : 0;
                        }

                        if (count != 0)
                        {
                            throw new InvalidDataException("Unexpected lookup hit.");
                        }

                        return count;
                    });
                    Measure("directory-exists-hit", 20000, () =>
                    {
                        long count = 0;
                        for (int i = 0; i < 20000; i++)
                        {
                            count += set.DirectoryExists(directories[i & 255]) ? 1 : 0;
                        }

                        if (count != 20000)
                        {
                            throw new InvalidDataException("Directory lookup result mismatch.");
                        }

                        return count;
                    });
                    Measure("directory-exists-miss", 20000, () =>
                    {
                        long count = 0;
                        for (int i = 0; i < 20000; i++)
                        {
                            count += set.DirectoryExists(missingDirectories[i & 255]) ? 1 : 0;
                        }

                        if (count != 0)
                        {
                            throw new InvalidDataException("Unexpected directory lookup hit.");
                        }

                        return count;
                    });
                    Measure("files-all", 10, () =>
                    {
                        long count = 0;
                        for (int i = 0; i < 10; i++)
                        {
                            foreach (var file in set.EnumerateFiles())
                            {
                                count++;
                            }
                        }

                        if (count != 10L * FileCount)
                        {
                            throw new InvalidDataException("File enumeration result mismatch.");
                        }

                        return count;
                    });
                    Measure("files-directory-recursive", 200, () =>
                    {
                        long count = 0;
                        for (int i = 0; i < 200; i++)
                        {
                            foreach (var file in set.EnumerateFiles("data/d127", recursive: true))
                            {
                                count++;
                            }
                        }

                        if (count != 200 * 32)
                        {
                            throw new InvalidDataException("Recursive file enumeration result mismatch.");
                        }

                        return count;
                    });
                    Measure("files-directory-direct", 200, () =>
                    {
                        long count = 0;
                        for (int i = 0; i < 200; i++)
                        {
                            foreach (var file in set.EnumerateFiles("data/d127", recursive: false))
                            {
                                count++;
                            }
                        }

                        if (count != 200 * 16)
                        {
                            throw new InvalidDataException("Direct file enumeration result mismatch.");
                        }

                        return count;
                    });
                    Measure("directories-all", 20, () =>
                    {
                        long count = 0;
                        for (int i = 0; i < 20; i++)
                        {
                            foreach (string directory in set.EnumerateDirectories("", recursive: true))
                            {
                                count++;
                            }
                        }

                        if (count != 20L * DirectoryCount)
                        {
                            throw new InvalidDataException("Directory enumeration result mismatch.");
                        }

                        return count;
                    });
                    Measure("directories-direct", 100, () =>
                    {
                        long count = 0;
                        for (int i = 0; i < 100; i++)
                        {
                            foreach (string directory in set.EnumerateDirectories("data", recursive: false))
                            {
                                count++;
                            }
                        }

                        if (count != 100 * 256)
                        {
                            throw new InvalidDataException("Direct directory enumeration result mismatch.");
                        }

                        return count;
                    });
                }
            }

            object fallback;
            using (var limited = PackageSet.Open(output, new ReadOptions { IndexMode = AssetIndexMode.Auto, MaxMergedIndexBytes = 0 }))
            {
                if (limited.IndexInfo.IsMerged || !limited.IndexInfo.IsFallback)
                {
                    throw new InvalidDataException("Auto mode did not report its zero-budget fallback.");
                }

                fallback = new
                {
                    RequestedMode = "Auto",
                    MaxMergedIndexBytes = 0,
                    EffectiveMode = limited.IndexInfo.IsMerged ? "Merged" : "PerPackage",
                    limited.IndexInfo.MemoryBytes,
                    limited.IndexInfo.IsFallback
                };
            }

            var report = new
            {
                TimestampUtc = DateTimeOffset.UtcNow,
                Runtime = RuntimeInformation.FrameworkDescription,
                OS = RuntimeInformation.OSDescription,
                ProcessorCount = Environment.ProcessorCount,
                TieredCompilation = Environment.GetEnvironmentVariable("DOTNET_TieredCompilation"),
                Files = FileCount,
                FileBytes = FileLength,
                DirectoriesExcludingRoot = DirectoryCount,
                Packages = PackageCount,
                ContentOverridesPerPatch = 128,
                MetadataOnlyOverridesPerPatch = 128,
                ReadBackend = "MemoryMapped",
                PageCacheBytesPerPackage = 8 * 1024 * 1024,
                MaxMergedIndexBytes = 64L * 1024 * 1024,
                Cache = "Warm OS and library caches; synchronous single-thread queries; each workload warmed before three measured rounds. Resident/Paged changes package directory residency only.",
                Opening = "After one warmup open for each configuration, three separate opens include package validation and index construction, but exclude Dispose. OS cache remains warm.",
                Allocation = "Managed allocation on the calling thread, including enumeration result strings. IndexInfo.MemoryBytes is the reported retained merged-index payload; it is not total process memory or package page-cache memory.",
                Cpu = "Process CPU elapsed per batch; short operations can round to zero at the OS CPU-time resolution.",
                LibrarySha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(PackageSet).Assembly.Location))),
                IndexDiagnostics = diagnostics,
                Openings = openings,
                Measurements = measurements,
                BudgetFallback = fallback,
                Checksum = _sink
            };
            Directory.CreateDirectory(Path.GetDirectoryName(resultPath)!);
            await File.WriteAllTextAsync(resultPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine(resultPath);
        }
        finally
        {
            string resolved = Path.GetFullPath(root);
            if (!ProjectConfig.Within(resolved, Path.GetFullPath(Path.GetTempPath())) || !Path.GetFileName(resolved).StartsWith("quire-merged-index-", StringComparison.Ordinal))
            {
                throw new IOException("Unsafe benchmark cleanup.");
            }

            Directory.Delete(resolved, true);
        }
    }
}

internal sealed record IndexOpening(string Storage, string Mode, int Round, double Milliseconds, double CpuMilliseconds, long AllocatedBytes, long IndexMemoryBytes, int? FileCount, int? DirectoryCount, bool IsFallback);
internal sealed record IndexMeasurement(string Storage, string Mode, string Workload, int Round, int Operations, double Milliseconds, double CpuMilliseconds, long AllocatedBytes, double BytesPerOperation, long Checksum);
