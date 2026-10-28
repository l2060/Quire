using Quire.Cli.Configuration;
using Quire.Cli.Sources;
using Quire.Package;
using Quire.Package.Internal;

namespace Quire.Cli.Extraction;

internal static class PackageExtractor
{
    internal static int DefaultJobs => Math.Min(Environment.ProcessorCount, 8);

    internal static async Task<(long Assets, long MetadataFiles)> ExtractAsync(string sourceDirectory, string destinationDirectory,
        ReadOptions options, CancellationToken token, int? jobs = null, IOperationProgress? progress = null)
    {
        token.ThrowIfCancellationRequested();
        int concurrency = jobs ?? DefaultJobs;
        if (concurrency is < 1 or > 256) throw new ArgumentOutOfRangeException(nameof(jobs), "Extraction jobs must be between 1 and 256.");
        string source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourceDirectory));
        string destination = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destinationDirectory));
        if (ProjectConfig.Within(source, destination) || ProjectConfig.Within(destination, source))
            throw new ArgumentException("Source and destination directories must not overlap.");
        RejectLinks(source); RejectLinks(destination);
        RequireEmpty(destination);
        progress ??= options.Progress;
        if (progress is not null) options = options with { Progress = progress };
        using var packages = await PackageSet.OpenDirectoryAsync(source, options, token).ConfigureAwait(false);
        if (packages.Packages.Count == 0) throw new InvalidDataException($"No .asset packages found in '{source}'.");
        if (progress is not null)
        {
            progress.StartStage("Preparing extraction");
            long totalBytes = 0, totalFiles = 0;
            foreach (var asset in packages.Enumerate())
            {
                token.ThrowIfCancellationRequested();
                totalBytes = checked(totalBytes + asset.Length);
                totalFiles = checked(totalFiles + 1 + (asset.HasMetadata ? 1 : 0));
            }
            // Content bytes drive the bar; sidecars also contribute to file completion and I/O.
            progress.StartStage("Extracting", totalBytes, totalFiles);
        }
        string parent = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(parent);
        string staging = Path.Combine(parent, ".asset-unpackage-" + Guid.NewGuid().ToString("N") + ".tmp");
        Directory.CreateDirectory(staging);
        long assets = 0, metadataFiles = 0;
        try
        {
            await Parallel.ForEachAsync(packages.Enumerate(), new ParallelOptions { MaxDegreeOfParallelism = concurrency, CancellationToken = token }, async (asset, ct) =>
            {
                string path = DestinationPath(staging, asset.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await using (var file = CreateFile(path))
                    await packages.CopyToVerifiedAsync(asset.Path, file, ct, progress).ConfigureAwait(false);
                progress?.Advance(files: 1);
                if (asset.HasMetadata)
                {
                    var metadata = await packages.ReadMetadataAsync(asset.Path, ct).ConfigureAwait(false);
                    await using (var file = CreateFile(DestinationPath(staging, asset.Path + ".meta")))
                    {
                        await using (var writer = new StreamWriter(file, AssetPath.Utf8, leaveOpen: true))
                            await writer.WriteAsync(PropertyMetadata.Format(metadata).AsMemory(), ct).ConfigureAwait(false);
                        progress?.AddWritten(file.Position);
                    }
                    Interlocked.Increment(ref metadataFiles);
                    progress?.Advance(files: 1);
                }
                Interlocked.Increment(ref assets);
            }).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            progress?.StartStage("Publishing extraction");
            RejectLinks(destination);
            RequireEmpty(destination);
            if (Directory.Exists(destination)) Directory.Delete(destination);
            Directory.Move(staging, destination);
            return (assets, metadataFiles);
        }
        finally { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
    }

    private static FileStream CreateFile(string path) => new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
        65536, FileOptions.Asynchronous | FileOptions.SequentialScan);

    private static void RequireEmpty(string directory)
    {
        if (File.Exists(directory) || (Directory.Exists(directory) && Directory.EnumerateFileSystemEntries(directory).Any()))
            throw new IOException($"Destination must be a new or empty directory: {directory}");
    }

    private static void RejectLinks(string path)
    {
        for (DirectoryInfo? directory = new(path); directory is not null; directory = directory.Parent)
        {
            FileAttributes attributes;
            try { attributes = File.GetAttributes(directory.FullName); }
            catch (FileNotFoundException) { continue; }
            catch (DirectoryNotFoundException) { continue; }
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException($"Extraction paths must not contain symbolic links or junctions: {directory.FullName}");
        }
    }

    private static string DestinationPath(string root, string assetPath)
    {
        AssetPath.Validate(assetPath);
        if (OperatingSystem.IsWindows())
        {
            foreach (string segment in assetPath.Split('/'))
            {
                string name = segment.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
                bool device = name is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" ||
                    (name.Length == 4 && (name.StartsWith("COM", StringComparison.Ordinal) || name.StartsWith("LPT", StringComparison.Ordinal)) &&
                     "123456789¹²³".Contains(name[3]));
                if (segment.EndsWith(' ') || segment.EndsWith('.') || device)
                    throw new InvalidDataException($"Asset path cannot be extracted on Windows: {assetPath}");
            }
        }
        string path = Path.GetFullPath(Path.Combine(root, assetPath.Replace('/', Path.DirectorySeparatorChar)));
        if (!ProjectConfig.Within(path, root) || path == root) throw new InvalidDataException($"Asset path escapes the destination: {assetPath}");
        return path;
    }
}
