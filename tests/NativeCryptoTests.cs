using System.Security.Cryptography;
using Quire;
using Quire.Internal;

internal static class NativeCryptoTests
{
    private const string GeneratorX = "6B17D1F2E12C4247F8BCE6E563A440F277037D812DEB33A0F4A13945D898C296";
    private const string GeneratorY = "4FE342E2FE1A7F9B8EE7EB4A7C0F9E162BCE33576B315ECECBB6406837BF51F5";
    private const string GroupOrder = "FFFFFFFF00000000FFFFFFFFFFFFFFFFBCE6FAADA7179E84F3B9CAC2FC632551";

    internal static void Register(Action<string, Func<Task>> test)
    {
        test("native P-256 signing matches the standard generator and interoperates with ECDsa", NativeInteropAsync);
        test("native client and publisher imports reject invalid curve points and private scalars", InvalidKeysAsync);
        test("one trusted client key supports concurrent package verification through KeyProvider", ConcurrentVerificationAsync);
    }

    private static Task NativeInteropAsync()
    {
        byte[] material = new byte[64];
        material.AsSpan(0, 32).Fill(0x42);
        material[^1] = 1;
        using var publisher = PublisherKey.Import(material);
        CryptographicOperations.ZeroMemory(material);
        AssetKey key = publisher.ClientKey;
        byte[] exported = key.Export();
        Check.Equal(96, exported.Length);
        Check.True(exported.AsSpan(0, 32).IndexOfAnyExcept((byte)0x42) < 0);
        Check.Bytes(Convert.FromHexString(GeneratorX), exported.AsSpan(32, 32));
        Check.Bytes(Convert.FromHexString(GeneratorY), exported.AsSpan(64, 32));

        byte[] scalar = new byte[32];
        scalar[^1] = 1;
        using var native = ECDsa.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, D = scalar });
        CryptographicOperations.ZeroMemory(scalar);
        byte[] hash = SHA256.HashData("Quire native signature interoperability"u8);
        byte[] signature = new byte[64];
        publisher.Sign(hash, signature);
        Check.True(native.VerifyHash(hash, signature, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
        byte[] nativeSignature = native.SignHash(hash, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        Check.Equal(64, nativeSignature.Length);
        Check.True(key.Verify(hash, nativeSignature));
        Check.True(!key.Verify(hash, nativeSignature.AsSpan(0, 63)));
        Check.True(!key.Verify(hash.AsSpan(0, 31), nativeSignature));
        hash[0] ^= 1;
        Check.True(!key.Verify(hash, nativeSignature));

        // Disposing the publisher must not invalidate the independent public client key.
        publisher.Dispose();
        hash[0] ^= 1;
        Check.True(key.Verify(hash, nativeSignature));
        Check.Throws<ObjectDisposedException>(() => publisher.Sign(hash, signature));
        return Task.CompletedTask;
    }

    private static Task InvalidKeysAsync()
    {
        Check.Throws<ArgumentException>(() => PublisherKey.Import(new byte[63]));
        foreach (byte[] invalidScalar in new[]
        {
            new byte[32], Convert.FromHexString(GroupOrder),
            Convert.FromHexString("FFFFFFFF00000000FFFFFFFFFFFFFFFFBCE6FAADA7179E84F3B9CAC2FC632552"),
            Enumerable.Repeat((byte)0xff, 32).ToArray()
        })
        {
            byte[] material = new byte[64];
            invalidScalar.CopyTo(material, 32);
            Check.Throws<ArgumentException>(() => PublisherKey.Import(material));
        }
        Check.Throws<ArgumentException>(() => new AssetKey(new byte[64]));
        Check.Throws<ArgumentException>(() => new AssetKey(new byte[AssetKey.Size]));
        byte[] offCurve = TestKeys.Client.Export();
        offCurve.AsSpan(32).Clear();
        offCurve[^1] = 1;
        Check.Throws<ArgumentException>(() => new AssetKey(offCurve));
        byte[] outsideField = TestKeys.Client.Export();
        outsideField.AsSpan(32, 32).Fill(0xff);
        Check.Throws<ArgumentException>(() => new AssetKey(outsideField));
        return Task.CompletedTask;
    }

    private static async Task ConcurrentVerificationAsync()
    {
        using var fixture = new Fixture(false, true);
        fixture.Put("body", [1, 2, 3, 4]);
        await fixture.Build();
        AssetKey shared = new(TestKeys.Client.ExportBase64());
        int calls = 0;
        var options = new ReadOptions
        {
            KeyProvider = package =>
            {
                Check.True(package.IsSigned && package.IsEncrypted);
                Interlocked.Increment(ref calls);
                return shared;
            }
        };
        await Task.WhenAll(Enumerable.Range(0, 16).Select(index => Task.Run(async () =>
        {
            using var bundle = index % 2 == 0 ? QuireBundle.Open(fixture.Output, options) : await QuireBundle.OpenAsync(fixture.Output, options);
            using var data = await bundle.ReadAsync("common/data/body");
            Check.Bytes([1, 2, 3, 4], data.Data.Span);
        })));
        Check.Equal(16, calls);
    }
}
