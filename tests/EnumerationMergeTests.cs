using Quire.Package;
using Quire.Package.Internal;

internal static class EnumerationMergeTests
{
    internal static void Register(Action<string, Func<Task>> test)
    {
        test("seven-package enumeration merge preserves content, metadata and directory ranges", async () =>
        {
            using var fixture = new Fixture(false);
            string[] directories = ["a", "a-b", "a.b", "a0", "ab", "nested/a", "nested/a-b", "nested/ab", "中文", "\ue000", "😀"];
            var paths = new List<string>();
            for (int index = 0; index < 66; index++)
            {
                string path = $"{directories[index % directories.Length]}/file{index:D3}.bin";
                paths.Add(path);
                fixture.Put(path, [(byte)index, 1, 2]);
                if (index % 3 == 0)
                {
                    fixture.Text(path + ".meta", "revision=0");
                }
            }

            paths.Add("root.bin");
            fixture.Put("root.bin", [1]);
            var previous = await fixture.Build();
            for (int overlay = 1; overlay <= 6; overlay++)
            {
                for (int index = 0; index < 66; index++)
                {
                    string path = paths[index];
                    if ((index + overlay) % 7 == 0)
                    {
                        fixture.Put(path, Enumerable.Repeat((byte)overlay, 3 + overlay).ToArray());
                    }

                    if ((index + overlay) % 5 == 0)
                    {
                        fixture.Text(path + ".meta", $"revision={overlay}");
                    }

                    if ((index + overlay) % 11 == 0)
                    {
                        File.Delete(Path.Combine(fixture.Input, path + ".meta"));
                        // A missing sidecar inherits metadata unless content is replaced too.
                        fixture.Put(path, Enumerable.Repeat((byte)(128 + overlay), 12 + overlay).ToArray());
                    }
                }

                for (int index = 0; index < 4; index++)
                {
                    string path = $"{directories[(overlay + index) % directories.Length]}/branch{overlay:D2}/new{index}.bin";
                    paths.Add(path);
                    fixture.Put(path, [(byte)overlay, (byte)index]);
                }

                previous = await fixture.Build(previous.TagId);
            }

            var expected = paths.Select(path => new AssetInfo("common/data/" + path, new FileInfo(Path.Combine(fixture.Input, path)).Length, File.Exists(Path.Combine(fixture.Input, path + ".meta")))).OrderBy(info => info.Path, AssetPath.Comparer).ToArray();
            foreach (var options in new[]
            {
                new ReadOptions(),
                new ReadOptions
                {
                    MaxResidentIndexBytes = 0,
                    PageCacheBytes = 196608
                }
            }

            )
            {
                using var set = PackageSet.Open(fixture.Output, options);
                Check.Equal(7, set.Packages.Count);
                var actualFiles = set.EnumerateFiles().ToArray();
                if (!expected.SequenceEqual(actualFiles))
                {
                    var differences = expected.Zip(actualFiles)
                        .Where(pair => pair.First != pair.Second)
                        .Take(5)
                        .Select(pair => $"expected {pair.First}; actual {pair.Second}");
                    throw new Exception($"Merged files differ with resident budget {options.MaxResidentIndexBytes}: " +
                        $"expected count {expected.Length}, actual count {actualFiles.Length}. " + string.Join(" | ", differences));
                }
                var scopes = directories.Select(directory => "common/data/" + directory).Concat(["", "common/data", "common/data/nested", "common/data/absent"]);
                foreach (string scope in scopes)
                {
                    string prefix = scope.Length == 0 ? "" : scope + "/";
                    var descendants = expected.Where(info => info.Path.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
                    foreach (bool recursive in new[]
                    {
                        false,
                        true
                    }

                    )
                    {
                        var expectedFiles = descendants.Where(info => recursive || !info.Path.AsSpan(prefix.Length).Contains('/'));
                        Check.True(expectedFiles.SequenceEqual(set.EnumerateFiles(scope, recursive)), scope);
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

                        var orderedDirectories = expectedDirectories.OrderBy(directory => directory + "/", AssetPath.Comparer);
                        Check.True(orderedDirectories.SequenceEqual(set.EnumerateDirectories(scope, recursive)), scope);
                    }
                }
            }
        });
    }
}
