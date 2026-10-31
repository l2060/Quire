using System.Security.Cryptography;
using Quire;
using Quire.Cli.Build;
using Quire.Cli.Tags;
using Quire.Internal;

internal static class SignedPackageTests
{
    private const string Body = "common/data/body";

    internal static void Register(Action<string, Func<Task>> test)
    {
        test("AssetKey imports Base64 with strict length and public-key validation", Base64ImportAsync);
        test("trusted client keys reject unsigned downgrade and require the pinned signing identity", TrustPolicyAsync);
        foreach (bool encrypted in new[] { false, true })
        {
            bool useEncryption = encrypted;
            test($"client keys cannot forge signed payloads or authentication tables, encrypted={encrypted}",
                () => ForgeryAsync(useEncryption));
            test($"signed metadata and descriptor pages authenticate before use, encrypted={encrypted}",
                () => IndexForgeryAsync(useEncryption));
        }
    }

    private static Task Base64ImportAsync()
    {
        byte[] expected = TestKeys.Client.Export();
        string encoded = Convert.ToBase64String(expected);
        var imported = new AssetKey(encoded);
        Check.Equal(128, encoded.Length);
        Check.Bytes(expected, imported.Export());
        Check.Equal(encoded, imported.ExportBase64());
        Check.Bytes(expected, new AssetKey(" \r\n" + encoded.Insert(44, "\t") + "\n").Export());
        Check.Throws<ArgumentNullException>(() => new AssetKey((string)null!));
        foreach (string invalid in new[]
        {
            "", " ", "not a Base64 key!", encoded[..^1], encoded + "AAAA",
            Convert.ToBase64String(expected.AsSpan(0, 95)), Convert.ToBase64String(new byte[97]), Convert.ToHexString(expected),
            Convert.ToBase64String(new byte[64])
        })
            Check.Throws<FormatException>(() => new AssetKey(invalid));
        Check.Throws<ArgumentException>(() => new AssetKey(Convert.ToBase64String(new byte[AssetKey.Size])));
        return Task.CompletedTask;
    }

    private static async Task TrustPolicyAsync()
    {
        using var fixture = new Fixture(false);
        fixture.Put("body", [1, 2, 3]);
        var tag = await BuildSignedAsync(fixture);
        string file = Path.Combine(fixture.Root, tag.Outputs.Single());
        byte[] original = File.ReadAllBytes(file);
        var header = PackageHeader.Decode(original, original.Length);
        await RejectOpenAsync(fixture.Output, new ReadOptions());
        await RejectOpenAsync(fixture.Output, new ReadOptions { Key = TestKeys.OtherClient });
        await RejectOpenAsync(fixture.Output, new ReadOptions { KeyProvider = _ => null });

        int calls = 0;
        var provider = new ReadOptions
        {
            KeyProvider = info =>
            {
                Check.True(info.IsSigned && !info.IsEncrypted);
                Check.Equal(header.PackageUid, info.PackageUid);
                calls++;
                return new AssetKey(TestKeys.Client.ExportBase64());
            }
        };
        using (var bundle = QuireBundle.Open(fixture.Output, provider))
        using (var data = bundle.Read(Body)) Check.Bytes([1, 2, 3], data.Data.Span);
        Check.Equal(1, calls);
        using (var bundle = await QuireBundle.OpenAsync(fixture.Output, new ReadOptions { Key = TestKeys.Client, KeyProvider = _ => null }))
        using (var data = await bundle.ReadAsync(Body)) Check.Bytes([1, 2, 3], data.Data.Span);

        // Remove the signature and digest table, repairing all public protection.
        // This is a valid unsigned package, but it is never valid for a pinned client.
        byte[] downgraded = original.AsSpan(0, (int)header.PayloadEnd).ToArray();
        var publicHeader = header with
        {
            Signed = false, KeyVersion = 0, FileLength = header.PayloadEnd,
            AuthenticationOffset = 0, AuthenticationHash = default
        };
        byte[] publicKey = BlockCipher.DeriveKey(publicHeader, null);
        try { publicHeader.Encode(publicKey).CopyTo(downgraded, 0); }
        finally { CryptographicOperations.ZeroMemory(publicKey); }
        File.WriteAllBytes(file, downgraded);
        using (var bundle = QuireBundle.Open(fixture.Output))
        using (var data = bundle.Read(Body)) Check.Bytes([1, 2, 3], data.Data.Span);
        await RejectOpenAsync(fixture.Output, new ReadOptions { Key = TestKeys.Client });
        await RejectOpenAsync(fixture.Output, new ReadOptions { KeyProvider = _ => TestKeys.Client });
        await RejectOpenAsync(fixture.Output, new ReadOptions { KeyProvider = _ => null });
    }

