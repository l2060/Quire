using System.Reflection;
using Quire.Package;
using Quire.Package.Internal;

internal static class MergedIndexTests
{
    private const string Root = "common/data";
    internal static void Register(Action<string, Func<Task>> test)
    {
        test("public indexing API selects automatically without exposing strategy controls", PublicApiAsync);
        test("merged index matches independent overlay model in resident, paged and uncached modes", OverlayModesAsync);
        test("merged index exact memory budgets, automatic fallback and invalid options", BudgetsAsync);
        test("merged index empty root and orphan metadata use only the root directory", EmptyAndOrphanAsync);
        test("canceled merged index construction closes package handles and permits reopening", CancellationAsync);
        test("merged index handles maximum UTF-8 path length and directory depth", MaximumDepthAsync);
        test("merged index supports concurrent queries, allocation-free lookups and held readers", OwnershipAndConcurrencyAsync);
        test("merged directory queries avoid scanning direct child files without page caching", DirectoryQueryIoAsync);
    }

    private static Task PublicApiAsync()
    {
        Type strategyType = typeof(AssetIndexMode);
        Check.True(!strategyType.IsVisible);
        Check.True(!typeof(PackageSet).Assembly.GetExportedTypes().Contains(strategyType));

        Type optionsType = typeof(ReadOptions);
        Check.True(optionsType.GetProperty(nameof(ReadOptions.IndexMode), BindingFlags.Instance | BindingFlags.Public) is null);
        var strategyOption = optionsType.GetProperty(nameof(ReadOptions.IndexMode), BindingFlags.Instance | BindingFlags.NonPublic);
        Check.True(strategyOption is not null);
        Check.True(strategyOption?.GetMethod?.IsAssembly == true);
        Check.True(strategyOption?.SetMethod?.IsAssembly == true);
        Check.Equal(AssetIndexMode.Auto, new ReadOptions().IndexMode);
        Check.Equal(typeof(long), optionsType.GetProperty(nameof(ReadOptions.MaxMergedIndexBytes))!.PropertyType);

        Type diagnosticsType = typeof(PackageIndexInfo);
        Check.True(diagnosticsType.GetProperty("Mode") is null);
        Check.Equal(typeof(bool), diagnosticsType.GetProperty(nameof(PackageIndexInfo.IsMerged))!.PropertyType);
        var signatures = diagnosticsType.GetConstructors()
            .SelectMany(constructor => constructor.GetParameters())
            .Select(parameter => parameter.ParameterType)
            .Concat(diagnosticsType.GetMethods().SelectMany(method =>
                method.GetParameters().Select(parameter => parameter.ParameterType).Append(method.ReturnType)))
            .Concat(diagnosticsType.GetProperties().Select(property => property.PropertyType))
            .Concat(diagnosticsType.GetFields().Select(field => field.FieldType));
        Check.True(!signatures.Any(ContainsStrategyType),
            "Public diagnostics must not expose the internal strategy through properties, constructors or Deconstruct.");

        using var fixture = new Fixture(false);
        Directory.CreateDirectory(fixture.Output);
        using var automatic = PackageSet.Open(fixture.Output);
        Check.True(automatic.IndexInfo.IsMerged);
        Check.True(!automatic.IndexInfo.IsFallback);
        using var constrained = PackageSet.Open(fixture.Output, new ReadOptions
        {
            MaxMergedIndexBytes = 0
        });
        Check.True(!constrained.IndexInfo.IsMerged);
        Check.True(constrained.IndexInfo.IsFallback);
        return Task.CompletedTask;
    }

    private static bool ContainsStrategyType(Type type)
    {
        if (type == typeof(AssetIndexMode))
        {
            return true;
        }
        if (type.HasElementType && ContainsStrategyType(type.GetElementType()!))
        {
            return true;
        }
        return type.IsGenericType && type.GetGenericArguments().Any(ContainsStrategyType);
    }

