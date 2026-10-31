using System.Security.Cryptography;
using Quire;
using Quire.Cli.Build;
using Quire.Cli.Configuration;
using Quire.Cli.Extraction;
using Quire.Cli.Sources;
using Quire.Cli.Tags;
using Quire.Internal;

internal static class DeletionTests
{
    private const string Root = "common/data";

    internal static void Register(Action<string, Func<Task>> test)
    {
        test("deletion patches hide resources across all indexes without modifying old packages", VisibilityAsync);
        test("deletion barriers reject metadata-only resurrection and allow new content", ResurrectionAsync);
        test("deletion planning handles filtering, package moves and removed package definitions", PlanningAsync);
        test("deleted record validation rejects content, metadata and block payloads", RecordValidationAsync);
    }

    private static async Task VisibilityAsync()
    {
        foreach (bool encrypted in new[] { false, true })
        {
            using var fixture = new Fixture(encrypted, encrypted);
            fixture.Put("keep", [1, 2, 3]);
            fixture.Put("gone/deep/file", [4, 5]);
            fixture.Text("gone/deep/file.meta", "old=true");
            fixture.Put("shared/remove", [6]);
            fixture.Put("shared/stay", [7]);
            var baseline = await fixture.Build();
            string original = Path.Combine(fixture.Root, baseline.Outputs.Single());
            byte[] digest = SHA256.HashData(File.ReadAllBytes(original));
            using var oldView = QuireBundle.Open(fixture.Output, fixture.ReadOptions);
            using var held = oldView.OpenRead(Root + "/gone/deep/file");
            File.Delete(Path.Combine(fixture.Input, "gone/deep/file.meta"));
            File.Delete(Path.Combine(fixture.Input, "gone/deep/file"));
            File.Delete(Path.Combine(fixture.Input, "shared/remove"));
            var deletion = await fixture.Build(baseline.TagId);
            Check.Equal(2, deletion.DeletedChanges);
            Check.Equal(0, deletion.ContentChanges);
            Check.Equal(0, deletion.MetadataChanges);
            Check.Equal(1, deletion.Outputs.Length);
            TagStore.Commit(fixture.Config().StateDirectory, deletion.TagId);
            Check.Equal(2, TagStore.LoadBaseline(fixture.Config().StateDirectory).Files.Count);
            Check.Bytes(digest, SHA256.HashData(File.ReadAllBytes(original)));
            Check.True(oldView.FileExists(Root + "/gone/deep/file"));
            byte[] heldBytes = new byte[2];
            held.ReadExactly(heldBytes);
            Check.Bytes([4, 5], heldBytes);

            AssetEntryInfo[] expected =
            [
                new(Root + "/keep", AssetEntryKind.File, 3, false),
                new(Root + "/shared/stay", AssetEntryKind.File, 1, false)
            ];
            foreach (var options in Modes(fixture.ReadOptions))
            {
                using var bundle = await QuireBundle.OpenAsync(fixture.Output, options);
                await AssertViewAsync(bundle, expected);
                foreach (string missing in new[] { Root + "/gone/deep/file", Root + "/shared/remove" })
                {
                    Check.True(!bundle.FileExists(missing));
                    Check.True(!bundle.TryGetFileInfo(missing, out var info));
                    Check.Equal(default, info);
                    Check.Throws<FileNotFoundException>(() => bundle.GetFileInfo(missing));
                    Check.Throws<FileNotFoundException>(() => bundle.OpenRead(missing));
                    Check.Throws<FileNotFoundException>(() => bundle.Read(missing));
                    Check.Throws<FileNotFoundException>(() => bundle.ReadMetadata(missing));
                    Check.Throws<FileNotFoundException>(() => bundle.ReadAt(missing, 0, new byte[1]));
                    await Check.ThrowsAsync<FileNotFoundException>(async () => await bundle.ReadAsync(missing));
                    await Check.ThrowsAsync<FileNotFoundException>(async () => await bundle.ReadMetadataAsync(missing));
                    await Check.ThrowsAsync<FileNotFoundException>(async () => await bundle.ReadAtAsync(missing, 0, new byte[1]));
                }
                Check.True(!bundle.DirectoryExists(Root + "/gone"));
                Check.True(!bundle.DirectoryExists(Root + "/gone/deep"));
                Check.True(bundle.DirectoryExists(Root + "/shared"));
                bundle.Verify();
                await bundle.VerifyAsync();

                using var deletionOnly = QuireBundle.Open(Path.Combine(fixture.Output, deletion.TagId), options);
                await AssertViewAsync(deletionOnly, []);
                Check.True(deletionOnly.DirectoryExists(""));
                Check.True(!deletionOnly.DirectoryExists("common"));
                await deletionOnly.VerifyAsync();
            }

            string destination = Path.Combine(fixture.Root, "extracted");
            var extracted = await PackageExtractor.ExtractAsync(fixture.Output, destination, fixture.ReadOptions, default);
            Check.Equal(2L, extracted.Assets);
            Check.Equal(0L, extracted.MetadataFiles);
            Check.True(!Directory.Exists(Path.Combine(destination, "common/data/gone")));
            Check.True(!File.Exists(Path.Combine(destination, "common/data/shared/remove")));
            var unchanged = await fixture.Build(deletion.TagId);
            Check.Equal(0, unchanged.DeletedChanges);
            Check.Equal(0, unchanged.Outputs.Length);
        }
    }

