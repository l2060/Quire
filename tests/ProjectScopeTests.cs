using System.Globalization;
using System.Text.Json;
using Quire.Cli;
using Quire.Cli.Configuration;
using Quire.Cli.Sources;
using Quire.Cli.Tags;

internal static class ProjectScopeTests
{
    internal static void Register(Action<string, Func<Task>> test)
    {
        test("named projects isolate baselines, matching Tag names and all history commands", IsolationAsync);
        test("alternate and renamed configurations share their named project baseline", SharedConfigurationAsync);
        test("named project history works without source files or publishing keys", OfflineHistoryAsync);
        test("source scans exclude every project state and reject state directory sources", StateExclusionAsync);
        test("project names are required portable directory components", NamesAsync);
        test("init creates multiple projects in one directory and emits structured results", InitializeAsync);
    }

    private static async Task IsolationAsync()
    {
        using var f = new Fixture(false);
        f.Put("a/only-a.bin", [1]);
        f.Put("b/only-b.bin", [2]);
        string a = WriteConfig(f, "a.yml", "game-a", "./input/a");
        string b = WriteConfig(f, "b.yml", "game-b", "./input/b");
        string stateA = ProjectConfig.Load(a).StateDirectory, stateB = ProjectConfig.Load(b).StateDirectory;
        var firstA = await Build(a, "v1");
        var firstB = await Build(b, "v1");
        Check.True(firstA.BaseTag is null && firstB.BaseTag is null);
        await Run("commit", "v1", "-f", a);
        await Run("commit", "v1", "-f", b);
        Check.Equal(firstA.TagId, TagStore.LoadBaseline(stateA).TagId);
        Check.Equal(firstB.TagId, TagStore.LoadBaseline(stateB).TagId);
        Check.Equal("assets/only-a.bin", TagStore.LoadBaseline(stateA).Files.Values.Single().Path);
        Check.Equal("assets/only-b.bin", TagStore.LoadBaseline(stateB).Files.Values.Single().Path);
        byte[] baselineB = File.ReadAllBytes(Path.Combine(stateB, "baseline.idx"));

        f.Put("a/only-a.bin", [3]);
        string changesA = await Run("diff", "-f", a);
        Check.True(changesA.Contains("1 changes; 0 added; 1 updated; 0 deleted.", StringComparison.Ordinal));
        Check.True((await Run("diff", "-f", b)).Contains("0 changes", StringComparison.Ordinal));
        var nextA = await Build(a, "only-a-release");
        Check.Equal(firstA.TagId, nextA.BaseTag);
        Check.Equal(1, nextA.UpdatedFiles);
        await Run("commit", nextA.Name, "-f", a);
        Check.Bytes(baselineB, File.ReadAllBytes(Path.Combine(stateB, "baseline.idx")));
        Check.True((await Run("tags", "-f", a)).Contains("only-a-release", StringComparison.Ordinal));
        Check.True(!(await Run("tags", "-f", b)).Contains("only-a-release", StringComparison.Ordinal));
        Check.True((await Run("changes", "v1", "-f", a)).Contains("assets/only-a.bin", StringComparison.Ordinal));
        Check.True((await Run("changes", "v1", "-f", b)).Contains("assets/only-b.bin", StringComparison.Ordinal));
        await Check.ThrowsAsync<FileNotFoundException>(() => Run("changes", nextA.Name, "-f", b));

        var pendingA = await Build(a, "waiting");
        var pendingB = await Build(b, "waiting");
        await Run("remove", "waiting", "-f", a);
        Check.True(TagStore.List(stateA).All(tag => tag.TagId != pendingA.TagId));
        Check.Equal(pendingB.TagId, TagStore.Resolve(stateB, "waiting"));
        Check.Equal(firstB.TagId, TagStore.LoadBaseline(stateB).TagId);

    }