    private static async Task OverlayModesAsync()
    {
        using var fixture = new Fixture(false);
        string[] names = ["root.bin", "a/direct.bin", "a/metadata.bin", "a/deep/body.bin", "a-b/body.bin", "a.b/body.bin", "a0/body.bin", "ab/body.bin", "中文/文件.bin", "\ue000/body.bin", "😀/body.bin", "collision", "replaced.bin", "retained.bin"];
        var expected = new Dictionary<string, ExpectedFile>(StringComparer.Ordinal);
        for (int index = 0; index < names.Length; index++)
        {
            byte[] bytes = [(byte)index, 1, 2];
            bool metadata = index % 2 == 0;
            fixture.Put(names[index], bytes);
            if (metadata)
            {
                fixture.Text(names[index] + ".meta", "revision=0");
            }

            expected.Add(Root + "/" + names[index], new(bytes, metadata ? 0 : null));
        }

        var previous = await fixture.Build();
        for (int revision = 1; revision <= 6; revision++)
        {
            fixture.Text("a/metadata.bin.meta", $"revision={revision}");
            expected[Root + "/a/metadata.bin"] = expected[Root + "/a/metadata.bin"] with
            {
                Revision = revision
            };
            byte[] replaced = Enumerable.Repeat((byte)(90 + revision), revision + 3).ToArray();
            fixture.Put("replaced.bin", replaced);
            int? replacedRevision = revision % 2 == 0 ? revision : null;
            if (replacedRevision.HasValue)
            {
                fixture.Text("replaced.bin.meta", $"revision={revision}");
            }
            else
            {
                File.Delete(Path.Combine(fixture.Input, "replaced.bin.meta"));
            }

            expected[Root + "/replaced.bin"] = new(replaced, replacedRevision);
            byte[] changed = [(byte)revision, 7, 8, 9];
            fixture.Put("a/deep/body.bin", changed);
            expected[Root + "/a/deep/body.bin"] = new(changed, null);
            string added = $"new{revision}/nested/body.bin";
            byte[] addedBytes = [(byte)revision];
            fixture.Put(added, addedBytes);
            expected.Add(Root + "/" + added, new(addedBytes, null));
            if (revision == 1)
            {
                File.Delete(Path.Combine(fixture.Input, "collision"));
                fixture.Put("collision/child.bin", [40, 41]);
                expected.Add(Root + "/collision/child.bin", new([40, 41], null));
                File.Delete(Path.Combine(fixture.Input, "retained.bin"));
            }

            previous = await fixture.Build(previous.TagId);
        }

        AssetInfo[] expectedFiles = expected.Select(pair => new AssetInfo(pair.Key, pair.Value.Bytes.Length, pair.Value.Revision.HasValue)).OrderBy(info => info.Path, AssetPath.Comparer).ToArray();
        string[] expectedDirectories = Directories(expectedFiles, "", true);
        string[] scopes = expectedDirectories.Concat(["", "common/dat", Root + "/missing", Root + "/root.bin", Root + "/a/de", Root + "/😀/missing"]).ToArray();
        foreach (var storage in StorageOptions())
        {
            foreach (var mode in new[]
            {
                storage with
                {
                    IndexMode = AssetIndexMode.PerPackage
                },
                storage with
                {
                    IndexMode = AssetIndexMode.Merged
                },
                storage with
                {
                    IndexMode = AssetIndexMode.Auto
                },
                storage with
                {
                    IndexMode = AssetIndexMode.Auto,
                    MaxMergedIndexBytes = 0
                }
            }

            )
            {
                using var packages = await PackageSet.OpenAsync(fixture.Output, mode);
                Check.Equal(7, packages.Packages.Count);
                bool merged = mode.IndexMode != AssetIndexMode.PerPackage && mode.MaxMergedIndexBytes != 0;
                Check.Equal(merged, packages.IndexInfo.IsMerged);
                Check.Equal(mode.IndexMode == AssetIndexMode.Auto && !merged, packages.IndexInfo.IsFallback);
                Check.True(expectedFiles.SequenceEqual(packages.EnumerateFiles()));
                Check.True(expectedDirectories.SequenceEqual(packages.EnumerateDirectories()));
                if (merged)
                {
                    Check.Equal<int?>(expectedFiles.Length, packages.IndexInfo.FileCount);
                    Check.Equal<int?>(expectedDirectories.Length, packages.IndexInfo.DirectoryCount);
                    Check.Equal(28L * expectedFiles.Length + 40L * (expectedDirectories.Length + 1), packages.IndexInfo.MemoryBytes);
                }
                else
                {
                    AssertPerPackageDiagnostics(packages);
                }

                foreach (var info in expectedFiles)
                {
                    Check.True(packages.FileExists(info.Path));
                    Check.True(packages.TryGetFileInfo(info.Path, out var actual));
                    Check.Equal(info, actual);
                    Check.Equal(info, packages.GetFileInfo(info.Path));
                    using var data = packages.Read(info.Path);
                    var model = expected[info.Path];
                    Check.Bytes(model.Bytes, data.Data.Span);
                    if (model.Revision.HasValue)
                    {
                        Check.Equal((long)model.Revision.Value, data.Metadata.GetInt64("revision"));
                    }
                    else
                    {
                        Check.Equal(0, data.Metadata.Count);
                    }
                }

                foreach (string scope in scopes)
                {
                    string prefix = scope.Length == 0 ? "" : scope + "/";
                    var descendants = expectedFiles.Where(info => info.Path.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
                    Check.Equal(scope.Length == 0 || descendants.Length != 0, packages.DirectoryExists(scope));
                    foreach (bool recursive in new[]
                    {
                        false,
                        true
                    }

                    )
                    {
                        var files = descendants.Where(info => recursive || !info.Path.AsSpan(prefix.Length).Contains('/'));
                        Check.True(files.SequenceEqual(packages.EnumerateFiles(scope, recursive)), scope);
                        Check.True(Directories(expectedFiles, scope, recursive).SequenceEqual(packages.EnumerateDirectories(scope, recursive)), scope);
                    }
                }

                Check.True(packages.FileExists(Root + "/collision"));
                Check.True(packages.DirectoryExists(Root + "/collision"));
                Check.True(!packages.TryGetFileInfo(Root + "/absent", out var absent));
                Check.Equal(default, absent);
                Check.Throws<FileNotFoundException>(() => packages.GetFileInfo(Root + "/absent"));
            }
        }
    }

    private static async Task BudgetsAsync()
    {
        using var fixture = new Fixture(false);
        fixture.Put("a/one.bin", [1]);
        fixture.Put("a/b/two.bin", [2, 3]);
        fixture.Put("root.bin", []);
        await fixture.Build();
        const long required = 3 * 28 + 5 * 40;
        Check.Equal(AssetIndexMode.Auto, new ReadOptions().IndexMode);
        Check.Equal(64L * 1024 * 1024, new ReadOptions().MaxMergedIndexBytes);
        foreach (long budget in new[]
        {
            required,
            required + 1,
            long.MaxValue
        }

        )
        {
            foreach (var mode in new[]
            {
                AssetIndexMode.Auto,
                AssetIndexMode.Merged
            }

            )
            {
                using var packages = PackageSet.Open(fixture.Output, new ReadOptions { IndexMode = mode, MaxMergedIndexBytes = budget });
                Check.True(packages.IndexInfo.IsMerged);
                Check.Equal(required, packages.IndexInfo.MemoryBytes);
                Check.Equal<int?>(3, packages.IndexInfo.FileCount);
                Check.Equal<int?>(4, packages.IndexInfo.DirectoryCount);
                Check.True(!packages.IndexInfo.IsFallback);
            }
        }

        foreach (long budget in new[]
        {
            0L,
            required - 1
        }

        )
        {
            var options = new ReadOptions
            {
                IndexMode = AssetIndexMode.Auto,
                MaxMergedIndexBytes = budget
            };
            using var fallback = PackageSet.Open(fixture.Output, options);
            AssertPerPackageDiagnostics(fallback);
            Check.True(fallback.IndexInfo.IsFallback);
            Check.Equal(3, fallback.EnumerateFiles().Count());
            Check.Equal(4, fallback.EnumerateDirectories().Count());
            Check.Throws<InvalidOperationException>(() => PackageSet.Open(fixture.Output, options with { IndexMode = AssetIndexMode.Merged }));
            await Check.ThrowsAsync<InvalidOperationException>(async () =>
            {
                using var rejected = await PackageSet.OpenAsync(fixture.Output, options with
                {
                    IndexMode = AssetIndexMode.Merged
                });
            });
        }

        using (var perPackage = PackageSet.Open(fixture.Output, new ReadOptions { IndexMode = AssetIndexMode.PerPackage, MaxMergedIndexBytes = 0 }))
        {
            AssertPerPackageDiagnostics(perPackage);
            Check.True(!perPackage.IndexInfo.IsFallback);
        }

        foreach (var invalid in new[]
        {
            new ReadOptions
            {
                IndexMode = (AssetIndexMode)int.MaxValue
            },
            new ReadOptions
            {
                MaxMergedIndexBytes = -1
            },
            new ReadOptions
            {
                IndexMode = AssetIndexMode.PerPackage,
                MaxMergedIndexBytes = -1
            }
        }

        )
        {
            Check.Throws<ArgumentOutOfRangeException>(() => PackageSet.Open(fixture.Output, invalid));
            await Check.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            {
                using var rejected = await PackageSet.OpenAsync(fixture.Output, invalid);
            });
        }
    }

