using System.Globalization;
using System.Text.Json;
using Quire;
using Quire.Cli;
using Quire.Cli.Configuration;
using Quire.Cli.Sources;
using Quire.Cli.Tags;

internal static class SourceRootTests
{
    internal static void Register(Action<string, Func<Task>> test)
    {
        test("source root override preserves baselines across machines and builds only logical changes", RelocationAsync);
        test("source roots rebase relative sources only and resolve CLI paths against the working directory", ResolutionAsync);
        test("source root options reject invalid directories, aliases and unrelated commands", ValidationAsync);
    }

    private static async Task RelocationAsync()
    {
        using var f = new Fixture(false);
        string repository = Path.Combine(f.Root, "repository");
        Directory.CreateDirectory(repository);
        string config = Path.Combine(repository, "quire.game.yml");
        File.WriteAllText(config, """
            version: 1
            project: relocation
            output: ./dist/{tag}
            compression: none
            build:
              jobs: 2
            packages:
              - name: Assets
                sources:
                  - directory: ./input
                    prefix: assets
            """);
        byte[] configurationBytes = File.ReadAllBytes(config);
        string machineA = Path.Combine(f.Root, "machine-a"), machineB = Path.Combine(f.Root, "machine-b");
        foreach (string root in new[] { machineA, machineB })
        {
            string input = Path.Combine(root, "input");
            Directory.CreateDirectory(input);
            File.WriteAllBytes(Path.Combine(input, "keep.bin"), [1]);
            File.WriteAllBytes(Path.Combine(input, "change.bin"), [2]);
            File.WriteAllBytes(Path.Combine(input, "delete.bin"), [3]);
            File.WriteAllBytes(Path.Combine(input, "metadata.bin"), [4]);
            File.WriteAllText(Path.Combine(input, "metadata.bin.meta"), "revision=1\n");
            File.WriteAllText(Path.Combine(input, ".assetignore"), "*.tmp\n");
            File.WriteAllBytes(Path.Combine(input, "ignored.tmp"), [5]);
        }
        File.WriteAllText(Path.Combine(repository, ".assetignore"), "*.bak\n");
        File.WriteAllBytes(Path.Combine(machineA, "input", "ignored.bak"), [6]);
        File.WriteAllBytes(Path.Combine(machineB, "input", "ignored.bak"), [7]);

        var events = await RunStructured("package", "-f", config, "-root", machineA, "-tag", "v1", "-progress");
        Check.Equal(4, events[^1].GetProperty("result").GetProperty("contentChanges").GetInt32());
        string state = Path.Combine(repository, ".quire", "relocation");
        var first = TagStore.List(state).Single();
        Check.Equal("dist/v1/Assets.asset", first.Outputs.Single());
        await Run("commit", "v1", "-f", config);
        byte[] baseline = File.ReadAllBytes(Path.Combine(state, "baseline.idx"));
        var differences = await RunStructured("diff", "-f", config, "-root", machineB, "-progress");
        Check.True(!differences.Any(item => item.GetProperty("type").GetString() == "item"));
        await Run("package", "-f", config, "-root", machineB, "-tag", "same-content");
        var unchanged = TagStore.List(state).Single(tag => tag.Name == "same-content");
        Check.Equal(first.TagId, unchanged.BaseTag);
        Check.Equal(0, unchanged.ContentChanges + unchanged.MetadataChanges + unchanged.DeletedChanges);
        Check.Equal(0, unchanged.Outputs.Length);
        Check.Bytes(baseline, File.ReadAllBytes(Path.Combine(state, "baseline.idx")));

        string secondInput = Path.Combine(machineB, "input");
        File.WriteAllBytes(Path.Combine(secondInput, "change.bin"), [20, 21]);
        File.WriteAllBytes(Path.Combine(secondInput, "added.bin"), [30]);
        File.Delete(Path.Combine(secondInput, "delete.bin"));
        File.WriteAllText(Path.Combine(secondInput, "metadata.bin.meta"), "revision=2\n");
        differences = await RunStructured("diff", "-f", config, "-root=" + machineB, "-progress");
        var changes = differences.Where(item => item.GetProperty("type").GetString() == "item")
            .Select(item => item.GetProperty("data")).ToArray();
        Check.Equal(4, changes.Length);
        foreach (var (path, kind) in new[]
        {
            ("assets/change.bin", "updated"), ("assets/metadata.bin", "updated"),
            ("assets/added.bin", "added"), ("assets/delete.bin", "deleted")
        })
            Check.Equal(kind, changes.Single(item => item.GetProperty("path").GetString() == path).GetProperty("kind").GetString());
        await Run("package", "-f", config, "-root", machineB, "-tag", "v2");
        var second = TagStore.List(state).Single(tag => tag.Name == "v2");
        Check.Equal(first.TagId, second.BaseTag);
        Check.Equal(2, second.ContentChanges);
        Check.Equal(1, second.MetadataChanges);
        Check.Equal(1, second.DeletedChanges);
        Check.Equal("dist/v2/Assets.asset", second.Outputs.Single());
        Check.Bytes(baseline, File.ReadAllBytes(Path.Combine(state, "baseline.idx")));
        await Run("commit", "v2", "-f", config);
        Check.Equal(second.TagId, TagStore.LoadBaseline(state).TagId);
        Check.True((await Run("diff", "-f", config, "-root", machineB)).Contains("0 changes", StringComparison.Ordinal));

        using (var bundle = QuireBundle.Open(Path.Combine(repository, "dist")))
        {
            Check.Equal(4, bundle.EnumerateFiles().Count());
            Check.True(!bundle.FileExists("assets/delete.bin"));
            using var changed = bundle.Read("assets/change.bin");
            using var added = bundle.Read("assets/added.bin");
            using var keep = bundle.Read("assets/keep.bin");
            Check.Bytes([20, 21], changed.Data.Span);
            Check.Bytes([30], added.Data.Span);
            Check.Bytes([1], keep.Data.Span);
            Check.Equal(2L, bundle.ReadMetadata("assets/metadata.bin").GetInt64("revision"));
        }
        Check.Bytes(configurationBytes, File.ReadAllBytes(config));
        foreach (string root in new[] { machineA, machineB })
        {
            Check.True(!Directory.Exists(Path.Combine(root, ".quire")));
            Check.True(!Directory.Exists(Path.Combine(root, "dist")));
        }
    }

