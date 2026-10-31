using Quire;
using Quire.Internal;

internal static class DirTests
{
    private const string Root = "common/data";

    internal static void Register(Action<string, Func<Task>> test)
    {
        test("Dir and DirAsync enumerate direct mixed entries across index and storage modes", MixedEntriesAsync);
        test("Dir and DirAsync omit orphan metadata and empty directories", OrphanMetadataAsync);
        test("Dir and DirAsync validate paths and enumerate absent and empty roots", EmptyAndValidationAsync);
        test("Dir snapshots are independent and entry streams retain ownership", OwnershipAsync);
        test("DirAsync and entry streams observe cancellation tokens", CancellationAsync);
        test("Dir and DirAsync skip large subtrees in paged indexes", SubtreeIoAsync);
        test("Dir and DirAsync support maximum-length paths and deep directories", MaximumPathAsync);
    }

    private static async Task MixedEntriesAsync()
    {
        using var fixture = new Fixture(false, true);
        string[] paths = ["root.bin", "plain.bin", "reset.bin", "a/direct.bin", "a/deep/one.bin", "a/deep/two.bin", "a-b/one.bin", "a.b/one.bin", "a0/one.bin", "ab/one.bin", "中文/文件.bin", "\ue000/one.bin", "😀/one.bin", "collision"];
        var expectedFiles = new Dictionary<string, AssetEntryInfo>(StringComparer.Ordinal);
        foreach (string path in paths)
        {
            fixture.Put(path, [1, 2, 3]);
            expectedFiles.Add(Root + "/" + path, new(Root + "/" + path, AssetEntryKind.File, 3, false));
        }
        fixture.Text("root.bin.meta", "revision=1");
        fixture.Text("reset.bin.meta", "revision=1");
        Directory.CreateDirectory(Path.Combine(fixture.Input, "empty"));
        var baseline = await fixture.Build();
        fixture.Text("root.bin.meta", "revision=2");
        fixture.Text("plain.bin.meta", "added=true");
        fixture.Put("reset.bin", [4, 5, 6, 7]);
        File.Delete(Path.Combine(fixture.Input, "reset.bin.meta"));
        fixture.Put("a/direct.bin", [8, 9]);
        File.Delete(Path.Combine(fixture.Input, "collision"));
        fixture.Put("collision/child.bin", [10]);
        // A separate full package retains the old file while adding a same-path directory.
        var patch = await fixture.Build();
        fixture.Text("plain.bin.meta", "added=true\nrevision=3");
        fixture.Put("a-b/one.bin", [1, 2, 3, 4]);
        fixture.Put("a/third.bin", [5, 6]);
        await fixture.Build(patch.TagId);
        expectedFiles[Root + "/root.bin"] = new(Root + "/root.bin", AssetEntryKind.File, 3, true);
        expectedFiles[Root + "/plain.bin"] = new(Root + "/plain.bin", AssetEntryKind.File, 3, true);
        expectedFiles[Root + "/reset.bin"] = new(Root + "/reset.bin", AssetEntryKind.File, 4, false);
        expectedFiles[Root + "/a/direct.bin"] = new(Root + "/a/direct.bin", AssetEntryKind.File, 2, false);
        expectedFiles[Root + "/a-b/one.bin"] = new(Root + "/a-b/one.bin", AssetEntryKind.File, 4, false);
        expectedFiles.Add(Root + "/a/third.bin", new(Root + "/a/third.bin", AssetEntryKind.File, 2, false));
        expectedFiles.Add(Root + "/collision/child.bin", new(Root + "/collision/child.bin", AssetEntryKind.File, 1, false));

        foreach (var options in Modes(fixture.ReadOptions))
        {
            using var bundle = await QuireBundle.OpenAsync(fixture.Output, options);
            Check.Equal(3, bundle.Packages.Count);
            foreach (string directory in new[] { "", "common", Root, Root + "/a", Root + "/a/deep", Root + "/collision", Root + "/中文", Root + "/😀", Root + "/missing", "common/dat", Root + "/root.bin", Root + "/empty" })
            {
                var expected = Expected(expectedFiles.Values, directory);
                var actual = bundle.Dir(directory);
                Check.True(expected.SequenceEqual(actual), $"Dir mismatch for '{directory}', {options.IndexMode}, resident={options.MaxResidentIndexBytes}.");
                var asyncActual = await bundle.DirAsync(directory);
                Check.True(expected.SequenceEqual(asyncActual), $"DirAsync mismatch for '{directory}', {options.IndexMode}.");
                Check.True(expected.SequenceEqual(bundle.EnumerateEntries(directory)), $"EnumerateEntries mismatch for '{directory}'.");
                var streamedAsync = await CollectAsync(bundle.EnumerateEntriesAsync(directory));
                Check.True(expected.SequenceEqual(streamedAsync), $"EnumerateEntriesAsync mismatch for '{directory}'.");
                foreach (var item in actual)
                {
                    Check.Equal(item.Kind == AssetEntryKind.Directory, item.IsDirectory);
                    Check.Equal(item.Kind == AssetEntryKind.File, item.IsFile);
                    Check.True(item.IsDirectory != item.IsFile);
                    Check.True(!item.Path.EndsWith('/'));
                    if (item.IsDirectory)
                    {
                        Check.Equal(0L, item.Length);
                        Check.True(!item.HasMetadata);
                    }
                }
            }
            Check.True(bundle.Dir().SequenceEqual(bundle.Dir("")));
            var rootAsync = await bundle.DirAsync();
            Check.True(rootAsync.SequenceEqual(bundle.Dir("")));
            var collision = bundle.Dir(Root).Where(item => item.Path == Root + "/collision").ToArray();
            Check.Equal(2, collision.Length);
            Check.True(collision[0].IsFile && collision[1].IsDirectory);
            Check.Equal(collision[0], bundle.GetFileInfo(Root + "/collision"));
            Check.True(bundle.TryGetFileInfo(Root + "/collision", out AssetEntryInfo collisionFile));
            Check.Equal(collision[0], collisionFile);
            Check.True(expectedFiles.Values.OrderBy(info => info.Path, AssetPath.Comparer).SequenceEqual(bundle.EnumerateFiles()));
        }
    }

