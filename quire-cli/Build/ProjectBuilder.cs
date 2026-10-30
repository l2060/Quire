using System.Security.Cryptography;
using Quire.Cli.Configuration;
using Quire.Cli.Progress;
using Quire.Cli.Sources;
using Quire.Cli.Tags;
using Quire.Package;
using Quire.Package.Internal;

namespace Quire.Cli.Build;

internal sealed record BuildPlan(BuildEntry[] Changes, FileState[] Files, int MissingSources);
internal static class ProjectBuilder
{
    private const string EncodingPrefix = "PK.QUIRE:2:";
    internal static async Task<BuildPlan> PlanAsync(ProjectConfig project, string? baseTag, BuildOptions options, string? password, CancellationToken token, IOperationProgress? progress = null)
    {
        if (project.Encrypted ? string.IsNullOrEmpty(password) : password is not null)
            throw new ArgumentException("A nonempty password is required exactly when project encryption is enabled.");
        var baseline = baseTag is { } id ? TagStore.LoadFiles(project.StateDirectory, id) : new Dictionary<(string Package, string Path), FileState>();
        if (baseline.Values.Any(f => !f.Encoding.StartsWith(EncodingPrefix, StringComparison.Ordinal)))
            throw new InvalidDataException("The base Tag uses an incompatible package format. Create a full package without -base.");
        var sources = await SourceScanner.ScanAsync(project, options.IoConcurrency, token, progress).ConfigureAwait(false);
        var changes = new List<BuildEntry>();
        var present = new HashSet<(string, string)>();
        // Fingerprints stay in the local .quire directory; no credentials are serialized.
        string keyFingerprint = KeyFingerprint(project.StateDirectory, password);
        foreach (var source in sources)
        {
            var key = (source.Package, source.Path);
            present.Add(key);
            string encoding = $"{EncodingPrefix}{options.BlockSize}:{source.Compression}:{project.Encrypted}:{keyFingerprint}";
            var current = new FileState(source.Package, source.Path, source.Length, Convert.ToHexString(source.ContentHash), source.MetadataHash is { } hash ? Convert.ToHexString(hash) : null, encoding);
            if (!baseline.TryGetValue(key, out var old) || old.Length != current.Length || old.ContentHash != current.ContentHash || old.Encoding != current.Encoding)
            {
                changes.Add(new(source, false));
                baseline[key] = current;
            }
            else if (current.MetadataHash is not null && current.MetadataHash != old.MetadataHash)
            {
                changes.Add(new(source, true));
                baseline[key] = current;
            }
            // Absence of a source or a sidecar is intentionally not a deletion operation.
        }

        return new(changes.ToArray(), baseline.Values.ToArray(), baseline.Keys.Count(k => !present.Contains(k)));
    }

    private static string KeyFingerprint(string state, string? password)
    {
        if (password is null)
            return "public";
        Directory.CreateDirectory(state);
        string path = Path.Combine(state, "key.salt");
        if (!File.Exists(path))
        {
            using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            file.Write(RandomNumberGenerator.GetBytes(16));
            file.Flush(true);
        }

        byte[] salt = File.ReadAllBytes(path);
        if (salt.Length != 16)
            throw new InvalidDataException("Invalid local key fingerprint salt.");
        byte[] derived = Rfc2898DeriveBytes.Pbkdf2(password, salt, 600000, HashAlgorithmName.SHA256, 32);
        try
        {
            return Convert.ToHexString(SHA256.HashData(derived));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(derived);
        }
    }