    private static async Task ResurrectionAsync()
    {
        using var fixture = new Fixture(false);
        fixture.Put("gone/deep/file", [1]);
        fixture.Text("gone/deep/file.meta", "revision=1");
        var baseline = await fixture.Build();
        File.Delete(Path.Combine(fixture.Input, "gone/deep/file"));
        File.Delete(Path.Combine(fixture.Input, "gone/deep/file.meta"));
        var deletion = await fixture.Build(baseline.TagId);
        long metadataTicks = deletion.BuildTimeUtc.UtcTicks + 1;
        byte[] metadata = PropertyMetadata.Parse("revision=99");
        var orphan = new SourceAsset("main", Root + "/gone/deep/file", "", 0, new byte[32], metadata, SHA256.HashData(metadata), CompressionAlgorithm.None);
        string orphanPackage = Path.Combine(fixture.Output, "orphan-after-delete.asset");
        await PackageWriter.WriteAsync(orphanPackage, Path.Combine(fixture.Root, "orphan-scratch"), "f0000001", metadataTicks,
            [new BuildEntry(orphan, EntryKind.MetadataOnly)], null, fixture.BuildOptions, default);
        foreach (var options in Modes(fixture.ReadOptions))
        {
            using var hidden = QuireBundle.Open(fixture.Output, options);
            await AssertViewAsync(hidden, []);
            Check.True(!hidden.DirectoryExists(Root));
            Check.True(!hidden.FileExists(orphan.Path));
        }

        fixture.Put("gone/deep/file", [9, 8]);
        var restored = await fixture.Build(deletion.TagId);
        Check.Equal(1, restored.ContentChanges);
        Check.Equal(0, restored.DeletedChanges);
        if (restored.BuildTimeUtc.UtcTicks <= metadataTicks)
            Fixture.RewriteHeader(Path.Combine(fixture.Root, restored.Outputs.Single()), header => header with { BuildTicks = metadataTicks + 1 });
        foreach (var options in Modes(fixture.ReadOptions))
        {
            using var visible = QuireBundle.Open(fixture.Output, options);
            await AssertViewAsync(visible, [new(orphan.Path, AssetEntryKind.File, 2, false)]);
            using var data = await visible.ReadAsync(orphan.Path);
            Check.Bytes([9, 8], data.Data.Span);
            Check.True(!data.Info.HasMetadata);
            Check.Equal(0, data.Metadata.Count);
        }
    }

