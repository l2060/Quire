using System;
using System.Globalization;

namespace Quire.Internal;

internal static class PackageTag
{
    internal static string Format(uint value) => value.ToString("x8", CultureInfo.InvariantCulture);

    internal static bool IsValid(string? value)
    {
        if (value is not { Length: 8 })
            return false;
        foreach (char character in value)
            if (!char.IsAsciiHexDigit(character))
                return false;
        return true;
    }

    internal static uint Parse(string value)
    {
        if (!IsValid(value))
            throw new ArgumentException("A Tag ID must contain exactly 8 hexadecimal characters.", nameof(value));
        return uint.Parse(value, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
    }
}