    private static async Task EmptyAndOrphanAsync()
    {
        using var fixture = new Fixture(false);
        Directory.CreateDirectory(fixture.Output);
        using (var empty = PackageSet.Open(fixture.Output, new ReadOptions { IndexMode = AssetIndexMode.Merged, MaxMergedIndexBytes = 40 }))
        {
            AssertEmptyMerged(empty);
        }

        using (var fallback = PackageSet.Open(fixture.Output, new ReadOptions { MaxMergedIndexBytes = 39 }))
        {
            AssertPerPackageDiagnostics(fallback);
            Check.True(fallback.IndexInfo.IsFallback);
            Check.True(fallback.DirectoryExists(""));
        }

        Check.Throws<InvalidOperationException>(() => PackageSet.Open(fixture.Output, new ReadOptions { IndexMode = AssetIndexMode.Merged, MaxMergedIndexBytes = 0 }));
        fixture.Put("orphan/deep/body.bin", [1, 2, 3]);
        fixture.Text("orphan/deep/body.bin.meta", "revision=1");
        var baseline = await fixture.Build();
        fixture.Text("orphan/deep/body.bin.meta", "revision=2");
        var patch = await fixture.Build(baseline.TagId);
        string patchDirectory = Path.Combine(fixture.Output, patch.TagId.ToString("N"));
        foreach (var storage in StorageOptions())
        {
            using var orphan = PackageSet.Open(patchDirectory, storage with
            {
                IndexMode = AssetIndexMode.Merged,
                MaxMergedIndexBytes = 40
            });
            AssertEmptyMerged(orphan);
            Check.True(!orphan.DirectoryExists("common"));
            Check.True(!orphan.DirectoryExists(Root + "/orphan"));
            Check.True(!orphan.FileExists(Root + "/orphan/deep/body.bin"));
        }
    }

