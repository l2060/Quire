using Quire.Package;
using Quire.Package.Internal;

internal static class EnumerationTests
{
    internal static void Register(Action<string, Func<Task>> test)
    {
        test("file and directory enumeration, overlays, prefix boundaries and Unicode", async () =>
        {
            using var fixture = new Fixture(false);
            string[] paths = ["root.bin", "a/direct.bin", "a/deep/one.bin", "a/deep/two.bin", "a-b/one.bin", "a.b/one.bin", "a0/one.bin", "ab/one.bin", "中文/文件.bin", "\ue000/one.bin", "😀/one.bin", "collision"];
            foreach (string path in paths)
            {
                fixture.Put(path, [1, 2, 3]);
            }

            fixture.Text("a/direct.bin.meta", "revision=1");
            var baseline = await fixture.Build();
            fixture.Text("a/direct.bin.meta", "revision=2");
            fixture.Put("a/deep/two.bin", [7, 8]);
            fixture.Put("new/leaf/item.bin", [9]);
            File.Delete(Path.Combine(fixture.Input, "collision"));
            fixture.Put("collision/child.bin", [4]);
            var patch = await fixture.Build(baseline.TagId);
            foreach (var options in new[]
            {
                new ReadOptions(),
                new ReadOptions
                {
                    MaxResidentIndexBytes = 0,
                    PageCacheBytes = 196608
                },
                new ReadOptions
                {
                    MaxResidentIndexBytes = 0,
                    PageCacheBytes = 0
                }
            }

            )
            {
                using var set = PackageSet.Open(fixture.Output, options);
                var all = set.EnumerateFiles().ToArray();
                Check.Equal(paths.Length + 2, all.Length);
                Check.True(all.Select(info => info.Path).SequenceEqual(all.Select(info => info.Path).Order(AssetPath.Comparer)));
                foreach (var info in all)
                {
                    Check.Equal(set.GetFileInfo(info.Path), info);
                }

                Check.True(set.FileExists("common/data/collision"));
                Check.True(set.DirectoryExists("common/data/collision"));
                Check.Equal(2L, set.ReadMetadata("common/data/a/direct.bin").GetInt64("revision"));
                Check.Equal(2L, set.GetFileInfo("common/data/a/deep/two.bin").Length);
                foreach (string directory in new[]
                {
                    "",
                    "common",
                    "common/data",
                    "common/data/a",
                    "common/data/a/deep",
                    "common/data/ab",
                    "common/data/😀",
                    "common/data/missing",
                    "common/dat",
                    "common/data/root.bin",
                    "common/data/collision"
                }

                )
                {
                    string prefix = directory.Length == 0 ? "" : directory + "/";
                    var descendants = all.Where(info => info.Path.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
                    Check.Equal(directory.Length == 0 || descendants.Length > 0, set.DirectoryExists(directory));
                    foreach (bool recursive in new[]
                    {
                        false,
                        true
                    }

                    )
                    {
                        var expectedFiles = descendants.Where(info => recursive || !info.Path.AsSpan(prefix.Length).Contains('/'));
                        Check.True(expectedFiles.SequenceEqual(set.EnumerateFiles(directory, recursive)), directory);
                        var expectedDirectories = new HashSet<string>(StringComparer.Ordinal);
                        foreach (var info in descendants)
                        {
                            int slash = info.Path.IndexOf('/', prefix.Length);
                            while (slash >= 0)
                            {
                                expectedDirectories.Add(info.Path[..slash]);
                                if (!recursive)
                                {
                                    break;
                                }

                                slash = info.Path.IndexOf('/', slash + 1);
                            }
                        }

                        var actual = set.EnumerateDirectories(directory, recursive).ToArray();
                        Check.Equal(expectedDirectories.Count, actual.Length);
                        Check.True(expectedDirectories.SetEquals(actual), directory);
                        Check.True(actual.SequenceEqual(actual.OrderBy(path => path + "/", AssetPath.Comparer)), directory);
                    }
                }

                Check.True(set.EnumerateDirectories().SequenceEqual(set.EnumerateDirectories("", true)),
                    "Default directory enumeration must match explicit recursive enumeration.");
                Check.True(set.EnumerateFiles().SequenceEqual(set.EnumerateFiles("", true)));
            }

            using var patchOnly = PackageSet.Open(Path.Combine(fixture.Output, patch.TagId));
            Check.True(!patchOnly.FileExists("common/data/a/direct.bin"));
            Check.True(patchOnly.EnumerateFiles().All(info => info.Path != "common/data/a/direct.bin"));
            Check.Equal(0, patchOnly.EnumerateDirectories("common/data/a/deep").Count());
        });
        test("orphan metadata does not create virtual directories", async () =>
        {
            using var fixture = new Fixture(false);
            fixture.Put("orphan/deep/item", [1]);
            fixture.Text("orphan/deep/item.meta", "version=1");
            var baseline = await fixture.Build();
            fixture.Text("orphan/deep/item.meta", "version=2");
            var patch = await fixture.Build(baseline.TagId);
            using var set = PackageSet.Open(Path.Combine(fixture.Output, patch.TagId));
            Check.Equal(0, set.EnumerateFiles().Count());
            Check.Equal(0, set.EnumerateDirectories("", true).Count());
            Check.True(!set.DirectoryExists("common"));
            Check.True(!set.DirectoryExists("common/data/orphan"));
            Check.True(set.DirectoryExists(""));
        });
        test("enumeration ownership, early exit, repeatability and input validation", async () =>
        {
            using var fixture = new Fixture(false);
            for (int i = 0; i < 32; i++)
            {
                fixture.Put($"dir{i:D2}/item.bin", [(byte)i]);
            }

            await fixture.Build();
            using var set = PackageSet.Open(fixture.Output);
            var files = set.EnumerateFiles();
            var directories = set.EnumerateDirectories("common/data", false);
            for (int i = 0; i < 20; i++)
            {
                foreach (var file in files)
                {
                    Check.True(file.Path.EndsWith("item.bin", StringComparison.Ordinal));
                    break;
                }

                foreach (var directory in directories)
                {
                    Check.True(directory.StartsWith("common/data/dir", StringComparison.Ordinal));
                    break;
                }
            }

            Check.Equal(32, files.Count());
            Check.Equal(32, directories.Count());
            foreach (string invalid in new[]
            {
                "/",
                "common/",
                "../a",
                "a//b",
                "a\\b"
            }

            )
            {
                Check.Throws<ArgumentException>(() => set.EnumerateFiles(invalid));
                Check.Throws<ArgumentException>(() => set.EnumerateDirectories(invalid));
                Check.Throws<ArgumentException>(() => set.DirectoryExists(invalid));
            }

            Check.Throws<ArgumentNullException>(() => set.EnumerateDirectories(null!));
            Check.True(!set.TryGetFileInfo("missing", out var missing));
            Check.Equal(default, missing);
            Check.Throws<FileNotFoundException>(() => set.GetFileInfo("missing"));
            using var fileIterator = files.GetEnumerator();
            using var directoryIterator = directories.GetEnumerator();
            Check.True(fileIterator.MoveNext());
            Check.True(directoryIterator.MoveNext());
            set.Dispose();
            int fileCount = 1;
            while (fileIterator.MoveNext())
            {
                fileCount++;
            }

            int directoryCount = 1;
            while (directoryIterator.MoveNext())
            {
                directoryCount++;
            }

            Check.Equal(32, fileCount);
            Check.Equal(32, directoryCount);
            Check.Throws<ObjectDisposedException>(() => files.GetEnumerator());
            Check.Throws<ObjectDisposedException>(() => set.EnumerateDirectories());
            Check.Throws<ObjectDisposedException>(() => set.DirectoryExists(""));
        });
        test("empty package root and absent directory enumeration", () =>
        {
            using var fixture = new Fixture(false);
            Directory.CreateDirectory(fixture.Output);
            using var set = PackageSet.Open(fixture.Output);
            Check.Equal(0, set.EnumerateFiles().Count());
            Check.Equal(0, set.EnumerateDirectories().Count());
            Check.True(set.DirectoryExists(""));
            Check.True(!set.DirectoryExists("absent"));
            return Task.CompletedTask;
        });
    }
}
