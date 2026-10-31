using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Quire.Cli.Configuration;
using Quire.Cli.Tags;
using Quire.Internal;

internal static class BuildStateBenchmarks
{
    private const int FileCount = 1_000_000, AddedCount = 3_000, UpdatedCount = 4_000, RemovedCount = 3_000;
    private const int Warmups = 2, Rounds = 5, HeaderIterations = 100;
    private static readonly string[] Packages = Enumerable.Range(0, 8).Select(i => $"Pack{i:D2}").ToArray();
    private static readonly string[] Encodings =
    [
        "PK.QUIRE:3:asset:262144:Lz4:Adaptive:False:public",
        "PK.QUIRE:3:asset:262144:Zlib:Adaptive:False:public",
        "PK.QUIRE:3:asset:262144:Lz4:Always:False:public"
    ];

    internal static void Run(string resultPath)
    {
        resultPath = Path.GetFullPath(resultPath);
        string root = Path.Combine(Path.GetTempPath(), "quire-build-state-" + Guid.NewGuid().ToString("N"));
        string state = Path.Combine(root, "current"), legacy = Path.Combine(root, "legacy");
        Directory.CreateDirectory(root);
        try
        {
            FileState[] files = CreateFiles();
            Console.WriteLine($"Prepared {files.Length:N0} synthetic records; no source asset payloads are created.");
            var first = Tag("10000001", "benchmark-full", null, FileCount, 0, 0);
            var second = Tag("10000002", "benchmark-update", first.TagId, AddedCount, UpdatedCount, RemovedCount);
            FileChange[] firstChanges = files.Select(f => new FileChange(f.Package, f.Path, ChangeKind.Added)).ToArray();
            var writes = new List<object>();
            writes.Add(Timed("current:initial_candidate", () => WriteCandidate(state, first, firstChanges, new(files, []))));
            writes.Add(Timed("current:initial_commit", () => TagStore.Commit(state, first.TagId)));
            Require(!File.Exists(DeltaPath(state, first)), "Initial commit retained its pending state.");
            Verify(TagStore.LoadBaseline(state).Files, files);
            firstChanges = [];

            var (updatedFiles, changes, delta) = CreateCandidate(files);
            string baselinePath = Path.Combine(state, "baseline.idx");
            byte[] beforeDigest = FileDigest(baselinePath);
            DateTime beforeTimestamp = File.GetLastWriteTimeUtc(baselinePath);
            writes.Add(Timed("current:10k_candidate", () => WriteCandidate(state, second, changes, delta)));
            Require(beforeDigest.AsSpan().SequenceEqual(FileDigest(baselinePath)) &&
                beforeTimestamp == File.GetLastWriteTimeUtc(baselinePath), "Candidate publication modified the formal baseline.");
            VerifyDelta(TagStore.LoadDelta(state, second.TagId), delta);
            VerifyChanges(TagStore.ReadChanges(state, second.TagId), changes);
            Verify(TagStore.LoadBaseline(state).Files, files);

            writes.Add(Timed("legacy:initial_snapshot", () => LegacyTagStore.Write(LegacyDirectory(legacy, first), LegacyTag(first), files)));
            writes.Add(Timed("legacy:changed_snapshot", () => LegacyTagStore.Write(LegacyDirectory(legacy, second), LegacyTag(second), updatedFiles)));
            Verify(LegacyTagStore.LoadFiles(legacy, first.TagId), files);
            Verify(LegacyTagStore.LoadFiles(legacy, second.TagId), updatedFiles);
            VerifyHeaderOnlyQueries(state, second, root);

            Func<Dictionary<(string Package, string Path), FileState>> readOld = () => LegacyTagStore.LoadFiles(legacy, first.TagId);
            Func<Dictionary<(string Package, string Path), FileState>> readNew = () => TagStore.LoadBaseline(state).Files;
            for (int i = 0; i < Warmups; i++)
            {
                _ = Measure(readOld, value => Verify(value, files));
                _ = Measure(readNew, value => Verify(value, files));
            }
            var oldSamples = new List<Sample>();
            var newSamples = new List<Sample>();
            for (int round = 0; round < Rounds; round++)
            {
                if ((round & 1) == 0)
                {
                    oldSamples.Add(Measure(readOld, value => Verify(value, files)));
                    newSamples.Add(Measure(readNew, value => Verify(value, files)));
                }
                else
                {
                    newSamples.Add(Measure(readNew, value => Verify(value, files)));
                    oldSamples.Add(Measure(readOld, value => Verify(value, files)));
                }
                Console.WriteLine($"Measured full baseline round {round + 1}/{Rounds}.");
            }

            var deltaSamples = RunSeries(() => TagStore.LoadDelta(state, second.TagId), value => VerifyDelta(value, delta));
            var changeSamples = RunSeries(() => TagStore.ReadChanges(state, second.TagId), value => VerifyChanges(value, changes));
            // Matches tags' storage work: lightweight Tag headers plus the committed-ID header.
            var headerSamples = RunSeries(() => (Tags: TagStore.List(state).ToArray(), Committed: TagStore.ReadCommittedTags(state)),
                value => Require(value.Tags.Length == 2 && value.Committed.SequenceEqual([first.TagId]), "Header listing mismatch."), HeaderIterations);

            long baselineBytes = Size(baselinePath), firstHistoryBytes = Size(TagPath(state, first));
            long historyBytes = Size(TagPath(state, second)), pendingBytes = Size(DeltaPath(state, second));
            long oldFirstBytes = Size(Path.Combine(LegacyDirectory(legacy, first), "files.idx"));
            long oldSecondBytes = Size(Path.Combine(LegacyDirectory(legacy, second), "files.idx"));
            long oldMetadataBytes = Size(Path.Combine(LegacyDirectory(legacy, first), "tag.json")) +
                Size(Path.Combine(LegacyDirectory(legacy, second), "tag.json"));

            writes.Add(Timed("current:10k_commit", () => TagStore.Commit(state, second.TagId)));
            var committed = TagStore.LoadBaseline(state);
            Verify(committed.Files, updatedFiles);
            Require(committed.CommittedTags.SequenceEqual([first.TagId, second.TagId]), "Commit chain mismatch.");
            Require(!File.Exists(DeltaPath(state, second)), "Commit retained its pending state.");
            VerifyChanges(TagStore.ReadChanges(state, second.TagId), changes);
            long committedBaselineBytes = Size(baselinePath);
            long newRetained = committedBaselineBytes + firstHistoryBytes + historyBytes;
            long oldRetained = oldFirstBytes + oldSecondBytes + oldMetadataBytes;

            var report = new
            {
                timestampUtc = DateTimeOffset.UtcNow,
                runtime = RuntimeInformation.FrameworkDescription,
                os = RuntimeInformation.OSDescription,
                architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                processorCount = Environment.ProcessorCount,
                cliSha256 = Convert.ToHexString(FileDigest(typeof(TagStore).Assembly.Location)),
                benchmarkSha256 = Convert.ToHexString(FileDigest(typeof(BuildStateBenchmarks).Assembly.Location)),
                filesPerBaseline = FileCount,
                metadataFilesInitially = files.Count(file => file.MetadataHash.HasValue),
                packageCount = Packages.Length,
                candidate = new { added = AddedCount, updated = UpdatedCount, removed = RemovedCount },
                sizes = new
                {
                    legacyInitialIndexBytes = oldFirstBytes, legacyChangedIndexBytes = oldSecondBytes,
                    legacyMetadataBytes = oldMetadataBytes, baselineBytes, firstHistoryBytes,
                    candidateHistoryBytes = historyBytes, candidatePendingBytes = pendingBytes,
                    candidateTotalBytes = historyBytes + pendingBytes,
                    committedBaselineBytes, retainedHistoryBytes = firstHistoryBytes + historyBytes,
                    retainedAfterTwoCommitsBytes = newRetained, legacyTwoSnapshotsBytes = oldRetained,
                    retainedReductionPercent = 100d * (1 - (double)newRetained / oldRetained)
                },
                condition = $"Release synchronous loads, warm OS cache, {Warmups} warmups and {Rounds} alternating full-read rounds. Full GC before each sample; current-thread allocations. All restored fields verified after timing. Header samples average {HeaderIterations} queries per sample. No cold-disk or asset-payload throughput claims.",
                legacyFullRead = Summarize(oldSamples), baselineFullRead = Summarize(newSamples),
                candidateDeltaRead = Summarize(deltaSamples), candidateHistoryRead = Summarize(changeSamples),
                twoTagHeaderQuery = Summarize(headerSamples), writes,
                correctness = "Every field round-trips; candidate leaves baseline bytes and timestamp unchanged; commit applies the frozen delta and removes pending; history survives commit; individual Tag headers load while baseline/pending are exclusively locked; history listing and committed-ID lookup succeed on copies whose resource bodies are zeroed."
            };
            string json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
            Directory.CreateDirectory(Path.GetDirectoryName(resultPath)!);
            File.WriteAllText(resultPath, json);
            Console.WriteLine(json);
            Console.WriteLine(resultPath);
            GC.KeepAlive(files);
            GC.KeepAlive(updatedFiles);
        }
        finally
        {
            string resolved = Path.GetFullPath(root);
            if (!ProjectConfig.Within(resolved, Path.GetFullPath(Path.GetTempPath())) ||
                !Path.GetFileName(resolved).StartsWith("quire-build-state-", StringComparison.Ordinal))
                throw new IOException("Unsafe build-state benchmark cleanup.");
            if (Directory.Exists(resolved)) Directory.Delete(resolved, true);
        }
    }

