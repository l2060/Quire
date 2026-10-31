using System.Security.Cryptography;
using Quire.Cli.Configuration;
using Quire.Cli.Progress;
using Quire.Cli.Sources;
using Quire.Cli.Tags;
using Quire;
using Quire.Internal;

namespace Quire.Cli.Build;

internal sealed record BuildPlan(BuildEntry[] Changes, StateDelta Delta, FileChange[] FileChanges, string? BaseTag, int DeletedChanges);
internal static class ProjectBuilder
{
    private const string EncodingPrefix = "PK.QUIRE:6:";
    internal static async Task<BuildPlan> PlanAsync(ProjectConfig project, BuildOptions options, CancellationToken token, IOperationProgress? progress = null)
    {
        var snapshot = TagStore.LoadBaseline(project.StateDirectory, token);
        var baseline = snapshot.Files;
        var previousByPath = new Dictionary<string, FileState>(baseline.Count, StringComparer.Ordinal);
        foreach (var file in baseline.Values)
        {
            token.ThrowIfCancellationRequested();
            if (!file.Encoding.StartsWith(EncodingPrefix, StringComparison.Ordinal))
                throw new InvalidDataException("The baseline uses an incompatible package format. Archive the old .quire directory and create a new full candidate.");
            if (!previousByPath.TryAdd(file.Path, file))
                throw new InvalidDataException($"The baseline contains multiple states for logical path '{file.Path}'. Restore a valid baseline before creating a candidate.");
        }

        var sources = await SourceScanner.ScanAsync(project, options.IoConcurrency, token, progress).ConfigureAwait(false);
        var currentByPath = new Dictionary<string, SourceAsset>(sources.Length, StringComparer.Ordinal);
        foreach (var source in sources)
        {
            token.ThrowIfCancellationRequested();
            // SourceScanner orders packages by their stable templates. The last source wins,
            // regardless of the Tag or date used to name its output package.
            currentByPath[source.Path] = source;
        }

        var changes = new List<BuildEntry>();
        var upserts = new List<FileState>();
        var removed = new List<FileKey>();
        var fileChanges = new List<FileChange>();
        var encodings = new Dictionary<(CompressionAlgorithm, CompressionPolicy), string>();
        foreach (var source in currentByPath.Values)
        {
            token.ThrowIfCancellationRequested();
            previousByPath.Remove(source.Path, out var old);
            var contentHash = new ContentDigest(source.ContentHash);
            ContentDigest? metadataHash = source.MetadataHash is { } hash ? new ContentDigest(hash) : (ContentDigest?)null;
            if (old is null || old.Length != source.Length || old.ContentHash != contentHash)
            {
                // Output settings describe newly written bytes. Changing a configuration file,
                // package assignment, compression or encryption does not change resource identity.
                var compression = (source.Compression, source.CompressionPolicy);
                if (!encodings.TryGetValue(compression, out string? encoding))
                {
                    encoding = $"{EncodingPrefix}{project.Suffix}:{options.BlockSize}:{source.Compression}:{source.CompressionPolicy}:{project.Encrypted}";
                    encodings.Add(compression, encoding);
                }
                changes.Add(new(source, EntryKind.Content));
                upserts.Add(new(source.Package, source.Path, source.Length, contentHash, metadataHash, encoding));
                if (old is not null && old.Package != source.Package) removed.Add(new(old.Package, old.Path));
                fileChanges.Add(new(source.Package, source.Path, old is null ? ChangeKind.Added : ChangeKind.Updated));
            }
            else if (metadataHash is not null && metadataHash != old.MetadataHash)
            {
                changes.Add(new(source, EntryKind.MetadataOnly));
                // Content remains in its original package with its original encoding. Only
                // the sidecar fingerprint advances; the new metadata can live in another package.
                upserts.Add(old with { MetadataHash = metadataHash });
                fileChanges.Add(new(source.Package, source.Path, ChangeKind.Updated));
            }
            // Removing only a sidecar does not clear previously published metadata.
        }

        foreach (var old in previousByPath.Values)
        {
            token.ThrowIfCancellationRequested();
            removed.Add(new(old.Package, old.Path));
            string package = DeletionPackage(project, old.Package, old.Path);
            fileChanges.Add(new(package, old.Path, ChangeKind.Deleted));
            // One path tombstone hides every older copy, regardless of the package holding it.
            var deletion = new SourceAsset(package, old.Path, string.Empty, 0, [], null, null, CompressionAlgorithm.None);
            changes.Add(new(deletion, EntryKind.Deleted));
        }

        return new(changes.ToArray(), new(upserts.ToArray(), removed.ToArray()), fileChanges.ToArray(), snapshot.TagId, previousByPath.Count);
    }

