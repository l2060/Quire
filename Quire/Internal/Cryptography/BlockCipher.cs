using System;
using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;

namespace Quire.Internal;

internal sealed class BlockCipher(byte[] master, string tag, int domain) : IDisposable
{
    private readonly uint _tag = PackageTag.Parse(tag);
    private long _group = -1;
    private AesGcm? _aes;
    private byte[]? _key;
    internal long Group => _group;

    internal static byte[] DeriveKey(PackageHeader header, AssetKey? clientKey)
    {
        if (header.Encrypted)
        {
            if (clientKey is null)
                throw new CryptographicException("An encrypted asset package requires a client key.");
            ReadOnlySpan<byte> label = "Quire/package-encryption/v1"u8;
            Span<byte> context = stackalloc byte[label.Length + 16];
            label.CopyTo(context);
            header.PackageUid.TryWriteBytes(context[label.Length..]);
            byte[] key = new byte[32];
            HKDF.DeriveKey(HashAlgorithmName.SHA256, clientKey.EncryptionKey, key,
                header.Salt, context);
            return key;
        }
        // Public, reversible protection: hides plaintext strings, deliberately NOT confidentiality.
        return HMACSHA256.HashData("Quire public index protection v1"u8, header.Salt);
    }

    private AesGcm Prepare(long sequence, int length, byte codec, Span<byte> nonce, Span<byte> aad)
    {
        if (sequence < 0)
            throw new InvalidDataException("Negative encryption sequence.");
        long group = sequence / 1024; // At most 1 GiB per derived key with the largest supported block size.
        if (_group != group)
        {
            _aes?.Dispose();
            if (_key is not null)
                CryptographicOperations.ZeroMemory(_key);
            Span<byte> context = stackalloc byte[16];
            context.Clear();
            BinaryPrimitives.WriteInt32LittleEndian(context, domain);
            BinaryPrimitives.WriteInt64LittleEndian(context[8..], group);
            _key = new byte[32];
            HKDF.DeriveKey(HashAlgorithmName.SHA256, master.AsSpan(), _key.AsSpan(), ReadOnlySpan<byte>.Empty, context);
            _aes = new AesGcm(_key, 16);
            _group = group;
        }
        nonce.Clear();
        BinaryPrimitives.WriteInt32LittleEndian(nonce, domain);
        BinaryPrimitives.WriteInt64LittleEndian(nonce[4..], sequence);
        aad.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(aad, _tag);
        BinaryPrimitives.WriteInt32LittleEndian(aad[16..], domain);
        BinaryPrimitives.WriteInt64LittleEndian(aad[20..], sequence);
        BinaryPrimitives.WriteInt32LittleEndian(aad[28..], length);
        aad[32] = codec;
        return _aes!;
    }

    internal void Encrypt(long sequence, ReadOnlySpan<byte> plain, Span<byte> output, int rawLength, byte codec = 0)
    {
        Span<byte> nonce = stackalloc byte[12];
        Span<byte> aad = stackalloc byte[40];
        var aes = Prepare(sequence, rawLength, codec, nonce, aad);
        BinaryPrimitives.WriteInt32LittleEndian(aad[36..], plain.Length);
        aes.Encrypt(nonce, plain, output[..plain.Length], output.Slice(plain.Length, 16), aad);
    }
    internal void Decrypt(long sequence, ReadOnlySpan<byte> input, Span<byte> plain, int rawLength, byte codec = 0)
    {
        Span<byte> nonce = stackalloc byte[12];
        Span<byte> aad = stackalloc byte[40];
        var aes = Prepare(sequence, rawLength, codec, nonce, aad);
        BinaryPrimitives.WriteInt32LittleEndian(aad[36..], input.Length - 16);
        aes.Decrypt(nonce, input[..^16], input[^16..], plain, aad);
    }
    public void Dispose()
    {
        _aes?.Dispose();
        if (_key is not null)
            CryptographicOperations.ZeroMemory(_key);
    }
}