    private static async Task ResolutionAsync()
    {
        using var f = new Fixture(false);
        string repository = Path.Combine(f.Root, "repository"), remote = Path.Combine(f.Root, "remote");
        string absolute = Path.Combine(f.Root, "absolute-source");
        foreach (string root in new[] { repository, remote })
        {
            foreach (string child in new[] { "textures", "audio" })
            {
                string directory = Path.Combine(root, child);
                Directory.CreateDirectory(directory);
                File.WriteAllText(Path.Combine(directory, Path.GetFileName(root) + ".bin"), child);
            }
        }
        Directory.CreateDirectory(absolute);
        File.WriteAllBytes(Path.Combine(absolute, "fixed.bin"), [42]);
        string config = Path.Combine(repository, "quire.game.yml");
        File.WriteAllText(config, $$"""
            version: 1
            project: roots
            output: ./dist
            compression: none
            packages:
              - name: Assets
                sources:
                  - directory: ./textures
                    prefix: gfx
                  - directory: audio
                    prefix: sfx
                  - directory: '{{absolute.Replace("'", "''", StringComparison.Ordinal)}}'
                    prefix: fixed
            """);
        var original = ProjectConfig.Load(config);
        Check.True(original.Packages.Single().Sources.Select(source => source.Directory).SequenceEqual(new[]
        {
            Path.Combine(repository, "textures"), Path.Combine(repository, "audio"), absolute
        }));
        var defaultFiles = await SourceScanner.ScanAsync(original, 2, default);
        Check.True(defaultFiles.Select(file => file.Path).Order().SequenceEqual(new[] { "fixed/fixed.bin", "gfx/repository.bin", "sfx/repository.bin" }));
        var overridden = ProjectConfig.Load(config, remote);
        Check.Equal(original.FilePath, overridden.FilePath);
        Check.Equal(repository, overridden.Root);
        Check.Equal(original.Output, overridden.Output);
        Check.Equal(original.StateDirectory, overridden.StateDirectory);
        Check.True(overridden.Packages.Single().Sources.Select(source => source.Directory).SequenceEqual(new[]
        {
            Path.Combine(remote, "textures"), Path.Combine(remote, "audio"), absolute
        }));
        string previous = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = f.Root;
            var relative = ProjectConfig.Load("./repository/quire.game.yml", "./remote");
            // macOS may report /private/var for a working directory supplied through /var.
            Check.Equal(Path.GetFullPath("./remote/textures"), relative.Packages[0].Sources[0].Directory);
            var result = await RunStructured("diff", "-f", "./repository/quire.game.yml", "-root", "./remote", "-progress");
            string[] paths = result.Where(item => item.GetProperty("type").GetString() == "item")
                .Select(item => item.GetProperty("data").GetProperty("path").GetString()!).Order().ToArray();
            Check.True(paths.SequenceEqual(new[] { "fixed/fixed.bin", "gfx/remote.bin", "sfx/remote.bin" }));
        }
        finally { Environment.CurrentDirectory = previous; }
        Check.True(Directory.Exists(Path.Combine(repository, ".quire", "roots")));
        Check.True(!Directory.Exists(Path.Combine(remote, ".quire")));
    }

    private static async Task ValidationAsync()
    {
        using var f = new Fixture(false);
        f.Put("asset.bin", [1]);
        foreach (string invalid in new[] { "", " ", "\t" })
        {
            Check.Throws<ArgumentException>(() => ProjectConfig.Load(f.ProjectPath, invalid));
            foreach (string command in new[] { "package", "diff" })
                await Check.ThrowsAsync<ArgumentException>(() => Run(command, "-f", f.ProjectPath, "-root=" + invalid));
        }
        string missing = Path.Combine(f.Root, "missing"), file = Path.Combine(f.Root, "file.txt");
        File.WriteAllText(file, "not a directory");
        foreach (string invalid in new[] { missing, file })
        {
            Check.Throws<DirectoryNotFoundException>(() => ProjectConfig.Load(f.ProjectPath, invalid));
            foreach (string command in new[] { "package", "diff" })
                await Check.ThrowsAsync<DirectoryNotFoundException>(() => Run(command, "-f", f.ProjectPath, "-root", invalid));
        }
        foreach (string command in new[] { "package", "diff" })
        {
            await Check.ThrowsAsync<ArgumentException>(() => Run(command, "-f", f.ProjectPath, "-root"));
            foreach (string alias in new[] { "-dir", "-d" })
                await Check.ThrowsAsync<ArgumentException>(() => Run(command, "-f", f.ProjectPath, alias, f.Root));
        }
        foreach (string[] arguments in new[]
        {
            new[] { "init", "new-project" }, ["tags"], ["changes", "v1"], ["commit", "v1"], ["remove", "v1"],
            ["inspect", f.Root], ["verify", f.Root], ["unpackage", f.Root, Path.Combine(f.Root, "unpacked")], ["keys", "list"]
        })
            await Check.ThrowsAsync<ArgumentException>(() => Run([.. arguments, "-root", f.Root]));
        Check.True(!Directory.Exists(Path.Combine(f.Root, ".quire")));
        Check.True(!Directory.Exists(f.Output));
    }

    private static async Task<JsonElement[]> RunStructured(params string[] arguments)
    {
        string output = await Run(arguments);
        JsonElement[] events = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(line =>
        {
            using var document = JsonDocument.Parse(line);
            return document.RootElement.Clone();
        }).ToArray();
        Check.Equal("started", events[0].GetProperty("type").GetString());
        Check.Equal("completed", events[^1].GetProperty("type").GetString());
        return events;
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
