using Quire.Cli.Build;
using Quire.Cli.Configuration;
using Quire.Cli.Extraction;
using Quire.Cli.Progress;
using Quire.Cli.Serialization;
using Quire.Cli.Tags;
using Quire;
using Quire.Internal;
using System.Globalization;
using System.Runtime.ExceptionServices;

namespace Quire.Cli;

internal static class CommandLine
{
    internal static async Task<int> RunAsync(string[] args, CancellationToken token)
    {
        using var output = new CommandOutput(args.Length == 0 ? "help" : args[0], UsesProgress(args), token);
        try
        {
            output.Started();
            int result = await RunCoreAsync(args, output.Token, output).ConfigureAwait(false);
            await output.CompleteAsync().ConfigureAwait(false);
            return result;
        }
        catch (Exception error)
        {
            ExceptionDispatchInfo.Capture(await output.FailAsync(error).ConfigureAwait(false)).Throw();
            throw;
        }
    }

    internal static bool UsesProgress(string[] args)
    {
        if (args.Length == 0 || args[0] is "help" or "-help" or "--help" or "-h") return false;
        if (args[0] != "keys") return args.AsSpan(1).Contains("-progress");
        // A key password/destination may itself be '-progress'. Only an extra
        // trailing switch enables structured output for the positional key grammar.
        int count = args.Length > 1 ? args[1] switch { "add" or "export" => 4, "show" or "remove" => 3, _ => 2 } : 2;
        return args.Length > count && args[^1] == "-progress";
    }

