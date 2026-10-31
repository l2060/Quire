using System.Globalization;
using System.Text;

namespace Quire.Cli.Configuration;

internal static class PackageNameTemplate
{
    // Validation substitutes the shortest possible value. Real values are validated again
    // when the build's Tag and timestamp are known, before any source scanning or writing.
    internal static void Validate(string template) => _ = ExpandCore(template, null, null, default, validateOnly: true);

    internal static string Expand(string template, string tag, string tagId, DateTimeOffset buildTimeUtc)
    {
        ArgumentException.ThrowIfNullOrEmpty(tag);
        ArgumentException.ThrowIfNullOrEmpty(tagId);
        return ExpandCore(template, tag, tagId, buildTimeUtc.ToLocalTime(), validateOnly: false);
    }

    private static string ExpandCore(string template, string? tag, string? tagId, DateTimeOffset time, bool validateOnly)
    {
        ArgumentNullException.ThrowIfNull(template);
        if (template.Length > 4096) throw new ArgumentException("Package name templates cannot exceed 4096 characters.", nameof(template));
        if (template.AsSpan().IndexOfAny('{', '}') < 0)
        {
            PackageName.Validate(template);
            return template;
        }

        var output = new StringBuilder(Math.Min(template.Length, PackageName.MaxUtf8Bytes));
        int position = 0;
        while (position < template.Length)
        {
            int delimiter = template.AsSpan(position).IndexOfAny('{', '}');
            if (delimiter < 0)
            {
                output.Append(template.AsSpan(position));
                break;
            }
            delimiter += position;
            output.Append(template.AsSpan(position, delimiter - position));
            if (template[delimiter] == '}') throw new ArgumentException("Unmatched '}' in package name template.", nameof(template));
            int end = template.IndexOf('}', delimiter + 1);
            if (end < 0) throw new ArgumentException("Unclosed variable in package name template.", nameof(template));
            var variable = template.AsSpan(delimiter + 1, end - delimiter - 1);
            string value = variable switch
            {
                "tag" => validateOnly ? "0" : tag!,
                "tagid" => validateOnly ? "0" : tagId!,
                "yyyy" or "MM" or "dd" or "HH" or "mm" or "ss" or "fff" =>
                    validateOnly ? "0" : time.ToString(variable.ToString(), CultureInfo.InvariantCulture),
                "date" => validateOnly ? "0" : time.ToString("yyyyMMdd", CultureInfo.InvariantCulture),
                "time" => validateOnly ? "0" : time.ToString("HHmmss", CultureInfo.InvariantCulture),
                "timestamp" => validateOnly ? "0" : time.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
                _ => throw new ArgumentException($"Unknown package name variable '{{{variable.ToString()}}}'. Supported variables: tag, tagid, yyyy, MM, dd, HH, mm, ss, fff, date, time, timestamp.", nameof(template))
            };
            output.Append(value);
            if (output.Length > PackageName.MaxUtf8Bytes)
                throw new ArgumentException("The expanded package name exceeds 255 UTF-8 bytes.", nameof(template));
            position = end + 1;
        }

        string result = output.ToString();
        PackageName.Validate(result);
        return result;
    }
}