    private static string DeletionPackage(ProjectConfig project, string previousPackage, string path)
    {
        if (project.Packages.Length == 0) throw new InvalidDataException("At least one output package is required to write deletions.");
        if (project.Packages.Length == 1) return project.Packages[0].Name;

        foreach (var package in project.Packages)
            if (package.Name == previousPackage && !package.OrderName.Contains('{')) return previousPackage;
        var selected = project.Packages[0];
        int longestPrefix = -1;
        foreach (var package in project.Packages)
        {
            if (longestPrefix < 0 && string.CompareOrdinal(package.OrderName, selected.OrderName) < 0) selected = package;
            foreach (var source in package.Sources)
            {
                string prefix = source.Prefix;
                bool matches = prefix.Length == 0 || (path.StartsWith(prefix, StringComparison.Ordinal) &&
                    (path.Length == prefix.Length || path[prefix.Length] == '/'));
                if (matches && (prefix.Length > longestPrefix ||
                    (prefix.Length == longestPrefix && string.CompareOrdinal(package.OrderName, selected.OrderName) < 0)))
                {
                    selected = package;
                    longestPrefix = prefix.Length;
                }
            }
        }
        return selected.Name;
    }

    internal static async Task<BuildTag> BuildAsync(ProjectConfig project, string? tagName, BuildOptions options, CancellationToken token, IOperationProgress? progress = null)
    {
        token.ThrowIfCancellationRequested();
        using var publisherKey = project.SecretKeyName is { } name ? SecretKeyStore.Load(name) : null;
        return await BuildAsync(project, tagName, options, publisherKey, token, progress).ConfigureAwait(false);
    }