    internal static Task<BuildTag> BuildAsync(ProjectConfig project, string? baseTag, BuildOptions options, CancellationToken token, IOperationProgress? progress = null) => BuildAsync(project, baseTag, options, project.Password, token, progress);
    internal static async Task<BuildTag> BuildAsync(ProjectConfig project, string? baseTag, BuildOptions options, string? password, CancellationToken token, IOperationProgress? progress = null)
    {
        options.Validate();
        Directory.CreateDirectory(project.StateDirectory);
        using var buildLock = new FileStream(Path.Combine(project.StateDirectory, "build.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var history = TagStore.List(project.StateDirectory).ToArray();
        var plan = await PlanAsync(project, baseTag, options, password, token, progress).ConfigureAwait(false);
        Directory.CreateDirectory(project.Output);
        using var outputLock = new FileStream(Path.Combine(project.Output, ".quire-build.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        string tagId = CreateTagId(project, history);
        long previous = history.Select(t => t.BuildTimeUtc.UtcTicks).DefaultIfEmpty(0).Max();
        long ticks = Math.Max(DateTime.UtcNow.Ticks, checked(previous + 1));
        string name = tagId;
        string stage = Path.Combine(project.Output, "." + name + ".tmp"), final = Path.Combine(project.Output, name);
        string scratch = Path.Combine(project.StateDirectory, "temp", name);
        string tagStage = Path.Combine(project.StateDirectory, "tags", "." + name + ".tmp"), tagFinal = Path.Combine(project.StateDirectory, "tags", name);
        Directory.CreateDirectory(stage);
        Directory.CreateDirectory(scratch);
        bool outputPublished = false;
        try
        {
            progress?.StartStage("Writing", plan.Changes.Sum(e => checked((e.MetadataOnly ? 0 : e.Source.Length) + (e.Source.Metadata?.LongLength ?? 0))), plan.Changes.LongLength);
            var outputs = new List<string>();
            foreach (var group in plan.Changes.GroupBy(e => e.Source.Package).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                token.ThrowIfCancellationRequested();
                string filename = group.Key + ".asset";
                await PackageWriter.WriteAsync(Path.Combine(stage, filename), Path.Combine(scratch, group.Key), tagId, ticks, group.OrderBy(e => e.Source.Path, AssetPath.Comparer).ToArray(), password, options, token, progress).ConfigureAwait(false);
                outputs.Add(Path.GetRelativePath(project.Root, Path.Combine(final, filename)).Replace('\\', '/'));
            }

            // Verify the actual staged artifacts, including metadata-only records and content digests.
            using (var packages = await PackageSet.OpenAsync(stage, new ReadOptions { Password = password, Progress = progress, IndexMode = AssetIndexMode.PerPackage }, token).ConfigureAwait(false))
                await packages.VerifyAsync(token).ConfigureAwait(false);
            progress?.StartStage("Publishing");
            string configHash;
            await using (var configFile = File.OpenRead(project.FilePath))
            {
                using var tracked = progress is null ? null : new ProgressReadStream(configFile, progress);
                configHash = Convert.ToHexString(await SHA256.HashDataAsync((Stream?)tracked ?? configFile, token).ConfigureAwait(false));
            }

            var tag = new BuildTag(tagId, new DateTimeOffset(ticks, TimeSpan.Zero), baseTag, configHash, outputs.ToArray(), plan.Changes.Count(e => !e.MetadataOnly), plan.Changes.Count(e => e.MetadataOnly));
            TagStore.Write(tagStage, tag, plan.Files);
            token.ThrowIfCancellationRequested();
            Directory.Move(stage, final);
            outputPublished = true;
            Directory.Move(tagStage, tagFinal);
            return tag;
        }
        catch
        {
            // Complete but unrecorded output is retained if publishing the Tag fails.
            if (outputPublished)
            {
                if (progress is ConsoleProgress console)
                    console.Fail();
                Console.Error.WriteLine($"Completed output retained at {final}; Tag publication did not finish.");
            }

            throw;
        }
        finally
        {
            DeleteOwnedDirectory(stage, project.Output);
            DeleteOwnedDirectory(tagStage, Path.Combine(project.StateDirectory, "tags"));
            DeleteOwnedDirectory(scratch, Path.Combine(project.StateDirectory, "temp"));
        }
    }

    internal static string CreateTagId(ProjectConfig project, IEnumerable<BuildTag> history, Func<uint>? generate = null) =>
        TagStore.CreateId(history.Select(t => t.TagId)
            .Concat(ReservedIds(Path.Combine(project.StateDirectory, "tags")))
            .Concat(ReservedIds(project.Output)), generate);

    private static IEnumerable<string> ReservedIds(string directory)
    {
        if (!Directory.Exists(directory))
            yield break;
        foreach (string path in Directory.EnumerateFileSystemEntries(directory))
        {
            string name = Path.GetFileName(path);
            if (name.StartsWith('.') && name.EndsWith(".tmp", StringComparison.Ordinal))
                name = name[1..^4];
            if (PackageTag.IsValid(name))
                yield return name;
        }
    }

    private static void DeleteOwnedDirectory(string path, string parent)
    {
        string resolved = Path.GetFullPath(path), root = Path.GetFullPath(parent);
        if (!ProjectConfig.Within(resolved, root) || resolved.Equals(root, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Unsafe temporary directory boundary.");
        if (Directory.Exists(resolved))
            Directory.Delete(resolved, true);
    }
}
