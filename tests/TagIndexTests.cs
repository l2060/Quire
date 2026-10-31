using System.Buffers.Binary;
using System.Security.Cryptography;
using Quire.Cli.Configuration;
using Quire.Cli.Tags;
using Quire.Internal;

internal static class TagIndexTests
{
    private const string Encoding = "PK.QUIRE:3:asset:262144:Zlib:Adaptive:True:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    internal static void Register(Action<string, Func<Task>> test)
    {
        test("Baseline and candidate codecs round trip Unicode, metadata presence and 64-bit lengths", RoundTripAsync);
        test("History stores only changes while one complete baseline retains file fingerprints", CompactAsync);
        test("Tag listing reads headers without parsing baseline, pending or historical change bodies", HeaderQueriesAsync);
        test("Tag change indexes reject malformed headers, paths, kinds and old formats", InvalidTagsAsync);
        test("Baseline indexes reject bad dimensions, checksums, tables, prefixes and truncation", InvalidBaselinesAsync);
        test("Candidate deltas reject corruption and parent mismatch without changing the baseline", InvalidDeltasAsync);
        test("Candidate writers reject invalid states and clean up failed paired writes", InvalidWritesAsync);
    }

    private static Task RoundTripAsync()
    {
        using var fixture = new Fixture();
        using var duplicate = new Fixture();
        string state = fixture.Config().StateDirectory;
        var initial = Tag("ffffffff");
        Write(fixture, initial, [], new([], []));
        TagStore.Commit(state, initial.TagId);
        Check.Equal(0, TagStore.LoadBaseline(state).Files.Count);
        ContentDigest zero = new(new byte[32]);
        string longest = "max/" + new string('x', 4092);
        FileState[] files =
        [
            State("main", "shared/\u00e90", 0),
            State("main", "shared/\u00ea0", 1) with { MetadataHash = zero },
            State("main", "shared/\U0001f6000", (1L << 32) + 17) with { MetadataHash = Hash("meta") },
            State("main", "shared/\U0001f6010", long.MaxValue) with { Encoding = "PK.QUIRE:3:asset:65536:Lz4:Always:False:public" },
            State("main", longest, 123),
            State("Other", "other/\u00e90", 23),
            State("other", "OTHER/\u00e90", 41),
            State(new string('\u957f', 60) + ". v1", "unicode/package", 5)
        ];
        var tag = Tag("12345678") with
        {
            Name = "release-\u6d4b\u8bd5", BaseTag = initial.TagId,
            BuildTimeUtc = new DateTimeOffset(2026, 10, 31, 12, 34, 56, TimeSpan.FromHours(8)).AddTicks(1234),
            ConfigHash = Convert.ToHexString(SHA256.HashData("config"u8)),
            ContentChanges = files.Length, MetadataChanges = 128, DeletedChanges = 9999, AddedFiles = files.Length
        };
        FileChange[] changes = files.Select(file => new FileChange(file.Package, file.Path, ChangeKind.Added)).ToArray();
        Write(fixture, tag, changes, new(files, []));
        Write(duplicate, tag, changes.Reverse().ToArray(), new(files.Reverse().ToArray(), []));
        string otherState = duplicate.Config().StateDirectory;
        Check.Bytes(File.ReadAllBytes(TagPath(state, tag.TagId)), File.ReadAllBytes(TagPath(otherState, tag.TagId)));
        Check.Bytes(File.ReadAllBytes(DeltaPath(state, tag.TagId)), File.ReadAllBytes(DeltaPath(otherState, tag.TagId)));
        AssertTag(tag, TagStore.Load(state, tag.TagId));
        AssertChanges(changes, TagStore.ReadChanges(state, tag.TagId));
        AssertStates(files, TagStore.LoadDelta(state, tag.TagId).Upserts);
        TagStore.Commit(state, tag.TagId);
        var baseline = TagStore.LoadBaseline(state);
        AssertStates(files, baseline.Files.Values);
        Check.True(baseline.CommittedTags.SequenceEqual(new[] { initial.TagId, tag.TagId }));
        Check.True(baseline.Files[("main", "shared/\u00e90")].MetadataHash is null);
        Check.True(baseline.Files[("main", "shared/\u00ea0")].MetadataHash is { IsZero: true });
        Check.Equal(4096, AssetPath.Utf8.GetByteCount(baseline.Files[("main", longest)].Path));
        Check.True(ReferenceEquals(baseline.Files[("main", "shared/\u00e90")].Encoding, baseline.Files[("main", "shared/\u00ea0")].Encoding));
        Check.True(!File.Exists(DeltaPath(state, tag.TagId)));
        Check.Equal(2, TagStore.List(state).Count());
        Check.Equal(0, Directory.GetDirectories(Path.Combine(state, "tags")).Length);
        return Task.CompletedTask;
    }