    private static async Task<int> RunCoreAsync(string[] args, CancellationToken token, CommandOutput output)
    {
        if (args.Length == 0 || args[0] is "help" or "-help" or "--help" or "-h")
        {
            Console.WriteLine("""
                Usage: quire-cli <command> [arguments] [options]

                Project
                  init PROJECT [DIRECTORY]    Create quire.<project>.yml (default: current directory).
                  diff                        Compare current files with the baseline.
                  package                     Build a candidate; leave the baseline unchanged.

                Release history
                  tags                        List tags, status and parent relationships.
                  changes NAME                Show a tag's recorded file changes.
                  commit NAME                 Advance the baseline to a tested candidate.
                  remove NAME                 Delete an uncommitted candidate; keep output files.

                Package files
                  inspect DIRECTORY           List packages and merged assets.
                  verify DIRECTORY            Verify package contents.
                  unpackage SOURCE DEST       Extract assets and .meta files to a new/empty directory.

                Local keys (shared by all projects)
                  keys add KEYNAME PASSWORD   Derive and save a named publishing key.
                  keys list                   List fingerprints, names and Base64 client keys.
                  keys show KEYNAME           Print one Base64 client key.
                  keys remove KEYNAME         Delete a locally stored key.
                  keys export KEYNAME FILE    Export a binary client key file.

                Options
                  -f FILE                     Config path for package, diff and history commands.
                                              Default: ./quire.project.yml.
                  -root DIRECTORY             package/diff: base for relative source directories.
                                              Default: config directory; absolute sources keep their paths.
                  -tag NAME                   package: candidate name (default: generated).
                  -jobs N                     package: override build.jobs; 0 = automatic.
                                              unpackage: workers, 1..256 (default: min(CPU, 8)).
                  -key FILE                   Package files: client key; required for signed packages.
                  -suffix EXT                 Package files: file extension (default: asset).
                  -progress                   Stream structured events for GUI/script integration.
                  -h, --help                  Show this help.
                """);
            return 0;
        }

        // Named keys are a separate command family. Parse its exact positional grammar
        // before option parsing so passwords may start with '-' without becoming flags.
        if (args[0] == "keys")
            return RunKeys(output.Structured ? args[..^1] : args, token, output);

        var flags = new Dictionary<string, string>(StringComparer.Ordinal);
        var positional = new List<string>();
        for (int i = 1; i < args.Length; i++)
        {
            if (args[i] == "-progress")
            {
                if (!flags.TryAdd("-progress", "true")) throw new ArgumentException("Duplicate option: -progress");
                continue;
            }
            if (!args[i].StartsWith('-'))
            {
                positional.Add(args[i]);
                continue;
            }

            int equal = args[i].IndexOf('=');
            string key = equal < 0 ? args[i] : args[i][..equal];
            if (key == "-progress") throw new ArgumentException("-progress is a switch and does not accept a value.");
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
        int Number(string key, int fallback) => !flags.TryGetValue(key, out string? value) ? fallback :
            int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number) ? number :
            throw new ArgumentException($"{key} requires an integer value.");
        string[] allowed = args[0] switch
        {
            "init" => [],
            "package" => ["-f", "-tag", "-jobs", "-root"],
            "diff" => ["-f", "-root"],
            "unpackage" => ["-key", "-jobs", "-suffix"],
            "tags" or "changes" or "commit" or "remove" => ["-f"],
            "inspect" or "verify" => ["-key", "-suffix"],
            _ => throw new ArgumentException($"Unknown command: {args[0]}")
        };
        foreach (var key in flags.Keys)
            if (key != "-progress" && !allowed.Contains(key))
                throw new ArgumentException($"Unknown option: {key}");
        if (args[0] == "init")
        {
            if (positional.Count is < 1 or > 2)
                throw new ArgumentException("Usage: quire-cli init PROJECT [DIRECTORY]. Specify a project name.");
            token.ThrowIfCancellationRequested();
            string created = ProjectInitializer.Create(positional[0], positional.Count == 1 ? Directory.GetCurrentDirectory() : positional[1]);
            if (!output.Structured) Console.WriteLine($"Created {created}");
            output.Result(new PathResult(created), CliJsonContext.Default.PathResult);
            return 0;
        }

        if (args[0] is "inspect" or "verify" or "unpackage")
        {
            int required = args[0] == "unpackage" ? 2 : 1;
            if (positional.Count != required)
                throw new ArgumentException(required == 2 ? "Specify a source package directory and a destination directory." : "Specify exactly one package directory.");
            AssetKey? clientKey;
            if (flags.TryGetValue("-key", out string? clientFile))
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(clientFile);
                clientKey = SecretKeyStore.LoadClientFile(clientFile);
            }
            else clientKey = null;
            var readOptions = new ReadOptions { Key = clientKey, Suffix = Get("-suffix", "asset") };
            if (args[0] == "unpackage")
            {
                int jobs = Number("-jobs", PackageExtractor.DefaultJobs);
                var result = await PackageExtractor.ExtractAsync(positional[0], positional[1], readOptions, token, jobs, output.StartProgress()).ConfigureAwait(false);
                string destination = Path.GetFullPath(positional[1]);
                output.FinishProgress();
                if (!output.Structured) Console.WriteLine($"Extracted {result.Assets} assets and {result.MetadataFiles} metadata files to {destination}");
                output.Result(new ExtractionResult(destination, result.Assets, result.MetadataFiles), CliJsonContext.Default.ExtractionResult);
                return 0;
            }

            if (args[0] == "verify")
            {
                using var set = await QuireBundle.OpenAsync(positional[0], readOptions with { Progress = output.StartProgress(), IndexMode = AssetIndexMode.PerPackage }, token).ConfigureAwait(false);
                await set.VerifyAsync(token).ConfigureAwait(false);
                output.FinishProgress();
                foreach (var p in set.Packages) PrintPackage(p, output);
                if (!output.Structured) Console.WriteLine("Verification passed.");
                output.Result(new CountResult(set.Packages.Count), CliJsonContext.Default.CountResult);
            }
            else
            {
                using var set = await QuireBundle.OpenAsync(positional[0], readOptions with { Progress = output.Structured ? output.StartProgress() : null }, token).ConfigureAwait(false);
                foreach (var p in set.Packages)
                    PrintPackage(p, output);
                long count = 0;
                foreach (var asset in set.EnumerateFiles())
                {
                    token.ThrowIfCancellationRequested();
                    if (output.Structured) output.Item("asset", new AssetResult(asset.Path, "file", asset.Length, asset.HasMetadata), CliJsonContext.Default.AssetResult);
                    else Console.WriteLine($"{asset.Length,12}  meta={asset.HasMetadata}  {asset.Path}");
                    count++;
                }
                output.Result(new InspectionResult(set.Packages.Count, count), CliJsonContext.Default.InspectionResult);
            }

            return 0;
        }

