using Quire.Internal;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Quire.Cli.Build;
using Quire.Cli.Configuration;
using Quire.Cli.Tags;
using Quire;

internal static class HotPathBenchmarks
{
    private const int FileCount = 2048;
    private const int FileLength = 4096;
    private static long _sink;
    internal static async Task RunAsync(string resultPath)
    {
        resultPath = Path.GetFullPath(resultPath);
        string root = Path.Combine(Path.GetTempPath(), "quire-hot-paths-" + Guid.NewGuid().ToString("N"));
        string input = Path.Combine(root, "input");
        Directory.CreateDirectory(input);
        using var signing = PublisherKey.Import(RandomNumberGenerator.GetBytes(64));
        var measurements = new List<HotPathMeasurement>();
        var openings = new List<object>();
        try
        {
            string[] paths = new string[FileCount];
            var random = new Random(1729);
            byte[] content = new byte[FileLength];
            for (int i = 0; i < FileCount; i++)
            {
                string relative = $"d{i / 32:D3}/" + (i % 32 < 16 ? "" : "child/") + $"f{i:D4}.bin";
                paths[i] = "data/" + relative;
                string path = Path.Combine(input, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                content.AsSpan().Clear();
                if ((i & 1) == 0)
                    random.NextBytes(content);
                BitConverter.TryWriteBytes(content, i);
                File.WriteAllBytes(path, content);
                File.WriteAllText(path + ".meta", $"id={i}\nrevision=0\n");
            }

            foreach (bool encrypted in new[]
            {
                false,
                true
            }

            )
                foreach (bool compressed in new[]
                {
                    false,
                    true
                }

                )
                {
                    string profile = (compressed ? "lz4" : "raw") + (encrypted ? "-aes" : "");
                    string profileRoot = Path.Combine(root, profile);
                    Directory.CreateDirectory(profileRoot);
                    string yaml = Path.Combine(profileRoot, "quire.project.yml");
                    File.WriteAllText(yaml, $"version: 1\nproject: benchmark\noutput: ./dist/{{tag}}\ncompression: {(compressed ? "lz4" : "none")}\ncompression-policy: adaptive\nsecret-key: {(encrypted ? "benchmark" : "none")}\npackages:\n  - name: data\n    sources:\n      - directory: ../input\n        prefix: data\n");
                    // Reset metadata because the same source tree is reused between profiles.
                    for (int i = 0; i < FileCount; i += 64)
                        File.WriteAllText(Path.Combine(input, paths[i][5..]) + ".meta", $"id={i}\nrevision=0\n");
                    var config = ProjectConfig.Load(yaml);
                    var buildOptions = new BuildOptions
                    {
                        Jobs = 4,
                        IoConcurrency = 4
                    };
                    var baseline = await ProjectBuilder.BuildAsync(config, null, buildOptions, encrypted ? signing : null, default);
                    TagStore.Commit(config.StateDirectory, baseline.TagId);
                    for (int i = 0; i < FileCount; i += 64)
                        File.WriteAllText(Path.Combine(input, paths[i][5..]) + ".meta", $"id={i}\nrevision=1\n");
                    await ProjectBuilder.BuildAsync(config, null, buildOptions, encrypted ? signing : null, default);
                    foreach (var mode in new[]
                    {
                        AssetReadMode.RandomAccess,
                        AssetReadMode.MemoryMapped
                    }

                    )
                    {
                        var options = new ReadOptions
                        {
                            Key = encrypted ? signing.ClientKey : null,
                            ReadMode = mode
                        };
                        long openingStart = Stopwatch.GetTimestamp();
                        long openingAllocation = GC.GetAllocatedBytesForCurrentThread();
                        using var set = QuireBundle.Open(Path.Combine(profileRoot, "dist"), options);
                        openingAllocation = GC.GetAllocatedBytesForCurrentThread() - openingAllocation;
                        double openingMs = Stopwatch.GetElapsedTime(openingStart).TotalMilliseconds;
                        openings.Add(new
                        {
                            Profile = profile,
                            Backend = mode.ToString(),
                            Milliseconds = openingMs,
                            AllocatedBytes = openingAllocation
                        });
                        byte[] buffer = new byte[FileLength];
                        foreach (string path in paths)
                        {
                            using var value = set.Read(path);
                            if (value.Data.Length != FileLength)
                                throw new InvalidDataException("Unexpected file size.");
                        }

                        if (set.ReadMetadata(paths[0]).GetInt64("revision") != 1)
                            throw new InvalidDataException("Metadata overlay was not applied.");
                        void Measure(string workload, int operations, Func<long> body)
                        {
                            _sink = body();
                            for (int round = 0; round < 3; round++)
                            {
                                GC.Collect();
                                GC.WaitForPendingFinalizers();
                                GC.Collect();
                                long start = Stopwatch.GetTimestamp();
                                long allocated = GC.GetAllocatedBytesForCurrentThread();
                                long checksum = body();
                                allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
                                double milliseconds = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                                _sink = checksum;
                                var measurement = new HotPathMeasurement(profile, mode.ToString(), workload, round, operations, milliseconds, allocated, allocated / (double)operations, checksum);
                                measurements.Add(measurement);
                                Console.WriteLine($"{profile}/{mode} {workload} #{round + 1}: {milliseconds:F3} ms; {measurement.BytesPerOperation:F2} B/op");
                            }
                        }

                        Measure("enumerate-all", 20, () =>
                        {
                            long count = 0;
                            for (int n = 0; n < 20; n++)
                                foreach (var file in set.EnumerateFiles())
                                    count++;
                            if (count != 20L * FileCount)
                                throw new InvalidDataException("Enumeration count mismatch.");
                            return count;
                        });
                        Measure("enumerate-directory-recursive", 200, () =>
                        {
                            long count = 0;
                            for (int n = 0; n < 200; n++)
                                foreach (var file in set.EnumerateFiles("data/d031", true))
                                    count++;
                            if (count != 200 * 32)
                                throw new InvalidDataException("Recursive enumeration count mismatch.");
                            return count;
                        });
                        Measure("enumerate-directory-direct", 200, () =>
                        {
                            long count = 0;
                            for (int n = 0; n < 200; n++)
                                foreach (var file in set.EnumerateFiles("data/d031", false))
                                    count++;
                            if (count != 200 * 16)
                                throw new InvalidDataException("Direct enumeration count mismatch.");
                            return count;
                        });
                        Measure("enumerate-directories-all", 100, () =>
                        {
                            long count = 0;
                            for (int n = 0; n < 100; n++)
                                foreach (string directory in set.EnumerateDirectories("", true))
                                    count++;
                            if (count != 100 * 129)
                                throw new InvalidDataException("Directory enumeration count mismatch.");
                            return count;
                        });
                        Measure("enumerate-directories-direct", 200, () =>
                        {
                            long count = 0;
                            for (int n = 0; n < 200; n++)
                                foreach (string directory in set.EnumerateDirectories("data", recursive: false))
                                    count++;
                            if (count != 200 * 64)
                                throw new InvalidDataException("Child directory enumeration count mismatch.");
                            return count;
                        });
                        Measure("lookup", 10000, () =>
                        {
                            long count = 0;
                            for (int n = 0; n < 10000; n++)
                                count += set.FileExists(paths[n % FileCount]) ? set.GetFileInfo(paths[n % FileCount]).Length : 0;
                            return count;
                        });
                        Measure("read-at-4KiB", 5000, () =>
                        {
                            long count = 0;
                            for (int n = 0; n < 5000; n++)
                            {
                                int i = n * 104729 & (FileCount - 1);
                                count += set.ReadAt(paths[i], 0, buffer);
                                if (BitConverter.ToInt32(buffer) != i)
                                    throw new InvalidDataException("Read identity mismatch.");
                            }

                            return count;
                        });
                        Measure("read-full-4KiB-with-metadata", 1000, () =>
                        {
                            long count = 0;
                            for (int n = 0; n < 1000; n++)
                            {
                                int i = n * 104729 & (FileCount - 1);
                                using var value = set.Read(paths[i]);
                                if (value.Metadata.GetInt64("id") != i || BitConverter.ToInt32(value.Data.Span) != i)
                                    throw new InvalidDataException("Full read identity mismatch.");
                                count += value.Data.Length;
                            }

                            return count;
                        });
                        using var reader = set.OpenRead(paths[0]);
                        Measure("reused-reader-range-256B", 10000, () =>
                        {
                            long count = 0;
                            for (int n = 0; n < 10000; n++)
                                count += reader.ReadAt(n % (FileLength - 256), buffer.AsSpan(0, 256));
                            return count;
                        });
                    }
                }

            Directory.CreateDirectory(Path.GetDirectoryName(resultPath)!);
            var report = new
            {
                TimestampUtc = DateTimeOffset.UtcNow,
                Runtime = RuntimeInformation.FrameworkDescription,
                OS = RuntimeInformation.OSDescription,
                ProcessorCount = Environment.ProcessorCount,
                TieredCompilation = Environment.GetEnvironmentVariable("DOTNET_TieredCompilation"),
                Files = FileCount,
                FileBytes = FileLength,
                Directories = 129,
                Packages = 2,
                MetadataOnlyOverrides = FileCount / 64,
                Cache = "Warm OS and library caches. Synchronous single-thread measurements. Allocation includes managed bytes on the calling thread; no cold-storage or async claims.",
                EnumerationOperation = "One complete foreach traversal, including returned path strings. Repeated enumeration does not retain the results.",
                ReusedReaderOperation = "One 256-byte positional read from a previously opened 4KiB file; block contents are warm and reader construction is excluded.",
                LookupOperation = "FileExists followed by GetFileInfo for an existing file.",
                LibrarySha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(QuireBundle).Assembly.Location))),
                Openings = openings,
                Measurements = measurements,
                Checksum = _sink
            };
            await File.WriteAllTextAsync(resultPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine(resultPath);
        }
        finally
        {
            string resolved = Path.GetFullPath(root);
            if (!ProjectConfig.Within(resolved, Path.GetFullPath(Path.GetTempPath())) || !Path.GetFileName(resolved).StartsWith("quire-hot-paths-", StringComparison.Ordinal))
                throw new IOException("Unsafe benchmark cleanup.");
            Directory.Delete(resolved, true);
        }
    }
}

internal sealed record HotPathMeasurement(string Profile, string Backend, string Workload, int Round, int Operations, double Milliseconds, long AllocatedBytes, double BytesPerOperation, long Checksum);
