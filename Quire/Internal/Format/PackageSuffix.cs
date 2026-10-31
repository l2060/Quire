using System;

namespace Quire.Internal;

internal static class PackageSuffix
{
    internal static string Normalize(string suffix)
    {
        ArgumentNullException.ThrowIfNull(suffix);
        int start = suffix.StartsWith('.') ? 1 : 0;
        bool separator = true;
        for (int i = start; i < suffix.Length; i++)
        {
            char c = suffix[i];
            if (c == '.')
            {
                if (separator)
                    throw new ArgumentException("Package suffix must contain nonempty segments separated by dots.", nameof(suffix));
                separator = true;
            }
            else
            {
                if (!char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_'))
                    throw new ArgumentException("Package suffix must use ASCII letters, numbers, '-', '_' or dots.", nameof(suffix));
                separator = false;
            }
        }
        if (separator)
            throw new ArgumentException("Package suffix must not be empty or end with a dot.", nameof(suffix));
        return start == 0 ? suffix : suffix[1..];
    }
}
