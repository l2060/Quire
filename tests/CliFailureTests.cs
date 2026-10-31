using System.Security.Cryptography;
using Quire.Cli.Build;
using Quire.Cli.Sources;
using Quire.Internal;

internal static class CliFailureTests
{
    internal static void Register(Action<string, Func<Task>> test)
    {
        test("writer initialization failure cancels producers waiting on a full queue", WorkerInitializationAsync);
        test("writer worker failure preserves the cause and releases pipeline resources", WorkerFailureAsync);
        test("writer source failure reaches the caller without being replaced by cancellation", ProducerFailureAsync);
        test("discovery callback failure stops peer scans and preserves the original cause", DiscoveryFailureAsync);
    }

    private static async Task WorkerInitializationAsync()
    {
        using var fixture = new Fixture(false);
        var source = CreateSource(fixture);
        using var publisher = TestKeys.Create();
        string destination = Path.Combine(fixture.Root, "failed.asset");
        string scratch = Path.Combine(fixture.Root, "scratch");
        var options = fixture.BuildOptions with { Jobs = 1, IoConcurrency = 1, BlockSize = 65536 };

        // Invalid tag parsing occurs in the worker's cipher constructor. Eight blocks
        // exceed the two-slot queue, so a failed consumer must wake the producer.
        await ExpectFailureAsync<ArgumentException>(token => PackageWriter.WriteAsync(destination, scratch,
            "invalid-tag", DateTime.UtcNow.Ticks, [new(source, EntryKind.Content)], publisher, options, token));
        AssertWriterClosed(destination, scratch, source.SourcePath);
    }

    private static async Task WorkerFailureAsync()
    {
        using var fixture = new Fixture();
        var source = CreateSource(fixture);
        string destination = Path.Combine(fixture.Root, "failed.asset");
        string scratch = Path.Combine(fixture.Root, "scratch");
        var expected = new IOException("Simulated worker output failure.");
        var progress = new FailingProgress(expected, failWriting: true);
        await ExpectFailureAsync<IOException>(token => PackageWriter.WriteAsync(destination, scratch,
            "1234abcd", DateTime.UtcNow.Ticks, [new(source, EntryKind.Content)], null,
            fixture.BuildOptions with { Jobs = 2, BlockSize = 65536 }, token, progress), expected);
        AssertWriterClosed(destination, scratch, source.SourcePath);
    }

    private static async Task ProducerFailureAsync()
    {
        using var fixture = new Fixture();
        var source = CreateSource(fixture) with { SourcePath = Path.Combine(fixture.Root, "missing.bin") };
        string destination = Path.Combine(fixture.Root, "failed.asset");
        string scratch = Path.Combine(fixture.Root, "scratch");
        await ExpectFailureAsync<FileNotFoundException>(token => PackageWriter.WriteAsync(destination, scratch,
            "1234abcd", DateTime.UtcNow.Ticks, [new(source, EntryKind.Content)], null,
            fixture.BuildOptions, token));
        AssertWriterClosed(destination, scratch, Path.Combine(fixture.Input, "content.bin"));
    }

    private static async Task DiscoveryFailureAsync()
    {
        using var fixture = new Fixture(false);
        for (int i = 0; i < 32; i++) fixture.Put($"directory-{i}/content.bin", [1]);
        var expected = new IOException("Simulated discovery reporting failure.");
        var progress = new FailingProgress(expected, failWriting: false);
        await ExpectFailureAsync<IOException>(async token =>
            _ = await SourceScanner.ScanAsync(fixture.Config(), 4, token, progress), expected);
        // This also confirms all directory enumerators were closed before returning.
        Directory.Delete(fixture.Input, true);
    }

    private static SourceAsset CreateSource(Fixture fixture)
    {
        byte[] bytes = new byte[8 * 65536];
        fixture.Put("content.bin", bytes);
        return new("main", "content.bin", Path.Combine(fixture.Input, "content.bin"), bytes.Length,
            SHA256.HashData(bytes), null, null, CompressionAlgorithm.Lz4);
    }

    private static async Task ExpectFailureAsync<TException>(Func<CancellationToken, Task> operation, Exception? expected = null)
        where TException : Exception
    {
        // The cancellation deadline releases a broken queue on regression; WaitAsync
        // is a second bound so a failure cannot stall the rest of the test process.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await operation(timeout.Token).WaitAsync(TimeSpan.FromSeconds(15));
        }
        catch (TException error)
        {
            Check.True(!timeout.IsCancellationRequested, "The operation only exited after the test deadline.");
            if (expected is not null) Check.True(ReferenceEquals(expected, error), "A peer's cancellation replaced the original failure.");
            return;
        }
        throw new Exception($"Expected {typeof(TException).Name}.");
    }

    private static void AssertWriterClosed(string destination, string scratch, string source)
    {
        Check.True(!File.Exists(destination), "A failed write published its destination.");
        Check.True(!File.Exists(destination + ".tmp"), "A failed write retained its temporary output.");
        using var input = new FileStream(source, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using var table = new FileStream(Path.Combine(scratch, "blocks.bin"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    private sealed class FailingProgress(Exception failure, bool failWriting) : IOperationProgress
    {
        private string? _stage;
        public void StartStage(string stage, long totalBytes = -1, long totalFiles = -1) => _stage = stage;
        public void Advance(long bytes = 0, long files = 0)
        {
            if (!failWriting && _stage == "Discovering") throw failure;
        }
        public void AddRead(long bytes) { }
        public void AddWritten(long bytes)
        {
            if (failWriting) throw failure;
        }
    }
}
