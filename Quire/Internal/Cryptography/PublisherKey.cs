using System;
using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Quire.Internal;

/// <summary>Build-side key material. Never export this as a client key.</summary>
internal sealed class PublisherKey : IDisposable
{
    internal const int MaterialSize = 64;
    internal const int ProfileVersion = 2;
    private const int PasswordIterations = 600000;
    private readonly byte[] _material;
    private readonly ECDsa _signer;
    private readonly object _sync = new();
    private bool _disposed;
    internal AssetKey ClientKey { get; }

    private static ReadOnlySpan<byte> CurveOrder =>
    [
        0xff, 0xff, 0xff, 0xff, 0x00, 0x00, 0x00, 0x00,
        0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff,
        0xbc, 0xe6, 0xfa, 0xad, 0xa7, 0x17, 0x9e, 0x84,
        0xf3, 0xb9, 0xca, 0xc2, 0xfc, 0x63, 0x25, 0x51
    ];

    private PublisherKey(ReadOnlySpan<byte> material)
    {
        if (material.Length != MaterialSize)
            throw new ArgumentException("Publisher key material must contain exactly 64 bytes.", nameof(material));
        _material = material.ToArray();
        Span<byte> client = stackalloc byte[AssetKey.Size];
        byte[]? scalar = null;
        ECDsa? signer = null;
        try
        {
            if (!IsValidScalar(_material.AsSpan(32)))
                throw new ArgumentException("Publisher key material contains an invalid P-256 private scalar.", nameof(material));
            scalar = _material.AsSpan(32).ToArray();
            signer = ECDsa.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, D = scalar });
            ECParameters publicParameters = signer.ExportParameters(false);
            if (publicParameters.Q.X is not { Length: 32 } || publicParameters.Q.Y is not { Length: 32 })
                throw new CryptographicException("The P-256 provider returned an invalid public key.");
            _material.AsSpan(0, 32).CopyTo(client);
            publicParameters.Q.X.CopyTo(client[32..64]);
            publicParameters.Q.Y.CopyTo(client[64..]);
            ClientKey = new AssetKey(client);
            _signer = signer;
        }
        catch
        {
            signer?.Dispose();
            CryptographicOperations.ZeroMemory(_material);
            throw;
        }
        finally
        {
            if (scalar is not null) CryptographicOperations.ZeroMemory(scalar);
            CryptographicOperations.ZeroMemory(client);
        }
    }

    internal static PublisherKey Import(ReadOnlySpan<byte> material) => new(material);

    internal static PublisherKey FromPassword(string password)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);
        byte[] encoded = AssetPath.Utf8.GetBytes(password);
        Span<byte> root = stackalloc byte[32];
        Span<byte> material = stackalloc byte[MaterialSize];
        try
        {
            // Profile 2 is fixed and independent of package settings or format version.
            // UTF-8 bytes are used verbatim, without trimming or Unicode normalization.
            Rfc2898DeriveBytes.Pbkdf2(encoded, "Quire/password-keys/v2"u8, root,
                PasswordIterations, HashAlgorithmName.SHA256);
            HKDF.DeriveKey(HashAlgorithmName.SHA256, root, material[..32],
                ReadOnlySpan<byte>.Empty, "Quire/encryption/v2"u8);
            HKDF.DeriveKey(HashAlgorithmName.SHA256, root, material[32..],
                ReadOnlySpan<byte>.Empty, "Quire/signing/v2"u8);
            if (!IsValidScalar(material[32..]))
            {
                // Rejection sampling keeps the private scalar unbiased. Curve operations
                // are delegated entirely to the platform's ECDSA implementation.
                ReadOnlySpan<byte> label = "Quire/signing/v2/retry/"u8;
                Span<byte> info = stackalloc byte[label.Length + sizeof(uint)];
                label.CopyTo(info);
                uint counter = 0;
                do
                {
                    counter = checked(counter + 1);
                    BinaryPrimitives.WriteUInt32BigEndian(info[label.Length..], counter);
                    HKDF.DeriveKey(HashAlgorithmName.SHA256, root, material[32..], ReadOnlySpan<byte>.Empty, info);
                }
                while (!IsValidScalar(material[32..]));
            }
            return new PublisherKey(material);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encoded);
            CryptographicOperations.ZeroMemory(root);
            CryptographicOperations.ZeroMemory(material);
        }
    }

    private static bool IsValidScalar(ReadOnlySpan<byte> scalar) =>
        scalar.Length == 32 && scalar.IndexOfAnyExcept((byte)0) >= 0 && scalar.SequenceCompareTo(CurveOrder) < 0;

    internal byte[] Export()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return (byte[])_material.Clone();
        }
    }

    internal void Sign(ReadOnlySpan<byte> digest, Span<byte> signature)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (digest.Length != 32)
                throw new ArgumentException("Package signatures require a SHA-256 digest.", nameof(digest));
            if (signature.Length != AssetKey.SignatureSize)
                throw new ArgumentException("A P-256 P1363 signature must contain exactly 64 bytes.", nameof(signature));
            if (!_signer.TrySignHash(digest, signature, DSASignatureFormat.IeeeP1363FixedFieldConcatenation, out int written) ||
                written != AssetKey.SignatureSize)
                throw new CryptographicException("The P-256 provider could not create a package signature.");
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            try { _signer.Dispose(); }
            finally { CryptographicOperations.ZeroMemory(_material); }
        }
    }
}