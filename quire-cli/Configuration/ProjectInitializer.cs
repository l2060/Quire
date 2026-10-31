using System.Text.Json;
using Quire.Cli.Serialization;
using Quire.Internal;

namespace Quire.Cli.Configuration;

internal static class ProjectInitializer
{
    internal static string Create(string projectName, string directory)
    {
        ProjectName.Validate(projectName);
        string root = Path.GetFullPath(directory), path = Path.Combine(root, $"quire.{projectName}.yml");
        if (File.Exists(path) || Directory.Exists(path)) throw new IOException($"Project configuration already exists: {path}");
        using var template = typeof(ProjectInitializer).Assembly.GetManifestResourceStream("Quire.Cli.Templates.quire.project.yml")
            ?? throw new InvalidOperationException("The project template is missing.");
        using var reader = new StreamReader(template, AssetPath.Utf8);
        // The template uses this sample name only inside double-quoted YAML scalars.
        // JSON escaping is also valid for the supported YAML quoted-string syntax.
        string escapedName = JsonSerializer.Serialize(projectName, CliJsonContext.Default.String)[1..^1];
        string text = reader.ReadToEnd().Replace("my-project", escapedName, StringComparison.Ordinal);
        var project = ProjectYaml.Parse(text);
        var packages = (ProjectYaml.Sequence)project.Values["packages"];
        var sources = packages.Values.Cast<ProjectYaml.Map>()
            .SelectMany(p => ((ProjectYaml.Sequence)p.Values["sources"]).Values.Cast<ProjectYaml.Map>())
            .Select(s => Path.GetFullPath(((ProjectYaml.Scalar)s.Values["directory"]).Value, root)).Distinct().ToArray();
        foreach (string source in sources)
        {
            if (!ProjectConfig.Within(source, root) || File.Exists(source))
                throw new IOException($"Cannot create template source directory: {source}");
        }
        Directory.CreateDirectory(root);
        foreach (string source in sources) Directory.CreateDirectory(source);
        string temporary = Path.Combine(root, ".quire-init-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.WriteAllText(temporary, text, AssetPath.Utf8);
            File.Move(temporary, path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return path;
    }
}
