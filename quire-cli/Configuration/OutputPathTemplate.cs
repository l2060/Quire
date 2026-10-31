using System.Text;
using System.Text.RegularExpressions;

namespace Quire.Cli.Configuration;

// A template is resolved component by component, so inserted values can never introduce
// a separator or a parent-directory traversal. The original project path stays literal.
internal sealed class OutputPathTemplate
{
    private readonly record struct Component(string Value, bool Variable);
    private readonly string _volumeRoot;
    private readonly Component[] _components;
    private readonly bool _excludeRoot, _unanchored;
    private readonly Regex? _firstVariable;
    private static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    internal string Template { get; }
    internal string Root { get; }

    private OutputPathTemplate(string volumeRoot, Component[] components, string projectRoot)
    {
        _volumeRoot = volumeRoot;
        _components = components;
        Template = Join(volumeRoot, components.Select(component => component.Value));
        int variable = Array.FindIndex(components, component => component.Variable);
        Root = variable < 0 ? Template : Join(volumeRoot, components.Take(variable).Select(component => component.Value));
        if (variable < 0)
        {
            _excludeRoot = true;
            ValidateResolved(Template, projectRoot);
            return;
        }

        if (IsWithin(Root, Path.Combine(projectRoot, ".quire")))
            throw new ArgumentException("Output cannot be inside .quire.");
        var pattern = ComponentPattern(components[variable].Value);
        // dist/{tag} reserves the whole fixed dist tree. A partial component, or a fixed
        // parent containing the project, needs a narrower match such as dist-* or 2026.
        _excludeRoot = !pattern.HasLiteral && !IsWithin(projectRoot, Root);
        _unanchored = !_excludeRoot && pattern.OnlyTagVariables;
        if (!_excludeRoot)
            _firstVariable = new Regex(pattern.Pattern, RegexOptions.CultureInvariant | RegexOptions.NonBacktracking |
                (OperatingSystem.IsWindows() ? RegexOptions.IgnoreCase : RegexOptions.None));
    }