    private static async Task SharedConfigurationAsync()
    {
        using var f = new Fixture(false);
        f.Put("resource.bin", [1]);
        string full = WriteConfig(f, "full.yml", "shared-game", "./input");
        string patch = WriteConfig(f, "patch.yml", "shared-game", "./input", package: "Patch");
        var first = await Build(full, "v1");
        await Run("commit", "v1", "-f", patch);
        Check.True((await Run("diff", "-f", patch)).Contains("0 changes", StringComparison.Ordinal));
        string renamed = Path.Combine(f.Root, "renamed.yml");
        File.Move(patch, renamed);
        var unchanged = await Build(renamed, "settings-only");
        Check.Equal(first.TagId, unchanged.BaseTag);
        Check.Equal(0, unchanged.ContentChanges + unchanged.MetadataChanges + unchanged.DeletedChanges);
        Check.Equal(0, unchanged.Outputs.Length);
        await Run("commit", unchanged.Name, "-f", full);
        f.Put("resource.bin", [2]);
        var changed = await Build(renamed, "v2");
        Check.Equal(unchanged.TagId, changed.BaseTag);
        Check.Equal(1, changed.UpdatedFiles);
        Check.True(changed.Outputs.Single().EndsWith("/Patch.asset", StringComparison.Ordinal));
        await Run("commit", "v2", "-f", full);
        Check.True((await Run("diff", "-f", full)).Contains("0 changes", StringComparison.Ordinal));
        Check.Equal(changed.TagId, TagStore.LoadBaseline(ProjectConfig.LoadStateDirectory(renamed)).TagId);
    }

    private static async Task OfflineHistoryAsync()
    {
        using var f = new Fixture(false, true);
        f.Put("resource.bin", [1]);
        string config = WriteConfig(f, "offline.yml", "offline-game", "./input", key: f.KeyName);
        string state = ProjectConfig.Load(config).StateDirectory;
        await Build(config, "v1");
        await Run("commit", "v1", "-f", config);
        f.Put("resource.bin", [2]);
        var accepted = await Build(config, "accepted");
        f.Put("resource.bin", [3]);
        var discarded = await Build(config, "discarded");
        Directory.Move(f.Input, Path.Combine(f.Root, "offline-input"));
        f.RemoveKeys();
        Check.Equal(state, ProjectConfig.LoadStateDirectory(config));
        Check.True((await Run("tags", "-f", config)).Contains("accepted", StringComparison.Ordinal));
        Check.True((await Run("changes", "accepted", "-f", config)).Contains("assets/resource.bin", StringComparison.Ordinal));
        await Run("commit", "accepted", "-f", config);
        Check.Equal(accepted.TagId, TagStore.LoadBaseline(state).TagId);
        await Run("remove", "discarded", "-f", config);
        Check.True(TagStore.List(state).All(tag => tag.TagId != discarded.TagId));
        Check.True(discarded.Outputs.All(output => File.Exists(Path.Combine(f.Root, output))));

        byte[] baseline = File.ReadAllBytes(Path.Combine(state, "baseline.idx"));
        File.WriteAllText(config, "project: [broken YAML\n");
        await Check.ThrowsAsync<InvalidDataException>(() => Run("tags", "-f", config));
        Check.Bytes(baseline, File.ReadAllBytes(Path.Combine(state, "baseline.idx")));
    }

    private static async Task StateExclusionAsync()
    {
        using var f = new Fixture(false);
        File.WriteAllBytes(Path.Combine(f.Root, "resource.bin"), [1]);
        string config = WriteConfig(f, "scan.yml", "scanner", "./");
        File.AppendAllText(config, "        include:\n          - \"**/*.bin\"\n");
        string stateRoot = Path.Combine(f.Root, ".quire");
        foreach (string directory in new[] { stateRoot, Path.Combine(stateRoot, "scanner"), Path.Combine(stateRoot, "other-game") })
        {
            Directory.CreateDirectory(directory);
            File.WriteAllBytes(Path.Combine(directory, "internal.bin"), [9]);
        }
        var project = ProjectConfig.Load(config);
        Check.Equal(stateRoot, project.StateRootDirectory);
        var files = await SourceScanner.ScanAsync(project, 2, default);
        Check.Equal(1, files.Length);
        Check.Equal("assets/resource.bin", files.Single().Path);
        foreach (string source in new[] { "./.quire", "./.quire/scanner", "./.quire/other-game" })
        {
            WriteConfig(f, "scan.yml", "scanner", source);
            Check.Throws<InvalidDataException>(() => ProjectConfig.Load(config));
        }
    }

