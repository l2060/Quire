using System.Security.Cryptography;
using Quire;
using Quire.Cli.Sources;
using Quire.Internal;

internal static class CompressionTests
{
    private const string AssetPathPrefix = "common/data/";
    private const int BlockSize = 256 * 1024;

    internal static void Register(Action<string, Func<Task>> test)
    {
        test("compression defaults, global settings and independent package overrides", ConfigurationAsync);
        test("compression algorithm and policy changes preserve unchanged content", IncrementalsAsync);
        test("malformed and truncated zlib blocks fail synchronous and asynchronous reads", MalformedZlibAsync);
        foreach (var algorithm in new[] { CompressionAlgorithm.Lz4, CompressionAlgorithm.Zlib })
            foreach (var policy in new[] { CompressionPolicy.Adaptive, CompressionPolicy.Always })
                foreach (bool encrypted in new[] { false, true })
                {
                    var selectedAlgorithm = algorithm;
                    var selectedPolicy = policy;
                    bool selectedEncryption = encrypted;
                    test($"compression={algorithm} policy={policy} encrypted={encrypted}: block storage and reads",
                        () => BlockStorageAsync(selectedAlgorithm, selectedPolicy, selectedEncryption));
                }
    }

    private static async Task ConfigurationAsync()
    {
        using var fixture = new Fixture();
        fixture.Put("a", [1]);
        string original = File.ReadAllText(fixture.ProjectPath);
        File.WriteAllText(fixture.ProjectPath, original.Replace("compression: lz4\n", "").Replace("compression-policy: adaptive\n", ""));
        var defaults = fixture.Config().Packages.Single();
        Check.Equal(CompressionAlgorithm.Lz4, defaults.Compression);
        Check.Equal(CompressionPolicy.Adaptive, defaults.CompressionPolicy);

        string configured = original.Replace("compression: lz4", "compression: zlib")
            .Replace("compression-policy: adaptive", "compression-policy: always") +
            "  - name: uncompressed\n    compression: none\n    compression-policy: adaptive\n    sources:\n      - directory: ./input\n        prefix: raw\n" +
            "  - name: lz4\n    compression: lz4\n    sources:\n      - directory: ./input\n        prefix: lz4\n" +
            "  - name: adaptive\n    compression-policy: adaptive\n    sources:\n      - directory: ./input\n        prefix: adaptive\n";
        File.WriteAllText(fixture.ProjectPath, configured);
        var sources = await SourceScanner.ScanAsync(fixture.Config(), 1, default);
        foreach (var source in sources)
        {
            var expected = source.Package switch
            {
                "main" => (CompressionAlgorithm.Zlib, CompressionPolicy.Always),
                "uncompressed" => (CompressionAlgorithm.None, CompressionPolicy.Adaptive),
                "lz4" => (CompressionAlgorithm.Lz4, CompressionPolicy.Always),
                "adaptive" => (CompressionAlgorithm.Zlib, CompressionPolicy.Adaptive),
                _ => throw new Exception("Unexpected package.")
            };
            Check.Equal(expected.Item1, source.Compression);
            Check.Equal(expected.Item2, source.CompressionPolicy);
        }
        Check.Equal(4, sources.Length);

        foreach (string value in new[] { "true", "false", "LZ4", "gzip", "1", "''" })
        {
            File.WriteAllText(fixture.ProjectPath, original.Replace("compression: lz4", "compression: " + value));
            Check.Throws<InvalidDataException>(() => fixture.Config());
            File.WriteAllText(fixture.ProjectPath, original.Replace("  - name: main", "  - name: main\n    compression: " + value));
            Check.Throws<InvalidDataException>(() => fixture.Config());
        }
        foreach (string value in new[] { "true", "false", "ALWAYS", "auto", "1", "''" })
        {
            File.WriteAllText(fixture.ProjectPath, original.Replace("compression-policy: adaptive", "compression-policy: " + value));
            Check.Throws<InvalidDataException>(() => fixture.Config());
            File.WriteAllText(fixture.ProjectPath, original.Replace("  - name: main", "  - name: main\n    compression-policy: " + value));
            Check.Throws<InvalidDataException>(() => fixture.Config());
        }
    }

