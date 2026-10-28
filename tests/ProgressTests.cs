using Quire.Cli;
using Quire.Cli.Build;
using Quire.Cli.Extraction;
using Quire.Cli.Progress;
using Quire.Cli.Sources;
using Quire.Package;
using Quire.Package.Internal;

internal static class ProgressTests
{
    internal static void Register(Action<string, Func<Task>> test)
    {
        foreach (bool compressed in new[] { false, true })
        test($"progress updates within a large file and measures transferred bytes, compressed={compressed}", async () =>
        {
            using var f = new Fixture(compressed);
            const int length = 3 * 1024 * 1024 + 17;
            f.Put("large.bin", new byte[length]);
            var build = new RecordingProgress();
            await ProjectBuilder.BuildAsync(f.Config(), null, f.BuildOptions, null, default, build);
            AssertCompleted(build);
            foreach (string stage in new[] { "Hashing", "Writing", "Verifying" })
            {
                var work = build.Stage(stage);
                Check.Equal((long)length, work.TotalBytes);
                Check.Equal(1L, work.TotalFiles);
                AssertStreaming(work);
            }
            Check.True(build.ReadBytes >= 2L * length, "Packaging must count both hash and writer source reads.");
            Check.True(build.WrittenBytes > 0);

            var verify = new RecordingProgress();
            using (var packages = await PackageSet.OpenDirectoryAsync(f.Output, f.ReadOptions with { Progress = verify }))
                await packages.VerifyAsync();
            AssertCompleted(verify); AssertStreaming(verify.Stage("Verifying"));
            Check.Equal(0L, verify.WrittenBytes);
            Check.True(verify.ReadBytes > 0);
            Check.True(compressed ? verify.ReadBytes < length : verify.ReadBytes >= length,
                "Verification I/O must count encoded package reads, not decoded content bytes.");

            string destination = Path.Combine(f.Root, "unpacked");
            var extract = new RecordingProgress();
            await PackageExtractor.ExtractAsync(f.Output, destination, f.ReadOptions, default, 4, extract);
            AssertCompleted(extract); AssertStreaming(extract.Stage("Extracting"));
            Check.Equal((long)length, extract.WrittenBytes);
            Check.True(compressed ? extract.ReadBytes < length : extract.ReadBytes >= length,
                "Extraction must distinguish package reads from restored file writes.");
            Check.Equal((long)length, new FileInfo(Path.Combine(destination, "common/data/large.bin")).Length);
        });

        test("progress handles empty builds, metadata-only changes and historical verification", async () =>
        {
            using var f = new Fixture();
            var empty = new RecordingProgress();
            var baseline = await ProjectBuilder.BuildAsync(f.Config(), null, f.BuildOptions, null, default, empty);
            Check.Equal(0, baseline.Outputs.Length); AssertCompleted(empty);
            Check.Equal(0L, empty.Stage("Hashing").TotalFiles);

            f.Put("empty", []); f.Text("empty.meta", "");
            f.Put("a", [1, 2, 3]); f.Text("a.meta", "version=1");
            var first = await f.Build(baseline.TagId);
            f.Text("a.meta", "version=2");
            var meta = new RecordingProgress();
            var patch = await ProjectBuilder.BuildAsync(f.Config(), first.TagId, f.BuildOptions, null, default, meta);
            Check.Equal(1, patch.MetadataChanges); Check.Equal(0, patch.ContentChanges);
            AssertCompleted(meta);
            Check.Equal((long)PropertyMetadata.Parse("version=2").Length, meta.Stage("Writing").TotalBytes);
            Check.Equal(1L, meta.Stage("Writing").TotalFiles);

            var unchanged = new RecordingProgress();
            var noChange = await ProjectBuilder.BuildAsync(f.Config(), patch.TagId, f.BuildOptions, null, default, unchanged);
            Check.Equal(0, noChange.Outputs.Length); AssertCompleted(unchanged);
            Check.Equal(0L, unchanged.Stage("Writing").TotalFiles);

            foreach (bool synchronous in new[] { false, true })
            {
                var verify = new RecordingProgress();
                using var packages = await PackageSet.OpenDirectoryAsync(f.Output, f.ReadOptions with { Progress = verify });
                if (synchronous) packages.Verify(); else await packages.VerifyAsync();
                AssertCompleted(verify);
                Check.Equal(3L, verify.Stage("Verifying").TotalFiles);
                long expected = 3L + PropertyMetadata.Parse("").Length +
                    PropertyMetadata.Parse("version=1").Length + PropertyMetadata.Parse("version=2").Length;
                Check.Equal(expected, verify.Stage("Verifying").TotalBytes);
            }

            string destination = Path.Combine(f.Root, "unpacked");
            var extract = new RecordingProgress();
            var result = await PackageExtractor.ExtractAsync(f.Output, destination, f.ReadOptions, default, 4, extract);
            AssertCompleted(extract);
            Check.Equal(2L, result.Assets); Check.Equal(2L, result.MetadataFiles);
            long written = Directory.EnumerateFiles(destination, "*", SearchOption.AllDirectories).Sum(p => new FileInfo(p).Length);
            Check.Equal(written, extract.WrittenBytes);
        });

        test("progress does not complete corrupt or canceled verification and extraction", async () =>
        {
            using var f = new Fixture(false); f.Put("a", new byte[1024 * 1024]);
            var tag = await f.Build();
            using var cancel = new CancellationTokenSource();
            var canceled = new RecordingProgress((stage, bytes, _) =>
            {
                if (stage == "Extracting" && bytes > 0) cancel.Cancel();
            });
            string destination = Path.Combine(f.Root, "canceled");
            await Check.ThrowsAsync<OperationCanceledException>(() =>
                PackageExtractor.ExtractAsync(f.Output, destination, f.ReadOptions, cancel.Token, 1, canceled));
            Check.Equal(0L, canceled.Stage("Extracting").Files);
            Check.True(!Directory.Exists(destination));

            string path = Path.Combine(f.Root, tag.Outputs.Single());
            byte[] bytes = File.ReadAllBytes(path); bytes[^1] ^= 1; File.WriteAllBytes(path, bytes);
            var verify = new RecordingProgress();
            using (var packages = await PackageSet.OpenDirectoryAsync(f.Output, f.ReadOptions with { Progress = verify }))
                await Check.ThrowsAsync<InvalidDataException>(() => packages.VerifyAsync());
            Check.Equal(0L, verify.Stage("Verifying").Files);
            Check.True(verify.Stage("Verifying").Bytes > 0);

            var extract = new RecordingProgress();
            destination = Path.Combine(f.Root, "corrupt");
            await Check.ThrowsAsync<InvalidDataException>(() =>
                PackageExtractor.ExtractAsync(f.Output, destination, f.ReadOptions, default, 1, extract));
            Check.Equal(0L, extract.Stage("Extracting").Files);
            Check.True(!Directory.Exists(destination));
        });

        test("read counters count sync, async and mapped transfers exactly once", async () =>
        {
            using var f = new Fixture(); byte[] expected = Enumerable.Range(0, 100).Select(i => (byte)i).ToArray();
            f.Put("raw", expected);
            foreach (bool mapped in new[] { false, true })
            {
                var progress = new RecordingProgress();
                using var storage = new FileStorage(Path.Combine(f.Input, "raw"), mapped, progress);
                byte[] bytes = new byte[17]; storage.Read(bytes, 1); Check.Bytes(expected.AsSpan(1, 17), bytes);
                await storage.ReadAsync(bytes, 20, default); Check.Bytes(expected.AsSpan(20, 17), bytes);
                storage.Read(Span<byte>.Empty, 100);
                Check.Equal(34L, progress.ReadBytes); Check.Equal(0L, progress.WrittenBytes);
                Check.Throws<EndOfStreamException>(() => storage.Read(bytes, 99));
                Check.Equal(34L, progress.ReadBytes);
            }
        });

        test("redirected progress preserves plain logs with metrics and explicit success", () =>
        {
            using var log = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
            using (var progress = new ConsoleProgress("package", log, false, false, () => 100, autoRefresh: false))
            {
                progress.StartStage("Writing", 1024 * 1024, 1);
                progress.AddRead(1024 * 1024); progress.AddWritten(512 * 1024);
                progress.Advance(1024 * 1024, 1); progress.Complete();
            }
            string text = log.ToString();
            Check.True(!text.Contains('\u001b') && !text.Replace(Environment.NewLine, "").Contains('\r'),
                "Redirected logs must not contain terminal cursor controls.");
            foreach (string expected in new[] { "[package]", "Done", "100.0", "1/1 files", "Read", "Write", "1.0 MiB", "512.0 KiB", "/s", "Elapsed", "ETA 00:00" })
                Check.True(text.Contains(expected, StringComparison.Ordinal), $"Progress log is missing {expected}.");
            return Task.CompletedTask;
        });

        test("interactive progress fits narrow terminals and waits for file validation", () =>
        {
            foreach (int width in new[] { 40, 100 })
            {
                using var log = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
                using var progress = new ConsoleProgress("verify", log, true, false, () => width, autoRefresh: false);
                progress.StartStage("Verifying", 1000, 1);
                progress.AddRead(1000); progress.Advance(1000); progress.Refresh();
                Check.True(log.ToString().Contains("99.9", StringComparison.Ordinal),
                    "Reading all content bytes must not imply a successful checksum.");
                progress.Advance(files: 1); progress.Complete();
                string visible = System.Text.RegularExpressions.Regex.Replace(log.ToString(), "\u001b\\[[0-9;]*[A-Za-z]", "");
                foreach (string line in visible.Split(['\r', '\n']))
                    Check.True(line.Length < width, $"Progress line would wrap at width {width}: {line}");
                Check.True(visible.Contains("Done", StringComparison.Ordinal));
                Check.True(visible.Contains("100.0", StringComparison.Ordinal));
            }
            return Task.CompletedTask;
        });

        test("failed and canceled progress never reports success", () =>
        {
            foreach (bool canceled in new[] { false, true })
            {
                using var log = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
                using (var progress = new ConsoleProgress("verify", log, false, false, () => 80, autoRefresh: false))
                {
                    progress.StartStage("Verifying", 100, 1); progress.Advance(100);
                    progress.Fail(canceled); progress.Complete();
                }
                string text = log.ToString();
                Check.True(text.Contains(canceled ? "Canceled" : "Failed", StringComparison.Ordinal));
                Check.True(!text.Contains("Done", StringComparison.Ordinal));
                Check.True(!text.Contains("100.0", StringComparison.Ordinal));
            }
            return Task.CompletedTask;
        });

        test("package, verify and unpackage keep progress on stderr and results on stdout", async () =>
        {
            using var f = new Fixture(false); f.Put("a", [1, 2, 3]);
            using var results = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
            using var status = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
            var originalOut = Console.Out; var originalError = Console.Error;
            try
            {
                Console.SetOut(results); Console.SetError(status);
                foreach (string[] command in new string[][]
                {
                    ["package", "-project", f.ProjectPath],
                    ["verify", f.Output],
                    ["unpackage", f.Output, Path.Combine(f.Root, "unpacked")]
                })
                {
                    results.GetStringBuilder().Clear(); status.GetStringBuilder().Clear();
                    Check.Equal(0, await CommandLine.RunAsync(command, default));
                    Check.True(results.ToString().Length > 0);
                    Check.True(!results.ToString().Contains("ETA", StringComparison.Ordinal));
                    foreach (string metric in new[] { "Read", "Write", "ETA", "Done" })
                        Check.True(status.ToString().Contains(metric, StringComparison.Ordinal), $"{command[0]} did not report {metric} to stderr.");
                }
                string package = Directory.EnumerateFiles(f.Output, "*.asset", SearchOption.AllDirectories).Single();
                byte[] bytes = File.ReadAllBytes(package); bytes[^1] ^= 1; File.WriteAllBytes(package, bytes);
                results.GetStringBuilder().Clear(); status.GetStringBuilder().Clear();
                await Check.ThrowsAsync<InvalidDataException>(() => CommandLine.RunAsync(["verify", f.Output], default));
                Check.True(status.ToString().Contains("Failed", StringComparison.Ordinal));
                Check.True(!status.ToString().Contains("Done", StringComparison.Ordinal));
                Check.True(!results.ToString().Contains("Verification passed", StringComparison.Ordinal));
            }
            finally { Console.SetOut(originalOut); Console.SetError(originalError); }
        });
    }