    private static async Task OrphanMetadataAsync()
    {
        using var fixture = new Fixture(false);
        fixture.Put("orphan/deep/item", [1]);
        fixture.Text("orphan/deep/item.meta", "revision=1");
        var baseline = await fixture.Build();
        fixture.Text("orphan/deep/item.meta", "revision=2");
        fixture.Put("visible/leaf/item", [2]);
        var patch = await fixture.Build(baseline.TagId);
        fixture.Text("orphan/deep/item.meta", "revision=3");
        var metadataOnly = await fixture.Build(patch.TagId);
        foreach (var options in Modes(fixture.ReadOptions))
        {
            using var bundle = QuireBundle.Open(Path.Combine(fixture.Output, patch.TagId), options);
            AssetEntryInfo[] expected = [new(Root + "/visible", AssetEntryKind.Directory, 0, false)];
            Check.True(expected.SequenceEqual(bundle.Dir(Root)));
            var snapshotAsync = await bundle.DirAsync(Root);
            Check.True(expected.SequenceEqual(snapshotAsync));
            Check.True(expected.SequenceEqual(bundle.EnumerateEntries(Root)));
            var asyncActual = await CollectAsync(bundle.EnumerateEntriesAsync(Root));
            Check.True(expected.SequenceEqual(asyncActual));
            Check.Equal(0, bundle.EnumerateEntries(Root + "/orphan").Count());
            Check.Equal(0, (await CollectAsync(bundle.EnumerateEntriesAsync(Root + "/orphan"))).Length);
            using var orphanOnly = QuireBundle.Open(Path.Combine(fixture.Output, metadataOnly.TagId), options);
            foreach (string directory in new[] { "", "common", Root, Root + "/orphan", Root + "/orphan/deep" })
            {
                Check.True(ReferenceEquals(Array.Empty<AssetEntryInfo>(), orphanOnly.Dir(directory)));
                var emptyAsync = await orphanOnly.DirAsync(directory);
                Check.True(ReferenceEquals(Array.Empty<AssetEntryInfo>(), emptyAsync));
                Check.Equal(0, orphanOnly.EnumerateEntries(directory).Count());
                Check.Equal(0, (await CollectAsync(orphanOnly.EnumerateEntriesAsync(directory))).Length);
            }
        }
    }

