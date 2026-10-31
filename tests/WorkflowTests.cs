using System.Globalization;
using System.Security.Cryptography;
using Quire;
using Quire.Cli;
using Quire.Cli.Build;
using Quire.Cli.Sources;
using Quire.Cli.Tags;
using Quire.Internal;

internal static class WorkflowTests
{
    internal static void Register(Action<string, Func<Task>> test)
    {
        test("Candidates preserve frozen state until commit and reject stale siblings", FrozenCandidatesAsync);
        test("Commit and candidate removal remain usable without source files or publishing keys", OfflineMetadataAsync);
        test("Package regrouping preserves unchanged assets without emitting mappings or content", PackageMoveAsync);
        test("Canceled and failed commits preserve the published baseline and can be retried", CommitRecoveryAsync);
        test("Empty candidates commit explicitly and historical changes survive pending cleanup", EmptyHistoryAsync);
    }

    private static async Task FrozenCandidatesAsync()
    {
        using var f = new Fixture(false);
        string state = f.Config().StateDirectory;
        f.Put("content", [1]);
        f.Put("meta", [2]);
        f.Text("meta.meta", "revision=1");
        f.Put("gone", [3]);
        var a = await Build(f, "A");
        Check.True(!File.Exists(Path.Combine(state, "baseline.idx")));
        await Run(f, "commit", "A");
        byte[] acceptedA = File.ReadAllBytes(Path.Combine(state, "baseline.idx"));
        f.Put("content", [4]);
        f.Text("meta.meta", "revision=2");
        File.Delete(Path.Combine(f.Input, "gone"));
        f.Put("added", [5]);
        var b = await Build(f, "B");
        Check.Equal(a.TagId, b.BaseTag);
        Check.Equal(1, b.AddedFiles);
        Check.Equal(2, b.UpdatedFiles);
        Check.Equal(1, b.RemovedFiles);
        Check.Bytes(acceptedA, File.ReadAllBytes(Path.Combine(state, "baseline.idx")));
        f.Put("content", [6]);
        f.Put("later", [7]);
        var c = await Build(f, "C");
        Check.Equal(a.TagId, c.BaseTag);
        Check.Bytes(acceptedA, File.ReadAllBytes(Path.Combine(state, "baseline.idx")));
        f.Put("content", [8]);
        f.Text("meta.meta", "revision=3");
        await Run(f, "commit", "B");
        var baseline = TagStore.LoadBaseline(state);
        Check.Equal(b.TagId, baseline.TagId);
        Check.True(baseline.CommittedTags.SequenceEqual(new[] { a.TagId, b.TagId }));
        Check.Equal(3, baseline.Files.Count);
        Check.Equal(Digest([4]), baseline.Files[("main", "common/data/content")].ContentHash);
        Check.Equal(Digest(PropertyMetadata.Parse("revision=2")), baseline.Files[("main", "common/data/meta")].MetadataHash!.Value);
        Check.True(!baseline.Files.ContainsKey(("main", "common/data/gone")));
        Check.True(!baseline.Files.ContainsKey(("main", "common/data/later")));
        Check.True(!File.Exists(Path.Combine(state, "pending", b.TagId + ".idx")));
        byte[] acceptedB = File.ReadAllBytes(Path.Combine(state, "baseline.idx"));
        await Check.ThrowsAsync<InvalidOperationException>(() => Run(f, "commit", "C"));
        Check.Bytes(acceptedB, File.ReadAllBytes(Path.Combine(state, "baseline.idx")));
        await Run(f, "commit", "B");
        Check.Bytes(acceptedB, File.ReadAllBytes(Path.Combine(state, "baseline.idx")));
        Check.True(File.Exists(Path.Combine(state, "pending", c.TagId + ".idx")));
        string differences = await Run(f, "diff");
        RequireChange(differences, 'M', "main/common/data/content");
        RequireChange(differences, 'M', "main/common/data/meta");
        RequireChange(differences, 'A', "main/common/data/later");
        string changes = await Run(f, "changes", "B");
        RequireChange(changes, 'A', "main/common/data/added");
        RequireChange(changes, 'M', "main/common/data/content");
        RequireChange(changes, 'M', "main/common/data/meta");
        RequireChange(changes, 'D', "main/common/data/gone");
        Check.True(changes.Contains("4 changes; 1 added; 2 updated; 1 deleted.", StringComparison.Ordinal));
        await Check.ThrowsAsync<InvalidOperationException>(() => Run(f, "remove", "A"));
        await Check.ThrowsAsync<InvalidOperationException>(() => Run(f, "remove", "B"));
        await Run(f, "remove", "C");
        Check.True(!File.Exists(Path.Combine(state, "tags", c.TagId + ".idx")));
        Check.True(!File.Exists(Path.Combine(state, "pending", c.TagId + ".idx")));
        Check.True(c.Outputs.All(output => File.Exists(Path.Combine(f.Root, output))));
        Check.Bytes(acceptedB, File.ReadAllBytes(Path.Combine(state, "baseline.idx")));
        Check.True((await Run(f, "tags")).Contains("A", StringComparison.Ordinal));
    }

