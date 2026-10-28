using System.Buffers;
using Quire.Package.Internal;
using Quire.Package.Internal.Lz4;

namespace Quire.Package;
/// <summary>A seekable asset stream with reusable buffers and positional reads.</summary>
/// <remarks>
/// Use one reader per concurrent operation. Positional reads leave <see cref = "Position"/> unchanged.
/// A reader keeps its packages alive after the owning <see cref = "PackageSet"/> is disposed.
/// Partial reads authenticate encrypted blocks but do not verify the whole-file checksum.
/// </remarks>
public sealed class AssetReader : Stream
{
    private readonly PackageReader _package;
    private readonly EntryRecord _entry;
    private PackageSet? _owner;
    private BlockCipher? _cipher;
    private byte[]? _encoded, _decoded, _plain, _descriptor;
    private long _position, _cachedBlock = -1;
    private int _busy;
    private bool _disposed;
    internal AssetReader(PackageReader package, EntryRecord entry, PackageSet? owner, AssetInfo info = default)
    {
        _package = package;
        _entry = entry;
        _owner = owner;
        Info = info;
    }

    /// <summary>The resolved file information captured when this reader was opened.</summary>
    public AssetInfo Info
    {
        get;
    }
    public override bool CanRead => !_disposed;
    public override bool CanSeek => !_disposed;
    public override bool CanWrite => false;

    public override long Length
    {
        get
        {
            Check();
            return _entry.Length;
        }
    }

    public override long Position
    {
        get
        {
            Check();
            return _position;
        }

        set => Seek(value, SeekOrigin.Begin);
    }

    private void Check() => ObjectDisposedException.ThrowIf(_disposed, this);
    private void Enter()
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
        {
            throw new InvalidOperationException("Use separate readers for concurrent operations.");
        }

        if (_disposed)
        {
            Volatile.Write(ref _busy, 0);
            Check();
        }
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
    public override int Read(Span<byte> buffer)
    {
        Enter();
        try
        {
            return ReadCore(ref _position, buffer);
        }
        finally
        {
            Volatile.Write(ref _busy, 0);
        }
    }

    public override int ReadByte()
    {
        Span<byte> single = stackalloc byte[1];
        return Read(single) == 0 ? -1 : single[0];
    }

    /// <summary>Reads at an absolute file offset without changing the stream position.</summary>
    /// <returns>The destination length, or the remaining file length when it is smaller.</returns>
    public int ReadAt(long offset, Span<byte> destination)
    {
        Enter();
        try
        {
            ArgumentOutOfRangeException.ThrowIfNegative(offset);
            return ReadCore(ref offset, destination);
        }
        finally
        {
            Volatile.Write(ref _busy, 0);
        }
    }

