using Quire.Internal.Lz4;
using System;
using System.IO;
using System.IO.Compression;

namespace Quire.Internal;

// Values are stored in block descriptors and authenticated with encrypted payloads.
internal enum CompressionAlgorithm : byte
{
    None = 0,
    Lz4 = 1,
    Zlib = 2
}

internal enum CompressionPolicy : byte
{
    Adaptive = 0,
    Always = 1
}

internal static class BlockCompression
{
    internal static int GetMaxCompressedLength(CompressionAlgorithm algorithm, int length) => algorithm switch
    {
        CompressionAlgorithm.None => length,
        CompressionAlgorithm.Lz4 => LZ4Codec.MaximumOutputSize(length),
        // zlib's compressBound for its default window and memory settings, including the wrapper.
        CompressionAlgorithm.Zlib => checked(length + (length >> 12) + (length >> 14) + (length >> 25) + 13),
        _ => throw new ArgumentOutOfRangeException(nameof(algorithm))
    };

    internal static int Encode(CompressionAlgorithm algorithm, ReadOnlySpan<byte> source, byte[] destination)
    {
        if (algorithm == CompressionAlgorithm.Lz4)
            return LZ4Codec.Encode(source, destination);
        if (algorithm != CompressionAlgorithm.Zlib)
            throw new ArgumentOutOfRangeException(nameof(algorithm));

        // Wrap the worker's pooled buffer directly. No expanding stream or ToArray copy.
        using var output = new MemoryStream(destination, 0, destination.Length, writable: true);
        using (var compressor = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
            compressor.Write(source);
        return checked((int)output.Position);
    }

    internal static int Decode(CompressionAlgorithm algorithm, byte[] source, int length, Span<byte> destination)
    {
        if (algorithm == CompressionAlgorithm.Lz4)
            return LZ4Codec.Decode(source.AsSpan(0, length), destination);
        if (algorithm != CompressionAlgorithm.Zlib)
            throw new InvalidDataException("Unknown block compression algorithm.");

        using var input = new BlockReadStream(source, length);
        using var decompressor = new ZLibStream(input, CompressionMode.Decompress);
        int total = 0;
        while (total < destination.Length)
        {
            int read = decompressor.Read(destination[total..]);
            if (read == 0)
                throw new InvalidDataException("Zlib output is shorter than the block descriptor.");
            total += read;
        }

        // Complete the stream to verify its trailer, and reject output beyond the declared block.
        Span<byte> extra = stackalloc byte[1];
        if (decompressor.Read(extra) != 0)
            throw new InvalidDataException("Zlib output is longer than the block descriptor.");
        return total;
    }

    private sealed class BlockReadStream(byte[] source, int length) : MemoryStream(source, 0, length, writable: false, publiclyVisible: true)
    {
        // ZLibStream can accept truncated input when its source reports EOF. A block must
        // reach zlib's end marker before needing another byte from the bounded source.
        public override int Read(Span<byte> buffer)
        {
            int read = (int)Math.Min(buffer.Length, Length - Position);
            if (read == 0 && !buffer.IsEmpty)
                throw new InvalidDataException("Truncated zlib block.");
            GetBuffer().AsSpan((int)Position, read).CopyTo(buffer);
            Position += read;
            return read;
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
    }
}