    internal static async Task<BuildTag> BuildAsync(ProjectConfig project, string? tagName, BuildOptions options, PublisherKey? publisherKey, CancellationToken token, IOperationProgress? progress = null)
    {
        if (project.Encrypted && publisherKey is null)
            throw new InvalidOperationException($"Project secret-key '{project.SecretKeyName}' requires a publisher key.");
        options.Validate();
        token.ThrowIfCancellationRequested();
        if (tagName is not null) TagStore.ValidateName(tagName);
        Directory.CreateDirectory(project.StateDirectory);
        using var buildLock = BuildLock.Acquire(project.StateDirectory);
        var history = TagStore.List(project.StateDirectory).ToArray();
        if (tagName is not null) TagStore.EnsureNameAvailable(project.StateDirectory, tagName);
        string tagId = CreateTagId(project, history);
        string name = tagName ?? tagId;
        long previous = history.Select(t => t.BuildTimeUtc.UtcTicks).DefaultIfEmpty(0).Max();
        long ticks = Math.Max(DateTime.UtcNow.Ticks, checked(previous + 1));
        project = project.ResolveBuildOutput(name, tagId, new DateTimeOffset(ticks, TimeSpan.Zero));
        string final = project.Output;
        bool outputCreated = !Directory.Exists(final);
        Directory.CreateDirectory(final);
        using var outputLock = BuildLock.Acquire(final);
        string stage = Path.Combine(final, "." + tagId + ".tmp");
        string scratch = Path.Combine(project.StateDirectory, "temp", tagId);
        string tagStage = Path.Combine(project.StateDirectory, "tags", "." + tagId + ".idx.tmp"), tagFinal = Path.Combine(project.StateDirectory, "tags", tagId + ".idx");
        string deltaStage = Path.Combine(project.StateDirectory, "pending", "." + tagId + ".idx.tmp"), deltaFinal = Path.Combine(project.StateDirectory, "pending", tagId + ".idx");
        bool stageOwned = false, scratchOwned = false, outputPublished = false, deltaPublished = false, tagPublished = false;
        string[] filenames = [];
        int publishedFiles = 0;
        try
        {
            var plan = await PlanAsync(project, options, token, progress).ConfigureAwait(false);
            var groups = plan.Changes.GroupBy(e => e.Source.Package).OrderBy(g => g.Key, StringComparer.Ordinal).ToArray();
            filenames = new string[groups.Length];
            for (int i = 0; i < groups.Length; i++)
            {
                filenames[i] = groups[i].Key + "." + project.Suffix;
                string destination = Path.Combine(final, filenames[i]);
                if (File.Exists(destination) || Directory.Exists(destination))
                    throw new IOException($"Output package already exists: {destination}. Use a unique package name or output directory; existing files are never overwritten.");
            }
            if (File.Exists(stage) || Directory.Exists(stage) || File.Exists(scratch) || Directory.Exists(scratch))
                throw new IOException("The build's temporary directory already exists. Restore or remove its unfinished build before retrying.");
            Directory.CreateDirectory(stage);
            stageOwned = true;
            Directory.CreateDirectory(scratch);
            scratchOwned = true;
            progress?.StartStage("Writing", plan.Changes.Sum(e => checked((e.Kind == EntryKind.Content ? e.Source.Length : 0) + (e.Source.Metadata?.LongLength ?? 0))), plan.Changes.LongLength);
            var members = new PackageManifestEntry[groups.Length];
            Guid tagUid = Guid.NewGuid();
            for (int i = 0; i < groups.Length; i++)
                members[i] = new(Guid.NewGuid(), filenames[i]);
            // Every package carries the same manifest for this build only. Unchanged packages
            // and packages from previous Tags are deliberately absent.
            var manifest = members.Length == 0 ? null : PackageManifest.Create(members);
            var outputs = new List<string>(groups.Length);
            for (int i = 0; i < groups.Length; i++)
            {
                token.ThrowIfCancellationRequested();
                var group = groups[i];
                string filename = members[i].FileName;
                await PackageWriter.WriteAsync(Path.Combine(stage, filename), Path.Combine(scratch, group.Key), tagId, ticks,
                    tagUid, name, members[i].PackageUid, manifest!, group.OrderBy(e => e.Source.Path, AssetPath.Comparer).ToArray(),
                    publisherKey, project.Encrypted, options, token, progress).ConfigureAwait(false);
                outputs.Add(Path.GetRelativePath(project.Root, Path.Combine(final, filename)).Replace('\\', '/'));
            }

            // Verify staged artifacts, including metadata-only records, deletion records and content digests.
            using (var packages = await QuireBundle.OpenAsync(stage, new ReadOptions { Suffix = project.Suffix, Key = publisherKey?.ClientKey, Progress = progress, IndexMode = AssetIndexMode.PerPackage, VerifyPackageChain = true }, token).ConfigureAwait(false))
                await packages.VerifyAsync(token).ConfigureAwait(false);
            progress?.StartStage("Publishing");
            string configHash;
            await using (var configFile = File.OpenRead(project.FilePath))
            {
                using var tracked = progress is null ? null : new ProgressReadStream(configFile, progress);
                configHash = Convert.ToHexString(await SHA256.HashDataAsync((Stream?)tracked ?? configFile, token).ConfigureAwait(false));
            }

            var tag = new BuildTag(tagId, new DateTimeOffset(ticks, TimeSpan.Zero), plan.BaseTag, configHash, outputs.ToArray(),
                plan.Changes.Count(e => e.Kind == EntryKind.Content), plan.Changes.Count(e => e.MetadataOnly), plan.DeletedChanges)
            {
                Name = name,
                AddedFiles = plan.FileChanges.Count(change => change.Kind == ChangeKind.Added),
                UpdatedFiles = plan.FileChanges.Count(change => change.Kind == ChangeKind.Updated),
                RemovedFiles = plan.FileChanges.Count(change => change.Kind == ChangeKind.Deleted)
            };
            TagStore.WriteCandidate(project.StateDirectory, tagStage, deltaStage, tag, plan.FileChanges, plan.Delta, token);
            token.ThrowIfCancellationRequested();
            // Output is the exact requested directory and may contain previous releases.
            // Move each verified package without replacement; until all are published,
            // an error withdraws only the files moved by this build.
            for (int i = 0; i < filenames.Length; i++)
            {
                token.ThrowIfCancellationRequested();
                File.Move(Path.Combine(stage, filenames[i]), Path.Combine(final, filenames[i]));
                publishedFiles++;
            }
            token.ThrowIfCancellationRequested();
            outputPublished = true;
            File.Move(deltaStage, deltaFinal);
            deltaPublished = true;
            File.Move(tagStage, tagFinal);
            tagPublished = true;
            return tag;
        }
        catch
        {
            if (!outputPublished)
            {
                for (int i = publishedFiles - 1; i >= 0; i--)
                {
                    string destination = Path.Combine(final, filenames[i]);
                    try { File.Move(destination, Path.Combine(stage, filenames[i])); }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                    { Console.Error.WriteLine($"Could not withdraw incomplete output '{destination}': {error.Message}"); }
                }
            }
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
            if (stageOwned) DeleteOwnedDirectory(stage, final);
            if (File.Exists(tagStage)) File.Delete(tagStage);
            if (File.Exists(deltaStage)) File.Delete(deltaStage);
            if (deltaPublished && !tagPublished) File.Delete(deltaFinal);
            if (scratchOwned) DeleteOwnedDirectory(scratch, Path.Combine(project.StateDirectory, "temp"));
            if (!outputPublished && outputCreated)
            {
                // A non-recursive delete cannot remove an existing file or another writer's
                // output. Ancestors created for a nested template may remain as empty folders.
                try { Directory.Delete(final); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    internal static string CreateTagId(ProjectConfig project, IEnumerable<BuildTag> history, Func<uint>? generate = null) =>
        TagStore.CreateId(history.SelectMany(t => new[] { t.TagId, t.Name })
            .Concat(ReservedIds(Path.Combine(project.StateDirectory, "tags")))
            .Concat(ReservedIds(Path.Combine(project.StateDirectory, "pending")))
            .Concat(ReservedIds(Path.Combine(project.StateDirectory, "temp")))
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
            if (name.EndsWith(".idx", StringComparison.Ordinal))
                name = name[..^4];
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