    private static Task CompactAsync()
    {
        using var fixture = new Fixture();
        string state = fixture.Config().StateDirectory;
        const int count = 6000;
        var files = new FileState[count];
        for (int i = 0; i < files.Length; i++)
            files[i] = new(i < count / 2 ? "desktop" : "mobile", $"Assets/Characters/Hero/Animations/Clip{i:D6}.anim", i * 31L,
                new ContentDigest(SHA256.HashData(BitConverter.GetBytes(i))), i % 5 == 0 ? Hash("meta") : (ContentDigest?)null, new string(Encoding.ToCharArray()));
        var first = AddedTag("00000001", files.Length);
        Write(fixture, first, Added(files), new(files, []));
        TagStore.Commit(state, first.TagId);
        string baselineFile = Path.Combine(state, "baseline.idx");
        long initialSize = new FileInfo(baselineFile).Length;
        long firstHistorySize = new FileInfo(TagPath(state, first.TagId)).Length;
        Check.True(firstHistorySize < initialSize * 0.35, $"History={firstHistorySize}; full baseline={initialSize} bytes.");
        byte[] hash = SHA256.HashData(BitConverter.GetBytes(100));
        Check.True(File.ReadAllBytes(TagPath(state, first.TagId)).AsSpan().IndexOf(hash) < 0, "Historical changes must not store content fingerprints.");
        var head = first;
        for (int i = 2; i <= 6; i++)
        {
            var tag = Tag(i.ToString("x8")) with { BaseTag = head.TagId };
            Write(fixture, tag, [], new([], []));
            Check.True(new FileInfo(DeltaPath(state, tag.TagId)).Length < 128);
            TagStore.Commit(state, tag.TagId);
            Check.True(new FileInfo(TagPath(state, tag.TagId)).Length < 256);
            head = tag;
        }
        Check.Equal(initialSize + 5 * sizeof(uint), new FileInfo(baselineFile).Length);
        Check.Equal(0, Directory.GetFiles(Path.Combine(state, "pending"), "*.idx").Length);
        long totalSize = Directory.GetFiles(state, "*.idx", SearchOption.AllDirectories).Sum(path => new FileInfo(path).Length);
        Check.True(totalSize < initialSize * 1.4, $"Six committed versions should keep only one full state; total={totalSize}, baseline={initialSize} bytes.");
        AssertStates(files, TagStore.LoadBaseline(state).Files.Values);
        return Task.CompletedTask;
    }