    private static async Task PlanningAsync()
    {
        using (var fixture = new Fixture(false))
        {
            fixture.Put("keep", [1]);
            fixture.Put("filtered", [2]);
            var baseline = await fixture.Build();
            fixture.Text(".assetignore", "filtered\n");
            var patch = await fixture.Build(baseline.TagId);
            Check.Equal(1, patch.DeletedChanges);
            Check.True(File.Exists(Path.Combine(fixture.Input, "filtered")));
            using var bundle = QuireBundle.Open(fixture.Output);
            Check.True(!bundle.FileExists(Root + "/filtered"));
        }

        using (var fixture = new Fixture(false))
        {
            fixture.Put("moving", [3]);
            var config = fixture.Config();
            var oldPackage = config.Packages.Single() with { Name = "zz_old" };
            config = config with { Packages = [oldPackage] };
            var baseline = await ProjectBuilder.BuildAsync(config, null, fixture.BuildOptions, default);
            var renamed = config with { Packages = [oldPackage with { Name = "aa_new" }] };
            TagStore.Commit(config.StateDirectory, baseline.TagId);
            var moved = await ProjectBuilder.BuildAsync(renamed, null, fixture.BuildOptions, default);
            Check.Equal(0, moved.DeletedChanges);
            Check.Equal(0, moved.ContentChanges);
            Check.Equal(0, moved.Outputs.Length);
            using (var bundle = QuireBundle.Open(fixture.Output))
                Check.True(bundle.FileExists(Root + "/moving"));

            string empty = Path.Combine(fixture.Root, "empty-source");
            Directory.CreateDirectory(empty);
            var removed = renamed with { Packages = [new PackageConfig("remaining", CompressionAlgorithm.None, [new SourceConfig(empty, Root, [], [])])] };
            TagStore.Commit(config.StateDirectory, moved.TagId);
            var deletion = await ProjectBuilder.BuildAsync(removed, null, fixture.BuildOptions, default);
            Check.Equal(1, deletion.DeletedChanges);
            Check.True(deletion.Outputs.Single().EndsWith("remaining.asset", StringComparison.Ordinal));
            TagStore.Commit(config.StateDirectory, deletion.TagId);
            Check.Equal(0, TagStore.LoadBaseline(config.StateDirectory).Files.Count);
            using var hidden = QuireBundle.Open(fixture.Output);
            Check.True(!hidden.FileExists(Root + "/moving"));
        }

        using (var fixture = new Fixture(false))
        {
            fixture.Put("duplicate", [1]);
            string other = Path.Combine(fixture.Root, "other-source");
            Directory.CreateDirectory(other);
            File.WriteAllBytes(Path.Combine(other, "duplicate"), [2]);
            var config = fixture.Config();
            var first = config.Packages.Single() with { Name = "aa_first" };
            var second = new PackageConfig("zz_second", CompressionAlgorithm.None, [new SourceConfig(other, Root, [], [])]);
            config = config with { Packages = [first, second] };
            var baseline = await ProjectBuilder.BuildAsync(config, null, fixture.BuildOptions, default);
            TagStore.Commit(config.StateDirectory, baseline.TagId);
            var patch = await ProjectBuilder.BuildAsync(config with { Packages = [first] }, null, fixture.BuildOptions, default);
            Check.Equal(0, patch.DeletedChanges);
            Check.Equal(1, patch.ContentChanges);
            foreach (var options in Modes(fixture.ReadOptions))
            {
                using var bundle = QuireBundle.Open(fixture.Output, options);
                using var data = bundle.Read(Root + "/duplicate");
                Check.Bytes([1], data.Data.Span);
            }
        }
    }

