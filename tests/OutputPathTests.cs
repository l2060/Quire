using System.Globalization;
using System.Text;
using Quire;
using Quire.Cli;
using Quire.Cli.Build;
using Quire.Cli.Configuration;
using Quire.Cli.Sources;
using Quire.Cli.Tags;
using Quire.Internal;

internal static class OutputPathTests
{
    internal static void Register(Action<string, Func<Task>> test)
    {
        test("Plain output writes packages directly and permits an unchanged candidate", FlatOutputAsync);
        test("Output and package templates share all variables and one frozen build timestamp", VariablesAsync);
        test("Flat output supports multiple Tags with dynamic filenames and preserves unrelated files", FlatHistoryAsync);
        test("Existing destination files and directories reject publication without overwriting", ConflictsAsync);
        test("Source-root scans exclude historical template outputs without excluding neighboring sources", HistoricalOutputsAsync);
        test("Canceled and failed publication clean new artifacts while preserving older outputs", CleanupAsync);
        test("Absolute output paths support direct and template destinations", AbsolutePathsAsync);
        test("Output templates reject malformed variables, unsafe paths and unsafe expansion", ValidationAsync);
    }

    private static async Task FlatOutputAsync()
    {
        using var fixture = new Fixture(false);
        WriteConfig(fixture, "./dist");
        fixture.Put("item", [1, 2, 3]);
        var project = fixture.Config();
        Check.Equal(fixture.Output, project.Output);
        var first = await Build(fixture, "release");
        Check.Equal("dist/main.asset", first.Outputs.Single());
        Check.True(File.Exists(Path.Combine(fixture.Output, "main.asset")));
        Check.Equal(0, Directory.GetDirectories(fixture.Output).Length);
        using (var bundle = QuireBundle.Open(fixture.Output))
        {
            Check.Equal("release", bundle.LatestTag!.Name);
            using var data = bundle.Read("assets/item");
            Check.Bytes([1, 2, 3], data.Data.Span);
        }
        TagStore.Commit(project.StateDirectory, first.TagId);
        var unchanged = await Build(fixture, "unchanged");
        Check.Equal(0, unchanged.Outputs.Length);
        Check.Equal(0, Directory.GetDirectories(fixture.Output).Length);
        byte[] original = File.ReadAllBytes(Path.Combine(fixture.Output, "main.asset"));
        fixture.Put("item", [4]);
        await Check.ThrowsAsync<IOException>(() => Build(fixture, "conflicting"));
        Check.Bytes(original, File.ReadAllBytes(Path.Combine(fixture.Output, "main.asset")));
        Check.Equal(2, TagStore.List(project.StateDirectory).Count());
        Check.Equal(first.TagId, TagStore.LoadBaseline(project.StateDirectory).TagId);
        AssertNoTemporaryFiles(fixture);
    }

    private static async Task VariablesAsync()
    {
        using var fixture = new Fixture(false);
        const string variables = "{tag}_{tagid}_{yyyy}_{MM}_{dd}_{HH}_{mm}_{ss}_{fff}_{date}_{time}_{timestamp}";
        WriteConfig(fixture, "./dist/" + variables, "Pack_" + variables);
        var project = fixture.Config();
        DateTime localClock = new(2031, 7, 9, 13, 45, 56, 789, DateTimeKind.Unspecified);
        var instant = new DateTimeOffset(localClock, TimeZoneInfo.Local.GetUtcOffset(localClock)).ToUniversalTime();
        string value = "release_abcdef12_2031_07_09_13_45_56_789_20310709_134556_" + instant.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var previousCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ar-SA");
            var resolved = project.ResolveBuildOutput("release", "abcdef12", instant);
            Check.Equal(Path.Combine(fixture.Output, value), resolved.Output);
            Check.Equal("Pack_" + value, resolved.Packages.Single().Name);
            Check.Equal(fixture.Output, resolved.OutputRoot);
            Check.Equal(fixture.Output, project.Output);
            Check.True(project.OutputTemplate.EndsWith(variables, StringComparison.Ordinal));
        }
        finally { CultureInfo.CurrentCulture = previousCulture; }