    private static async Task EmptyAndValidationAsync()
    {
        using var fixture = new Fixture(false);
        Directory.CreateDirectory(fixture.Output);
        foreach (var options in Modes(fixture.ReadOptions))
        {
            using var bundle = QuireBundle.Open(fixture.Output, options);
            Check.True(ReferenceEquals(Array.Empty<AssetEntryInfo>(), bundle.Dir()));
            Check.True(ReferenceEquals(Array.Empty<AssetEntryInfo>(), bundle.Dir("absent")));
            var emptyRootAsync = await bundle.DirAsync();
            var emptyAbsentAsync = await bundle.DirAsync("absent");
            Check.True(ReferenceEquals(Array.Empty<AssetEntryInfo>(), emptyRootAsync));
            Check.True(ReferenceEquals(Array.Empty<AssetEntryInfo>(), emptyAbsentAsync));
            Check.Equal(0, bundle.EnumerateEntries().Count());
            Check.Equal(0, bundle.EnumerateEntries("absent").Count());
            Check.Equal(0, (await CollectAsync(bundle.EnumerateEntriesAsync())).Length);
            Check.Equal(0, (await CollectAsync(bundle.EnumerateEntriesAsync("absent"))).Length);
            foreach (string invalid in new[] { "/", "common/", "../a", "a//b", "a\\b", ".", "a/..", "a:*", new string('x', 4097) })
            {
                Check.Throws<ArgumentException>(() => bundle.Dir(invalid));
                await Check.ThrowsAsync<ArgumentException>(async () => await bundle.DirAsync(invalid));
                Check.Throws<ArgumentException>(() => bundle.EnumerateEntries(invalid));
                Check.Throws<ArgumentException>(() => bundle.EnumerateEntriesAsync(invalid));
            }
            Check.Throws<ArgumentNullException>(() => bundle.EnumerateEntries(null!));
            Check.Throws<ArgumentNullException>(() => bundle.EnumerateEntriesAsync(null!));
            Check.Throws<ArgumentNullException>(() => bundle.Dir(null!));
            await Check.ThrowsAsync<ArgumentNullException>(async () => await bundle.DirAsync(null!));
        }
    }

    private static async Task OwnershipAsync()
    {
        using var fixture = new Fixture(false);
        fixture.Put("a", [1]);
        fixture.Put("b/child", [2]);
        fixture.Put("c", [3]);
        await fixture.Build();
        foreach (var mode in new[] { AssetIndexMode.Merged, AssetIndexMode.PerPackage })
        {
            using var bundle = QuireBundle.Open(fixture.Output, new ReadOptions { IndexMode = mode, MaxResidentIndexBytes = 0, PageCacheBytes = 0 });
            var sync = bundle.EnumerateEntries(Root);
            var asynchronous = bundle.EnumerateEntriesAsync(Root);
            var expected = sync.ToArray();
            var snapshot = bundle.Dir(Root);
            var asyncSnapshot = await bundle.DirAsync(Root);
            Check.True(expected.SequenceEqual(snapshot));
            Check.True(expected.SequenceEqual(asyncSnapshot));
            Check.True(!ReferenceEquals(snapshot, asyncSnapshot));
            snapshot[0] = default;
            Check.True(expected.SequenceEqual(asyncSnapshot), "Mutating a returned array must not affect another snapshot.");
            Check.True(expected.SequenceEqual(bundle.Dir(Root)), "Mutating a returned array must not change package state.");
            for (int repeat = 0; repeat < 8; repeat++)
            {
                foreach (var item in sync)
                {
                    Check.Equal(expected[0], item);
                    break;
                }
                await foreach (var item in asynchronous)
                {
                    Check.Equal(expected[0], item);
                    break;
                }
            }
            Check.True(expected.SequenceEqual(sync));
            var asyncActual = await CollectAsync(asynchronous);
            Check.True(expected.SequenceEqual(asyncActual));
            var concurrent = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => CollectAsync(asynchronous)));
            Check.True(concurrent.All(items => expected.SequenceEqual(items)));