    private static Task HeaderQueriesAsync()
    {
        using var fixture = new Fixture();
        string state = fixture.Config().StateDirectory;
        FileState[] files = [State("main", "shared/a", 1), State("main", "shared/b", 2)];
        var a = AddedTag("00000001", files.Length);
        Write(fixture, a, Added(files), new(files, []));
        TagStore.Commit(state, a.TagId);
        var b = Tag("00000002") with { BaseTag = a.TagId, UpdatedFiles = 1 };
        Write(fixture, b, [new("main", "shared/a", ChangeKind.Updated)], new([files[0] with { Length = 4 }], []));
        string baselineFile = Path.Combine(state, "baseline.idx");
        byte[] baselineBytes = File.ReadAllBytes(baselineFile);
        var layout = ParseFiles(baselineBytes, 24 + 4 + 32, files.Length, Int(baselineBytes, 16));
        byte[] corruptBaseline = (byte[])baselineBytes.Clone();
        corruptBaseline[layout.Records[0] + 17] ^= 1;
        File.WriteAllBytes(baselineFile, corruptBaseline);
        File.WriteAllBytes(DeltaPath(state, b.TagId), [1]);
        Check.True(TagStore.ReadCommittedTags(state).SequenceEqual(new[] { a.TagId }));
        Check.Equal(2, TagStore.List(state).Count());
        AssertChanges(Added(files), TagStore.ReadChanges(state, a.TagId));
        Check.Throws<InvalidDataException>(() => TagStore.LoadBaseline(state));
        Check.Throws<InvalidDataException>(() => TagStore.LoadDelta(state, b.TagId));
        byte[] tagBytes = File.ReadAllBytes(TagPath(state, a.TagId));
        var tagLayout = ParseTagKeys(tagBytes);
        tagBytes[tagLayout.Records[0] + 8] = 0xff;
        File.WriteAllBytes(TagPath(state, a.TagId), tagBytes);
        AssertTag(a, TagStore.Load(state, a.TagId));
        Check.Equal(2, TagStore.List(state).Count());
        Check.Throws<InvalidDataException>(() => TagStore.ReadChanges(state, a.TagId));
        return Task.CompletedTask;
    }

