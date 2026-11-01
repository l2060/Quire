using System.Reflection;

namespace Quire.Cli;

internal static class CliVersion
{
    // SourceLink appends "+<commit>" to the informational version; only the version number is shown.
    internal static string Current { get; } = Read();

    private static string Read()
    {
        var assembly = typeof(CliVersion).Assembly;
        string? informational = InformationalVersion(assembly);
        if (!string.IsNullOrEmpty(informational))
        {
            int metadata = informational.IndexOf('+');
            return metadata < 0 ? informational : informational[..metadata];
        }

        // Native AOT can drop assembly attributes; the assembly version is always available.
        Version? version = assembly.GetName().Version;
        return version switch
        {
            null => "unknown",
            { Build: >= 0, Revision: <= 0 } => version.ToString(3),
            _ => version.ToString()
        };
    }

    private static string? InformationalVersion(Assembly assembly)
    {
        // Version display must not fail a command, so unreadable attribute metadata falls back.
        try { return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion; }
        catch (Exception) { return null; }
    }
}
