using System.Globalization;
using Quire.Cli.Build;
using Quire.Internal;
using static Quire.Cli.Configuration.ProjectYaml;

namespace Quire.Cli.Configuration;

internal sealed record SourceConfig(string Directory, string Prefix, string[] Include, string[] Exclude);
internal sealed record PackageConfig(string Name, CompressionAlgorithm Compression, SourceConfig[] Sources,
    CompressionPolicy CompressionPolicy = CompressionPolicy.Adaptive)
{
    internal string? NameTemplate { get; init; }
    internal string OrderName => NameTemplate ?? Name;
}
internal sealed record ProjectConfig(string FilePath, string Root, string Output, string? SecretKeyName, PackageConfig[] Packages)
{
    internal bool Encrypted => SecretKeyName is not null;
    internal BuildOptions BuildOptions { get; init; } = new();
    internal string Suffix { get; init; } = "asset";
    internal required string ProjectName { get; init; }
    private OutputPathTemplate? OutputPaths { get; init; }
    internal string OutputTemplate => OutputPaths?.Template ?? Output;
    internal string OutputRoot => OutputPaths?.Root ?? Output;
    internal string StateRootDirectory => Path.Combine(Root, ".quire");
    internal string StateDirectory => Path.Combine(StateRootDirectory, ProjectName);

    // History commands need the project identity, but not available sources or publishing keys.
    internal static string LoadStateDirectory(string path)
    {
        path = Path.GetFullPath(path);
        string state = Path.Combine(Path.GetDirectoryName(path)!, ".quire");
        return Path.Combine(state, ReadProjectName(ReadConfig(path)));
    }