    private static void VerifyHeaderOnlyQueries(string state, BuildTag candidate, string root)
    {
        using (var baseline = new FileStream(Path.Combine(state, "baseline.idx"), FileMode.Open, FileAccess.Read, FileShare.None))
        using (var pending = new FileStream(DeltaPath(state, candidate), FileMode.Open, FileAccess.Read, FileShare.None))
            Require(TagStore.Load(state, candidate.TagId).Name == candidate.Name, "Loading a Tag header accessed baseline or pending contents.");

        string headerState = Path.Combine(root, "header-only");
        Directory.CreateDirectory(headerState);
        string[] expected = TagStore.ReadCommittedTags(state);
        // ASBASE01's 24-byte header, 4 bytes per committed ID, and a 32-byte ledger checksum.
        // Keep physical lengths valid, but deliberately omit the resource bodies and footer digests.
        CopyHeader(Path.Combine(state, "baseline.idx"), Path.Combine(headerState, "baseline.idx"), 24 + sizeof(uint) * expected.Length + 32);
        foreach (var tag in TagStore.List(state))
            CopyHeader(TagPath(state, tag), TagPath(headerState, tag), 1024);
        Require(TagStore.ReadCommittedTags(headerState).SequenceEqual(expected), "Reading committed IDs requires the resource body.");
        Require(TagStore.List(headerState).Count() == 2, "Listing Tags requires complete resource records.");
        bool fullReadRejected = false;
        try { TagStore.LoadBaseline(headerState); }
        catch (InvalidDataException) { fullReadRejected = true; }
        Require(fullReadRejected, "The header-only copy unexpectedly contains a valid full baseline.");
    }