    private static void AssertStreaming(StageProgress stage) => Check.True(
        stage.Snapshots.Count(s => s.Bytes > 0 && s.Bytes < stage.TotalBytes && s.Files == 0) >= 2,
        $"{stage.Name} must report multiple partial chunks before completing a single large file.");

    private static void AssertCompleted(RecordingProgress progress)
    {
        foreach (var stage in progress.Stages)
        {
            if (stage.TotalBytes >= 0) Check.Equal(stage.TotalBytes, stage.Bytes);
            if (stage.TotalFiles >= 0) Check.Equal(stage.TotalFiles, stage.Files);
            long priorBytes = 0, priorFiles = 0;
            foreach (var snapshot in stage.Snapshots)
            {
                Check.True(snapshot.Bytes >= priorBytes && snapshot.Files >= priorFiles, "Progress must be monotonic within each stage.");
                if (stage.TotalBytes >= 0) Check.True(snapshot.Bytes <= stage.TotalBytes, "Work bytes exceeded their announced total.");
                if (stage.TotalFiles >= 0) Check.True(snapshot.Files <= stage.TotalFiles, "Completed files exceeded their announced total.");
                priorBytes = snapshot.Bytes; priorFiles = snapshot.Files;
            }
        }
    }

    private sealed class StageProgress(string name, long totalBytes, long totalFiles)
    {
        internal string Name { get; } = name;
        internal long TotalBytes { get; } = totalBytes;
        internal long TotalFiles { get; } = totalFiles;
        internal long Bytes, Files;
        internal List<(long Bytes, long Files)> Snapshots { get; } = [];
    }

    private sealed class RecordingProgress(Action<string, long, long>? advanced = null) : IOperationProgress
    {
        private readonly object _gate = new();
        private StageProgress? _current;
        internal List<StageProgress> Stages { get; } = [];
        internal long ReadBytes, WrittenBytes;
        internal StageProgress Stage(string name) => Stages.Single(s => s.Name == name);
        public void StartStage(string stage, long totalBytes = -1, long totalFiles = -1)
        {
            lock (_gate) { _current = new(stage, totalBytes, totalFiles); Stages.Add(_current); }
        }
        public void Advance(long bytes = 0, long files = 0)
        {
            lock (_gate)
            {
                var stage = _current ?? throw new InvalidOperationException("No progress stage was announced.");
                stage.Bytes += bytes; stage.Files += files; stage.Snapshots.Add((stage.Bytes, stage.Files));
                advanced?.Invoke(stage.Name, stage.Bytes, stage.Files);
            }
        }
        public void AddRead(long bytes) { lock (_gate) ReadBytes += bytes; }
        public void AddWritten(long bytes) { lock (_gate) WrittenBytes += bytes; }
    }
}