    private static async Task NamesAsync()
    {
        using var f = new Fixture(false);
        string original = File.ReadAllText(f.ProjectPath).Replace("project: test-project\n", "", StringComparison.Ordinal);
        File.WriteAllText(f.ProjectPath, original);
        Check.Throws<InvalidDataException>(() => f.Config());
        Check.Throws<InvalidDataException>(() => ProjectConfig.LoadStateDirectory(f.ProjectPath));
        foreach (string command in new[] { "package", "diff", "tags", "changes", "commit", "remove" })
        {
            string[] arguments = command is "changes" or "commit" or "remove"
                ? [command, "v1", "-f", f.ProjectPath] : [command, "-f", f.ProjectPath];
            await Check.ThrowsAsync<InvalidDataException>(() => Run(arguments));
        }
        Check.True(!Directory.Exists(Path.Combine(f.Root, ".quire")));
        foreach (string name in new[] { "game-a", "game_a.2026", "游戏项目", "tags", "PENDING", "temp", "baseline.idx", "paths.idx", new string('a', 128), new string('界', 80) })
        {
            File.WriteAllText(f.ProjectPath, "project: '" + name + "'\n" + original);
            var project = f.Config();
            Check.Equal(name, project.ProjectName);
            Check.Equal(Path.Combine(f.Root, ".quire", name), project.StateDirectory);
            Check.Equal(project.StateDirectory, ProjectConfig.LoadStateDirectory(f.ProjectPath));
        }
        foreach (string name in new[]
        {
            "", " ", " a", "a ", ".", "..", ".hidden", "-flag", "../escape", "a/b", "a\\b", "C:drive", "a?b",
            "trailing.", "CON", "con.txt", "LPT1", "game-{tag}", "game}",
            new string('a', 129), new string('界', 81)
        })
        {
            File.WriteAllText(f.ProjectPath, "project: '" + name + "'\n" + original);
            Check.Throws<InvalidDataException>(() => f.Config());
            Check.Throws<InvalidDataException>(() => ProjectConfig.LoadStateDirectory(f.ProjectPath));
        }
        File.WriteAllText(f.ProjectPath, "project:\n  name: nested\n" + original);
        Check.Throws<InvalidDataException>(() => f.Config());
    }

    private static async Task InitializeAsync()
    {
        using var f = new Fixture(false);
        string root = Path.Combine(f.Root, "projects");
        await Run("init", "game-a", root);
        string firstPath = Path.Combine(root, "quire.game-a.yml");
        string firstContents = File.ReadAllText(firstPath);
        const string name = "游戏's # b";
        string result = await Run("init", name, root, "-progress");
        string secondPath = Path.Combine(root, $"quire.{name}.yml");
        var first = ProjectConfig.Load(firstPath);
        var second = ProjectConfig.Load(secondPath);
        Check.Equal(name, second.ProjectName);
        Check.Equal(Path.Combine(root, ".quire", name), second.StateDirectory);
        Check.Equal(Path.Combine(root, "dist", name), second.Output);
        Check.Equal(firstContents, File.ReadAllText(firstPath));
        Check.True(!first.Packages.SelectMany(p => p.Sources).Select(s => s.Directory)
            .Intersect(second.Packages.SelectMany(p => p.Sources).Select(s => s.Directory), StringComparer.OrdinalIgnoreCase).Any());
        foreach (var source in second.Packages.SelectMany(p => p.Sources))
            Check.True(ProjectConfig.Within(source.Directory, Path.Combine(root, "assets", name)));
        string[] lines = result.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        using var started = JsonDocument.Parse(lines[0]);
        using var completed = JsonDocument.Parse(lines[^1]);
        Check.Equal("started", started.RootElement.GetProperty("type").GetString());
        Check.Equal("completed", completed.RootElement.GetProperty("type").GetString());
        Check.Equal(secondPath, completed.RootElement.GetProperty("result").GetProperty("path").GetString());
        await Check.ThrowsAsync<IOException>(() => Run("init", name, root));
        string invalidRoot = Path.Combine(f.Root, "invalid-project");
        await Check.ThrowsAsync<ArgumentException>(() => Run("init", "../escape", invalidRoot));
        Check.True(!Directory.Exists(invalidRoot));
    }

    private static string WriteConfig(Fixture fixture, string fileName, string name, string source, string package = "main", string key = "none")
    {
        string path = Path.Combine(fixture.Root, fileName);
        File.WriteAllText(path, $$"""
            version: 1
            project: {{name}}
            output: ./dist/{{name}}/{tag}
            compression: none
            secret-key: {{key}}
            build:
              jobs: 2
            packages:
              - name: {{package}}
                sources:
                  - directory: {{source}}
                    prefix: assets

            """);
        return path;
    }

    private static async Task<BuildTag> Build(string config, string tag)
    {
        await Run("package", "-f", config, "-tag", tag);
        return TagStore.List(ProjectConfig.LoadStateDirectory(config)).Single(item => item.Name == tag);
    }

    private static async Task<string> Run(params string[] arguments)
    {
        TextWriter previous = Console.Out;
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        try
        {
            Console.SetOut(output);
            Check.Equal(0, await CommandLine.RunAsync(arguments, default));
            return output.ToString();
        }
        finally { Console.SetOut(previous); }
    }
}