    private static async Task BlockStorageAsync(CompressionAlgorithm algorithm, CompressionPolicy policy, bool encrypted)
    {
        using var fixture = new Fixture(algorithm, encrypted, policy);
        byte[] expected = new byte[2 * BlockSize + 37];
        new Random(176).NextBytes(expected.AsSpan(BlockSize));
        fixture.Put("mixed", expected);
        fixture.Put("tiny", [23]);
        fixture.Put("empty", []);
        var tag = await fixture.Build();
        using (var package = await PackageReader.OpenAsync(Path.Combine(fixture.Root, tag.Outputs.Single()), fixture.ReadOptions, true, default))
        {
            var entry = package.Find(AssetPathPrefix + "mixed")!.Value;
            Check.True(entry.Blocked);
            Check.Equal(3L, entry.BlockCount);
            for (int block = 0; block < entry.BlockCount; block++)
            {
                var record = ReadBlock(package, entry, block);
                byte expectedCodec = policy == CompressionPolicy.Always || block == 0 ? (byte)algorithm : (byte)0;
                Check.Equal(expectedCodec, record.Codec);
                if (expectedCodec == 0)
                    Check.Equal(record.Length + (encrypted ? 16 : 0), record.StoredLength);
            }
            var tiny = ReadBlock(package, package.Find(AssetPathPrefix + "tiny")!.Value, 0);
            Check.Equal(policy == CompressionPolicy.Always ? (byte)algorithm : (byte)0, tiny.Codec);
            if (policy == CompressionPolicy.Always)
                Check.True(tiny.StoredLength > tiny.Length + (encrypted ? 16 : 0), "Always must retain compressed blocks even when they expand.");
        }

        foreach (var readMode in new[] { AssetReadMode.RandomAccess, AssetReadMode.MemoryMapped })
        {
            using var bundle = await QuireBundle.OpenAsync(fixture.Output, fixture.ReadOptions with { ReadMode = readMode });
            using var data = await bundle.ReadAsync(AssetPathPrefix + "mixed");
            Check.Bytes(expected, data.Data.Span);
            using var reader = bundle.OpenRead(AssetPathPrefix + "mixed");
            byte[] actual = new byte[BlockSize + 17];
            reader.Position = 17;
            var random = new Random(819);
            for (int i = 0; i < 24; i++)
            {
                int offset = random.Next(expected.Length);
                int length = Math.Min(random.Next(1, actual.Length), expected.Length - offset);
                int count = i % 2 == 0 ? reader.ReadAt(offset, actual.AsSpan(0, length)) : await reader.ReadAtAsync(offset, actual.AsMemory(0, length));
                Check.Equal(length, count);
                Check.Bytes(expected.AsSpan(offset, length), actual.AsSpan(0, length));
                Check.Equal(17L, reader.Position);
            }
            Check.Equal(actual.Length, await bundle.ReadAtAsync(AssetPathPrefix + "mixed", BlockSize - 3, actual));
            Check.Bytes(expected.AsSpan(BlockSize - 3, actual.Length), actual);
            using var tiny = bundle.Read(AssetPathPrefix + "tiny");
            Check.Bytes([23], tiny.Data.Span);
            using var empty = await bundle.ReadAsync(AssetPathPrefix + "empty");
            Check.Equal(0, empty.Data.Length);
            await bundle.VerifyAsync();
        }
    }

