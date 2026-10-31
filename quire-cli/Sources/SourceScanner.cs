using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using Quire.Cli.Configuration;
using Quire.Cli.Progress;
using Quire.Internal;

namespace Quire.Cli.Sources;

internal sealed record SourceAsset(string Package, string Path, string SourcePath, long Length,
    byte[] ContentHash, byte[]? Metadata, byte[]? MetadataHash, CompressionAlgorithm Compression,
    CompressionPolicy CompressionPolicy = CompressionPolicy.Adaptive);

internal static class SourceScanner
{
    private sealed record Candidate(PackageConfig Package, string Path, string FullPath, string? MetadataPath);
    private sealed record ScanDirectory(PackageConfig Package, SourceConfig Source, string Directory, IReadOnlyList<IgnoreRule> Rules, Glob[] Includes, Glob[] Excludes);

    internal static async Task<SourceAsset[]> ScanAsync(ProjectConfig project, int concurrency, CancellationToken token, IOperationProgress? progress = null)
    {
        progress?.StartStage("Discovering");
        string stateRoot = project.StateRootDirectory;
        string projectIgnore = Path.Combine(project.Root, ".assetignore");
        StringComparison pathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var candidates = new ConcurrentBag<Candidate>();
        var roots = project.Packages.SelectMany(p => p.Sources.Select(s => new ScanDirectory(p, s, s.Directory,
            IgnoreRule.Load(projectIgnore, s.Directory), s.Include.Select(x => new Glob(x)).ToArray(), s.Exclude.Select(x => new Glob(x)).ToArray()))).ToArray();
        var pending = new ConcurrentQueue<ScanDirectory>(roots);
        int unfinished = roots.Length;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        CancellationToken discoveryToken = cancellation.Token;
        ExceptionDispatchInfo? discoveryFailure = null;
        var scans = Enumerable.Range(0, concurrency).Select(_ => Task.Run(async () =>
        {
            try
            {
                while (Volatile.Read(ref unfinished) > 0)
                {
                    discoveryToken.ThrowIfCancellationRequested();
                    if (!pending.TryDequeue(out var dir)) { await Task.Delay(1, discoveryToken).ConfigureAwait(false); continue; }
                    try
                    {
                        var rules = dir.Rules;
                        string ignoreFile = Path.Combine(dir.Directory, ".assetignore");
                        if (!Path.GetFullPath(ignoreFile).Equals(projectIgnore, pathComparison))
                            rules = rules.Concat(IgnoreRule.Load(ignoreFile, dir.Directory)).ToArray();
                        foreach (var item in new DirectoryInfo(dir.Directory).EnumerateFileSystemInfos())
                        {
                            discoveryToken.ThrowIfCancellationRequested();
                            bool isDirectory = item is DirectoryInfo;
                            if ((item.Attributes & FileAttributes.ReparsePoint) != 0 || ProjectConfig.Within(item.FullName, stateRoot) ||
                                project.IsOutputPath(item.FullName, isDirectory)) continue;
                            if (IgnoreRule.IsIgnored(rules, item.FullName, isDirectory)) continue;
                            if (isDirectory)
                            {
                                Interlocked.Increment(ref unfinished);
                                pending.Enqueue(dir with { Directory = item.FullName, Rules = rules }); continue;
                            }
                            if (item.Name == ".assetignore" || Path.GetFullPath(item.FullName).Equals(project.FilePath, pathComparison)) continue;
                            string relative = Path.GetRelativePath(dir.Source.Directory, item.FullName).Replace('\\', '/');
                            if (item.Name.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
                            {
                                string assetPath = item.FullName[..^5];
                                string assetRelative = relative[..^5];
                                if (!File.Exists(assetPath) && Included(assetRelative, dir) && !IgnoreRule.IsIgnored(rules, assetPath, false))
                                    throw new InvalidDataException($"Orphan metadata file: {item.FullName}");
                                continue;
                            }
                            if (!Included(relative, dir)) continue;
                            string path = dir.Source.Prefix.Length == 0 ? relative : dir.Source.Prefix + "/" + relative;
                            AssetPath.Validate(path);
                            string metaPath = item.FullName + ".meta";
                            if (!File.Exists(metaPath) || (File.GetAttributes(metaPath) & FileAttributes.ReparsePoint) != 0 ||
                                IgnoreRule.IsIgnored(rules, metaPath, false) || dir.Excludes.Any(g => g.Matches(relative + ".meta"))) metaPath = null!;
                            candidates.Add(new(dir.Package, path, item.FullName, metaPath));
                            progress?.Advance(files: 1);
                        }
                    }
                    finally { Interlocked.Decrement(ref unfinished); }
                }
            }
            catch (Exception error)
            {
                Interlocked.CompareExchange(ref discoveryFailure, ExceptionDispatchInfo.Capture(error), null);
                cancellation.Cancel();
                throw;
            }
        }, CancellationToken.None)).ToArray();
        try { await Task.WhenAll(scans).ConfigureAwait(false); }
        catch
        {
            discoveryFailure?.Throw();
            throw;
        }
        // Variable values must not change precedence between overlapping source mappings.
        var inputs = candidates.OrderBy(x => x.Package.OrderName, StringComparer.Ordinal).ThenBy(x => x.Path, AssetPath.Comparer).ToArray();
        for (int i = 1; i < inputs.Length; i++)
            if (inputs[i - 1].Package.Name == inputs[i].Package.Name && inputs[i - 1].Path == inputs[i].Path)
                throw new InvalidDataException($"Duplicate path '{inputs[i].Path}' in package '{inputs[i].Package.Name}'.");
        if (progress is not null)
        {
            long totalBytes = 0;
            foreach (var input in inputs)
            {
                token.ThrowIfCancellationRequested();
                totalBytes = checked(totalBytes + new FileInfo(input.FullPath).Length);
                if (input.MetadataPath is { } metadataPath) totalBytes = checked(totalBytes + new FileInfo(metadataPath).Length);
            }
            progress.StartStage("Hashing", totalBytes, inputs.LongLength);
        }
        var output = new SourceAsset[inputs.Length];
        await Parallel.ForEachAsync(Enumerable.Range(0, inputs.Length), new ParallelOptions { MaxDegreeOfParallelism = concurrency, CancellationToken = token }, async (i, ct) =>
        {
            var input = inputs[i]; var before = new FileInfo(input.FullPath); long length = before.Length; DateTime stamp = before.LastWriteTimeUtc;
            await using var file = new FileStream(input.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var tracked = progress is null ? null : new ProgressReadStream(file, progress, advance: true);
            byte[] hash = await SHA256.HashDataAsync((Stream?)tracked ?? file, ct).ConfigureAwait(false);
            before.Refresh();
            if (before.Length != length || before.LastWriteTimeUtc != stamp || file.Length != length) throw new IOException($"Source changed while scanning: {input.FullPath}");
            byte[]? meta = null;
            if (input.MetadataPath is { } metadataPath)
            {
                if (new FileInfo(metadataPath).Length > PackageHeader.MaxMetadataBytes) throw new InvalidDataException($"Metadata exceeds 1 MiB: {metadataPath}");
                if (progress is null)
                    meta = PropertyMetadata.Parse(await File.ReadAllTextAsync(metadataPath, AssetPath.Utf8, ct).ConfigureAwait(false), metadataPath);
                else
                {
                    await using var metadataFile = new FileStream(metadataPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
                    using var reader = new StreamReader(new ProgressReadStream(metadataFile, progress, advance: true), AssetPath.Utf8);
                    meta = PropertyMetadata.Parse(await reader.ReadToEndAsync(ct).ConfigureAwait(false), metadataPath);
                }
            }
            output[i] = new(input.Package.Name, input.Path, input.FullPath, length, hash, meta, meta is null ? null : SHA256.HashData(meta),
                input.Package.Compression, input.Package.CompressionPolicy);
            progress?.Advance(files: 1);
        }).ConfigureAwait(false);
        return output;
    }
    private static bool Included(string path, ScanDirectory dir) => (dir.Includes.Length == 0 || dir.Includes.Any(g => g.Matches(path))) && !dir.Excludes.Any(g => g.Matches(path));
}