        string projectPath = Path.GetFullPath(Get("-f", "quire.project.yml"));
        if (args[0] is "tags" or "changes" or "commit" or "remove")
        {
            int required = args[0] == "tags" ? 0 : 1;
            if (positional.Count != required)
                throw new ArgumentException(required == 0 ? "tags accepts no positional arguments." : $"{args[0]} requires exactly one Tag name.");
            token.ThrowIfCancellationRequested();
            string state = ProjectConfig.LoadStateDirectory(projectPath);
            if (args[0] == "tags")
            {
                PrintTags(state, output);
                return 0;
            }
            string id = TagStore.Resolve(state, positional[0]);
            if (args[0] == "changes")
                PrintChanges(TagStore.ReadChanges(state, id), output);
            else if (args[0] == "commit")
            {
                var committed = TagStore.Commit(state, id, token);
                if (!output.Structured) Console.WriteLine($"Baseline: {committed.Name} ({committed.TagId})");
                output.Result(TagResult.From(committed, "baseline"), CliJsonContext.Default.TagResult);
            }
            else
            {
                var removed = TagStore.Load(state, id);
                TagStore.Remove(state, id);
                if (!output.Structured) Console.WriteLine($"Removed candidate {removed.Name}. Output files were retained.");
                output.Result(TagResult.From(removed, "removed"), CliJsonContext.Default.TagResult);
            }
            return 0;
        }

        if (positional.Count != 0)
            throw new ArgumentException("Unexpected positional argument.");
        var project = ProjectConfig.Load(projectPath, flags.GetValueOrDefault("-root"));
        if (args[0] == "diff")
        {
            Directory.CreateDirectory(project.StateDirectory);
            using var stateLock = BuildLock.Acquire(project.StateDirectory);
            var plan = await ProjectBuilder.PlanAsync(project, project.BuildOptions, token, output.Structured ? output.StartProgress() : null).ConfigureAwait(false);
            PrintChanges(plan.FileChanges, output);
            return 0;
        }