    private static async Task RecordValidationAsync()
    {
        using var fixture = new Fixture(false, true);
        fixture.Put("a", [1]);
        var baseline = await fixture.Build();
        File.Delete(Path.Combine(fixture.Input, "a"));
        var deletion = await fixture.Build(baseline.TagId);
        using var reader = await PackageReader.OpenAsync(Path.Combine(fixture.Root, deletion.Outputs.Single()), fixture.ReadOptions, true, default);
        var entry = reader.ReadEntry(0);
        Check.True(entry.Deleted);
        Check.Equal(EntryKind.Deleted, entry.Kind);
        Check.True(entry.ContentHash.IsZero);
        Check.Equal(entry, EntryRecord.Decode(entry.Encode(), reader.Header));
        foreach (var malformed in new[]
        {
            entry with { Kind = (EntryKind)3 },
            entry with { Length = 1 },
            entry with { DataOffset = reader.Header.DataOffset },
            entry with { StoredLength = 1 },
            entry with { HasMetadata = true },
            entry with { MetadataOffset = 1 },
            entry with { MetadataLength = 8 },
            entry with { Blocked = true },
            entry with { FirstBlock = 1 },
            entry with { BlockCount = 1 },
            entry with { ContentHash = SHA256.HashData(new byte[] { 1 }) }
        })
            Check.Throws<InvalidDataException>(() => EntryRecord.Decode(malformed.Encode(), reader.Header));
    }

    private static IEnumerable<ReadOptions> Modes(ReadOptions defaults)
    {
        foreach (var mode in new[] { AssetIndexMode.Merged, AssetIndexMode.PerPackage, AssetIndexMode.Auto })
        {
            var options = defaults with { IndexMode = mode, MaxMergedIndexBytes = mode == AssetIndexMode.Auto ? 0 : defaults.MaxMergedIndexBytes };
            yield return options;
            yield return options with { MaxResidentIndexBytes = 0, PageCacheBytes = 0 };
        }
    }

    private static async Task AssertViewAsync(QuireBundle bundle, AssetEntryInfo[] files)
    {
        var ordered = files.OrderBy(file => file.Path, AssetPath.Comparer).ToArray();
        Check.True(ordered.SequenceEqual(bundle.EnumerateFiles()));
        var directories = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in files)
            for (int slash = file.Path.IndexOf('/'); slash >= 0; slash = file.Path.IndexOf('/', slash + 1))
                directories.Add(file.Path[..slash]);
        Check.True(directories.OrderBy(path => path + "/", AssetPath.Comparer).SequenceEqual(bundle.EnumerateDirectories()));
        foreach (string scope in directories.Concat(["", "common", Root, Root + "/gone", Root + "/gone/deep", Root + "/shared"]).Distinct(StringComparer.Ordinal))
        {
            string prefix = scope.Length == 0 ? "" : scope + "/";
            var expected = files.Where(file => file.Path.StartsWith(prefix, StringComparison.Ordinal) && !file.Path.AsSpan(prefix.Length).Contains('/'))
                .Concat(directories.Where(path => path.StartsWith(prefix, StringComparison.Ordinal) && !path.AsSpan(prefix.Length).Contains('/'))
                    .Select(path => new AssetEntryInfo(path, AssetEntryKind.Directory, 0, false)))
                .OrderBy(entry => entry.Path + (entry.IsDirectory ? "/" : ""), AssetPath.Comparer).ToArray();
            Check.True(expected.SequenceEqual(bundle.Dir(scope)), scope);
            var asyncEntries = await bundle.DirAsync(scope);
            Check.True(expected.SequenceEqual(asyncEntries), scope);
            Check.True(expected.SequenceEqual(bundle.EnumerateEntries(scope)), scope);
            var streamed = new List<AssetEntryInfo>();
            await foreach (var entry in bundle.EnumerateEntriesAsync(scope))
                streamed.Add(entry);
            Check.True(expected.SequenceEqual(streamed), scope);
            Check.Equal(scope.Length == 0 || directories.Contains(scope), bundle.DirectoryExists(scope));
        }
    }
}