    private static async Task OfflineMetadataAsync()
    {
        using var f = new Fixture(true, true);
        f.Put("a", [1]);
        var a = await Build(f, "initial");
        await Run(f, "commit", a.Name);
        f.Put("a", [2]);
        var b = await Build(f, "accepted");
        f.Put("a", [3]);
        var c = await Build(f, "discarded");
        string state = f.Config().StateDirectory;
        string sourceBackup = Path.Combine(f.Root, "moved-source");
        Directory.Move(f.Input, sourceBackup);
        f.RemoveKeys();
        Check.True((await Run(f, "tags")).Contains("accepted", StringComparison.Ordinal));
        RequireChange(await Run(f, "changes", "accepted"), 'M', "main/common/data/a");
        await Run(f, "commit", "accepted");
        Check.Equal(b.TagId, TagStore.LoadBaseline(state).TagId);
        Check.Equal(Digest([2]), TagStore.LoadBaseline(state).Files[("main", "common/data/a")].ContentHash);
        await Run(f, "remove", "discarded");
        Check.True(c.Outputs.All(output => File.Exists(Path.Combine(f.Root, output))));
        Check.True(!File.Exists(Path.Combine(state, "pending", b.TagId + ".idx")));
        Check.True(!File.Exists(Path.Combine(state, "pending", c.TagId + ".idx")));
    }

    private static async Task PackageMoveAsync()
    {
        using var f = new Fixture(false);
        f.Put("moving", [1, 2]);
        var config = f.Config();
        var a = await ProjectBuilder.BuildAsync(config, "old-package", f.BuildOptions, default);
        TagStore.Commit(config.StateDirectory, a.TagId);
        var renamed = config with { Packages = [config.Packages.Single() with { Name = "relocated" }] };
        var b = await ProjectBuilder.BuildAsync(renamed, "moved", f.BuildOptions, default);
        Check.Equal(0, b.AddedFiles);
        Check.Equal(0, b.UpdatedFiles);
        Check.Equal(0, b.RemovedFiles);
        Check.Equal(0, b.DeletedChanges);
        Check.Equal(0, b.ContentChanges);
        Check.Equal(0, b.Outputs.Length);
        var changes = TagStore.ReadChanges(config.StateDirectory, b.TagId);
        Check.Equal(0, changes.Length);
        TagStore.Commit(config.StateDirectory, b.TagId);
        var baseline = TagStore.LoadBaseline(config.StateDirectory);
        Check.Equal(1, baseline.Files.Count);
        Check.Equal("common/data/moving", baseline.Files.Values.Single().Path);
        using var bundle = QuireBundle.Open(f.Output);
        using var data = bundle.Read("common/data/moving");
        Check.Bytes([1, 2], data.Data.Span);
    }