        const string output = "./dist/{date}/{tag}_{tagid}_{time}_{fff}";
        const string package = "Pack_{tag}_{tagid}_{date}_{time}_{fff}";
        WriteConfig(fixture, output, package);
        fixture.Put("item", [7]);
        var tag = await Build(fixture, "release_{yyyy}");
        var clock = tag.BuildTimeUtc.ToLocalTime();
        string date = clock.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        string time = clock.ToString("HHmmss_fff", CultureInfo.InvariantCulture);
        string directory = $"{tag.Name}_{tag.TagId}_{time}";
        string filename = $"Pack_{tag.Name}_{tag.TagId}_{date}_{time}.asset";
        Check.Equal($"dist/{date}/{directory}/{filename}", tag.Outputs.Single());
        byte[] bytes = File.ReadAllBytes(Path.Combine(fixture.Root, tag.Outputs.Single()));
        var header = PackageHeader.Decode(bytes, bytes.Length);
        Check.Equal(tag.BuildTimeUtc.UtcTicks, header.BuildTicks);
        Check.Equal(tag.TagId, header.TagId);
        Check.Equal(tag.Name, header.TagName);
        Check.True(!Directory.Exists(Path.Combine(fixture.Output, tag.Name)));
    }

    private static async Task FlatHistoryAsync()
    {
        using var fixture = new Fixture(false);
        WriteConfig(fixture, "./dist", "Patch_{tag}");
        fixture.Put("changed", [1]);
        fixture.Put("gone", [2]);
        fixture.Put("keep", [3]);
        Directory.CreateDirectory(fixture.Output);
        string unrelated = Path.Combine(fixture.Output, "notes.txt");
        File.WriteAllText(unrelated, "keep me");
        var first = await Build(fixture, "first");
        byte[] firstBytes = File.ReadAllBytes(Path.Combine(fixture.Root, first.Outputs.Single()));
        TagStore.Commit(fixture.Config().StateDirectory, first.TagId);
        fixture.Put("changed", [9]);
        File.Delete(Path.Combine(fixture.Input, "gone"));
        var second = await Build(fixture, "second");
        Check.Equal("dist/Patch_first.asset", first.Outputs.Single());
        Check.Equal("dist/Patch_second.asset", second.Outputs.Single());
        Check.Bytes(firstBytes, File.ReadAllBytes(Path.Combine(fixture.Root, first.Outputs.Single())));
        Check.Equal("keep me", File.ReadAllText(unrelated));
        Check.Equal(0, Directory.GetDirectories(fixture.Output).Length);
        using var bundle = await QuireBundle.OpenAsync(fixture.Output, new ReadOptions { VerifyPackageChain = true });
        Check.Equal(2, bundle.Packages.Count);
        Check.Equal("second", bundle.LatestTag!.Name);
        Check.True(!bundle.FileExists("assets/gone"));
        using var changed = bundle.Read("assets/changed");
        Check.Bytes([9], changed.Data.Span);
        using var keep = bundle.Read("assets/keep");
        Check.Bytes([3], keep.Data.Span);
    }

    private static async Task ConflictsAsync()
    {
        foreach (bool directoryConflict in new[] { false, true })
        {
            using var fixture = new Fixture(false);
            WriteConfig(fixture, "./dist", "A", additionalPackage: "B");
            fixture.Put("item", [1]);
            Directory.CreateDirectory(fixture.Output);
            string conflict = Path.Combine(fixture.Output, "B.asset");
            string preserved;
            if (directoryConflict)
            {
                Directory.CreateDirectory(conflict);
                preserved = Path.Combine(conflict, "keep.txt");
            }
            else preserved = conflict;
            File.WriteAllText(preserved, "original");
            File.WriteAllText(Path.Combine(fixture.Output, "unrelated.txt"), "unrelated");
            await Check.ThrowsAsync<IOException>(() => Build(fixture, "blocked"));
            Check.Equal("original", File.ReadAllText(preserved));
            Check.Equal("unrelated", File.ReadAllText(Path.Combine(fixture.Output, "unrelated.txt")));
            Check.True(!File.Exists(Path.Combine(fixture.Output, "A.asset")));
            Check.Equal(0, TagStore.List(fixture.Config().StateDirectory).Count());
            Check.True(!File.Exists(Path.Combine(fixture.Config().StateDirectory, "baseline.idx")));
            AssertNoTemporaryFiles(fixture);
        }
    }

    private static async Task HistoricalOutputsAsync()
    {
        foreach (string output in new[] { "./dist/{date}/{tag}", "./dist-{tag}" })
        {
            using var fixture = new Fixture(false);
            WriteConfig(fixture, output, source: "./");
            fixture.Put("item", [1]);
            string historical = output.Contains("dist/", StringComparison.Ordinal)
                ? Path.Combine(fixture.Output, "20000101", "old") : Path.Combine(fixture.Root, "dist-old");
            Directory.CreateDirectory(historical);
            File.WriteAllText(Path.Combine(historical, "old.asset"), "historical package");
            File.WriteAllText(Path.Combine(historical, "not-a-package.txt"), "historical auxiliary file");
            string neighbor = Path.Combine(fixture.Root, "distribution");
            Directory.CreateDirectory(neighbor);
            File.WriteAllText(Path.Combine(neighbor, "source.txt"), "include me");
            var project = fixture.Config();
            var sources = await SourceScanner.ScanAsync(project, 2, default);
            Check.True(sources.Select(file => file.Path).ToHashSet(StringComparer.Ordinal)
                .SetEquals(["assets/input/item", "assets/distribution/source.txt"]));
            var first = await Build(fixture, "first");
            TagStore.Commit(project.StateDirectory, first.TagId);
            var second = await Build(fixture, "second");
            Check.Equal(0, second.Outputs.Length);
            Check.Equal(0, (await ProjectBuilder.PlanAsync(fixture.Config(), fixture.BuildOptions, default)).Changes.Length);
            Check.Equal("historical package", File.ReadAllText(Path.Combine(historical, "old.asset")));
            AssertNoTemporaryFiles(fixture);
        }
    }

    private static async Task CleanupAsync()
    {
        using var fixture = new Fixture(false);
        WriteConfig(fixture, "./dist", "A_{tag}", additionalPackage: "B_{tag}");
        fixture.Put("item", new byte[131073]);
        var first = await Build(fixture, "first");
        string state = fixture.Config().StateDirectory;
        TagStore.Commit(state, first.TagId);
        var originals = first.Outputs.ToDictionary(path => Path.Combine(fixture.Root, path), path => File.ReadAllBytes(Path.Combine(fixture.Root, path)));
        File.WriteAllText(Path.Combine(fixture.Output, "notes.txt"), "preserve");
        fixture.Put("item", [2, 3]);
        using var cancellation = new CancellationTokenSource();
        bool canceledDuringVerification = false;
        var cancelProgress = new StageAction(stage =>
        {
            if (stage != "Verifying") return;
            canceledDuringVerification = true;
            cancellation.Cancel();
        });
        await Check.ThrowsAsync<OperationCanceledException>(() => ProjectBuilder.BuildAsync(fixture.Config(), "canceled", fixture.BuildOptions, cancellation.Token, cancelProgress));
        Check.True(canceledDuringVerification);
        Check.True(!File.Exists(Path.Combine(fixture.Output, "A_canceled.asset")));
        Check.True(!File.Exists(Path.Combine(fixture.Output, "B_canceled.asset")));
        AssertNoTemporaryFiles(fixture);

        string? blocker = null;
        var failProgress = new StageAction(stage =>
        {
            if (stage != "Publishing") return;
            blocker = Path.Combine(fixture.Output, "B_failed.asset");
            Directory.CreateDirectory(blocker);
        });
        try
        {
            await Check.ThrowsAsync<IOException>(() => ProjectBuilder.BuildAsync(fixture.Config(), "failed", fixture.BuildOptions, default, failProgress));
            Check.True(blocker is not null);
            Check.True(!File.Exists(Path.Combine(fixture.Output, "A_failed.asset")));
            Check.True(Directory.Exists(blocker!));
        }
        finally { if (blocker is not null) Directory.Delete(blocker); }
        foreach (var original in originals) Check.Bytes(original.Value, File.ReadAllBytes(original.Key));
        Check.Equal("preserve", File.ReadAllText(Path.Combine(fixture.Output, "notes.txt")));
        Check.Equal(1, TagStore.List(state).Count());
        Check.Equal(first.TagId, TagStore.LoadBaseline(state).TagId);
        Check.Equal(0, Directory.GetFiles(Path.Combine(state, "pending")).Length);
        AssertNoTemporaryFiles(fixture);
    }

    private static async Task AbsolutePathsAsync()
    {
        foreach (bool template in new[] { false, true })
        {
            using var fixture = new Fixture(false);
            string destination = Path.Combine(fixture.Root, "absolute output");
            WriteConfig(fixture, template ? Path.Combine(destination, "{tag}") : destination);
            fixture.Put("item", [1]);
            var tag = await Build(fixture, "release");
            string expected = Path.Combine(template ? Path.Combine(destination, tag.Name) : destination, "main.asset");
            Check.Equal(expected, Path.GetFullPath(tag.Outputs.Single(), fixture.Root));
            Check.True(File.Exists(expected));
            Check.Equal(destination, fixture.Config().OutputRoot);
        }
    }

    private static async Task ValidationAsync()
    {
        using var fixture = new Fixture(false);
        foreach (string invalid in new[] { "./dist/{unknown}", "./dist/{Tag}", "./dist/{", "./dist/}", "./dist/{}", "./dist/{{tag}}", "./.quire/{tag}", "./dist/../.quire/{tag}", "./", "./input" })
        {
            WriteConfig(fixture, invalid);
            Check.Throws<InvalidDataException>(() => fixture.Config());
        }
        WriteConfig(fixture, "./{tag}", source: "./");
        Check.Throws<InvalidDataException>(() => fixture.Config());
        WriteConfig(fixture, "./dist/AU{tag}");
        var project = fixture.Config();
        Check.Throws<ArgumentException>(() => project.ResolveBuildOutput("x", "abcdef12", DateTimeOffset.UtcNow));
        await Check.ThrowsAsync<ArgumentException>(() => Build(fixture, "x"));
        Check.Equal(0, TagStore.List(project.StateDirectory).Count());
        WriteConfig(fixture, "./dist/{tag}");
        foreach (string unsafeTag in new[] { "../escape", "a/b", "a\\b", "..", "NUL" })
            await Check.ThrowsAsync<ArgumentException>(() => Build(fixture, unsafeTag));
        Check.Equal(0, TagStore.List(project.StateDirectory).Count());
        Check.True(!Directory.Exists(Path.Combine(fixture.Root, "escape")));
    }

    private static void WriteConfig(Fixture fixture, string output, string package = "main", string source = "./input", string? additionalPackage = null)
    {
        var yaml = new StringBuilder("version: 1\nproject: test-project\noutput: ").Append(Quote(output)).Append("\ncompression: none\nbuild:\n  jobs: 1\npackages:\n");
        Add(package, "assets");
        if (additionalPackage is not null) Add(additionalPackage, "other");
        File.WriteAllText(fixture.ProjectPath, yaml.ToString());
        void Add(string name, string prefix) => yaml.Append("  - name: ").Append(Quote(name)).Append("\n    sources:\n      - directory: ")
            .Append(Quote(source)).Append("\n        prefix: ").Append(prefix).Append('\n');
    }

    private static string Quote(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    private static async Task<BuildTag> Build(Fixture fixture, string name)
    {
        var previous = Console.Out;
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        try
        {
            Console.SetOut(output);
            Check.Equal(0, await CommandLine.RunAsync(["package", "-f", fixture.ProjectPath, "-tag", name], default));
        }
        finally { Console.SetOut(previous); }
        return TagStore.List(fixture.Config().StateDirectory).Single(tag => tag.Name == name);
    }

    private static void AssertNoTemporaryFiles(Fixture fixture)
    {
        Check.Equal(0, Directory.GetFileSystemEntries(fixture.Root, "*.tmp", SearchOption.AllDirectories).Length);
        string scratch = Path.Combine(fixture.Config().StateDirectory, "temp");
        if (Directory.Exists(scratch)) Check.Equal(0, Directory.GetFileSystemEntries(scratch).Length);
    }

    private sealed class StageAction(Action<string> action) : IOperationProgress
    {
        public void StartStage(string stage, long totalBytes = -1, long totalFiles = -1) => action(stage);
        public void Advance(long bytes = 0, long files = 0) { }
        public void AddRead(long bytes) { }
        public void AddWritten(long bytes) { }
    }
}
