using Quire.Cli.Build;
using Quire.Cli.Configuration;
using Quire.Cli.Extraction;
using Quire.Cli.Progress;
using Quire.Cli.Tags;
using Quire.Package;

namespace Quire.Cli;

internal static class CommandLine
{
    internal static async Task<int> RunAsync(string[] args, CancellationToken token)
    {
        if (args.Length == 0 || args[0] is "help" or "-help" or "--help" or "-h")
        {
            Console.WriteLine("""
                quire-cli init [DIRECTORY]
                quire-cli package [-project quire.project.yml] [-base-tag TAG]
                                  [-jobs N]
                                  [-pwd PASSWORD | -pwd-env NAME]
                quire-cli unpackage SOURCE_DIRECTORY DESTINATION_DIRECTORY
                                    [-jobs N] [-pwd PASSWORD | -pwd-env NAME]
                quire-cli diff  [-project quire.project.yml] -base-tag TAG
                                [-pwd PASSWORD | -pwd-env NAME]
                quire-cli tags  [-project quire.project.yml]
                quire-cli inspect DIRECTORY [-pwd PASSWORD | -pwd-env NAME]
                quire-cli verify  DIRECTORY [-pwd PASSWORD | -pwd-env NAME]

                Paths in quire.project.yml are relative to that file. State is stored in .asset/.
                Meta is property=value only. Deleted source files never produce deletion records.
                unpackage restores the merged assets and .meta files into a new or empty directory.
                unpackage -jobs defaults to min(processor count, 8); valid range is 1..256.
                package, unpackage and verify show progress, I/O rates and stage ETA on stderr.
                Password options override the project's key-environment; encryption.enabled still applies.
                """);
            return 0;
        }

        var flags = new Dictionary<string, string>(StringComparer.Ordinal);
        var positional = new List<string>();
        for (int i = 1; i < args.Length; i++)
        {
            if (!args[i].StartsWith('-'))
            {
                positional.Add(args[i]);
                continue;
            }

            int equal = args[i].IndexOf('=');
            string key = equal < 0 ? args[i] : args[i][..equal];
            string value;
            if (equal >= 0)
                value = args[i][(equal + 1)..];
            else
            {
                if (i + 1 >= args.Length || args[i + 1].StartsWith('-'))
                    throw new ArgumentException($"Missing value for {key}.");
                value = args[++i];
            }

            if (!flags.TryAdd(key, value))
                throw new ArgumentException($"Duplicate option: {key}");
        }

        string Get(string key, string fallback) => flags.GetValueOrDefault(key, fallback);
        int Number(string key, int fallback) => flags.TryGetValue(key, out string? value) ? int.Parse(value, System.Globalization.CultureInfo.InvariantCulture) : fallback;
        string[] allowed = args[0] switch
        {
            "init" => [],
            "package" or "diff" => ["-project", "-base-tag", "-jobs", "-pwd", "-pwd-env"],
            "unpackage" => ["-pwd", "-pwd-env", "-jobs"],
            "tags" => ["-project"],
            "inspect" or "verify" => ["-pwd", "-pwd-env"],
            _ => throw new ArgumentException($"Unknown command: {args[0]}")
        };
        foreach (var key in flags.Keys)
            if (!allowed.Contains(key))
                throw new ArgumentException($"Unknown option: {key}");
        if (args[0] == "init")
        {
            if (positional.Count > 1)
                throw new ArgumentException("init accepts at most one destination directory.");
            token.ThrowIfCancellationRequested();
            Console.WriteLine($"Created {ProjectInitializer.Create(positional.Count == 0 ? Directory.GetCurrentDirectory() : positional[0])}");
            return 0;
        }

        if (flags.ContainsKey("-pwd") && flags.ContainsKey("-pwd-env"))
            throw new ArgumentException("Specify either -pwd or -pwd-env, not both.");
        string? suppliedPassword = null;
        if (flags.TryGetValue("-pwd", out var directPassword))
            suppliedPassword = directPassword.Length > 0 ? directPassword : throw new ArgumentException("-pwd must not be empty.");
        else if (flags.TryGetValue("-pwd-env", out var variable))
            suppliedPassword = variable.Length > 0 && Environment.GetEnvironmentVariable(variable) is { Length: > 0 } environmentPassword ? environmentPassword : throw new ArgumentException($"Environment variable '{variable}' is missing or empty.");
        if (args[0] is "inspect" or "verify" or "unpackage")
        {
            int required = args[0] == "unpackage" ? 2 : 1;
            if (positional.Count != required)
                throw new ArgumentException(required == 2 ? "Specify a source package directory and a destination directory." : "Specify exactly one package directory.");
            if (args[0] == "unpackage")
            {
                int jobs = Number("-jobs", PackageExtractor.DefaultJobs);
                var result = await WithProgressAsync("unpackage", progress => PackageExtractor.ExtractAsync(positional[0], positional[1], new ReadOptions { Password = suppliedPassword }, token, jobs, progress)).ConfigureAwait(false);
                Console.WriteLine($"Extracted {result.Assets} assets and {result.MetadataFiles} metadata files to {Path.GetFullPath(positional[1])}");
                return 0;
            }

            if (args[0] == "verify")
            {
                var packages = await WithProgressAsync("verify", async progress =>
                {
                    using var set = await PackageSet.OpenAsync(positional[0], new ReadOptions { Password = suppliedPassword, Progress = progress, IndexMode = AssetIndexMode.PerPackage }, token).ConfigureAwait(false);
                    await set.VerifyAsync(token).ConfigureAwait(false);
                    return set.Packages.ToArray();
                }).ConfigureAwait(false);
                foreach (var p in packages)
                    PrintPackage(p);
                Console.WriteLine("Verification passed.");
            }
            else
            {
                using var set = await PackageSet.OpenAsync(positional[0], new ReadOptions { Password = suppliedPassword }, token).ConfigureAwait(false);
                foreach (var p in set.Packages)
                    PrintPackage(p);
                foreach (var asset in set.EnumerateFiles())
                    Console.WriteLine($"{asset.Length,12}  meta={asset.HasMetadata}  {asset.Path}");
            }

            return 0;
        }

        if (positional.Count != 0)
            throw new ArgumentException("Unexpected positional argument.");
        var project = ProjectConfig.Load(Get("-project", "quire.project.yml"));
        if (args[0] == "tags")
        {
            foreach (var tag in TagStore.List(project.StateDirectory).OrderBy(t => t.BuildTimeUtc))
                Console.WriteLine($"{tag.TagId:N}  {tag.BuildTimeUtc:O}  content={tag.ContentChanges} meta={tag.MetadataChanges}  base={tag.BaseTag?.ToString("N") ?? "full"}");
            return 0;
        }

        if (!project.Encrypted && suppliedPassword is not null)
            throw new ArgumentException("Password options require encryption.enabled: true in the project.");
        string? password = suppliedPassword ?? project.Password;
        Guid? baseline = flags.TryGetValue("-base-tag", out var tagText) ? Guid.Parse(tagText) : null;
        var defaults = project.BuildOptions;
        var options = defaults with
        {
            Jobs = Number("-jobs", defaults.Jobs)
        };
        options.Validate();
        if (args[0] == "diff")
        {
            if (baseline is null)
                throw new ArgumentException("diff requires -base-tag.");
            var plan = await ProjectBuilder.PlanAsync(project, baseline, options, password, token).ConfigureAwait(false);
            foreach (var e in plan.Changes)
                Console.WriteLine($"{(e.MetadataOnly ? "META" : "CONTENT")}  {e.Source.Package}/{e.Source.Path}");
            Console.WriteLine($"{plan.Changes.Length} changes; {plan.MissingSources} inherited paths absent from source.");
            return 0;
        }

        var built = await WithProgressAsync("package", progress => ProjectBuilder.BuildAsync(project, baseline, options, password, token, progress)).ConfigureAwait(false);
        Console.WriteLine($"Tag: {built.TagId:N}\nTime: {built.BuildTimeUtc:O}\nContent: {built.ContentChanges}; Meta: {built.MetadataChanges}");
        foreach (var output in built.Outputs)
            Console.WriteLine(output);
        return 0;
    }

    private static void PrintPackage(AssetPackageInfo package) => Console.WriteLine($"{package.BuildTimeUtc:O}  {package.TagId:N}  {package.EntryCount} entries  encrypted={package.IsEncrypted}  {package.FilePath}");
    private static async Task<T> WithProgressAsync<T>(string command, Func<ConsoleProgress, Task<T>> operation)
    {
        using var progress = new ConsoleProgress(command);
        try
        {
            T result = await operation(progress).ConfigureAwait(false);
            progress.Complete();
            return result;
        }
        catch (OperationCanceledException)
        {
            progress.Fail(canceled: true);
            throw;
        }
        catch
        {
            progress.Fail();
            throw;
        }
    }
}