    private static void CopyHeader(string source, string destination, int bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        using var input = File.OpenRead(source);
        byte[] header = new byte[Math.Min(bytes, checked((int)input.Length))];
        input.ReadExactly(header);
        using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write);
        output.Write(header);
        output.SetLength(input.Length);
    }

    private static (FileState[] Files, FileChange[] Changes, StateDelta Delta) CreateCandidate(FileState[] original)
    {
        var result = new FileState[FileCount - RemovedCount + AddedCount];
        Array.Copy(original, RemovedCount, result, 0, FileCount - RemovedCount);
        var upserts = new FileState[UpdatedCount + AddedCount];
        var removed = new FileKey[RemovedCount];
        var changes = new FileChange[UpdatedCount + AddedCount + RemovedCount];
        for (int i = 0; i < RemovedCount; i++)
        {
            removed[i] = new(original[i].Package, original[i].Path);
            changes[i] = new(original[i].Package, original[i].Path, ChangeKind.Deleted);
        }
        for (int i = 0; i < UpdatedCount; i++)
        {
            var file = result[i] with { Length = result[i].Length + 1, ContentHash = Digest(FileCount + i) };
            result[i] = upserts[i] = file;
            changes[RemovedCount + i] = new(file.Package, file.Path, ChangeKind.Updated);
        }
        for (int i = 0; i < AddedCount; i++)
        {
            var file = original[i] with { Path = $"assets/new_iteration/new_{i:D6}.bin", ContentHash = Digest(2 * FileCount + i) };
            result[FileCount - RemovedCount + i] = upserts[UpdatedCount + i] = file;
            changes[RemovedCount + UpdatedCount + i] = new(file.Package, file.Path, ChangeKind.Added);
        }
        return (result, changes, new(upserts, removed));
    }

    private static FileState[] CreateFiles()
    {
        var files = new FileState[FileCount];
        for (int i = 0; i < files.Length; i++)
        {
            int package = i / 125000;
            string kind = (i % 4) switch { 0 => "animations", 1 => "textures", 2 => "audio", _ => "data" };
            string extension = (i % 4) switch { 0 => "anim", 1 => "png", 2 => "wav", _ => "json" };
            string path = i % 125000 == 0 ? $"shared/p{package:D2}/license.txt" : $"assets/p{package:D2}/group{(i / 1000) % 125:D3}/{kind}/resource_{i:D8}.{extension}";
            long length = (i % 100) switch
            {
                0 => 0, <= 10 => 1 + i % 127, <= 89 => 1024 + (long)i * 7919 % 100000,
                <= 98 => 100000 + (long)i * 104729 % 4000000, _ => (1L << 32) + i
            };
            files[i] = new(Packages[package], path, length, Digest(i), i % 101 == 0 ? Digest(i, 1) : (ContentDigest?)null, Encodings[i % Encodings.Length]);
        }
        return files;
    }

    private static ContentDigest Digest(int value, byte domain = 0)
    {
        Span<byte> seed = stackalloc byte[9];
        BinaryPrimitives.WriteInt64LittleEndian(seed, value);
        seed[8] = domain;
        Span<byte> digest = stackalloc byte[32];
        SHA256.HashData(seed, digest);
        return new(digest);
    }

    private static BuildTag Tag(string id, string name, string? parent, int added, int updated, int removed) =>
        new(id, new DateTimeOffset(2026, 10, 31, 0, 0, id == "10000001" ? 0 : 1, TimeSpan.Zero), parent,
            new string('0', 64), [], added + updated, 0, removed)
        { Name = name, AddedFiles = added, UpdatedFiles = updated, RemovedFiles = removed };

    private static LegacyBuildTag LegacyTag(BuildTag tag) =>
        new(tag.TagId, tag.BuildTimeUtc, tag.BaseTag, tag.ConfigHash, tag.Outputs, tag.ContentChanges, tag.MetadataChanges, tag.DeletedChanges);
    private static string TagPath(string state, BuildTag tag) => Path.Combine(state, "tags", tag.TagId + ".idx");
    private static string DeltaPath(string state, BuildTag tag) => Path.Combine(state, "pending", tag.TagId + ".idx");
    private static string LegacyDirectory(string state, BuildTag tag) => Path.Combine(state, "tags", tag.TagId);
    private static long Size(string path) => new FileInfo(path).Length;
    private static byte[] FileDigest(string path) { using var stream = File.OpenRead(path); return SHA256.HashData(stream); }
    private static void WriteCandidate(string state, BuildTag tag, FileChange[] changes, StateDelta delta) =>
        TagStore.WriteCandidate(state, TagPath(state, tag), DeltaPath(state, tag), tag, changes, delta);

    private static void Verify(Dictionary<(string Package, string Path), FileState> actual, FileState[] expected)
    {
        Require(actual.Count == expected.Length, "File count mismatch.");
        foreach (var file in expected)
            Require(actual.TryGetValue((file.Package, file.Path), out var restored) && restored == file, "File state mismatch.");
    }

    private static void VerifyDelta(StateDelta actual, StateDelta expected)
    {
        Verify(actual.Upserts.ToDictionary(file => (file.Package, file.Path)), expected.Upserts);
        Require(actual.Removed.Length == expected.Removed.Length && actual.Removed.ToHashSet().SetEquals(expected.Removed), "Removed keys mismatch.");
    }

    private static void VerifyChanges(FileChange[] actual, FileChange[] expected) =>
        Require(actual.Length == expected.Length && actual.ToHashSet().SetEquals(expected), "Change history mismatch.");

    private static List<Sample> RunSeries<T>(Func<T> action, Action<T> validate, int iterations = 1)
    {
        for (int i = 0; i < Warmups; i++) _ = Measure(action, validate, iterations);
        var samples = new List<Sample>(Rounds);
        for (int i = 0; i < Rounds; i++) samples.Add(Measure(action, validate, iterations));
        return samples;
    }

    private static Sample Measure<T>(Func<T> action, Action<T> validate, int iterations = 1)
    {
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        long allocated = GC.GetAllocatedBytesForCurrentThread(), start = Stopwatch.GetTimestamp();
        T result = default!;
        for (int i = 0; i < iterations; i++) result = action();
        double elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
        validate(result);
        GC.KeepAlive(result);
        return new(elapsed / iterations, allocated / iterations);
    }

    private static object Timed(string name, Action action)
    {
        long allocated = GC.GetAllocatedBytesForCurrentThread(), start = Stopwatch.GetTimestamp();
        action();
        return new { name, elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds,
            allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocated };
    }

    private static object Summarize(List<Sample> samples) => new
    {
        medianMs = samples.Select(s => s.ElapsedMs).Order().ElementAt(samples.Count / 2),
        minMs = samples.Min(s => s.ElapsedMs), maxMs = samples.Max(s => s.ElapsedMs),
        allocatedBytes = samples.Select(s => s.AllocatedBytes).Order().ElementAt(samples.Count / 2), samples
    };
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
    private sealed record Sample(double ElapsedMs, long AllocatedBytes);
}
