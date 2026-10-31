using System;
using System.Buffers.Binary;

namespace Quire.Internal;

internal readonly struct ContentDigest(ReadOnlySpan<byte> bytes) : IEquatable<ContentDigest>
{
    private readonly ulong _a = BinaryPrimitives.ReadUInt64LittleEndian(bytes);
    private readonly ulong _b = BinaryPrimitives.ReadUInt64LittleEndian(bytes[8..]);
    private readonly ulong _c = BinaryPrimitives.ReadUInt64LittleEndian(bytes[16..]);
    private readonly ulong _d = BinaryPrimitives.ReadUInt64LittleEndian(bytes[24..]);
    internal bool IsZero => (_a | _b | _c | _d) == 0;
    internal void WriteTo(Span<byte> bytes)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, _a);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[8..], _b);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[16..], _c);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[24..], _d);
    }
    internal bool Matches(ReadOnlySpan<byte> bytes) => bytes.Length == 32 &&
        ((_a ^ BinaryPrimitives.ReadUInt64LittleEndian(bytes)) | (_b ^ BinaryPrimitives.ReadUInt64LittleEndian(bytes[8..])) |
         (_c ^ BinaryPrimitives.ReadUInt64LittleEndian(bytes[16..])) | (_d ^ BinaryPrimitives.ReadUInt64LittleEndian(bytes[24..]))) == 0;
    public bool Equals(ContentDigest other) => _a == other._a && _b == other._b && _c == other._c && _d == other._d;
    public override bool Equals(object? obj) => obj is ContentDigest other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(_a, _b, _c, _d);
    public static bool operator ==(ContentDigest left, ContentDigest right) => left.Equals(right);
    public static bool operator !=(ContentDigest left, ContentDigest right) => !left.Equals(right);
    public static implicit operator ContentDigest(byte[] bytes) => new(bytes);
}