    private static async Task CommitRecoveryAsync()
    {
        using var f = new Fixture(false);
        f.Put("a", [1]);
        var a = await Build(f, "A");
        string state = f.Config().StateDirectory;
        TagStore.Commit(state, a.TagId);
        string baselineFile = Path.Combine(state, "baseline.idx");
        byte[] previous = File.ReadAllBytes(baselineFile);
        f.Put("a", [2]);
        var b = await Build(f, "B");
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        Check.Throws<OperationCanceledException>(() => TagStore.Commit(state, b.TagId, canceled.Token));
        Check.Bytes(previous, File.ReadAllBytes(baselineFile));
        Check.True(File.Exists(Path.Combine(state, "pending", b.TagId + ".idx")));
        await Check.ThrowsAsync<OperationCanceledException>(() => ProjectBuilder.BuildAsync(f.Config(), "canceled", f.BuildOptions, canceled.Token));
        Check.True(TagStore.List(state).All(tag => tag.Name != "canceled"));
        Check.Bytes(previous, File.ReadAllBytes(baselineFile));
        if (OperatingSystem.IsWindows())
        {
            using (new FileStream(baselineFile, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                try
                {
                    TagStore.Commit(state, b.TagId);
                    throw new Exception("A baseline held without delete sharing must not be replaced.");
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
            Check.Bytes(previous, File.ReadAllBytes(baselineFile));
            Check.True(File.Exists(Path.Combine(state, "pending", b.TagId + ".idx")));
            Check.Equal(0, Directory.GetFiles(state, "*.tmp", SearchOption.AllDirectories).Length);
        }
        TagStore.Commit(state, b.TagId);
        Check.Equal(b.TagId, TagStore.LoadBaseline(state).TagId);
        Check.Equal(Digest([2]), TagStore.LoadBaseline(state).Files[("main", "common/data/a")].ContentHash);
        if (OperatingSystem.IsWindows())
        {
            f.Put("a", [3]);
            var c = await Build(f, "C");
            string pending = Path.Combine(state, "pending", c.TagId + ".idx");
            using (new FileStream(pending, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                TagStore.Commit(state, c.TagId);
                Check.Equal(c.TagId, TagStore.LoadBaseline(state).TagId);
                Check.True(File.Exists(pending), "Pending cleanup failure must not undo a committed baseline.");
            }
            byte[] committed = File.ReadAllBytes(baselineFile);
            TagStore.Commit(state, c.TagId);
            Check.True(!File.Exists(pending));
            Check.Bytes(committed, File.ReadAllBytes(baselineFile));
        }
        string head = TagStore.LoadBaseline(state).TagId!;
        byte[] savedBaseline = File.ReadAllBytes(baselineFile);
        string backup = baselineFile + ".backup";
        File.Move(baselineFile, backup);
        try
        {
            Check.Throws<InvalidDataException>(() => TagStore.LoadBaseline(state));
            Check.Throws<InvalidDataException>(() => TagStore.List(state).ToArray());
            Check.Throws<InvalidDataException>(() => TagStore.Commit(state, head));
            Check.Throws<InvalidDataException>(() => TagStore.Remove(state, head));
            Check.True(File.Exists(Path.Combine(state, "tags", head + ".idx")));
            Check.True(!File.Exists(baselineFile));
        }
        finally { File.Move(backup, baselineFile, overwrite: true); }
        Check.Bytes(savedBaseline, File.ReadAllBytes(baselineFile));
        Check.Equal(head, TagStore.LoadBaseline(state).TagId);
        Check.True(TagStore.List(state).Any(tag => tag.TagId == head));
    }

    private static async Task EmptyHistoryAsync()
    {
        using var f = new Fixture(false);
        string state = f.Config().StateDirectory;
        var a = await Build(f, "empty-A");
        Check.Equal(0, a.Outputs.Length);
        Check.True(TagStore.LoadBaseline(state).TagId is null);
        TagStore.Commit(state, a.TagId);
        var b = await Build(f, "empty-B");
        Check.Equal(a.TagId, b.BaseTag);
        Check.Equal(0, b.AddedFiles + b.UpdatedFiles + b.RemovedFiles);
        TagStore.Commit(state, b.TagId);
        Check.Equal(0, TagStore.LoadBaseline(state).Files.Count);
        Check.Equal(0, Directory.GetFiles(Path.Combine(state, "pending"), "*.idx").Length);
        Check.Equal(0, TagStore.ReadChanges(state, a.TagId).Length);
        Check.Equal(0, TagStore.ReadChanges(state, b.TagId).Length);
        Check.True(TagStore.ReadCommittedTags(state).SequenceEqual(new[] { a.TagId, b.TagId }));
        Check.Equal(a.TagId, TagStore.Load(state, b.TagId).BaseTag);
        await Check.ThrowsAsync<InvalidOperationException>(() => Run(f, "commit", "empty-A"));
        Check.Equal(b.TagId, TagStore.LoadBaseline(state).TagId);
        string listing = await Run(f, "tags");
        Check.True(listing.Contains("COMMITTED", StringComparison.Ordinal));
        Check.True(listing.Contains("BASELINE", StringComparison.Ordinal));
        Check.True((await Run(f, "changes", "empty-A")).Contains("0 changes", StringComparison.Ordinal));
    }

    private static Task<BuildTag> Build(Fixture fixture, string name) => ProjectBuilder.BuildAsync(fixture.Config(), name, fixture.BuildOptions, default);
    private static ContentDigest Digest(byte[] bytes) => new(SHA256.HashData(bytes));
    private static void RequireChange(string text, char kind, string path) => Check.True(text.Split('\n').Any(line => line.TrimEnd('\r') == $"{kind}  {path}"), $"Missing {kind} {path} in: {text}");
    private static async Task<string> Run(Fixture fixture, params string[] arguments)
    {
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        var previous = Console.Out;
        try
        {
            Console.SetOut(output);
            Check.Equal(0, await CommandLine.RunAsync([.. arguments, "-f", fixture.ProjectPath], default));
            return output.ToString();
        }
        finally { Console.SetOut(previous); }
    }
}
