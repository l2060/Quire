using System.Security.Cryptography;
using Quire;
using Quire.Cli.Extraction;
using Quire.Internal;

internal static class ReadIntegrityTests
{
    private const string Body = "common/data/body";
    private const string Optional = "common/data/optional";
    private const string Empty = "common/data/empty";

    internal static void Register(Action<string, Func<Task>> test)
    {
        test("content hash defaults and per-file overrides apply to complete reads", OptionalHashAsync);
        test("optional hashing supports compressed content and empty files", CompressedAndEmptyAsync);
        test("disabled content hashing retains encrypted payload and index authentication", AuthenticationAsync);
        test("disabled content hashing retains zlib format validation", CompressionValidationAsync);
    }

    private static async Task OptionalHashAsync()
    {
        Check.True(new ReadOptions().VerifyContentHash);
        using var fixture = new Fixture(false);
        byte[] expected = [1, 2, 3, 4, 5];
        fixture.Put("body", expected);
        fixture.Put("optional", expected);
        var tag = await fixture.Build();
        string path = Path.Combine(fixture.Root, tag.Outputs.Single());
        byte[] damaged = File.ReadAllBytes(path);
        using (var package = await PackageReader.OpenAsync(path, fixture.ReadOptions, false, default))
            foreach (string assetPath in new[] { Body, Optional })
            {
                var entry = package.Find(assetPath)!.Value;
                damaged[checked((int)(entry.DataOffset + entry.Length - 1))] ^= 1;
            }
        expected[^1] ^= 1;
        File.WriteAllBytes(path, damaged);

        foreach (var mode in new[] { AssetReadMode.RandomAccess, AssetReadMode.MemoryMapped })
        {
            foreach (var options in new[]
            {
                new ReadOptions { ReadMode = mode },
                new ReadOptions { ReadMode = mode, VerifyContentHash = true }
            })
            {
                using var bundle = QuireBundle.Open(fixture.Output, options);
                await AssertChecksumRejectedAsync(bundle, Body);
                await AssertChecksumRejectedAsync(bundle, Body, verifyContentHash: true);
                using (var data = bundle.Read(Optional, verifyContentHash: false))
                    Check.Bytes(expected, data.Data.Span);
                using (var data = await bundle.ReadAsync(Optional, verifyContentHash: false))
                    Check.Bytes(expected, data.Data.Span);
                // Disabling one read must not change the default of subsequent reads.
                await AssertChecksumRejectedAsync(bundle, Optional);
            }

            using var uncheckedBundle = await QuireBundle.OpenAsync(fixture.Output,
                new ReadOptions { ReadMode = mode, VerifyContentHash = false });
            await AssertChecksumRejectedAsync(uncheckedBundle, Body, verifyContentHash: true);
            using (var data = uncheckedBundle.Read(Body))
                Check.Bytes(expected, data.Data.Span);
            using (var data = await uncheckedBundle.ReadAsync(Body))
                Check.Bytes(expected, data.Data.Span);
            using (var data = uncheckedBundle.Read(Optional, verifyContentHash: false))
                Check.Bytes(expected, data.Data.Span);
            using (var data = await uncheckedBundle.ReadAsync(Optional, verifyContentHash: false))
                Check.Bytes(expected, data.Data.Span);

            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            foreach (bool verify in new[] { false, true })
            {
                Check.Throws<OperationCanceledException>(() => { using var data = uncheckedBundle.Read(Body, verify, cancellation.Token); });
                await Check.ThrowsAsync<OperationCanceledException>(async () =>
                { using var data = await uncheckedBundle.ReadAsync(Body, verify, cancellation.Token); });
            }
            Check.Throws<InvalidDataException>(() => uncheckedBundle.Verify());
            await Check.ThrowsAsync<InvalidDataException>(() => uncheckedBundle.VerifyAsync());
        }

        // Extraction must still reject damaged assets even when its ReadOptions disables
        // optional verification for the ordinary buffered-read APIs.
        string destination = Path.Combine(fixture.Root, "unpacked");
        await Check.ThrowsAsync<InvalidDataException>(() => PackageExtractor.ExtractAsync(fixture.Output, destination,
            new ReadOptions { VerifyContentHash = false }, default));
        Check.True(!Directory.Exists(destination));
    }

    private static async Task CompressedAndEmptyAsync()
    {
        foreach (var algorithm in new[] { CompressionAlgorithm.Lz4, CompressionAlgorithm.Zlib })
        {
            using var fixture = new Fixture(algorithm, policy: CompressionPolicy.Always);
            byte[] expected = new byte[65537];
            expected.AsSpan().Fill(37);
            fixture.Put("body", expected);
            fixture.Put("empty", []);
            var tag = await fixture.Build();
            string path = Path.Combine(fixture.Root, tag.Outputs.Single());
            RewriteExpectedHashes(path, fixture.ReadOptions);

            foreach (var mode in new[] { AssetReadMode.RandomAccess, AssetReadMode.MemoryMapped })
            {
                using (var bundle = QuireBundle.Open(fixture.Output, new ReadOptions { ReadMode = mode }))
                {
                    await AssertChecksumRejectedAsync(bundle, Body);
                    await AssertChecksumRejectedAsync(bundle, Empty);
                }
                using var uncheckedBundle = QuireBundle.Open(fixture.Output,
                    new ReadOptions { ReadMode = mode, VerifyContentHash = false });
                using (var body = uncheckedBundle.Read(Body))
                    Check.Bytes(expected, body.Data.Span);
                using (var body = await uncheckedBundle.ReadAsync(Body))
                    Check.Bytes(expected, body.Data.Span);
                using (var empty = uncheckedBundle.Read(Empty))
                    Check.Equal(0, empty.Data.Length);
                using (var empty = await uncheckedBundle.ReadAsync(Empty))
                    Check.Equal(0, empty.Data.Length);
            }
        }
    }