    private static async Task IncrementalsAsync()
    {
        using var fixture = new Fixture(CompressionAlgorithm.None);
        byte[] expected = new byte[12345];
        fixture.Put("a", expected);
        var baseline = await fixture.Build();
        string original = File.ReadAllText(fixture.ProjectPath);
        foreach (var setting in new[]
        {
            (CompressionAlgorithm.Lz4, CompressionPolicy.Adaptive),
            (CompressionAlgorithm.Zlib, CompressionPolicy.Adaptive),
            (CompressionAlgorithm.Zlib, CompressionPolicy.Always),
            (CompressionAlgorithm.Lz4, CompressionPolicy.Always)
        })
        {
            File.WriteAllText(fixture.ProjectPath, original.Replace("compression: none", "compression: " + setting.Item1.ToString().ToLowerInvariant())
                .Replace("compression-policy: adaptive", "compression-policy: " + setting.Item2.ToString().ToLowerInvariant()));
            var changed = await fixture.Build(baseline.TagId);
            Check.Equal(0, changed.ContentChanges);
            Check.Equal(0, changed.Outputs.Length);
            using (var bundle = QuireBundle.Open(fixture.Output))
            {
                using var data = bundle.Read(AssetPathPrefix + "a");
                Check.Bytes(expected, data.Data.Span);
            }
            expected = Enumerable.Repeat((byte)setting.Item1, 12346 + (int)setting.Item2).ToArray();
            fixture.Put("a", expected);
            baseline = await fixture.Build(changed.TagId);
            Check.Equal(1, baseline.ContentChanges);
            Check.Equal(1, baseline.Outputs.Length);
            using (var package = await PackageReader.OpenAsync(Path.Combine(fixture.Root, baseline.Outputs.Single()), fixture.ReadOptions, false, default))
                Check.Equal((byte)setting.Item1, ReadBlock(package, package.Find(AssetPathPrefix + "a")!.Value, 0).Codec);
            baseline = await fixture.Build(baseline.TagId);
            Check.Equal(0, baseline.ContentChanges);
            Check.Equal(0, baseline.Outputs.Length);
        }
    }

    private static async Task MalformedZlibAsync()
    {
        using var fixture = new Fixture(CompressionAlgorithm.Zlib, policy: CompressionPolicy.Always);
        fixture.Put("a", new byte[16385]);
        var tag = await fixture.Build();
        string path = Path.Combine(fixture.Root, tag.Outputs.Single());
        byte[] original = File.ReadAllBytes(path);
        PackageHeader header;
        BlockRecord record;
        using (var package = await PackageReader.OpenAsync(path, fixture.ReadOptions, false, default))
        {
            header = package.Header;
            record = ReadBlock(package, package.ReadEntry(0), 0);
        }
        Check.Equal((byte)CompressionAlgorithm.Zlib, record.Codec);
        byte[] encoded = original.AsSpan((int)record.Offset, record.StoredLength).ToArray();
        foreach (int outputLength in new[] { 16384, 16386 })
            Check.Throws<InvalidDataException>(() => BlockCompression.Decode(CompressionAlgorithm.Zlib, encoded, encoded.Length, new byte[outputLength]));
        byte[] key = BlockCipher.DeriveKey(header, null);
        try
        {
            foreach (int removed in new[] { -1, 0, 1, 2, 3, 4, record.StoredLength / 2 })
            {
                byte[] invalid = (byte[])original.Clone();
                if (removed < 0)
                    invalid[(int)record.Offset + record.StoredLength - 1] ^= 1; // Invalid Adler-32 trailer.
                else if (removed == 0)
                    invalid[(int)record.Offset] = 0; // Invalid zlib header, while package/index authentication remains valid.
                else
                {
                    var shortened = record with { StoredLength = record.StoredLength - removed };
                    using var cipher = new BlockCipher(key, header.TagId, 3);
                    cipher.Encrypt(0, shortened.Encode(), invalid.AsSpan((int)header.BlocksOffset, PackageHeader.BlockEntrySize + 16), PackageHeader.BlockEntrySize);
                }
                File.WriteAllBytes(path, invalid);
                using var bundle = QuireBundle.Open(fixture.Output);
                using var reader = bundle.OpenRead(AssetPathPrefix + "a");
                byte[] output = new byte[16385];
                Check.Throws<InvalidDataException>(() => reader.ReadAt(0, output));
                await Check.ThrowsAsync<InvalidDataException>(async () => await reader.ReadAtAsync(0, output));
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private static BlockRecord ReadBlock(PackageReader package, EntryRecord entry, int block)
    {
        byte[] descriptor = new byte[PackageHeader.BlockEntrySize];
        long sequence = entry.FirstBlock + block;
        package.Blocks.Read(descriptor, sequence * PackageHeader.BlockEntrySize);
        int expectedLength = (int)Math.Min(package.Header.BlockSize, entry.Length - (long)block * package.Header.BlockSize);
        return BlockRecord.Decode(descriptor, sequence, expectedLength, package.Header);
    }
}
