using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Quire.Internal;

/// <summary>One trusted digest table per package; actual encoded pages and blocks are checked on access.</summary>
internal sealed class PackageAuthentication : IDisposable
{
    private byte[] _hashes;
    private readonly long _metadataStart, _blocksStart, _dataStart, _count;
    private bool _disposed;

    private PackageAuthentication(PackageHeader header, byte[] hashes)
    {
        _hashes = hashes;
        _metadataStart = PackageHeader.Pages(header.DirectoryLength, PackageHeader.PageSize);
        _blocksStart = _metadataStart + PackageHeader.Pages(header.MetadataLength, PackageHeader.PageSize);
        _dataStart = _blocksStart + PackageHeader.Pages(header.BlocksLength, PackageHeader.PageSize);
        _count = header.AuthenticationCount;
    }

    internal static async ValueTask<PackageAuthentication> LoadAsync(FileStorage file, PackageHeader header,
        ReadOnlyMemory<byte> headerBytes, AssetKey key, bool asynchronous, CancellationToken token)
    {
        VerifyHeader(headerBytes.Span, key);
        token.ThrowIfCancellationRequested();
        if (header.AuthenticationLength > Array.MaxLength)
            throw new InvalidDataException("Package authentication table exceeds the supported size.");
        byte[] hashes = new byte[checked((int)header.AuthenticationLength)];
        if (asynchronous)
            await file.ReadAsync(hashes, header.AuthenticationOffset, token).ConfigureAwait(false);
        else
            file.Read(hashes, header.AuthenticationOffset);
        Span<byte> digest = stackalloc byte[32];
        SHA256.HashData(hashes, digest);
        if (!header.AuthenticationHash.Matches(digest))
            throw new InvalidDataException("Package authentication table does not match its signed digest.");
        return new(header, hashes);
    }

    private static void VerifyHeader(ReadOnlySpan<byte> header, AssetKey key)
    {
        Span<byte> digest = stackalloc byte[32];
        SHA256.HashData(header[..PackageHeader.SignatureOffset], digest);
        if (!key.Verify(digest, header.Slice(PackageHeader.SignatureOffset, PackageHeader.SignatureLength)))
            throw new CryptographicException("Package signature does not match the trusted publisher key.");
    }

    internal void Validate(int domain, long sequence, ReadOnlySpan<byte> stored)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        (long start, long end) = domain switch
        {
            1 => (0, _metadataStart),
            2 => (_metadataStart, _blocksStart),
            3 => (_blocksStart, _dataStart),
            4 => (_dataStart, _count),
            _ => throw new InvalidDataException("Invalid package authentication domain.")
        };
        if (sequence < 0 || sequence >= end - start)
            throw new InvalidDataException("Package page or block is outside its authenticated section.");
        Span<byte> digest = stackalloc byte[32];
        SHA256.HashData(stored, digest);
        int offset = checked((int)((start + sequence) * 32));
        if (!CryptographicOperations.FixedTimeEquals(digest, _hashes.AsSpan(offset, 32)))
            throw new CryptographicException($"Package authentication failed for section {domain}, page or block {sequence}.");
    }

    public void Dispose()
    {
        _disposed = true;
        _hashes = [];
    }
}