    internal static OutputPathTemplate Create(string template, string projectRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(template);
        if (template.Length > 32768) throw new ArgumentException("The output path template is too long.", nameof(template));
        projectRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectRoot));
        string suppliedRoot = Path.GetPathRoot(template) ?? string.Empty;
        if (suppliedRoot.AsSpan().IndexOfAny('{', '}') >= 0)
            throw new ArgumentException("Variables are not allowed in an output drive or network share name.", nameof(template));
        string[] supplied = Split(template.AsSpan(suppliedRoot.Length).ToString());
        int firstVariable = -1;
        for (int i = 0; i < supplied.Length; i++)
        {
            string component = supplied[i];
            if (component is "." or "..") continue;
            PackageNameTemplate.Validate(component);
            if (firstVariable < 0 && component.Contains('{')) firstVariable = i;
        }

        // Resolve the literal prefix first; braces in the surrounding project directory
        // are ordinary filename characters and must never become template variables.
        string prefix = firstVariable < 0 ? template : Path.Combine(suppliedRoot,
            string.Join(Path.DirectorySeparatorChar, supplied.AsSpan(0, firstVariable).ToArray()));
        string absolutePrefix = Path.GetFullPath(prefix.Length == 0 ? "." : prefix, projectRoot);
        string volumeRoot = Path.GetPathRoot(absolutePrefix)!;
        var components = new List<Component>();
        foreach (string component in Split(absolutePrefix.AsSpan(volumeRoot.Length).ToString()))
            components.Add(new(component, false));
        if (firstVariable >= 0)
        {
            for (int i = firstVariable; i < supplied.Length; i++)
            {
                string component = supplied[i];
                if (component == ".") continue;
                if (component == "..")
                {
                    if (components.Count != 0) components.RemoveAt(components.Count - 1);
                }
                else components.Add(new(component, component.Contains('{')));
            }
        }
        return new(volumeRoot, components.ToArray(), projectRoot);
    }

    internal string Resolve(string tag, string tagId, DateTimeOffset buildTimeUtc, string projectRoot)
    {
        var components = new string[_components.Length];
        for (int i = 0; i < components.Length; i++)
            components[i] = _components[i].Variable
                ? PackageNameTemplate.Expand(_components[i].Value, tag, tagId, buildTimeUtc)
                : _components[i].Value;
        string result = Join(_volumeRoot, components);
        ValidateResolved(result, projectRoot);
        return result;
    }

    internal bool IsOutputPath(string fullPath, bool isDirectory = true)
    {
        if (!IsWithin(fullPath, Root)) return false;
        if (_excludeRoot) return true;
        if (fullPath.Length <= Root.Length) return false;
        int start = Root.Length + (Path.EndsInDirectorySeparator(Root) ? 0 : 1);
        var relative = fullPath.AsSpan(start);
        int slash = relative.IndexOfAny(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        // A sibling file named dist-logo.png is not a directory in the dist-{tag} tree.
        if (slash < 0 && !isDirectory) return false;
        return _firstVariable!.IsMatch(slash < 0 ? relative : relative[..slash]);
    }

    internal void ValidateSourceDirectory(string directory)
    {
        if (_unanchored && (IsWithin(directory, Root) || IsWithin(Root, directory)))
            throw new ArgumentException("Output templates beginning with only {tag} or {tagid} cannot overlap source directories. Use a fixed output tree such as dist/{tag}, or a prefix such as dist-{tag}.");
        if (IsOutputPath(directory))
            throw new ArgumentException("Source directory cannot be inside the output template's reserved directory tree.");
    }

    private static void ValidateResolved(string directory, string projectRoot)
    {
        if (IsWithin(directory, Path.Combine(projectRoot, ".quire")) || IsWithin(projectRoot, directory))
            throw new ArgumentException("Output must not contain the project root or be inside .quire.");
    }

    private static (string Pattern, bool HasLiteral, bool OnlyTagVariables) ComponentPattern(string component)
    {
        var pattern = new StringBuilder("\\A");
        bool literal = false, onlyTags = true;
        int position = 0;
        while (position < component.Length)
        {
            int open = component.IndexOf('{', position);
            if (open < 0) open = component.Length;
            if (open > position)
            {
                pattern.Append(Regex.Escape(component[position..open]));
                literal = true;
            }
            if (open == component.Length) break;
            int close = component.IndexOf('}', open + 1);
            string variable = component[(open + 1)..close];
            onlyTags &= variable is "tag" or "tagid";
            pattern.Append(variable switch
            {
                "tag" => ".+",
                "tagid" => "[0-9a-fA-F]{8}",
                "yyyy" => "[0-9]{4}",
                "MM" or "dd" or "HH" or "mm" or "ss" => "[0-9]{2}",
                "fff" => "[0-9]{3}",
                "date" => "[0-9]{8}",
                "time" => "[0-9]{6}",
                "timestamp" => "-?[0-9]+",
                _ => throw new ArgumentException("Unknown output path variable.")
            });
            position = close + 1;
        }
        pattern.Append("\\z");
        return (pattern.ToString(), literal, !literal && onlyTags);
    }

    private static bool IsWithin(string fullPath, string root)
    {
        if (!fullPath.AsSpan().StartsWith(root, PathComparison)) return false;
        return fullPath.Length == root.Length || Path.EndsInDirectorySeparator(root) ||
            fullPath[root.Length] == Path.DirectorySeparatorChar || fullPath[root.Length] == Path.AltDirectorySeparatorChar;
    }

    private static string Join(string volumeRoot, IEnumerable<string> components) =>
        Path.TrimEndingDirectorySeparator(Path.Combine(volumeRoot, string.Join(Path.DirectorySeparatorChar, components)));

    private static string[] Split(string path) => path.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
}