    private static Task InvalidTagsAsync()
    {
        using var fixture = new Fixture();
        string state = fixture.Config().StateDirectory;
        FileState[] files = [State("main", "shared/a", 1), State("main", "shared/b", 2), State("next", "shared/c", 3)];
        var tag = AddedTag("bad01234", files.Length) with { Outputs = ["dist/main.asset"] };
        Write(fixture, tag, Added(files), new(files, []));
        string path = TagPath(state, tag.TagId);
        byte[] original = File.ReadAllBytes(path);
        var keys = ParseTagKeys(original);
        void Reject(Action<byte[]> change)
        {
            byte[] bytes = (byte[])original.Clone(); change(bytes); File.WriteAllBytes(path, bytes);
            Check.Throws<InvalidDataException>(() => TagStore.ReadChanges(state, tag.TagId));
        }
        Reject(bytes => bytes[8] ^= 1);
        Reject(bytes => BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(12), -1));
        Reject(bytes => BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(20), (ulong)uint.MaxValue + 2));
        foreach (int offset in new[] { 60, 64, 68, 72, 76, 80, 84, 88, 92, 96 })
            Reject(bytes => PutInt(bytes, offset, -1));
        Reject(bytes => PutInt(bytes, 88, int.MaxValue));
        Reject(bytes => PutInt(bytes, 92, 0));
        Reject(bytes => PutInt(bytes, 96, 513));
        Reject(bytes => bytes[100] = 0xff);
        Reject(bytes => bytes[100] = (byte)'/');
        Reject(bytes => PutInt(bytes, keys.Strings[0], PackageName.MaxUtf8Bytes + 1));
        Reject(bytes => bytes[keys.Strings[0] + 4] = (byte)'/');
        Reject(bytes => "main"u8.CopyTo(bytes.AsSpan(keys.Strings[1] + 4)));
        Reject(bytes => PutInt(bytes, keys.Records[0], 2));
        Reject(bytes => BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(keys.Records[0] + 4), 1));
        Reject(bytes => BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(keys.Records[1] + 4), 4096));
        Reject(bytes => BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(keys.Records[1] + 6), 4097));
        Reject(bytes => bytes[keys.Records[0] + 8] = 0);
        Reject(bytes => bytes[keys.Records[0] + 8] = (byte)ChangeKind.Updated);
        Reject(bytes => bytes[keys.Suffixes[0]] = 0xff);
        Reject(bytes => bytes[keys.Suffixes[1]] = (byte)'a');
        foreach (int length in new[] { 0, 8, 99, 100, keys.Records[0] + 8, original.Length - 1 })
        {
            File.WriteAllBytes(path, original.AsSpan(0, length).ToArray());
            Check.Throws<InvalidDataException>(() => TagStore.ReadChanges(state, tag.TagId));
        }
        File.WriteAllBytes(path, [.. original, 0]);
        Check.Throws<InvalidDataException>(() => TagStore.ReadChanges(state, tag.TagId));
        foreach (string magic in new[] { "ASTAG001", "ASTAG002", "ASTAG003" })
            Reject(bytes => AssetPath.Utf8.GetBytes(magic).CopyTo(bytes, 0));
        return Task.CompletedTask;
    }

    private static Task InvalidBaselinesAsync()
    {
        using var fixture = new Fixture();
        string state = fixture.Config().StateDirectory;
        FileState[] files = [State("main", "shared/a", 1), State("main", "shared/b", 2) with { MetadataHash = Hash("meta") }, State("next", "shared/c", 3)];
        var a = AddedTag("00000001", files.Length);
        Write(fixture, a, Added(files), new(files, [])); TagStore.Commit(state, a.TagId);
        var b = Tag("00000002") with { BaseTag = a.TagId };
        Write(fixture, b, [], new([], [])); TagStore.Commit(state, b.TagId);
        string path = Path.Combine(state, "baseline.idx");
        byte[] original = File.ReadAllBytes(path);
        const int ledgerEnd = 24 + 2 * 4;
        var layout = ParseFiles(original, ledgerEnd + 32, files.Length, Int(original, 16));
        void Reject(Action<byte[]> change, bool sign = true)
        {
            byte[] bytes = (byte[])original.Clone(); change(bytes);
            if (sign)
            {
                SHA256.HashData(bytes.AsSpan(0, ledgerEnd), bytes.AsSpan(ledgerEnd, 32));
                Sign(bytes);
            }
            File.WriteAllBytes(path, bytes);
            Check.Throws<InvalidDataException>(() => TagStore.LoadBaseline(state));
        }
        Reject(bytes => bytes[layout.Records[0] + 17] ^= 1, sign: false);
        Reject(bytes => bytes[^1] ^= 1, sign: false);
        Reject(bytes => bytes[24] ^= 1, sign: false);
        Check.Throws<InvalidDataException>(() => TagStore.ReadCommittedTags(state));
        foreach (int offset in new[] { 8, 12, 16 }) Reject(bytes => PutInt(bytes, offset, -1));
        Reject(bytes => PutInt(bytes, 8, 0));
        Reject(bytes => PutInt(bytes, 12, int.MaxValue));
        Reject(bytes => PutInt(bytes, 20, 1));
        Reject(bytes => bytes.AsSpan(24, 4).CopyTo(bytes.AsSpan(28, 4)));
        Reject(bytes => PutInt(bytes, layout.Strings[0], PackageName.MaxUtf8Bytes + 1));
        Reject(bytes => PutInt(bytes, layout.Strings[1], 16385));
        Reject(bytes => bytes[layout.Strings[1] + 4] = 0xff);
        Reject(bytes => "main"u8.CopyTo(bytes.AsSpan(layout.Strings[2] + 4)));
        Reject(bytes => PutInt(bytes, layout.Records[0], 2));
        Reject(bytes => BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(layout.Records[0] + 8), -1));
        Reject(bytes => bytes[layout.Records[0] + 16] = 2);
        Reject(bytes => BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(layout.Records[0] + 4), 1));
        Reject(bytes => BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(layout.Records[1] + 6), 4097));
        Reject(bytes => bytes[layout.Suffixes[0]] = 0xff);
        Reject(bytes => bytes[layout.Suffixes[1]] = (byte)'a');
        foreach (int length in new[] { 0, 8, 23, 31, layout.Records[0] + 48, original.Length - 1 })
        {
            File.WriteAllBytes(path, original.AsSpan(0, length).ToArray());
            Check.Throws<InvalidDataException>(() => TagStore.LoadBaseline(state));
        }
        byte[] trailing = [.. original.AsSpan(0, original.Length - 32), 0, .. original.AsSpan(original.Length - 32)];
        Sign(trailing); File.WriteAllBytes(path, trailing);
        Check.Throws<InvalidDataException>(() => TagStore.LoadBaseline(state));
        return Task.CompletedTask;
    }

    private static Task InvalidDeltasAsync()
    {
        using var fixture = new Fixture();
        string state = fixture.Config().StateDirectory;
        FileState[] files = [State("main", "shared/a", 1), State("main", "shared/b", 2)];
        var a = AddedTag("00000001", files.Length);
        Write(fixture, a, Added(files), new(files, [])); TagStore.Commit(state, a.TagId);
        var b = Tag("00000002") with { BaseTag = a.TagId, AddedFiles = 1, UpdatedFiles = 1, RemovedFiles = 1 };
        FileState[] upserts = [files[0] with { Length = 8 }, State("next", "shared/c", 3)];
        Write(fixture, b, [new("main", "shared/a", ChangeKind.Updated), new("main", "shared/b", ChangeKind.Deleted), new("next", "shared/c", ChangeKind.Added)],
            new(upserts, [new("main", "shared/b")]));
        string path = DeltaPath(state, b.TagId);
        byte[] original = File.ReadAllBytes(path), baseline = File.ReadAllBytes(Path.Combine(state, "baseline.idx"));
        var layout = ParseFiles(original, 32, upserts.Length, Int(original, 28));
        var removed = ParseKeys(original, layout.End + 4, 1, Int(original, layout.End));
        void Reject(Action<byte[]> change, bool sign = true)
        {
            byte[] bytes = (byte[])original.Clone(); change(bytes); if (sign) Sign(bytes); File.WriteAllBytes(path, bytes);
            Check.Throws<InvalidDataException>(() => TagStore.LoadDelta(state, b.TagId));
        }
        Reject(bytes => bytes[8] ^= 1);
        Reject(bytes => BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(12), (ulong)uint.MaxValue + 2));
        foreach (int offset in new[] { 20, 24, 28 }) Reject(bytes => PutInt(bytes, offset, -1));
        Reject(bytes => PutInt(bytes, 20, int.MaxValue));
        Reject(bytes => PutInt(bytes, layout.End, -1));
        Reject(bytes => bytes[removed.Records[0] + 8] = (byte)ChangeKind.Added);
        Reject(bytes => bytes[removed.Suffixes[0]] = 0xff);
        Reject(bytes => bytes[layout.Records[0] + 17] ^= 1, sign: false);
        Check.Throws<InvalidDataException>(() => TagStore.Commit(state, b.TagId));
        Check.Bytes(baseline, File.ReadAllBytes(Path.Combine(state, "baseline.idx")));
        byte[] wrongParent = (byte[])original.Clone();
        BinaryPrimitives.WriteUInt64LittleEndian(wrongParent.AsSpan(12), 10); Sign(wrongParent);
        File.WriteAllBytes(path, wrongParent);
        Check.Throws<InvalidDataException>(() => TagStore.Commit(state, b.TagId));
        Check.Bytes(baseline, File.ReadAllBytes(Path.Combine(state, "baseline.idx")));
        foreach (int length in new[] { 0, 8, 31, layout.Records[0] + 48, original.Length - 1 })
        {
            File.WriteAllBytes(path, original.AsSpan(0, length).ToArray());
            Check.Throws<InvalidDataException>(() => TagStore.LoadDelta(state, b.TagId));
        }
        File.WriteAllBytes(path, original);
        TagStore.Commit(state, b.TagId);
        AssertStates(upserts, TagStore.LoadBaseline(state).Files.Values);
        return Task.CompletedTask;
    }

    private static Task InvalidWritesAsync()
    {
        using var fixture = new Fixture();
        string state = fixture.Config().StateDirectory;
        var valid = State("main", "shared/file", 7);
        var a = AddedTag("00000000", 1);
        Write(fixture, a, Added([valid]), new([valid], [])); TagStore.Commit(state, a.TagId);
        byte[] baseline = File.ReadAllBytes(Path.Combine(state, "baseline.idx"));
        FileState[] invalid =
        [
            valid with { Package = "" }, valid with { Package = "../main" }, valid with { Path = "a/../b" },
            valid with { Path = "" }, valid with { Path = new string('x', 4097) }, valid with { Path = "\ud800" },
            valid with { Length = -1 }, valid with { Encoding = "" }, valid with { Encoding = new string('x', 16385) }
        ];
        for (int i = 0; i < invalid.Length; i++)
        {
            var tag = Tag((i + 1).ToString("x8")) with { BaseTag = a.TagId, UpdatedFiles = 1 };
            RejectWriter(() => Write(fixture, tag, [new(invalid[i].Package, invalid[i].Path, ChangeKind.Updated)], new([invalid[i]], [])));
            Check.True(!File.Exists(TagPath(state, tag.TagId)) && !File.Exists(DeltaPath(state, tag.TagId)));
            Check.Bytes(baseline, File.ReadAllBytes(Path.Combine(state, "baseline.idx")));
        }
        var b = Tag("000000ff") with { BaseTag = a.TagId, UpdatedFiles = 1 };
        RejectWriter(() => Write(fixture, b, [new(valid.Package, valid.Path, ChangeKind.Updated)], new([valid, valid], [])));
        var alias = valid with { Package = "other" };
        RejectWriter(() => Write(fixture, b with { UpdatedFiles = 2 },
            [new(valid.Package, valid.Path, ChangeKind.Updated), new(alias.Package, alias.Path, ChangeKind.Updated)], new([valid, alias], [])));
        RejectWriter(() => Write(fixture, b, [new(valid.Package, valid.Path, ChangeKind.Updated)], new([valid], [new(valid.Package, valid.Path)])));
        foreach (var tag in new[] { b with { ConfigHash = "unused" }, b with { ContentChanges = -1 }, b with { Name = "../escape" }, b with { Name = a.Name } })
            RejectWriter(() => Write(fixture, tag, [new(valid.Package, valid.Path, ChangeKind.Updated)], new([valid], [])));
        byte[] sentinel = [7, 8, 9];
        File.WriteAllBytes(TagPath(state, b.TagId), sentinel);
        // Use an unlisted staged target so name validation does not read the occupied bytes.
        string occupied = Path.Combine(state, "tags", ".occupied.idx.tmp");
        File.Move(TagPath(state, b.TagId), occupied);
        Check.Throws<IOException>(() => TagStore.WriteCandidate(state, occupied, DeltaPath(state, b.TagId), b,
            [new(valid.Package, valid.Path, ChangeKind.Updated)], new([valid], [])));
        Check.Bytes(sentinel, File.ReadAllBytes(occupied));
        Check.True(!File.Exists(DeltaPath(state, b.TagId)));
        Check.Bytes(baseline, File.ReadAllBytes(Path.Combine(state, "baseline.idx")));
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        Check.Throws<OperationCanceledException>(() => TagStore.WriteCandidate(state, TagPath(state, b.TagId), DeltaPath(state, b.TagId), b,
            [new(valid.Package, valid.Path, ChangeKind.Updated)], new([valid], []), canceled.Token));
        Check.True(!File.Exists(TagPath(state, b.TagId)) && !File.Exists(DeltaPath(state, b.TagId)));
        return Task.CompletedTask;
    }

    private static ContentDigest Hash(string value) => new(SHA256.HashData(AssetPath.Utf8.GetBytes(value)));
    private static FileState State(string package, string path, long length) => new(package, path, length, Hash(path), null, Encoding);
    private static BuildTag Tag(string id) => new(id, DateTimeOffset.UnixEpoch, null, new string('0', 64), [], 0, 0, 0);
    private static BuildTag AddedTag(string id, int count) => Tag(id) with { AddedFiles = count, ContentChanges = count };
    private static FileChange[] Added(FileState[] files) => files.Select(file => new FileChange(file.Package, file.Path, ChangeKind.Added)).ToArray();
    private static string TagPath(string state, string id) => Path.Combine(state, "tags", id + ".idx");
    private static string DeltaPath(string state, string id) => Path.Combine(state, "pending", id + ".idx");
    private static void Write(Fixture fixture, BuildTag tag, FileChange[] changes, StateDelta delta)
    {
        string state = fixture.Config().StateDirectory;
        TagStore.WriteCandidate(state, TagPath(state, tag.TagId), DeltaPath(state, tag.TagId), tag, changes, delta);
    }
    private static void RejectWriter(Action action)
    {
        try { action(); throw new Exception("Invalid candidate state was accepted."); }
        catch (Exception error) when (error is ArgumentException or InvalidDataException) { }
    }
    private static void AssertTag(BuildTag expected, BuildTag actual)
    {
        Check.Equal(expected.TagId, actual.TagId); Check.Equal(expected.Name, actual.Name);
        Check.Equal(expected.BuildTimeUtc, actual.BuildTimeUtc); Check.Equal(TimeSpan.Zero, actual.BuildTimeUtc.Offset);
        Check.Equal(expected.BaseTag, actual.BaseTag); Check.Equal(expected.ConfigHash, actual.ConfigHash);
        Check.True(expected.Outputs.SequenceEqual(actual.Outputs));
        Check.Equal(expected.ContentChanges, actual.ContentChanges); Check.Equal(expected.MetadataChanges, actual.MetadataChanges);
        Check.Equal(expected.DeletedChanges, actual.DeletedChanges); Check.Equal(expected.AddedFiles, actual.AddedFiles);
        Check.Equal(expected.UpdatedFiles, actual.UpdatedFiles); Check.Equal(expected.RemovedFiles, actual.RemovedFiles);
    }
    private static void AssertStates(FileState[] expected, IEnumerable<FileState> actual)
    {
        var values = actual.ToDictionary(file => (file.Package, file.Path));
        Check.Equal(expected.Length, values.Count);
        foreach (var file in expected) Check.Equal(file, values[(file.Package, file.Path)]);
    }
    private static void AssertChanges(FileChange[] expected, FileChange[] actual) => Check.True(expected.ToHashSet().SetEquals(actual) && expected.Length == actual.Length);
    private static int Int(byte[] bytes, int offset) => BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset));
    private static void PutInt(byte[] bytes, int offset, int value) => BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset), value);
    private static void Sign(byte[] bytes) => SHA256.HashData(bytes.AsSpan(0, bytes.Length - 32), bytes.AsSpan(bytes.Length - 32));
    private sealed record Layout(List<int> Strings, List<int> Records, List<int> Suffixes, int End);
    private static Layout ParseTagKeys(byte[] bytes)
    {
        int offset = 100 + Int(bytes, 96);
        for (int i = 0; i < Int(bytes, 84); i++) offset += 4 + Int(bytes, offset);
        return ParseKeys(bytes, offset, Int(bytes, 88), Int(bytes, 92));
    }
    private static Layout ParseKeys(byte[] bytes, int offset, int count, int packages)
    {
        var strings = new List<int>(); var records = new List<int>(); var suffixes = new List<int>();
        for (int i = 0; i < packages; i++) { strings.Add(offset); offset += 4 + Int(bytes, offset); }
        for (int i = 0; i < count; i++)
        {
            records.Add(offset);
            int suffix = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset + 6));
            offset += 9; suffixes.Add(offset); offset += suffix;
        }
        return new(strings, records, suffixes, offset);
    }
    private static Layout ParseFiles(byte[] bytes, int offset, int count, int tables)
    {
        var strings = new List<int>(); var records = new List<int>(); var suffixes = new List<int>();
        for (int i = 0; i < tables * 2; i++) { strings.Add(offset); offset += 4 + Int(bytes, offset); }
        for (int i = 0; i < count; i++)
        {
            records.Add(offset);
            int suffix = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset + 6));
            bool metadata = bytes[offset + 16] != 0;
            offset += 49 + (metadata ? 32 : 0); suffixes.Add(offset); offset += suffix;
        }
        return new(strings, records, suffixes, offset);
    }
}
