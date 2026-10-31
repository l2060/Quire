using System.Globalization;
using System.Text;
using Quire;
using Quire.Cli;
using Quire.Cli.Build;
using Quire.Cli.Configuration;
using Quire.Cli.Sources;
using Quire.Cli.Tags;
using Quire.Internal;

internal static class PackageNameTests
{
    internal static void Register(Action<string, Func<Task>> test)
    {
        test("Package name variables use one local timestamp, invariant formatting and single-pass insertion", ExpansionAsync);
        test("Automatic and named Tags produce matching expanded package filenames and manifests", BuildNamesAsync);
        test("Dynamic package names preserve unchanged content and route metadata and deletion updates", IncrementalsAsync);
        test("Package name templates reject malformed variables, unsafe names and resolved collisions", ValidationAsync);
        test("Package staging preserves unrelated files and cleans failures before and after writing", TemporaryOutputAsync);
        test("Logical resource precedence follows original templates when expanded order reverses", PrecedenceAsync);
    }

    private static Task ExpansionAsync()
    {
        DateTime localClock = new(2031, 7, 9, 13, 45, 56, 789, DateTimeKind.Unspecified);
        var instant = new DateTimeOffset(localClock, TimeZoneInfo.Local.GetUtcOffset(localClock)).ToUniversalTime();
        const string template = "{tag}_{tagid}_{yyyy}_{MM}_{dd}_{HH}_{mm}_{ss}_{fff}_{date}_{time}_{timestamp}";
        string expected = "release_abcdef12_2031_07_09_13_45_56_789_20310709_134556_" + instant.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        PackageNameTemplate.Validate("AU{yyyy}");
        Check.Equal("AU2031", PackageNameTemplate.Expand("AU{yyyy}", "release", "abcdef12", instant));
        Check.Throws<ArgumentException>(() => PackageNameTemplate.Expand("AU{tag}", "x", "abcdef12", instant));
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ar-SA");
            Check.Equal(expected, PackageNameTemplate.Expand(template, "release", "abcdef12", instant));
            Check.Equal("pkg_literal_{yyyy}_abcdef12", PackageNameTemplate.Expand("pkg_{tag}_{tagid}", "literal_{yyyy}", "abcdef12", instant));
        }
        finally { CultureInfo.CurrentCulture = previous; }
        using var fixture = new Fixture(false);
        WriteConfig(fixture, new("Patch-{MM}", "./input", "months"), new("Patch-{mm}", "./input", "minutes"));
        var project = fixture.Config();
        var resolved = project.ResolvePackageNames("release", "abcdef12", instant);
        Check.Equal("Patch-07", resolved.Packages[0].Name);
        Check.Equal("Patch-45", resolved.Packages[1].Name);
        Check.Equal("Patch-{MM}", resolved.Packages[0].OrderName);
        Check.Equal("Patch-{mm}", resolved.Packages[1].OrderName);
        Check.Equal("Patch-{MM}", project.Packages[0].Name);
        return Task.CompletedTask;
    }

    private static async Task BuildNamesAsync()
    {
        foreach (bool named in new[] { false, true })
        {
            using var fixture = new Fixture(false);
            fixture.Put("core/item", [1]);
            fixture.Put("media/item", [2]);
            WriteConfig(fixture,
                new("Core_{tag}_{tagid}_{date}_{time}_{fff}", "./input/core", "assets/core"),
                new("-\u8d44\u6e90.{tag}_{yyyy}{MM}{dd}_{HH}{mm}{ss}", "./input/media", "assets/media"));
            string? requested = named ? "\u7248\u672c 1.2_{yyyy}" : null;
            var tag = await Build(fixture, requested);
            Check.Equal(requested ?? tag.TagId, tag.Name);
            var local = tag.BuildTimeUtc.ToLocalTime();
            string date = local.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
            string time = local.ToString("HHmmss", CultureInfo.InvariantCulture);
            string millis = local.ToString("fff", CultureInfo.InvariantCulture);
            string[] expected = [$"Core_{tag.Name}_{tag.TagId}_{date}_{time}_{millis}.asset", $"-\u8d44\u6e90.{tag.Name}_{date}_{time}.asset"];
            Check.True(expected.ToHashSet(StringComparer.Ordinal).SetEquals(tag.Outputs.Select(path => Path.GetFileName(path))));
            foreach (string output in tag.Outputs)
            {
                string file = Path.Combine(fixture.Root, output);
                byte[] bytes = File.ReadAllBytes(file);
                var header = PackageHeader.Decode(bytes, bytes.Length);
                Check.Equal(tag.BuildTimeUtc.UtcTicks, header.BuildTicks);
                Check.Equal(tag.TagId, header.TagId);
                Check.Equal(tag.Name, header.TagName);
                var manifest = PackageManifest.Decode(bytes.AsMemory(PackageHeader.Size, header.ManifestLength), header.PackageUid, header.ManifestHash);
                Check.True(expected.ToHashSet(StringComparer.Ordinal).SetEquals(manifest.Entries.Select(entry => entry.FileName)));
            }
            string state = fixture.Config().StateDirectory;
            Check.True(expected.Select(Path.GetFileNameWithoutExtension).ToHashSet(StringComparer.Ordinal)
                .SetEquals(TagStore.ReadChanges(state, tag.TagId).Select(change => change.Package)));
            TagStore.Commit(state, tag.TagId);
            Check.True(expected.Select(Path.GetFileNameWithoutExtension).ToHashSet(StringComparer.Ordinal)
                .SetEquals(TagStore.LoadBaseline(state).Files.Values.Select(file => file.Package)));
            using var bundle = await QuireBundle.OpenAsync(fixture.Output, new ReadOptions { VerifyPackageChain = true });
            Check.Equal(2, bundle.Packages.Count);
            Check.Equal(tag.Name, bundle.LatestTag!.Name);
            using var content = bundle.Read("assets/media/item");
            Check.Bytes([2], content.Data.Span);
        }
    }

    private static async Task IncrementalsAsync()
    {
        using var fixture = new Fixture(false);
        const string template = "Patch_{tag}_{timestamp}";
        WriteConfig(fixture, new Package(template, "./input", "assets"));
        fixture.Put("keep", [1]);
        fixture.Put("meta", [2]);
        fixture.Text("meta.meta", "revision=1");
        fixture.Put("gone", [3]);
        string state = fixture.Config().StateDirectory;
        string preview = await Run(fixture, "diff");
        Check.True(preview.Contains("A  " + template + "/assets/keep", StringComparison.Ordinal));
        Check.True(!File.Exists(Path.Combine(state, "baseline.idx")));
        Check.Equal(0, TagStore.List(state).Count());
        if (Directory.Exists(state)) Check.Equal(0, Directory.GetFiles(state, "*", SearchOption.AllDirectories).Length);
        Check.True(!Directory.Exists(fixture.Output));
        var a = await Build(fixture, "initial");
        TagStore.Commit(state, a.TagId);
        var oldMetadata = TagStore.LoadBaseline(state).Files.Values.Single(file => file.Path == "assets/meta");
        var b = await Build(fixture, "settings-only");
        Check.Equal(0, b.Outputs.Length);
        Check.Equal(0, TagStore.ReadChanges(state, b.TagId).Length);
        TagStore.Commit(state, b.TagId);
        fixture.Text("meta.meta", "revision=2");
        File.Delete(Path.Combine(fixture.Input, "gone"));
        var before = Snapshot(fixture.Root);
        int tagCount = TagStore.List(state).Count();
        string[] directories = Directory.GetDirectories(fixture.Output);
        preview = await Run(fixture, "diff");
        Check.True(preview.Contains("M  " + template + "/assets/meta", StringComparison.Ordinal));
        Check.True(preview.Contains("D  " + template + "/assets/gone", StringComparison.Ordinal));
        AssertSnapshot(before, Snapshot(fixture.Root));
        Check.Equal(tagCount, TagStore.List(state).Count());
        Check.True(directories.ToHashSet(StringComparer.Ordinal).SetEquals(Directory.GetDirectories(fixture.Output)));
        var c = await Build(fixture, "delta");
        string package = "Patch_delta_" + c.BuildTimeUtc.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        Check.Equal(1, c.Outputs.Length);
        Check.Equal(package + ".asset", Path.GetFileName(c.Outputs.Single()));
        Check.Equal(0, c.ContentChanges);
        Check.Equal(1, c.MetadataChanges);
        Check.Equal(1, c.DeletedChanges);
        Check.True(TagStore.ReadChanges(state, c.TagId).All(change => change.Package == package));
        var metadata = TagStore.LoadDelta(state, c.TagId).Upserts.Single();
        Check.Equal(oldMetadata.Package, metadata.Package);
        Check.Equal(oldMetadata.Encoding, metadata.Encoding);
        TagStore.Commit(state, c.TagId);
        var d = await Build(fixture, "another-name");
        Check.Equal(0, d.Outputs.Length);
        using var bundle = QuireBundle.Open(fixture.Output, new ReadOptions { VerifyPackageChain = true });
        Check.Equal(2, bundle.EnumerateFiles().Count());
        Check.True(!bundle.FileExists("assets/gone"));
        using var data = bundle.Read("assets/meta");
        Check.Bytes([2], data.Data.Span);
        Check.Equal(2L, data.Metadata.GetInt64("revision"));
    }

    private static async Task ValidationAsync()
    {
        using var fixture = new Fixture(false);
        fixture.Put("item", [1]);
        foreach (string invalid in new[] { "bad{", "bad}", "{tag", "tag}", "{}", "{unknown}", "{Tag}", "{YYYY}", "{{tag}}", "{tag{tag}}" })
        {
            Check.Throws<ArgumentException>(() => PackageNameTemplate.Validate(invalid));
            WriteConfig(fixture, new Package(invalid, "./input", "assets"));
            try { _ = fixture.Config(); throw new Exception("Invalid package template was accepted."); }
            catch (InvalidDataException error)
            {
                string[] location = error.Message.Split(':', 3);
                Check.Equal("quire.project.yml", location[0]);
                Check.True(location.Length == 3 && int.TryParse(location[1], NumberStyles.None, CultureInfo.InvariantCulture, out int line) && line > 0);
            }
        }
        foreach (string invalid in new[] { "", "../escape", "a/b", "a\\b", "a:b", "name.", "name ", "bad\tname", "CON", "NUL.txt", "COM\u00b9" })
        {
            WriteConfig(fixture, new Package(invalid, "./input", "assets"));
            Check.Throws<InvalidDataException>(() => fixture.Config());
        }
        WriteConfig(fixture, new("Patch_{tag}", "./input", "one"), new("patch_release", "./input", "two"));
        Check.Equal(2, fixture.Config().Packages.Length);
        await Check.ThrowsAsync<InvalidDataException>(() => Run(fixture, "package", "-tag", "release"));
        Check.Equal(0, TagStore.List(fixture.Config().StateDirectory).Count());
        if (Directory.Exists(fixture.Output)) Check.Equal(0, Directory.GetFiles(fixture.Output, "*.asset", SearchOption.AllDirectories).Length);
        foreach (string tooLong in new[] { new string('x', 250), new string('\u4e2d', 84) })
        {
            WriteConfig(fixture, new Package(tooLong, "./input", "assets"));
            Check.Equal(tooLong, fixture.Config().Packages.Single().Name);
            await Check.ThrowsAsync<ArgumentException>(() => Run(fixture, "package", "-tag", "long-name"));
            Check.Equal(0, TagStore.List(fixture.Config().StateDirectory).Count());
        }
        // 249 UTF-8 bytes plus ".asset" fits the actual filename limit exactly.
        // Temporary output names must not extend either ASCII or multibyte names.
        byte content = 1;
        foreach (string boundary in new[] { new string('x', 249), new string('\u4e2d', 83) })
        {
            fixture.Put("item", [content]);
            WriteConfig(fixture, new Package(boundary, "./input", "assets"));
            var valid = await Build(fixture, "boundary-" + content);
            Check.Equal(255, AssetPath.Utf8.GetByteCount(Path.GetFileName(valid.Outputs.Single())));
            TagStore.Commit(fixture.Config().StateDirectory, valid.TagId);
            Check.Equal(boundary, TagStore.LoadBaseline(fixture.Config().StateDirectory).Files.Values.Single().Package);
            using var bundle = QuireBundle.Open(Path.Combine(fixture.Output, valid.Name));
            using var data = bundle.Read("assets/item");
            Check.Bytes([content], data.Data.Span);
            Check.Equal(0, Directory.GetFiles(fixture.Root, "*.tmp", SearchOption.AllDirectories).Length);
            content++;
        }
    }

    private static async Task TemporaryOutputAsync()
    {
        using var fixture = new Fixture(false);
        fixture.Put("item", [1, 2, 3]);
        var source = (await SourceScanner.ScanAsync(fixture.Config(), 1, default)).Single();
        Directory.CreateDirectory(fixture.Output);
        string destination = Path.Combine(fixture.Output, "main.asset");
        string unrelated = destination + ".tmp";
        File.WriteAllBytes(unrelated, [9]);
        string occupiedScratch = Path.Combine(fixture.Root, "occupied-scratch");
        Directory.CreateDirectory(occupiedScratch);
        string occupiedTable = Path.Combine(occupiedScratch, "blocks.bin");
        File.WriteAllBytes(occupiedTable, [8]);
        await Check.ThrowsAsync<IOException>(() => Write(occupiedScratch));
        Check.True(!File.Exists(destination));
        Check.Bytes([8], File.ReadAllBytes(occupiedTable));
        AssertTemporaryFiles();

        await Write(Path.Combine(fixture.Root, "success-scratch"));
        byte[] original = File.ReadAllBytes(destination);
        using (var bundle = QuireBundle.Open(fixture.Output))
        using (var data = bundle.Read(source.Path)) Check.Bytes([1, 2, 3], data.Data.Span);
        AssertTemporaryFiles();
        await Check.ThrowsAsync<IOException>(() => Write(Path.Combine(fixture.Root, "conflict-scratch")));
        Check.Bytes(original, File.ReadAllBytes(destination));
        AssertTemporaryFiles();

        Task Write(string scratch) => PackageWriter.WriteAsync(destination, scratch, "1234abcd", DateTime.UtcNow.Ticks,
            [new(source, EntryKind.Content)], null, fixture.BuildOptions, default);
        void AssertTemporaryFiles()
        {
            Check.Bytes([9], File.ReadAllBytes(unrelated));
            Check.True(Directory.GetFiles(fixture.Output, "*.tmp").SequenceEqual(new[] { unrelated }));
        }
    }

    private static async Task PrecedenceAsync()
    {
        using var fixture = new Fixture(false);
        fixture.Put("middle/same", [1]);
        fixture.Put("winner/same", [9]);
        WriteConfig(fixture, new("middle", "./input/middle", "assets"), new("{tag}_winner", "./input/winner", "assets"));
        var a = await Build(fixture, "A"); // A_winner sorts before middle; the original {tag}_winner sorts after it.
        Check.Equal(1, a.ContentChanges);
        Check.Equal("A_winner.asset", Path.GetFileName(a.Outputs.Single()));
        using (var bundle = QuireBundle.Open(fixture.Output))
        using (var data = bundle.Read("assets/same")) Check.Bytes([9], data.Data.Span);
        string state = fixture.Config().StateDirectory;
        TagStore.Commit(state, a.TagId);
        Check.True((await Run(fixture, "diff")).Contains("0 changes", StringComparison.Ordinal));
        var b = await Build(fixture, "zz"); // Expanded order reverses again, but logical resource precedence must not.
        Check.Equal(0, b.Outputs.Length);
        TagStore.Commit(state, b.TagId);
        fixture.Put("middle/same", [2]);
        var c = await Build(fixture, "loser-changed");
        Check.Equal(0, c.Outputs.Length);
        TagStore.Commit(state, c.TagId);
        fixture.Put("winner/same", [10]);
        var d = await Build(fixture, "latest");
        Check.Equal(1, d.ContentChanges);
        Check.Equal("latest_winner.asset", Path.GetFileName(d.Outputs.Single()));
        using var final = QuireBundle.Open(fixture.Output, new ReadOptions { VerifyPackageChain = true });
        using var changed = final.Read("assets/same");
        Check.Bytes([10], changed.Data.Span);
    }

    private sealed record Package(string Name, string Directory, string Prefix);
    private static void WriteConfig(Fixture fixture, params Package[] packages)
    {
        var yaml = new StringBuilder("version: 1\nproject: test-project\noutput: ./dist/{tag}\ncompression: none\nbuild:\n  jobs: 1\nsecret-key: none\npackages:\n");
        foreach (var package in packages)
            yaml.Append("  - name: '").Append(package.Name.Replace("'", "''", StringComparison.Ordinal)).Append("'\n    sources:\n      - directory: ")
                .Append(package.Directory).Append("\n        prefix: ").Append(package.Prefix).Append('\n');
        File.WriteAllText(fixture.ProjectPath, yaml.ToString());
    }
    private static async Task<BuildTag> Build(Fixture fixture, string? name)
    {
        await Run(fixture, name is null ? ["package"] : ["package", "-tag", name]);
        return TagStore.List(fixture.Config().StateDirectory).OrderBy(tag => tag.BuildTimeUtc).Last();
    }
    private static async Task<string> Run(Fixture fixture, params string[] arguments)
    {
        var previous = Console.Out;
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        try
        {
            Console.SetOut(output);
            Check.Equal(0, await CommandLine.RunAsync([.. arguments, "-f", fixture.ProjectPath], default));
            return output.ToString();
        }
        finally { Console.SetOut(previous); }
    }
    private static Dictionary<string, byte[]> Snapshot(string root) => Directory.GetFiles(root, "*", SearchOption.AllDirectories)
        .ToDictionary(file => Path.GetRelativePath(root, file), File.ReadAllBytes, StringComparer.Ordinal);
    private static void AssertSnapshot(Dictionary<string, byte[]> expected, Dictionary<string, byte[]> actual)
    {
        Check.True(expected.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(actual.Keys));
        foreach (var pair in expected) Check.Bytes(pair.Value, actual[pair.Key]);
    }
}
