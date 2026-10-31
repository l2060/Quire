using Quire.Internal;

namespace Quire.Cli.Configuration;

internal static class PackageName
{
    internal const int MaxUtf8Bytes = 255;

    internal static void Validate(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (name.Length is < 1 or > MaxUtf8Bytes || name is "." or ".." ||
            char.IsWhiteSpace(name[0]) || char.IsWhiteSpace(name[^1]) || name[^1] == '.')
            throw new ArgumentException("Package names must be nonempty file name components without surrounding whitespace or a trailing dot.", nameof(name));
        foreach (char character in name)
            if (char.IsControl(character) || character is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|')
                throw new ArgumentException("Package names contain an invalid file name character.", nameof(name));
        if (AssetPath.Utf8.GetByteCount(name) > MaxUtf8Bytes)
            throw new ArgumentException("Package names cannot exceed 255 UTF-8 bytes.", nameof(name));

        var stem = name.AsSpan();
        int dot = stem.IndexOf('.');
        if (dot >= 0) stem = stem[..dot];
        bool reserved = stem.Equals("CON", StringComparison.OrdinalIgnoreCase) || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
            (stem.Length == 4 && (stem[3] is >= '1' and <= '9' or '\u00b9' or '\u00b2' or '\u00b3') &&
                (stem[..3].Equals("COM", StringComparison.OrdinalIgnoreCase) || stem[..3].Equals("LPT", StringComparison.OrdinalIgnoreCase)));
        if (reserved) throw new ArgumentException("Package names cannot use reserved device names.", nameof(name));
    }
}