            var firstSync = sync.GetEnumerator();
            var copiedSync = firstSync;
            Check.True(firstSync.MoveNext());
            firstSync.Dispose();
            copiedSync.Dispose();
            Check.True(!firstSync.MoveNext() && !copiedSync.MoveNext());
            var firstAsync = asynchronous.GetAsyncEnumerator();
            var copiedAsync = firstAsync;
            Check.True(await firstAsync.MoveNextAsync());
            await firstAsync.DisposeAsync();
            await copiedAsync.DisposeAsync();
            Check.True(!await firstAsync.MoveNextAsync() && !await copiedAsync.MoveNextAsync());
            Check.True(expected.SequenceEqual(sync));
            var afterCopies = await CollectAsync(asynchronous);
            Check.True(expected.SequenceEqual(afterCopies));

            using var syncIterator = sync.GetEnumerator();
            await using var asyncIterator = asynchronous.GetAsyncEnumerator();
            var pendingSnapshot = bundle.DirAsync(Root);
            bundle.Dispose();
            var completedSnapshot = await pendingSnapshot;
            Check.True(expected.SequenceEqual(completedSnapshot));
            Check.True(expected.SequenceEqual(asyncSnapshot), "Snapshots remain usable after the bundle is disposed.");
            var remainingSync = new List<AssetEntryInfo>();
            var remainingAsync = new List<AssetEntryInfo>();
            while (syncIterator.MoveNext())
                remainingSync.Add(syncIterator.Current);
            while (await asyncIterator.MoveNextAsync())
                remainingAsync.Add(asyncIterator.Current);
            Check.True(expected.SequenceEqual(remainingSync));
            Check.True(expected.SequenceEqual(remainingAsync));
            Check.True(!syncIterator.MoveNext());
            Check.True(!await asyncIterator.MoveNextAsync());
            Check.Throws<ObjectDisposedException>(() => sync.GetEnumerator());
            Check.Throws<ObjectDisposedException>(() => asynchronous.GetAsyncEnumerator());
            Check.Throws<ObjectDisposedException>(() => bundle.EnumerateEntries());
            Check.Throws<ObjectDisposedException>(() => bundle.EnumerateEntriesAsync());
            Check.Throws<ObjectDisposedException>(() => bundle.Dir());
            await Check.ThrowsAsync<ObjectDisposedException>(async () => await bundle.DirAsync());
        }
    }

    private static async Task CancellationAsync()
    {
        using var fixture = new Fixture(false);
        fixture.Put("a", [1]);
        fixture.Put("b/child", [2]);
        fixture.Put("c", [3]);
        await fixture.Build();
        foreach (var options in Modes(fixture.ReadOptions))
        {
            using var bundle = QuireBundle.Open(fixture.Output, options);
            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            await Check.ThrowsAsync<OperationCanceledException>(async () => await bundle.DirAsync(Root, canceled.Token));
            await Check.ThrowsAsync<OperationCanceledException>(async () => await CollectAsync(bundle.EnumerateEntriesAsync(Root, canceled.Token)));
            await Check.ThrowsAsync<OperationCanceledException>(async () =>
            {
                await foreach (var _ in bundle.EnumerateEntriesAsync(Root).WithCancellation(canceled.Token)) { }
            });
            foreach (bool cancelApiToken in new[] { false, true })
            {
                using var api = new CancellationTokenSource();
                using var consumer = new CancellationTokenSource();
                await using var iterator = bundle.EnumerateEntriesAsync(Root, api.Token).GetAsyncEnumerator(consumer.Token);
                Check.True(await iterator.MoveNextAsync());
                (cancelApiToken ? api : consumer).Cancel();
                await Check.ThrowsAsync<OperationCanceledException>(async () => await iterator.MoveNextAsync());
            }
            Check.Equal(3, (await CollectAsync(bundle.EnumerateEntriesAsync(Root))).Length);
            Check.Equal(3, (await bundle.DirAsync(Root)).Length);
        }
        foreach (var mode in new[] { AssetIndexMode.Merged, AssetIndexMode.PerPackage })
        {
            var progress = new ReadCounter();
            using var bundle = QuireBundle.Open(fixture.Output, new ReadOptions { IndexMode = mode, MaxResidentIndexBytes = 0, PageCacheBytes = 0, Progress = progress });
            foreach (bool streaming in new[] { false, true })
            {
                using var duringRead = new CancellationTokenSource();
                progress.OnRead = duringRead.Cancel;
                if (streaming)
                    await Check.ThrowsAsync<OperationCanceledException>(async () => await CollectAsync(bundle.EnumerateEntriesAsync(Root, duringRead.Token)));
                else
                    await Check.ThrowsAsync<OperationCanceledException>(async () => await bundle.DirAsync(Root, duringRead.Token));
                progress.OnRead = null;
                Check.True(duringRead.IsCancellationRequested);
                Check.Equal(3, (await bundle.DirAsync(Root)).Length);
            }
            Check.Equal(3, (await CollectAsync(bundle.EnumerateEntriesAsync(Root))).Length);
        }
    }

    private static async Task SubtreeIoAsync()
    {
        using var fixture = new Fixture(false);
        const int descendants = 768;
        for (int index = 0; index < descendants; index++)
            fixture.Put($"a/deep/item{index:D4}.bin", [(byte)index]);
        fixture.Put("z.bin", [17]);
        await fixture.Build();
        foreach (var mode in new[] { AssetIndexMode.Merged, AssetIndexMode.PerPackage })
        {
            var progress = new ReadCounter();
            using var bundle = QuireBundle.Open(fixture.Output, new ReadOptions { IndexMode = mode, MaxResidentIndexBytes = 0, PageCacheBytes = 0, Progress = progress });
            progress.Reset();
            AssetEntryInfo[] expected = [new(Root + "/a", AssetEntryKind.Directory, 0, false), new(Root + "/z.bin", AssetEntryKind.File, 1, false)];
            Check.True(expected.SequenceEqual(bundle.Dir(Root)));
            long syncBytes = progress.Bytes;
            progress.Reset();
            var asyncActual = await bundle.DirAsync(Root);
            Check.True(expected.SequenceEqual(asyncActual));
            long asyncBytes = progress.Bytes;
            progress.Reset();
            Check.True(expected.SequenceEqual(bundle.EnumerateEntries(Root)));
            long streamedSyncBytes = progress.Bytes;
            progress.Reset();
            var streamedAsync = await CollectAsync(bundle.EnumerateEntriesAsync(Root));
            Check.True(expected.SequenceEqual(streamedAsync));
            long streamedAsyncBytes = progress.Bytes;
            progress.Reset();
            Check.Equal(descendants + 1, bundle.EnumerateFiles(Root).Count());
            long recursiveBytes = progress.Bytes;
            Check.True(syncBytes > 0 && syncBytes * 8 < recursiveBytes, $"Dir ({mode}) read {syncBytes} bytes versus {recursiveBytes} for descendants.");
            Check.True(asyncBytes > 0 && asyncBytes * 8 < recursiveBytes, $"DirAsync ({mode}) read {asyncBytes} bytes versus {recursiveBytes} for descendants.");
            Check.True(streamedSyncBytes > 0 && streamedSyncBytes * 8 < recursiveBytes, $"EnumerateEntries ({mode}) read {streamedSyncBytes} bytes versus {recursiveBytes} for descendants.");
            Check.True(streamedAsyncBytes > 0 && streamedAsyncBytes * 8 < recursiveBytes, $"EnumerateEntriesAsync ({mode}) read {streamedAsyncBytes} bytes versus {recursiveBytes} for descendants.");
        }
        foreach (var mode in new[] { AssetIndexMode.Merged, AssetIndexMode.PerPackage })
        {
            using var bundle = QuireBundle.Open(fixture.Output, new ReadOptions { IndexMode = mode });
            var expected = Enumerable.Range(0, descendants)
                .Select(index => new AssetEntryInfo($"{Root}/a/deep/item{index:D4}.bin", AssetEntryKind.File, 1, false)).ToArray();
            var snapshot = bundle.Dir(Root + "/a/deep");
            var asyncSnapshot = await bundle.DirAsync(Root + "/a/deep");
            Check.Equal(descendants, snapshot.Length);
            Check.Equal(descendants, asyncSnapshot.Length);
            Check.True(expected.SequenceEqual(snapshot), "Large directory snapshots must contain every direct child without spare buffer capacity.");
            Check.True(expected.SequenceEqual(asyncSnapshot));
        }
    }

    private static async Task MaximumPathAsync()
    {
        using var fixture = new Fixture(false);
        string prefix = string.Join("/", Enumerable.Repeat("d", 2047));
        string path = prefix + "/xx";
        Check.Equal(4096, AssetPath.Utf8.GetByteCount(path));
        string configuration = File.ReadAllText(fixture.ProjectPath);
        File.WriteAllText(fixture.ProjectPath, configuration.Replace("prefix: common/data", "prefix: " + prefix, StringComparison.Ordinal));
        fixture.Put("xx", [3, 2, 1]);
        await fixture.Build();
        AssetEntryInfo[] files = [new(path, AssetEntryKind.File, 3, false)];
        foreach (var options in Modes(fixture.ReadOptions))
        {
            using var bundle = QuireBundle.Open(fixture.Output, options);
            foreach (string directory in new[] { "", "d", prefix[..^2], prefix, path, prefix + "/yy" })
            {
                var expected = Expected(files, directory);
                Check.True(expected.SequenceEqual(bundle.Dir(directory)), $"Dir failed at path length {directory.Length}.");
                var snapshotAsync = await bundle.DirAsync(directory);
                Check.True(expected.SequenceEqual(snapshotAsync), $"DirAsync failed at path length {directory.Length}.");
                Check.True(expected.SequenceEqual(bundle.EnumerateEntries(directory)), $"Dir failed at path length {directory.Length}.");
                var asyncActual = await CollectAsync(bundle.EnumerateEntriesAsync(directory));
                Check.True(expected.SequenceEqual(asyncActual), $"DirAsync failed at path length {directory.Length}.");
            }
        }
    }

    private static IEnumerable<ReadOptions> Modes(ReadOptions defaults)
    {
        foreach (var mode in new[] { AssetIndexMode.Merged, AssetIndexMode.PerPackage, AssetIndexMode.Auto })
        {
            var options = defaults with { IndexMode = mode, MaxMergedIndexBytes = mode == AssetIndexMode.Auto ? 0 : defaults.MaxMergedIndexBytes };
            yield return options;
            yield return options with { MaxResidentIndexBytes = 0, PageCacheBytes = 196608 };
            yield return options with { MaxResidentIndexBytes = 0, PageCacheBytes = 0 };
            yield return options with { MaxResidentIndexBytes = 0, PageCacheBytes = 0, ReadMode = AssetReadMode.MemoryMapped };
        }
    }

    private static AssetEntryInfo[] Expected(IEnumerable<AssetEntryInfo> files, string directory)
    {
        string prefix = directory.Length == 0 ? "" : directory + "/";
        var result = new List<AssetEntryInfo>();
        var directories = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            if (!file.Path.StartsWith(prefix, StringComparison.Ordinal))
                continue;
            int slash = file.Path.IndexOf('/', prefix.Length);
            if (slash < 0)
                result.Add(file);
            else
                directories.Add(file.Path[..slash]);
        }
        result.AddRange(directories.Select(path => new AssetEntryInfo(path, AssetEntryKind.Directory, 0, false)));
        return result.OrderBy(item => item.Path + (item.IsDirectory ? "/" : ""), AssetPath.Comparer).ToArray();
    }

    private static async Task<AssetEntryInfo[]> CollectAsync(IAsyncEnumerable<AssetEntryInfo> entries)
    {
        var result = new List<AssetEntryInfo>();
        await foreach (var entry in entries)
            result.Add(entry);
        return result.ToArray();
    }

    private sealed class ReadCounter : IOperationProgress
    {
        private long _bytes;
        internal Action? OnRead { get; set; }
        internal long Bytes => Interlocked.Read(ref _bytes);
        internal void Reset() => Interlocked.Exchange(ref _bytes, 0);
        public void StartStage(string stage, long totalBytes = -1, long totalFiles = -1) { }
        public void Advance(long bytes = 0, long files = 0) { }
        public void AddRead(long bytes)
        {
            Interlocked.Add(ref _bytes, bytes);
            OnRead?.Invoke();
        }
        public void AddWritten(long bytes) { }
    }
}