    private static async Task ForgeryAsync(bool encrypted)
    {
        using var fixture = new Fixture(false, encrypted);
        byte[] expected = new byte[fixture.BuildOptions.BlockSize + 19];
        RandomNumberGenerator.Fill(expected);
        fixture.Put("body", expected);
        var tag = await BuildSignedAsync(fixture);
        string file = Path.Combine(fixture.Root, tag.Outputs.Single());
        byte[] original = File.ReadAllBytes(file);
        var header = PackageHeader.Decode(original, original.Length);
        Check.True(header.Signed);
        Check.Equal(encrypted, header.Encrypted);
        var options = new ReadOptions { Key = new AssetKey(TestKeys.Client.ExportBase64()), VerifyContentHash = false, PageCacheBytes = 0, MaxResidentIndexBytes = 0 };
        BlockRecord second;
        using (var package = await PackageReader.OpenAsync(file, options, true, default))
        {
            var entry = package.Find(Body)!.Value;
            byte[] descriptor = new byte[PackageHeader.BlockEntrySize];
            long sequence = entry.FirstBlock + 1;
            await package.Blocks.ReadAsync(descriptor, sequence * PackageHeader.BlockEntrySize, default);
            second = BlockRecord.Decode(descriptor, sequence, 19, header);
        }

        // The attacker has exactly the client material. Re-encryption is valid AES-GCM,
        // but it cannot authenticate as this publisher without the signing scalar.
        byte[] aes = BlockCipher.DeriveKey(header, TestKeys.Client);
        try
        {
            byte[] forged = (byte[])original.Clone();
            ChangePayload(forged, header, second, aes);
            File.WriteAllBytes(file, forged);
            foreach (var mode in new[] { AssetReadMode.RandomAccess, AssetReadMode.MemoryMapped })
            {
                using var bundle = await QuireBundle.OpenAsync(fixture.Output, options with { ReadMode = mode });
                byte[] prefix = new byte[8];
                Check.Equal(prefix.Length, await bundle.ReadAtAsync(Body, 0, prefix));
                Check.Bytes(expected.AsSpan(0, prefix.Length), prefix);
                byte[] destination = Enumerable.Repeat((byte)0xcc, 19).ToArray();
                Check.Throws<CryptographicException>(() => bundle.ReadAt(Body, header.BlockSize, destination));
                Check.True(destination.All(value => value == 0xcc), "Unverified bytes escaped into the caller's buffer.");
                await Check.ThrowsAsync<CryptographicException>(async () => { _ = await bundle.ReadAtAsync(Body, header.BlockSize, destination); });
                Check.True(destination.All(value => value == 0xcc));
                Check.Throws<CryptographicException>(() => { using var data = bundle.Read(Body, verifyContentHash: false); });
                await Check.ThrowsAsync<CryptographicException>(async () => { using var data = await bundle.ReadAsync(Body, verifyContentHash: false); });
                using var stream = bundle.OpenRead(Body);
                Check.Throws<CryptographicException>(() => stream.ReadAt(header.BlockSize, destination));
                await Check.ThrowsAsync<CryptographicException>(async () => { _ = await stream.ReadAtAsync(header.BlockSize, destination); });
                Check.Throws<CryptographicException>(() => bundle.CopyTo(Body, Stream.Null));
                await Check.ThrowsAsync<CryptographicException>(() => bundle.CopyToAsync(Body, Stream.Null));
            }

            // Updating the digest table still fails its signed root.
            long dataHashBase = PackageHeader.Pages(header.DirectoryLength, PackageHeader.PageSize) +
                PackageHeader.Pages(header.MetadataLength, PackageHeader.PageSize) + PackageHeader.Pages(header.BlocksLength, PackageHeader.PageSize);
            SHA256.HashData(forged.AsSpan((int)second.Offset, second.StoredLength),
                forged.AsSpan(checked((int)(header.AuthenticationOffset + (dataHashBase + second.Sequence) * 32)), 32));
            File.WriteAllBytes(file, forged);
            await RejectOpenAsync(fixture.Output, options);

            // The attacker can also recompute the table root, header HMAC and checksum.
            // Only the original publisher signature is beyond the client's capabilities.
            SHA256.HashData(forged.AsSpan((int)header.AuthenticationOffset, (int)header.AuthenticationLength), forged.AsSpan(448, 32));
            RefreshPublicAuthentication(forged, aes);
            File.WriteAllBytes(file, forged);
            await RejectOpenAsync(fixture.Output, options);

            // Treating the public X coordinate as a signing scalar creates a different
            // identity; it never grants the ability to sign for the pinned publisher.
            using var impostor = PublisherKey.Import(TestKeys.Client.Export().AsSpan(0, 64));
            Span<byte> digest = stackalloc byte[32];
            SHA256.HashData(forged.AsSpan(0, PackageHeader.SignatureOffset), digest);
            impostor.Sign(digest, forged.AsSpan(PackageHeader.SignatureOffset, PackageHeader.SignatureLength));
            RefreshPublicAuthentication(forged, aes);
            File.WriteAllBytes(file, forged);
            await RejectOpenAsync(fixture.Output, options);
        }
        finally { CryptographicOperations.ZeroMemory(aes); }
    }

