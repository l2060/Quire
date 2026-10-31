using Quire.Internal;

namespace Quire.Cli.Configuration;

internal static class ProjectName
{
    internal static void Validate(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (name.Length is < 1 or > 128 || name[0] is '-' or '.' ||
            char.IsWhiteSpace(name[0]) || char.IsWhiteSpace(name[^1]) || name[^1] == '.')
            throw new ArgumentException("Project names must be 1-128 characters without a leading '-' or '.', surrounding whitespace or a trailing dot.", nameof(name));
        foreach (char character in name)
            if (char.IsControl(character) || character is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|' or '{' or '}')
                throw new ArgumentException("Project names must be single directory names without path separators, braces or invalid file name characters.", nameof(name));
        if (AssetPath.Utf8.GetByteCount(name) > 240)
            throw new ArgumentException("Project names cannot exceed 240 UTF-8 bytes.", nameof(name));

        var stem = name.AsSpan();
        int dot = stem.IndexOf('.');
        if (dot >= 0) stem = stem[..dot];
        bool device = stem.Equals("CON", StringComparison.OrdinalIgnoreCase) || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
            (stem.Length == 4 && (stem[3] is >= '1' and <= '9' or '\u00b9' or '\u00b2' or '\u00b3') &&
                (stem[..3].Equals("COM", StringComparison.OrdinalIgnoreCase) || stem[..3].Equals("LPT", StringComparison.OrdinalIgnoreCase)));
        if (device)
            throw new ArgumentException("Project names cannot use reserved device names.", nameof(name));
    }
}