    private static async Task CancellationAsync()
    {
        using var fixture = new Fixture(false);
        fixture.Put("a/body.bin", [1, 2, 3]);
        fixture.Put("a/second.bin", [4, 5]);
        fixture.Put("b/third.bin", [6]);
        await fixture.Build();
        // Three physical entries are visited in each pass; four visits reach population.
        foreach (long completedFiles in new[]
        {
            0L,
            1L,
            4L
        }

        )
        {
            using var cancellation = new CancellationTokenSource();
            var progress = new IndexProgress(cancellation, completedFiles == 0, completedFiles);
            await Check.ThrowsAsync<OperationCanceledException>(async () =>
            {
                using var rejected = await PackageSet.OpenAsync(fixture.Output, new ReadOptions { IndexMode = AssetIndexMode.Merged, MaxResidentIndexBytes = 0, PageCacheBytes = 0, Progress = progress }, cancellation.Token);
            });
            Check.True(progress.SawBuildStage);
            if (completedFiles > 0)
            {
                Check.Equal(completedFiles, progress.BuildFiles);
            }

            foreach (string file in Directory.EnumerateFiles(fixture.Output, "*.asset", SearchOption.AllDirectories))
            {
                using var exclusive = File.Open(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                Check.True(exclusive.Length > 0);
            }
        }

        using var reopened = PackageSet.Open(fixture.Output, new ReadOptions { IndexMode = AssetIndexMode.Merged });
        Check.True(reopened.FileExists(Root + "/a/body.bin"));
    }

    private static async Task MaximumDepthAsync()
    {
        using var fixture = new Fixture(false);
        const int depth = 2047;
        string prefix = string.Join("/", Enumerable.Repeat("d", depth));
        string path = prefix + "/xx";
        Check.Equal(4096, AssetPath.Utf8.GetByteCount(path));
        string configuration = File.ReadAllText(fixture.ProjectPath);
        File.WriteAllText(fixture.ProjectPath, configuration.Replace("prefix: common/data", "prefix: " + prefix, StringComparison.Ordinal));
        fixture.Put("xx", [3, 2, 1]);
        await fixture.Build();
        foreach (var mode in new[]
        {
            AssetIndexMode.PerPackage,
            AssetIndexMode.Merged
        }

        )
        {
            using var packages = PackageSet.Open(fixture.Output, new ReadOptions { IndexMode = mode });
            Check.True(packages.FileExists(path));
            Check.Equal(new AssetInfo(path, 3, false), packages.GetFileInfo(path));
            Check.True(packages.DirectoryExists(prefix));
            Check.True(packages.EnumerateDirectories("", false).SequenceEqual(["d"]));
            Check.Equal(0, packages.EnumerateDirectories(prefix).Count());
            Check.Equal(0, packages.EnumerateFiles("", false).Count());
            Check.Equal(path, packages.EnumerateFiles(prefix, false).Single().Path);
            int count = 0;
            foreach (string directory in packages.EnumerateDirectories())
            {
                count++;
                Check.Equal(count * 2 - 1, directory.Length);
                Check.True(prefix.StartsWith(directory, StringComparison.Ordinal));
            }

            Check.Equal(depth, count);
            if (mode == AssetIndexMode.Merged)
            {
                Check.Equal<int?>(depth, packages.IndexInfo.DirectoryCount);
                Check.Equal(28L + 40L * (depth + 1), packages.IndexInfo.MemoryBytes);
            }
        }
    }

    private static async Task OwnershipAndConcurrencyAsync()
    {
        using var fixture = new Fixture(false);
        const int fileCount = 48;
        for (int index = 0; index < fileCount; index++)
        {
            fixture.Put($"group{index % 4}/item{index:D2}.bin", [(byte)index, 17, 25]);
        }

        await fixture.Build();
        using var packages = PackageSet.Open(fixture.Output, new ReadOptions { IndexMode = AssetIndexMode.Merged, ReadMode = AssetReadMode.MemoryMapped });
        const string first = Root + "/group0/item00.bin";
        Parallel.For(0, 128, index =>
        {
            int file = index % fileCount;
            string path = $"{Root}/group{file % 4}/item{file:D2}.bin";
            Check.True(packages.FileExists(path));
            Check.True(packages.DirectoryExists(Root + "/group0"));
            Check.Equal(3L, packages.GetFileInfo(path).Length);
            Check.Equal(fileCount, packages.EnumerateFiles().Count());
            Check.Equal(4, packages.EnumerateDirectories(Root, false).Count());
            Span<byte> bytes = stackalloc byte[3];
            Check.Equal(3, packages.ReadAt(path, 0, bytes));
            Check.Equal((byte)file, bytes[0]);
        });
        for (int index = 0; index < 2048; index++)
        {
            _ = packages.FileExists(first);
            _ = packages.TryGetFileInfo(first, out _);
            _ = packages.GetFileInfo(first);
            _ = packages.DirectoryExists(Root + "/group0");
        }

        long sum = 0;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < 1000; index++)
        {
            sum += packages.FileExists(first) ? 1 : 0;
            sum += packages.TryGetFileInfo(first, out var info) ? info.Length : 0;
            sum += packages.GetFileInfo(first).Length;
            sum += packages.DirectoryExists(Root + "/group0") ? 1 : 0;
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Check.Equal(0L, allocated);
        Check.Equal(8000L, sum);
        var files = packages.EnumerateFiles();
        var directories = packages.EnumerateDirectories(Root, false);
        using var fileIterator = files.GetEnumerator();
        using var directoryIterator = directories.GetEnumerator();
        using var reader = packages.OpenRead(first);
        packages.Dispose();
        int enumeratedFiles = 0;
        while (fileIterator.MoveNext())
        {
            Check.Equal(3L, fileIterator.Current.Length);
            enumeratedFiles++;
        }

        int enumeratedDirectories = 0;
        while (directoryIterator.MoveNext())
        {
            Check.True(directoryIterator.Current.StartsWith(Root + "/group", StringComparison.Ordinal));
            enumeratedDirectories++;
        }

        Check.Equal(fileCount, enumeratedFiles);
        Check.Equal(4, enumeratedDirectories);
        byte[] result = new byte[3];
        Check.Equal(3, await reader.ReadAtAsync(0, result));
        Check.Bytes([0, 17, 25], result);
        Check.Throws<ObjectDisposedException>(() => files.GetEnumerator());
        Check.Throws<ObjectDisposedException>(() => directories.GetEnumerator());
        Check.Throws<ObjectDisposedException>(() => packages.GetFileInfo(first));
        Check.Throws<ObjectDisposedException>(() => packages.DirectoryExists(""));
        Check.Throws<ObjectDisposedException>(() => packages.OpenRead(first));
    }

    private static async Task DirectoryQueryIoAsync()
    {
        using var fixture = new Fixture(false);
        for (int index = 0; index < 640; index++)
        {
            fixture.Put($"a{index:D4}.bin", [(byte)index]);
        }

        fixture.Put("z/leaf.bin", [5]);
        await fixture.Build();
        var perPackageProgress = new IndexProgress();
        var mergedProgress = new IndexProgress();
        var options = new ReadOptions
        {
            MaxResidentIndexBytes = 0,
            PageCacheBytes = 0,
            MetadataCacheBytes = 0
        };
        using var perPackage = PackageSet.Open(fixture.Output, options with
        {
            IndexMode = AssetIndexMode.PerPackage,
            Progress = perPackageProgress
        });
        using var merged = PackageSet.Open(fixture.Output, options with
        {
            IndexMode = AssetIndexMode.Merged,
            Progress = mergedProgress
        });
        perPackageProgress.ResetReadBytes();
        mergedProgress.ResetReadBytes();
        Check.True(perPackage.EnumerateDirectories(Root, false).SequenceEqual([Root + "/z"]));
        Check.True(merged.EnumerateDirectories(Root, false).SequenceEqual([Root + "/z"]));
        Check.True(perPackageProgress.ReadBytes > 0);
        Check.True(mergedProgress.ReadBytes * 8 < perPackageProgress.ReadBytes, $"Merged directory query read {mergedProgress.ReadBytes} bytes versus {perPackageProgress.ReadBytes} for per-package scanning.");
        mergedProgress.ResetReadBytes();
        Check.True(merged.DirectoryExists(Root + "/z"));
        Check.True(!merged.DirectoryExists(Root + "/absent"));
        Check.True(mergedProgress.ReadBytes * 8 < perPackageProgress.ReadBytes, "Directory existence queries must use the merged directory index.");
    }

    private static ReadOptions[] StorageOptions() => [new(), new()
    {
        MaxResidentIndexBytes = 0,
        PageCacheBytes = 196608
    }, new()
    {
        MaxResidentIndexBytes = 0,
        PageCacheBytes = 0
    }

    ];
    private static string[] Directories(IEnumerable<AssetInfo> files, string directory, bool recursive)
    {
        string prefix = directory.Length == 0 ? "" : directory + "/";
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            if (!file.Path.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            int slash = file.Path.IndexOf('/', prefix.Length);
            while (slash >= 0)
            {
                found.Add(file.Path[..slash]);
                if (!recursive)
                {
                    break;
                }

                slash = file.Path.IndexOf('/', slash + 1);
            }
        }

        return found.OrderBy(path => path + "/", AssetPath.Comparer).ToArray();
    }

    private static void AssertPerPackageDiagnostics(PackageSet packages)
    {
        Check.True(!packages.IndexInfo.IsMerged);
        Check.Equal(0L, packages.IndexInfo.MemoryBytes);
        Check.Equal<int?>(null, packages.IndexInfo.FileCount);
        Check.Equal<int?>(null, packages.IndexInfo.DirectoryCount);
    }

    private static void AssertEmptyMerged(PackageSet packages)
    {
        Check.True(packages.IndexInfo.IsMerged);
        Check.Equal(40L, packages.IndexInfo.MemoryBytes);
        Check.Equal<int?>(0, packages.IndexInfo.FileCount);
        Check.Equal<int?>(0, packages.IndexInfo.DirectoryCount);
        Check.True(!packages.IndexInfo.IsFallback);
        Check.True(packages.DirectoryExists(""));
        Check.Equal(0, packages.EnumerateFiles().Count());
        Check.Equal(0, packages.EnumerateDirectories().Count());
    }

    private sealed record ExpectedFile(byte[] Bytes, int? Revision);
    private sealed class IndexProgress(CancellationTokenSource? cancellation = null, bool cancelOnStage = true, long cancelAfterBuildFiles = 0) : IOperationProgress
    {
        private long _readBytes;
        private long _buildFiles;
        internal bool SawBuildStage
        {
            get;
            private set;
        }
        internal long ReadBytes => Interlocked.Read(ref _readBytes);
        internal long BuildFiles => Interlocked.Read(ref _buildFiles);

        internal void ResetReadBytes() => Interlocked.Exchange(ref _readBytes, 0);
        public void StartStage(string stage, long totalBytes = -1, long totalFiles = -1)
        {
            if (stage == "Building merged index")
            {
                SawBuildStage = true;
                if (cancelOnStage)
                {
                    cancellation?.Cancel();
                }
            }
        }

        public void Advance(long bytes = 0, long files = 0)
        {
            if (SawBuildStage)
            {
                long completed = Interlocked.Add(ref _buildFiles, files);
                if (cancelAfterBuildFiles > 0 && completed >= cancelAfterBuildFiles)
                {
                    cancellation?.Cancel();
                }
            }
        }

        public void AddRead(long bytes)
        {
            Interlocked.Add(ref _readBytes, bytes);
        }

        public void AddWritten(long bytes)
        {
        }
    }
}