    private static async Task AuthenticationAsync()
    {
        using var fixture = new Fixture(compression: true, encryption: true);
        fixture.Put("body", [1, 2, 3, 4]);
        fixture.Text("body.meta", "name=authenticated");
        var tag = await fixture.Build();
        string path = Path.Combine(fixture.Root, tag.Outputs.Single());
        byte[] original = File.ReadAllBytes(path);
        var header = PackageHeader.Decode(original, original.Length);
        var options = fixture.ReadOptions with { VerifyContentHash = false };

        byte[] damaged = (byte[])original.Clone();
        damaged[checked((int)header.PayloadEnd - 1)] ^= 1;
        File.WriteAllBytes(path, damaged);
        using (var bundle = QuireBundle.Open(fixture.Output, options))
        {
            Check.Throws<CryptographicException>(() => { using var data = bundle.Read(Body); });
            await Check.ThrowsAsync<CryptographicException>(async () => { using var data = await bundle.ReadAsync(Body); });
        }

        damaged = (byte[])original.Clone();
        damaged[(int)header.MetadataOffset] ^= 1;
        File.WriteAllBytes(path, damaged);
        using (var bundle = QuireBundle.Open(fixture.Output, options))
        {
            Check.Throws<CryptographicException>(() => bundle.ReadMetadata(Body));
            await Check.ThrowsAsync<CryptographicException>(async () => { _ = await bundle.ReadMetadataAsync(Body); });
            Check.Throws<CryptographicException>(() => { using var data = bundle.Read(Body); });
            await Check.ThrowsAsync<CryptographicException>(async () => { using var data = await bundle.ReadAsync(Body); });
        }

        damaged = (byte[])original.Clone();
        damaged[(int)header.DirectoryOffset] ^= 1;
        File.WriteAllBytes(path, damaged);
        Check.Throws<InvalidDataException>(() => { using var bundle = QuireBundle.Open(fixture.Output, options); });
        await Check.ThrowsAsync<InvalidDataException>(async () => { using var bundle = await QuireBundle.OpenAsync(fixture.Output, options); });
    }

    private static async Task CompressionValidationAsync()
    {
        using var fixture = new Fixture(CompressionAlgorithm.Zlib, policy: CompressionPolicy.Always);
        fixture.Put("body", new byte[65536]);
        var tag = await fixture.Build();
        string path = Path.Combine(fixture.Root, tag.Outputs.Single());
        byte[] damaged = File.ReadAllBytes(path);
        var header = PackageHeader.Decode(damaged, damaged.Length);
        damaged[(int)header.DataOffset] = 0; // Invalid zlib header; index and declared sizes remain valid.
        File.WriteAllBytes(path, damaged);
        using var bundle = QuireBundle.Open(fixture.Output, new ReadOptions { VerifyContentHash = false });
        Check.Throws<InvalidDataException>(() => { using var data = bundle.Read(Body); });
        await Check.ThrowsAsync<InvalidDataException>(async () => { using var data = await bundle.ReadAsync(Body); });
    }

    private static async Task AssertChecksumRejectedAsync(QuireBundle bundle, string path, bool? verifyContentHash = null)
    {
        Check.Throws<InvalidDataException>(() =>
        { using var data = verifyContentHash is { } verify ? bundle.Read(path, verify) : bundle.Read(path); });
        await Check.ThrowsAsync<InvalidDataException>(async () =>
        { using var data = verifyContentHash is { } verify ? await bundle.ReadAsync(path, verify) : await bundle.ReadAsync(path); });
    }

    private static void RewriteExpectedHashes(string path, ReadOptions options)
    {
        byte[] bytes = File.ReadAllBytes(path);
        var header = PackageHeader.Decode(bytes, bytes.Length);
        Check.True(header.DirectoryLength <= PackageHeader.PageSize);
        byte[] key = BlockCipher.DeriveKey(header, options.Key);
        try
        {
            byte[] directory = new byte[(int)header.DirectoryLength];
            using var cipher = new BlockCipher(key, header.TagId, 1);
            cipher.Decrypt(0, bytes.AsSpan((int)header.DirectoryOffset, directory.Length + 16), directory, directory.Length);
            for (int i = 0; i < header.EntryCount; i++)
                directory[i * PackageHeader.EntrySize + 72] ^= 1;
            // Preserve valid authentication and payloads so only the expected SHA-256 differs.
            cipher.Encrypt(0, directory, bytes.AsSpan((int)header.DirectoryOffset, directory.Length + 16), directory.Length);
            File.WriteAllBytes(path, bytes);
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }
}