    private static async Task IndexForgeryAsync(bool encrypted)
    {
        using var fixture = new Fixture(false, encrypted);
        fixture.Put("body", [1, 2, 3, 4]);
        fixture.Text("body.meta", "label=trusted");
        var tag = await BuildSignedAsync(fixture);
        string file = Path.Combine(fixture.Root, tag.Outputs.Single());
        byte[] original = File.ReadAllBytes(file);
        var header = PackageHeader.Decode(original, original.Length);
        var options = new ReadOptions { Key = TestKeys.Client, VerifyContentHash = false, PageCacheBytes = 0, MaxResidentIndexBytes = 0, MetadataCacheBytes = 0 };
        byte[] aes = BlockCipher.DeriveKey(header, TestKeys.Client);
        try
        {
            foreach (int domain in new[] { 1, 2, 3 })
            {
                long position = domain == 1 ? header.DirectoryOffset : domain == 2 ? header.MetadataOffset : header.BlocksOffset;
                int length = (int)Math.Min(PackageHeader.PageSize, domain == 1 ? header.DirectoryLength : domain == 2 ? header.MetadataLength : header.BlocksLength);
                byte[] forged = (byte[])original.Clone();
                byte[] plain = new byte[length];
                using var cipher = new BlockCipher(aes, header.TagId, domain);
                cipher.Decrypt(0, forged.AsSpan((int)position, length + 16), plain, length);
                plain[^1] ^= 1;
                cipher.Encrypt(0, plain, forged.AsSpan((int)position, length + 16), length);
                File.WriteAllBytes(file, forged);
                if (domain == 1)
                {
                    await RejectOpenAsync(fixture.Output, options);
                    continue;
                }
                foreach (var mode in new[] { AssetReadMode.RandomAccess, AssetReadMode.MemoryMapped })
                {
                    using var bundle = await QuireBundle.OpenAsync(fixture.Output, options with { ReadMode = mode });
                    if (domain == 2)
                    {
                        Check.Throws<CryptographicException>(() => bundle.ReadMetadata(Body));
                        await Check.ThrowsAsync<CryptographicException>(async () => { _ = await bundle.ReadMetadataAsync(Body); });
                    }
                    else
                    {
                        Check.Throws<CryptographicException>(() => bundle.ReadAt(Body, 0, new byte[1]));
                        await Check.ThrowsAsync<CryptographicException>(async () => { _ = await bundle.ReadAtAsync(Body, 0, new byte[1]); });
                    }
                }
            }
        }
        finally { CryptographicOperations.ZeroMemory(aes); }
    }

    private static async Task<BuildTag> BuildSignedAsync(Fixture fixture)
    {
        using var publisher = TestKeys.Create();
        return await ProjectBuilder.BuildAsync(fixture.Config(), null, fixture.BuildOptions, publisher, default);
    }

    private static void ChangePayload(byte[] bytes, PackageHeader header, BlockRecord block, byte[] aes)
    {
        Span<byte> stored = bytes.AsSpan((int)block.Offset, block.StoredLength);
        if (!header.Encrypted) { stored[0] ^= 1; return; }
        byte[] plain = new byte[block.Length];
        using var cipher = new BlockCipher(aes, header.TagId, 4);
        cipher.Decrypt(block.Sequence, stored, plain, block.Length, block.Codec);
        plain[0] ^= 1;
        cipher.Encrypt(block.Sequence, plain, stored, block.Length, block.Codec);
        byte[] decoded = new byte[plain.Length];
        cipher.Decrypt(block.Sequence, stored, decoded, block.Length, block.Codec);
        Check.Bytes(plain, decoded);
    }

    private static void RefreshPublicAuthentication(byte[] bytes, byte[] key)
    {
        HMACSHA256.HashData(key, bytes.AsSpan(0, PackageHeader.MacOffset), bytes.AsSpan(PackageHeader.MacOffset, PackageHeader.MacLength));
        SHA256.HashData(bytes.AsSpan(0, PackageHeader.ChecksumOffset), bytes.AsSpan(PackageHeader.ChecksumOffset, PackageHeader.ChecksumLength));
    }

    private static async Task RejectOpenAsync(string directory, ReadOptions options)
    {
        Check.Throws<InvalidDataException>(() => { using var bundle = QuireBundle.Open(directory, options); });
        await Check.ThrowsAsync<InvalidDataException>(async () => { using var bundle = await QuireBundle.OpenAsync(directory, options); });
    }
}
