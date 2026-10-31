using Quire;
using Quire.Internal;

internal static class ReaderTests
{
    private const string Path = "common/data/body.bin";
    private const int BlockSize = 256 * 1024;
    internal static void Register(Action<string, Func<Task>> test)
    {
        foreach (var compression in new[]
        {
            CompressionAlgorithm.None,
            CompressionAlgorithm.Lz4,
            CompressionAlgorithm.Zlib
        }

        )
        {
            foreach (bool encryption in new[]
            {
                false,
                true
            }

            )
            {
                var algorithm = compression;
                bool encrypted = encryption;
                test($"reusable positional reader and direct block decoding, compression={algorithm}, AES={encrypted}", async () =>
                {
                    using var fixture = new Fixture(algorithm, encrypted);
                    byte[] expected = new byte[3 * BlockSize + 12345];
                    var random = new Random(274);
                    random.NextBytes(expected.AsSpan(BlockSize, BlockSize));
                    random.NextBytes(expected.AsSpan(3 * BlockSize));
                    expected.AsSpan(2 * BlockSize, BlockSize).Fill(71);
                    fixture.Put("body.bin", expected);
                    fixture.Text("body.bin.meta", "reader=true");
                    fixture.Put("empty.bin", []);
                    await fixture.Build();
                    foreach (var mode in new[]
                    {
                        AssetReadMode.RandomAccess,
                        AssetReadMode.MemoryMapped
                    }

                    )
                    {
                        using var packages = await QuireBundle.OpenAsync(fixture.Output, fixture.ReadOptions with
                        {
                            ReadMode = mode
                        });
                        using var reader = packages.OpenRead(Path);
                        Check.Equal(new AssetEntryInfo(Path, AssetEntryKind.File, expected.Length, true), reader.Info);
                        Check.Equal((long)expected.Length, reader.Length);
                        reader.Position = 19;
                        byte[] actual = new byte[expected.Length];
                        Check.Equal(expected.Length, reader.ReadAt(0, actual));
                        Check.Bytes(expected, actual);
                        Check.Equal(19L, reader.Position);
                        actual.AsSpan().Clear();
                        Check.Equal(expected.Length, await reader.ReadAtAsync(0, actual));
                        Check.Bytes(expected, actual);
                        Check.Equal(19L, reader.Position);
                        byte[] range = new byte[BlockSize + 91];
                        Check.Equal(range.Length, reader.ReadAt(BlockSize - 37, range));
                        Check.Bytes(expected.AsSpan(BlockSize - 37, range.Length), range);
                        Check.Equal(19L, reader.Position);
                        Check.Equal(range.Length, await reader.ReadAtAsync(BlockSize - 37, range));
                        Check.Bytes(expected.AsSpan(BlockSize - 37, range.Length), range);
                        // A direct decode of another block must not replace the cached partial block.
                        Check.Equal(63, reader.ReadAt(13, range.AsSpan(0, 63)));
                        Check.Equal(BlockSize, reader.ReadAt(BlockSize, range.AsSpan(0, BlockSize)));
                        Check.Bytes(expected.AsSpan(BlockSize, BlockSize), range.AsSpan(0, BlockSize));
                        Check.Equal(63, reader.ReadAt(13, range.AsSpan(0, 63)));
                        Check.Bytes(expected.AsSpan(13, 63), range.AsSpan(0, 63));
                        Check.Equal(BlockSize, await reader.ReadAtAsync(2 * BlockSize, range.AsMemory(0, BlockSize)));
                        Check.Equal(63, await reader.ReadAtAsync(13, range.AsMemory(0, 63)));
                        Check.Bytes(expected.AsSpan(13, 63), range.AsSpan(0, 63));
                        for (int i = 0; i < 100; i++)
                        {
                            int offset = random.Next(expected.Length + 1);
                            int requested = random.Next(range.Length + 1);
                            int count = Math.Min(requested, expected.Length - offset);
                            int read = i % 2 == 0 ? reader.ReadAt(offset, range.AsSpan(0, requested)) : await reader.ReadAtAsync(offset, range.AsMemory(0, requested));
                            Check.Equal(count, read);
                            Check.Bytes(expected.AsSpan(offset, count), range.AsSpan(0, count));
                            Check.Equal(19L, reader.Position);
                        }

                        Check.Equal(0, reader.ReadAt(0, Span<byte>.Empty));
                        Check.Equal(0, await reader.ReadAtAsync(0, Memory<byte>.Empty));
                        Check.Equal(0, reader.ReadAt(expected.Length, range));
                        Check.Equal(0, await reader.ReadAtAsync(expected.Length + 100L, range));
                        Check.Equal(0, reader.ReadAt(long.MaxValue, range));
                        Check.Equal(0, await reader.ReadAtAsync(long.MaxValue, range));
                        Check.Throws<ArgumentOutOfRangeException>(() => reader.ReadAt(-1, range));
                        await Check.ThrowsAsync<ArgumentOutOfRangeException>(async () => await reader.ReadAtAsync(-1, range));
                        Check.Equal(19L, reader.Position);
                        using var cancellation = new CancellationTokenSource();
                        cancellation.Cancel();
                        await Check.ThrowsAsync<OperationCanceledException>(async () => await reader.ReadAtAsync(BlockSize, range, cancellation.Token));
                        await Check.ThrowsAsync<OperationCanceledException>(async () =>
                        {
                            _ = await reader.ReadAsync(range, cancellation.Token);
                        });
                        Check.Equal(19L, reader.Position);
                        Check.Equal(range.Length, await reader.ReadAtAsync(BlockSize, range));
                        Check.Bytes(expected.AsSpan(BlockSize, range.Length), range);
                        reader.Position = long.MaxValue;
                        Check.Equal(0, reader.Read(range));
                        Check.Equal(0, await reader.ReadAsync(range));
                        Check.Equal(-1, reader.ReadByte());
                        reader.Position = 0;
                        reader.ReadExactly(actual);
                        Check.Bytes(expected, actual);
                        Check.Equal((long)expected.Length, reader.Position);
                        reader.Position = 0;
                        await reader.ReadExactlyAsync(actual);
                        Check.Bytes(expected, actual);
                        Check.Equal((long)expected.Length, reader.Position);
                        using var empty = packages.OpenRead("common/data/empty.bin");
                        Check.Equal(0, empty.ReadAt(0, range));
                        Check.Equal(0, await empty.ReadAtAsync(0, range));
                        Check.Equal(-1, empty.ReadByte());
                        AssertReaderDoesNotAllocate(reader);
                        packages.Dispose();
                        Check.Equal(expected.Length, reader.ReadAt(0, actual));
                        Check.Bytes(expected, actual);
                        Check.Equal(expected.Length, await reader.ReadAtAsync(0, actual));
                        Check.Bytes(expected, actual);
                        reader.Dispose();
                        reader.Dispose();
                        Check.True(!reader.CanRead && !reader.CanSeek);
                        Check.Throws<ObjectDisposedException>(() => reader.ReadAt(0, range));
                        await Check.ThrowsAsync<ObjectDisposedException>(async () => await reader.ReadAtAsync(0, range));
                    }
                });
            }
        }

        test("warmed file lookup and raw positional reads allocate zero bytes", async () =>
        {
            using var fixture = new Fixture(false);
            byte[] expected = new byte[4096];
            new Random(72).NextBytes(expected);
            fixture.Put("body.bin", expected);
            await fixture.Build();
            foreach (var mode in new[]
            {
                AssetReadMode.RandomAccess,
                AssetReadMode.MemoryMapped
            }

            )
            {
                using var packages = QuireBundle.Open(fixture.Output, new ReadOptions { ReadMode = mode });
                byte[] buffer = new byte[64];
                for (int i = 0; i < 2048; i++)
                {
                    _ = packages.FileExists(Path);
                    _ = packages.GetFileInfo(Path);
                    _ = packages.ReadAt(Path, 13, buffer);
                }

                long checksum = 0;
                long before = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 1000; i++)
                {
                    checksum += packages.FileExists(Path) ? 1 : 0;
                    checksum += packages.GetFileInfo(Path).Length;
                    checksum += packages.ReadAt(Path, 13, buffer);
                    checksum += buffer[0];
                }

                long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
                Check.Equal(0L, allocated);
                Check.Equal(1000L * (1 + expected.Length + buffer.Length + expected[13]), checksum);
                Check.Bytes(expected.AsSpan(13, buffer.Length), buffer);
            }
        });
    }

    private static void AssertReaderDoesNotAllocate(AssetReader reader)
    {
        byte[] buffer = new byte[63];
        for (int i = 0; i < 2048; i++)
        {
            _ = reader.ReadAt(13, buffer);
            reader.Position = 13;
            _ = reader.ReadByte();
        }

        int count = 0;
        int checksum = 0;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++)
        {
            count += reader.ReadAt(13, buffer);
            reader.Position = 13;
            checksum += reader.ReadByte();
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Check.Equal(0L, allocated);
        Check.Equal(63000, count);
        Check.Equal(0, checksum);
    }
}