    private int ReadCore(ref long position, Span<byte> buffer)
    {
        int count = (int)Math.Min(buffer.Length, Math.Max(0, _entry.Length - position));
        var remaining = buffer[..count];
        if (!_entry.Blocked)
        {
            if (count > 0)
            {
                _package.File.Read(remaining, checked(_entry.DataOffset + position));
            }

            position += count;
            return count;
        }

        while (!remaining.IsEmpty)
        {
            long block = position / _package.Header.BlockSize;
            int within = (int)(position % _package.Header.BlockSize);
            int rawLength = BlockLength(block);
            int take = Math.Min(remaining.Length, rawLength - within);
            if (_cachedBlock == block)
            {
                _decoded.AsSpan(within, take).CopyTo(remaining);
            }
            else if (within == 0 && take == rawLength)
            {
                Decode(block, remaining[..take]);
            }
            else
            {
                _cachedBlock = -1;
                Decode(block, GetBuffer(ref _decoded, rawLength, _package.Header.Encrypted).AsSpan(0, rawLength));
                _cachedBlock = block;
                _decoded.AsSpan(within, take).CopyTo(remaining);
            }

            remaining = remaining[take..];
            position += take;
        }

        return count;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token) => ReadAsync(buffer.AsMemory(offset, count), token).AsTask();
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        Enter();
        try
        {
            return await ReadCoreAsync(_position, buffer, true, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref _busy, 0);
        }
    }

    /// <summary>Reads at an absolute file offset without changing the stream position.</summary>
    public async ValueTask<int> ReadAtAsync(long offset, Memory<byte> destination, CancellationToken cancellationToken = default)
    {
        Enter();
        try
        {
            ArgumentOutOfRangeException.ThrowIfNegative(offset);
            return await ReadCoreAsync(offset, destination, false, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref _busy, 0);
        }
    }

    private async ValueTask<int> ReadCoreAsync(long position, Memory<byte> buffer, bool advancePosition, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        int count = (int)Math.Min(buffer.Length, Math.Max(0, _entry.Length - position));
        var remaining = buffer[..count];
        if (!_entry.Blocked)
        {
            if (count > 0)
            {
                await _package.File.ReadAsync(remaining, checked(_entry.DataOffset + position), token).ConfigureAwait(false);
            }

            if (advancePosition)
            {
                _position = position + count;
            }

            return count;
        }

        while (!remaining.IsEmpty)
        {
            token.ThrowIfCancellationRequested();
            long block = position / _package.Header.BlockSize;
            int within = (int)(position % _package.Header.BlockSize);
            int rawLength = BlockLength(block);
            int take = Math.Min(remaining.Length, rawLength - within);
            if (_cachedBlock == block)
            {
                _decoded.AsMemory(within, take).CopyTo(remaining);
            }
            else if (within == 0 && take == rawLength)
            {
                await DecodeAsync(block, remaining[..take], token).ConfigureAwait(false);
            }
            else
            {
                _cachedBlock = -1;
                var decoded = GetBuffer(ref _decoded, rawLength, _package.Header.Encrypted).AsMemory(0, rawLength);
                await DecodeAsync(block, decoded, token).ConfigureAwait(false);
                _cachedBlock = block;
                _decoded.AsMemory(within, take).CopyTo(remaining);
            }

            remaining = remaining[take..];
            position += take;
            if (advancePosition)
            {
                _position = position;
            }
        }

        return count;
    }

    private int BlockLength(long block) => (int)Math.Min(_package.Header.BlockSize, _entry.Length - block * _package.Header.BlockSize);
    private void Decode(long block, Span<byte> output)
    {
        Span<byte> descriptor = stackalloc byte[PackageHeader.BlockEntrySize];
        long sequence = checked(_entry.FirstBlock + block);
        _package.Blocks.Read(descriptor, checked(sequence * PackageHeader.BlockEntrySize));
        var record = BlockRecord.Decode(descriptor, sequence, output.Length, _package.Header);
        if (!_package.Header.Encrypted && record.Codec == 0)
        {
            _package.File.Read(output, record.Offset);
            return;
        }

        var encoded = GetBuffer(ref _encoded, record.StoredLength, false).AsSpan(0, record.StoredLength);
        _package.File.Read(encoded, record.Offset);
        DecodePayload(record, encoded, output);
    }

    private async ValueTask DecodeAsync(long block, Memory<byte> output, CancellationToken token)
    {
        var descriptor = GetBuffer(ref _descriptor, PackageHeader.BlockEntrySize, false).AsMemory(0, PackageHeader.BlockEntrySize);
        long sequence = checked(_entry.FirstBlock + block);
        await _package.Blocks.ReadAsync(descriptor, checked(sequence * PackageHeader.BlockEntrySize), token).ConfigureAwait(false);
        var record = BlockRecord.Decode(descriptor.Span, sequence, output.Length, _package.Header);
        if (!_package.Header.Encrypted && record.Codec == 0)
        {
            await _package.File.ReadAsync(output, record.Offset, token).ConfigureAwait(false);
            return;
        }

        var encoded = GetBuffer(ref _encoded, record.StoredLength, false).AsMemory(0, record.StoredLength);
        await _package.File.ReadAsync(encoded, record.Offset, token).ConfigureAwait(false);
        DecodePayload(record, encoded.Span, output.Span);
    }

    private void DecodePayload(BlockRecord record, ReadOnlySpan<byte> payload, Span<byte> output)
    {
        if (_package.Header.Encrypted)
        {
            if (_cipher is null || (_cipher.Group >= 0 && _cipher.Group != record.Sequence / 1024))
            {
                if (_cipher is not null)
                {
                    var previous = _cipher;
                    _cipher = null;
                    _package.Ciphers.Return(previous);
                }

                _cipher = _package.Ciphers.Rent(record.Sequence);
            }

            if (record.Codec == 0)
            {
                _cipher.Decrypt(record.Sequence, payload, output, output.Length, record.Codec);
                return;
            }

            var plain = GetBuffer(ref _plain, payload.Length - 16, true).AsSpan(0, payload.Length - 16);
            _cipher.Decrypt(record.Sequence, payload, plain, output.Length, record.Codec);
            payload = plain;
        }

        if (LZ4Codec.Decode(payload, output) != output.Length)
        {
            throw new InvalidDataException("LZ4 output length does not match the block descriptor.");
        }
    }

    private static byte[] GetBuffer(ref byte[]? buffer, int length, bool clear)
    {
        if (buffer is null)
        {
            buffer = ArrayPool<byte>.Shared.Rent(length);
        }
        else if (buffer.Length < length)
        {
            byte[] replacement = ArrayPool<byte>.Shared.Rent(length);
            ArrayPool<byte>.Shared.Return(buffer, clear);
            buffer = replacement;
        }

        return buffer;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        Enter();
        try
        {
            long position = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => checked(_position + offset),
                SeekOrigin.End => checked(_entry.Length + offset),
                _ => throw new ArgumentOutOfRangeException(nameof(origin))
            };
            if (position < 0)
            {
                throw new IOException("Cannot seek before the asset.");
            }

            return _position = position;
        }
        finally
        {
            Volatile.Write(ref _busy, 0);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            Enter();
            try
            {
                _disposed = true;
                if (_encoded is not null)
                {
                    ArrayPool<byte>.Shared.Return(_encoded);
                }

                if (_decoded is not null)
                {
                    ArrayPool<byte>.Shared.Return(_decoded, _package.Header.Encrypted);
                }

                if (_plain is not null)
                {
                    ArrayPool<byte>.Shared.Return(_plain, true);
                }

                if (_descriptor is not null)
                {
                    ArrayPool<byte>.Shared.Return(_descriptor);
                }

                if (_cipher is not null)
                {
                    _package.Ciphers.Return(_cipher);
                }

                _encoded = _decoded = _plain = _descriptor = null;
                _cipher = null;
                _owner?.ReleaseReference();
                _owner = null;
            }
            finally
            {
                Volatile.Write(ref _busy, 0);
            }
        }

        base.Dispose(disposing);
    }

    public override void Flush() => Check();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