    internal static ProjectConfig Load(string path, string? sourceRoot = null)
    {
        path = Path.GetFullPath(path); string root = Path.GetDirectoryName(path)!;
        var map = ReadConfig(path);
        string projectName = ReadProjectName(map);
        string sourceBase = root;
        if (sourceRoot is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(sourceRoot);
            sourceBase = Path.GetFullPath(sourceRoot);
            if (!Directory.Exists(sourceBase)) throw new DirectoryNotFoundException($"Source root directory does not exist: {sourceBase}");
        }
        OutputPathTemplate outputPaths;
        try { outputPaths = OutputPathTemplate.Create(Text(map, "output", "./dist"), root); }
        catch (ArgumentException error) { throw Error(map.Line, error.Message); }
        string output = outputPaths.Root;
        string suffix = PackageSuffix.Normalize(Text(map, "suffix", "asset"));
        string state = Path.Combine(root, ".quire");
        var compression = Algorithm(map, CompressionAlgorithm.Lz4);
        var compressionPolicy = Policy(map, CompressionPolicy.Adaptive);
        string? secretKeyName = null;
        if (map.Values.ContainsKey("secret-key"))
        {
            string name = Text(map, "secret-key");
            if (!name.Equals("none", StringComparison.OrdinalIgnoreCase))
            {
                try { SecretKeyStore.ValidateName(name); }
                catch (ArgumentException error) { throw Error(map.Values["secret-key"].Line, error.Message); }
                secretKeyName = name;
            }
        }
        var buildOptions = new BuildOptions();
        if (map.Values.TryGetValue("build", out var buildNode))
        {
            var build = AsMap(buildNode); Check(build, "jobs", "memory-mib", "block-size");
            buildOptions = buildOptions with
            {
                Jobs = Integer(build, "jobs", buildOptions.Jobs),
                MemoryMiB = Integer(build, "memory-mib", buildOptions.MemoryMiB),
                BlockSize = Integer(build, "block-size", buildOptions.BlockSize)
            };
            if (buildOptions.Jobs is < 0 or > 256 || buildOptions.MemoryMiB is < 8 or > 65536 || buildOptions.BlockSize is < 65536 or > 1048576 ||
                (buildOptions.BlockSize & (buildOptions.BlockSize - 1)) != 0)
                throw Error(build.Line, "build.jobs must be 0..256; build.memory-mib must be 8..65536; build.block-size must be a power of two from 65536 to 1048576.");
        }
        if (!map.Values.TryGetValue("packages", out var packageNode) || packageNode is not Sequence sequence || sequence.Values.Count == 0)
            throw Error(map.Line, "At least one package is required.");
        var packages = new List<PackageConfig>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        var literalNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in sequence.Values)
        {
            var p = AsMap(node); Check(p, "name", "compression", "compression-policy", "sources");
            string name = Text(p, "name");
            try { PackageNameTemplate.Validate(name); }
            catch (ArgumentException error) { throw Error(p.Line, error.Message); }
            if (!names.Add(name) || (!name.Contains('{') && !literalNames.Add(name)))
                throw Error(p.Line, "Package name templates must be unique; literal names are compared ignoring case.");
            if (!p.Values.TryGetValue("sources", out var s) || s is not Sequence sources || sources.Values.Count == 0) throw Error(p.Line, "Package sources are required.");
            var inputs = new List<SourceConfig>();
            foreach (var input in sources.Values)
            {
                var m = AsMap(input); Check(m, "directory", "prefix", "include", "exclude");
                string directory = Path.GetFullPath(Text(m, "directory"), sourceBase), prefix = Text(m, "prefix", "");
                AssetPath.Validate(prefix, true);
                if (!System.IO.Directory.Exists(directory)) throw new DirectoryNotFoundException(directory);
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) throw Error(m.Line, "Symbolic-link source roots are not supported.");
                if (Within(directory, state)) throw Error(m.Line, "Source directory cannot be inside .quire.");
                try { outputPaths.ValidateSourceDirectory(directory); }
                catch (ArgumentException error) { throw Error(m.Line, error.Message); }
                inputs.Add(new(directory, prefix, Strings(m, "include"), Strings(m, "exclude")));
            }
            packages.Add(new(name, Algorithm(p, compression), inputs.ToArray(), Policy(p, compressionPolicy)));
        }
        return new(path, root, output, secretKeyName, packages.ToArray())
        {
            ProjectName = projectName,
            BuildOptions = buildOptions,
            Suffix = suffix,
            OutputPaths = outputPaths
        };
    }

    internal ProjectConfig ResolvePackageNames(string tag, string tagId, DateTimeOffset buildTimeUtc)
    {
        var resolved = new PackageConfig[Packages.Length];
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < resolved.Length; i++)
        {
            var package = Packages[i];
            string template = package.OrderName;
            string name = PackageNameTemplate.Expand(template, tag, tagId, buildTimeUtc);
            PackageName.Validate(name + "." + Suffix);
            if (!names.Add(name)) throw new InvalidDataException($"Package name template '{template}' expands to duplicate package name '{name}'.");
            resolved[i] = package with { Name = name, NameTemplate = template };
        }
        return this with { Packages = resolved };
    }

    internal ProjectConfig ResolveBuildOutput(string tag, string tagId, DateTimeOffset buildTimeUtc)
    {
        var paths = OutputPaths ?? OutputPathTemplate.Create(Output, Root);
        string directory = paths.Resolve(tag, tagId, buildTimeUtc, Root);
        foreach (var package in Packages)
            foreach (var source in package.Sources)
            {
                paths.ValidateSourceDirectory(source.Directory);
                if (Within(source.Directory, directory))
                    throw new ArgumentException("Source directory cannot be inside the resolved output directory.");
            }
        return ResolvePackageNames(tag, tagId, buildTimeUtc) with { Output = directory, OutputPaths = paths };
    }

    internal bool IsOutputPath(string fullPath, bool isDirectory = true) =>
        OutputPaths?.IsOutputPath(fullPath, isDirectory) ?? Within(fullPath, Output);
    internal static bool Within(string path, string root)
    {
        string relative = Path.GetRelativePath(root, path);
        return relative == "." || (!Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal));
    }
    private static Map ReadConfig(string path)
    {
        var map = ProjectYaml.Parse(File.ReadAllText(path, AssetPath.Utf8));
        Check(map, "version", "project", "output", "suffix", "compression", "compression-policy", "secret-key", "build", "packages");
        if (Text(map, "version", "1") != "1") throw Error(map.Line, "Unsupported project version.");
        return map;
    }
    private static string ReadProjectName(Map map)
    {
        string name = Text(map, "project");
        try { global::Quire.Cli.Configuration.ProjectName.Validate(name); }
        catch (ArgumentException error) { throw Error(map.Values["project"].Line, error.Message); }
        return name;
    }
    private static Map AsMap(Node node) => node as Map ?? throw Error(node.Line, "Expected mapping.");
    private static void Check(Map map, params string[] known)
    { foreach (var key in map.Values.Keys) if (!known.Contains(key, StringComparer.Ordinal)) throw Error(map.Line, $"Unknown setting '{key}'."); }
    private static string Text(Map map, string key, string? fallback = null) => map.Values.TryGetValue(key, out var n)
        ? n is Scalar s ? s.Value : throw Error(n.Line, $"'{key}' must be a scalar.") : fallback ?? throw Error(map.Line, $"Missing '{key}'.");
    private static CompressionAlgorithm Algorithm(Map map, CompressionAlgorithm fallback) => !map.Values.ContainsKey("compression") ? fallback :
        Text(map, "compression") switch
        {
            "none" => CompressionAlgorithm.None,
            "lz4" => CompressionAlgorithm.Lz4,
            "zlib" => CompressionAlgorithm.Zlib,
            _ => throw Error(map.Line, "'compression' must be none, lz4 or zlib.")
        };
    private static CompressionPolicy Policy(Map map, CompressionPolicy fallback) => !map.Values.ContainsKey("compression-policy") ? fallback :
        Text(map, "compression-policy") switch
        {
            "adaptive" => CompressionPolicy.Adaptive,
            "always" => CompressionPolicy.Always,
            _ => throw Error(map.Line, "'compression-policy' must be always or adaptive.")
        };
    private static int Integer(Map map, string key, int fallback) =>
        int.TryParse(Text(map, key, fallback.ToString(CultureInfo.InvariantCulture)), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
            ? value : throw Error(map.Line, $"'{key}' must be an integer.");
    private static string[] Strings(Map map, string key) => !map.Values.TryGetValue(key, out var n) ? [] :
        n is Sequence s ? s.Values.Select(v => v is Scalar text ? text.Value : throw Error(v.Line, "Expected string.")).ToArray() : throw Error(n.Line, $"'{key}' must be a block sequence.");
}