        var defaults = project.BuildOptions;
        var options = defaults with
        {
            Jobs = Number("-jobs", defaults.Jobs)
        };
        options.Validate();
        string? name = flags.GetValueOrDefault("-tag");
        var built = await ProjectBuilder.BuildAsync(project, name, options, token, output.StartProgress()).ConfigureAwait(false);
        if (!output.Structured)
        {
            string parent = built.BaseTag is { } parentId ? TagStore.Load(project.StateDirectory, parentId).Name : "NULL";
            output.FinishProgress();
            Console.WriteLine($"Tag: {built.Name}\nId: {built.TagId}\nParent: {parent}\nStatus: CANDIDATE\nTime: {built.BuildTimeUtc:O}\nContent: {built.ContentChanges}; Meta: {built.MetadataChanges}; Deleted: {built.DeletedChanges}");
            foreach (var path in built.Outputs) Console.WriteLine(path);
        }
        output.Result(TagResult.From(built, "candidate"), CliJsonContext.Default.TagResult);
        return 0;
    }

    private static int RunKeys(string[] args, CancellationToken token, CommandOutput output)
    {
        token.ThrowIfCancellationRequested();
        if (args.Length == 4 && args[1] == "add")
        {
            SecretKeyStore.ValidateName(args[2]);
            using var publisher = PublisherKey.FromPassword(args[3]);
            SecretKeyStore.Add(args[2], publisher, token);
            string fingerprint = SecretKeyStore.Fingerprint(publisher.ClientKey);
            if (!output.Structured) Console.WriteLine($"Added key {args[2]} ({fingerprint}).");
            output.Result(new KeyResult(fingerprint, args[2], null), CliJsonContext.Default.KeyResult);
        }
        else if (args.Length == 2 && args[1] == "list")
        {
            var entries = SecretKeyStore.List();
            int nameWidth = Math.Max("KEYNAME".Length, entries.Select(entry => entry.Name.Length).DefaultIfEmpty().Max());
            string format = $"{{0,-64}}  {{1,-{nameWidth}}}  {{2}}";
            if (!output.Structured) Console.WriteLine(format, "FINGERPRINT", "KEYNAME", "CLIENTKEY");
            foreach (var entry in entries)
            {
                token.ThrowIfCancellationRequested();
                if (output.Structured) output.Item("key", new KeyResult(SecretKeyStore.Fingerprint(entry.ClientKey), entry.Name, entry.ClientKey.ExportBase64()), CliJsonContext.Default.KeyResult);
                else Console.WriteLine(format, SecretKeyStore.Fingerprint(entry.ClientKey), entry.Name, entry.ClientKey.ExportBase64());
            }
            output.Result(new CountResult(entries.Length), CliJsonContext.Default.CountResult);
        }
        else if (args.Length == 3 && args[1] == "show")
        {
            using var publisher = SecretKeyStore.Load(args[2]);
            string clientKey = publisher.ClientKey.ExportBase64();
            if (output.Structured)
                output.Result(new KeyResult(SecretKeyStore.Fingerprint(publisher.ClientKey), args[2], clientKey), CliJsonContext.Default.KeyResult);
            else Console.WriteLine(clientKey);
        }
        else if (args.Length == 3 && args[1] == "remove")
        {
            SecretKeyStore.Remove(args[2], token);
            if (!output.Structured) Console.WriteLine($"Removed key {args[2]}.");
            output.Result(new NameResult(args[2]), CliJsonContext.Default.NameResult);
        }
        else if (args.Length == 4 && args[1] == "export")
        {
            string path = SecretKeyStore.ExportClient(args[2], args[3], token);
            if (!output.Structured) Console.WriteLine($"Client key: {path}");
            output.Result(new PathResult(path), CliJsonContext.Default.PathResult);
        }
        else
            throw new ArgumentException("Use 'keys add KEYNAME PASSWORD', 'keys list', 'keys show KEYNAME', 'keys remove KEYNAME', or 'keys export KEYNAME DESTINATION'. Keys commands do not accept project options.");
        return 0;
    }

    private static void PrintTags(string state, CommandOutput output)
    {
        // Only headers are needed, regardless of how many resources the baseline contains.
        string[] chain = TagStore.ReadCommittedTags(state);
        var tags = TagStore.List(state).OrderBy(tag => tag.BuildTimeUtc).ToArray();
        var names = tags.ToDictionary(tag => tag.TagId, tag => tag.Name, StringComparer.Ordinal);
        var committed = chain.ToHashSet(StringComparer.Ordinal);
        string? head = chain.LastOrDefault();
        int nameWidth = Math.Max(8, tags.Select(tag => tag.Name.Length).DefaultIfEmpty().Max());
        string format = $"{{0,-{nameWidth}}}  {{1,-{nameWidth}}}  {{2,-9}}  {{3,-19}}  {{4,-12}}  {{5,-8}}  {{6,-8}}  {{7,-8}}";
        if (!output.Structured) Console.WriteLine(format, "TAG", "PARENT", "STATUS", "BUILD TIME", "OUTPUT FILES", "ADDED", "UPDATED", "DELETED");
        foreach (var tag in tags)
        {
            output.Token.ThrowIfCancellationRequested();
            string parent = "NULL";
            if (tag.BaseTag is { } parentId && !names.TryGetValue(parentId, out parent!))
                throw new InvalidDataException($"Tag '{tag.Name}' references missing parent '{parentId}'.");
            string status = tag.TagId == head ? "BASELINE" : committed.Contains(tag.TagId) ? "COMMITTED" : "CANDIDATE";
            if (output.Structured) output.Item("tag", TagResult.From(tag, status.ToLowerInvariant()), CliJsonContext.Default.TagResult);
            else Console.WriteLine(format, tag.Name, parent, status,
                tag.BuildTimeUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                tag.Outputs.Length, tag.AddedFiles, tag.UpdatedFiles, tag.RemovedFiles);
        }
        output.Result(new CountResult(tags.Length), CliJsonContext.Default.CountResult);
    }

    private static void PrintChanges(IEnumerable<FileChange> changes, CommandOutput output)
    {
        int added = 0, updated = 0, deleted = 0;
        foreach (var change in changes)
        {
            output.Token.ThrowIfCancellationRequested();
            char kind;
            switch (change.Kind)
            {
                case ChangeKind.Added: kind = 'A'; added++; break;
                case ChangeKind.Updated: kind = 'M'; updated++; break;
                case ChangeKind.Deleted: kind = 'D'; deleted++; break;
                default: throw new InvalidDataException("Invalid file change kind.");
            }
            if (output.Structured) output.Item("change", new ChangeResult(change.Package, change.Path,
                kind switch { 'A' => "added", 'M' => "updated", _ => "deleted" }), CliJsonContext.Default.ChangeResult);
            else Console.WriteLine($"{kind}  {change.Package}/{change.Path}");
        }
        if (!output.Structured) Console.WriteLine($"{added + updated + deleted} changes; {added} added; {updated} updated; {deleted} deleted.");
        output.Result(new ChangesResult(added, updated, deleted), CliJsonContext.Default.ChangesResult);
    }

    private static void PrintPackage(AssetPackageInfo package, CommandOutput output)
    {
        if (output.Structured) output.Item("package", package, CliJsonContext.Default.AssetPackageInfo);
        else Console.WriteLine($"{package.BuildTimeUtc:O}  {package.TagId}  {package.EntryCount} entries  encrypted={package.IsEncrypted}  signed={package.IsSigned}  {package.FilePath}");
    }
}
