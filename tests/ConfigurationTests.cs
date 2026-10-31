using System.Buffers.Binary;
using System.Text;
using Quire;
using Quire.Cli;
using Quire.Cli.Build;
using Quire.Cli.Configuration;
using Quire.Cli.Tags;
using Quire.Internal;

internal static class ConfigurationTests
{
    internal static void Register(Action<string, Func<Task>> test)
    {
        test("package without arguments uses named keys, YAML suffix and automatic jobs", ConfiguredPackageAsync);
        test("global named keys reproduce client material without persisting passwords", KeyManagementAsync);
        test("keys show returns only the requested client key without project access or vault enumeration", KeyShowAsync);
        test("named credentials reject incompatible profiles and ignore old project credentials", KeyProfileIsolationAsync);
        test("projects share named keys and changing the name preserves the baseline", SharedKeyAsync);
        test("none and omitted secret keys disable signing and encryption; old settings are rejected", SecretKeyConfigurationAsync);
        test("diff compares encrypted baselines without passwords or packaging memory budgets", DiffWithoutBuildRequirementsAsync);
        test("automatic jobs respect CPU and memory while explicit jobs remain strict", JobsAsync);
        test("package suffix validation, defaults and changed-suffix incrementals", SuffixAsync);
        test("configuration still requires explicit packages and sources", RequiredSourcesAsync);
    }

    private static async Task ConfiguredPackageAsync()
    {
        using var fixture = new Fixture(false);
        fixture.InitializeKeys();
        WriteConfig(fixture, "suffix: .pak\n", "jobs: 0\n  memory-mib: 15\n  block-size: 1048576",
            fixture.KeyName, "./configured-output/{tag}");
        fixture.Put("keep", [1, 2, 3]);
        fixture.Put("gone", [4]);
        var config = fixture.Config();
        Check.Equal("pak", config.Suffix);
        Check.Equal(0, config.BuildOptions.Jobs);
        Check.Equal(1, config.BuildOptions.EffectiveJobs);
        Check.Equal(1048576, config.BuildOptions.BlockSize);
        string previousDirectory = Directory.GetCurrentDirectory();
        var logs = new StringBuilder();
        try
        {
            Directory.SetCurrentDirectory(fixture.Root);
            logs.Append(await CaptureAsync(["package"]));
            var baseline = Latest(config);
            Check.True(baseline.Outputs.Single().EndsWith(".pak", StringComparison.Ordinal));
            byte[] bytes = File.ReadAllBytes(Path.Combine(fixture.Root, baseline.Outputs.Single()));
            var header = PackageHeader.Decode(bytes, bytes.Length);
            Check.True(header.Encrypted);
            Check.Equal(1048576, header.BlockSize);
            using (var ignored = QuireBundle.Open(config.Output))
                Check.Equal(0, ignored.Packages.Count);
            using (var bundle = await QuireBundle.OpenAsync(config.Output, new ReadOptions { Suffix = ".pak", Key = TestKeys.Client }))
            {
                Check.Equal(2, bundle.Dir("common/data").Length);
                using var data = await bundle.ReadAsync("common/data/keep");
                Check.Bytes([1, 2, 3], data.Data.Span);
            }

            File.Delete(Path.Combine(fixture.Input, "gone"));
            logs.Append(await CaptureAsync(["commit", baseline.Name]));
            logs.Append(await CaptureAsync(["package"]));
            var deletion = Latest(config);
            Check.Equal(1, deletion.DeletedChanges);
            Check.True(deletion.Outputs.Single().EndsWith(".pak", StringComparison.Ordinal));
            using (var bundle = QuireBundle.Open(config.Output, new ReadOptions { Suffix = "pak", Key = TestKeys.Client, MaxMergedIndexBytes = 0 }))
            {
                Check.True(!bundle.FileExists("common/data/gone"));
                Check.Equal(1, bundle.Dir("common/data").Length);
            }

            string inspection = await CaptureAsync(["inspect", config.Output, "-suffix", "pak", "-key", fixture.ClientKeyPath]);
            Check.True(inspection.Contains("common/data/keep", StringComparison.Ordinal));
            Check.True(!inspection.Contains("common/data/gone", StringComparison.Ordinal));
            logs.Append(inspection);
            logs.Append(await CaptureAsync(["verify", config.Output, "-suffix", ".pak", "-key", fixture.ClientKeyPath]));
            string destination = Path.Combine(fixture.Root, "restored");
            logs.Append(await CaptureAsync(["unpackage", config.Output, destination, "-suffix", "pak", "-key", fixture.ClientKeyPath]));
            Check.Bytes([1, 2, 3], File.ReadAllBytes(Path.Combine(destination, "common/data/keep")));
            Check.True(!File.Exists(Path.Combine(destination, "common/data/gone")));
        }
        finally
        {
            Directory.SetCurrentDirectory(previousDirectory);
        }
        Check.True(!logs.ToString().Contains(Convert.ToBase64String(TestKeys.Client.Export()), StringComparison.Ordinal));
    }

    private static async Task KeyManagementAsync()
    {
        using var first = new Fixture(false);
        using var second = new Fixture(false);
        const string password = "a reproducible high-entropy test password 中文 448b2fae";
        string previousDirectory = Directory.GetCurrentDirectory();
        string emptyDirectory = Path.Combine(first.Root, "no-project");
        Directory.CreateDirectory(emptyDirectory);
        try
        {
            Directory.SetCurrentDirectory(emptyDirectory);
            using var testPublisher = TestKeys.Create();
            foreach (string invalid in new[] { "", " ", "none", "NoNe", "-bad", " leading", "trailing ", "line\nbreak", new string('a', 129) })
                Check.Throws<ArgumentException>(() => SecretKeyStore.Add(invalid, testPublisher, default));
            using (var canceled = new CancellationTokenSource())
            {
                canceled.Cancel();
                await Check.ThrowsAsync<OperationCanceledException>(() => CommandLine.RunAsync(["keys", "add", first.KeyName, password], canceled.Token));
                Check.True(!File.Exists(SecretKeyStore.GetStoragePath(first.KeyName)));
            }
            await Check.ThrowsAsync<ArgumentException>(() => CommandLine.RunAsync(["keys", "add", first.KeyName, ""], default));
            string output = await CaptureAsync(["keys", "add", first.KeyName, password]);
            Check.True(!output.Contains(password, StringComparison.Ordinal));
            await CaptureAsync(["keys", "add", second.KeyName, password]);
            await CaptureAsync(["keys", "export", first.KeyName, first.ClientKeyPath]);
            await CaptureAsync(["keys", "export", second.KeyName, second.ClientKeyPath]);
            byte[] client = File.ReadAllBytes(first.ClientKeyPath);
            Check.Equal(96, client.Length);
            // Fixed v2 profile vector, independently calculated with PBKDF2, RFC 5869 and P-256.
            Check.Bytes(Convert.FromHexString("A802A6D03B1369CCB3A028C60F2EAE4B9FA27EEEFC951061FDEF397A1E6B0F8AC81B49F1C7F14D9529B3593027B56A5A9D8E6309CD8DB44807E1799404A295BFCCD66A41064F3EF558DBFF488D29A221162A6289800B74E98F99A8C8BE8BDE03"), client);
            Check.Bytes(client, File.ReadAllBytes(second.ClientKeyPath));
            using var signing = SecretKeyStore.Load(first.KeyName.ToUpperInvariant());
            using var restored = SecretKeyStore.Load(second.KeyName);
            Check.Equal(SecretKeyStore.GetStoragePath(first.KeyName), SecretKeyStore.GetStoragePath(first.KeyName.ToUpperInvariant()));
            Check.Bytes(signing.Export(), restored.Export());
            Check.Bytes(Convert.FromHexString("A802A6D03B1369CCB3A028C60F2EAE4B9FA27EEEFC951061FDEF397A1E6B0F8AB01C085C79E113C1D72611E7AEBF9500CF2BD37D2178E7D6B36046E4722E76D4"), signing.Export());
            Check.Bytes(client, signing.ClientKey.Export());
            Check.True(!client.AsSpan(32).SequenceEqual(signing.Export().AsSpan(32)));

            string listing = await CaptureAsync(["keys", "list"]);
            Check.True(listing.Contains("FINGERPRINT", StringComparison.Ordinal) && listing.Contains("KEYNAME", StringComparison.Ordinal) && listing.Contains("CLIENTKEY", StringComparison.Ordinal));
            string[] row = listing.Split('\n').Single(line => line.Contains(first.KeyName, StringComparison.Ordinal)).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            Check.Equal(3, row.Length);
            Check.Equal(SecretKeyStore.Fingerprint(signing.ClientKey), row[0]);
            Check.Equal(first.KeyName, row[1]);
            Check.Equal(Convert.ToBase64String(client), row[2]);
            Check.True(!listing.Contains(password, StringComparison.Ordinal));

            string exported = Path.Combine(first.Root, "exported-client.key");
            await CaptureAsync(["keys", "export", first.KeyName, exported]);
            Check.Bytes(client, File.ReadAllBytes(exported));
            await CaptureAsync(["keys", "export", first.KeyName, exported]);
            File.WriteAllBytes(exported, [1, 2, 3]);
            await Check.ThrowsAsync<IOException>(() => CommandLine.RunAsync(["keys", "export", first.KeyName, exported], default));
            Check.Bytes([1, 2, 3], File.ReadAllBytes(exported));
            byte[] saved = File.ReadAllBytes(SecretKeyStore.GetStoragePath(first.KeyName));
            Check.Throws<IOException>(() => SecretKeyStore.Add(first.KeyName.ToUpperInvariant(), testPublisher, default));
            Check.Bytes(saved, File.ReadAllBytes(SecretKeyStore.GetStoragePath(first.KeyName)));
            await Check.ThrowsAsync<ArgumentException>(() => CommandLine.RunAsync(["keys", "list", "-f", first.ProjectPath], default));
            await Check.ThrowsAsync<ArgumentException>(() => CommandLine.RunAsync(["key", "init"], default));
            foreach (string option in new[] { "-pwd", "-pwd-env" })
                foreach (string command in new[] { "package", "inspect", "verify", "unpackage", "diff" })
                    await Check.ThrowsAsync<ArgumentException>(() => CommandLine.RunAsync([command, option, "secret"], default));
            byte[] secret = Encoding.UTF8.GetBytes(password);
            Check.True(saved.AsSpan().IndexOf(secret) < 0);
            Check.Equal(0, Directory.GetFileSystemEntries(emptyDirectory).Length);

            WriteConfig(first, "", "jobs: 1", first.KeyName);
            first.Put("content", [7, 8, 9]);
            await first.Build();
            await CaptureAsync(["keys", "remove", first.KeyName.ToUpperInvariant()]);
            Check.True(!File.Exists(SecretKeyStore.GetStoragePath(first.KeyName)));
            Check.True(File.Exists(SecretKeyStore.GetStoragePath(second.KeyName)));
            Check.Throws<FileNotFoundException>(() => { using var missing = SecretKeyStore.Load(first.KeyName); });
            await Check.ThrowsAsync<FileNotFoundException>(() => CommandLine.RunAsync(["keys", "remove", first.KeyName], default));
            using var bundle = QuireBundle.Open(first.Output, new ReadOptions { Key = SecretKeyStore.LoadClientFile(first.ClientKeyPath) });
            using var data = bundle.Read("common/data/content");
            Check.Bytes([7, 8, 9], data.Data.Span);
        }
        finally { Directory.SetCurrentDirectory(previousDirectory); }
    }

    private static async Task KeyShowAsync()
    {
        using var fixture = new Fixture(false, true);
        using var unrelated = new Fixture(false, true);
        string storage = SecretKeyStore.GetStoragePath(fixture.KeyName);
        byte[] stored = File.ReadAllBytes(storage);
        DateTime modified = File.GetLastWriteTimeUtc(storage);
        string brokenStorage = SecretKeyStore.GetStoragePath(unrelated.KeyName);
        File.WriteAllBytes(brokenStorage, [1, 2, 3]);
        Check.Throws<InvalidDataException>(() => SecretKeyStore.List());
        File.WriteAllText(fixture.ProjectPath, "not: [valid YAML\n");
        string previousDirectory = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = fixture.Root;
            string output = await CaptureAsync(["keys", "show", fixture.KeyName.ToUpperInvariant()]);
            string client = output.TrimEnd('\r', '\n');
            Check.Equal(128, client.Length);
            Check.Equal(client + Environment.NewLine, output);
            Check.Equal(TestKeys.Client.ExportBase64(), client);
            var imported = new AssetKey(client);
            string exported = Path.Combine(fixture.Root, "shown-client.key");
            await CaptureAsync(["keys", "export", fixture.KeyName, exported]);
            Check.Bytes(File.ReadAllBytes(exported), imported.Export());
            Check.Bytes(stored, File.ReadAllBytes(storage));
            Check.Equal(modified, File.GetLastWriteTimeUtc(storage));
            Check.Bytes([1, 2, 3], File.ReadAllBytes(brokenStorage));
            Check.True(!Directory.Exists(Path.Combine(fixture.Root, ".quire")));
            await Check.ThrowsAsync<FileNotFoundException>(() => CommandLine.RunAsync(["keys", "show", fixture.KeyName + "-missing"], default));
            foreach (string[] arguments in new string[][]
            {
                ["keys", "show"], ["keys", "show", ""], ["keys", "show", fixture.KeyName, "extra"],
                ["keys", "show", fixture.KeyName, "-f", fixture.ProjectPath],
                ["keys", "show", fixture.KeyName, "-root", fixture.Root]
            })
                await Check.ThrowsAsync<ArgumentException>(() => CommandLine.RunAsync(arguments, default));
        }
        finally { Environment.CurrentDirectory = previousDirectory; }
    }

    private static Task KeyProfileIsolationAsync()
    {
        using var fixture = new Fixture(false);
        using var publisher = TestKeys.Create();
        fixture.InitializeKeys();
        string current = SecretKeyStore.GetStoragePath(fixture.KeyName);
        string legacy = Path.Combine(fixture.Root, ".quire", "publisher.key");
        Directory.CreateDirectory(Path.GetDirectoryName(legacy)!);
        byte[] oldProfile = File.ReadAllBytes(current);
        BinaryPrimitives.WriteInt32LittleEndian(oldProfile.AsSpan(12), 1);
        File.WriteAllBytes(legacy, oldProfile);
        File.Delete(current);
        Check.Throws<FileNotFoundException>(() => { using var missing = SecretKeyStore.Load(fixture.KeyName); });

        fixture.InitializeKeys();
        byte[] activeProfile = File.ReadAllBytes(current);
        Check.Bytes(oldProfile, File.ReadAllBytes(legacy));
        using (var restored = SecretKeyStore.Load(fixture.KeyName))
            Check.Bytes(publisher.Export(), restored.Export());

        File.WriteAllBytes(current, oldProfile);
        Check.Throws<InvalidDataException>(() => { using var invalid = SecretKeyStore.Load(fixture.KeyName); });
        File.WriteAllBytes(current, activeProfile);
        byte[] oldClient = new byte[64];
        File.WriteAllBytes(fixture.ClientKeyPath, oldClient);
        Check.Throws<InvalidDataException>(() => SecretKeyStore.LoadClientFile(fixture.ClientKeyPath));
        Check.Throws<IOException>(() => SecretKeyStore.ExportClient(fixture.KeyName, fixture.ClientKeyPath, default));
        Check.Bytes(oldClient, File.ReadAllBytes(fixture.ClientKeyPath));
        Check.Bytes(activeProfile, File.ReadAllBytes(current));
        Check.Bytes(oldProfile, File.ReadAllBytes(legacy));
        return Task.CompletedTask;
    }

    private static async Task SharedKeyAsync()
    {
        using var first = new Fixture(false, true);
        using var second = new Fixture(false, true);
        WriteConfig(first, "", "jobs: 1", first.KeyName);
        WriteConfig(second, "", "jobs: 1", first.KeyName);
        first.Put("content", [1, 2, 3]);
        second.Put("content", [4, 5, 6]);
        var baseline = await first.Build();
        await second.Build();
        foreach (var fixture in new[] { first, second })
        {
            Check.Equal(first.KeyName, fixture.Config().SecretKeyName);
            using var bundle = QuireBundle.Open(fixture.Output, new ReadOptions { Key = TestKeys.Client });
            using var data = bundle.Read("common/data/content");
            Check.Bytes(File.ReadAllBytes(Path.Combine(fixture.Input, "content")), data.Data.Span);
        }
        TagStore.Commit(first.Config().StateDirectory, baseline.Name);
        byte[] accepted = File.ReadAllBytes(Path.Combine(first.Config().StateDirectory, "baseline.idx"));
        WriteConfig(first, "", "jobs: 1", second.KeyName);
        Check.True((await CaptureAsync(["diff", "-f", first.ProjectPath])).Contains("0 changes;", StringComparison.Ordinal));
        var changedName = await first.Build();
        Check.Equal(0, changedName.ContentChanges + changedName.MetadataChanges + changedName.DeletedChanges);
        Check.Equal(0, changedName.Outputs.Length);
        Check.Bytes(accepted, File.ReadAllBytes(Path.Combine(first.Config().StateDirectory, "baseline.idx")));
    }

    private static async Task SecretKeyConfigurationAsync()
    {
        foreach (string? name in new string?[] { null, "none", "NoNe" })
        {
            using var fixture = new Fixture(false);
            fixture.InitializeKeys();
            WriteConfig(fixture, "", "jobs: 1", name);
            Check.True(fixture.Config().SecretKeyName is null && !fixture.Config().Encrypted);
            fixture.Put("content", [1, 2, 3]);
            var tag = await fixture.Build();
            byte[] bytes = File.ReadAllBytes(Path.Combine(fixture.Root, tag.Outputs.Single()));
            var header = PackageHeader.Decode(bytes, bytes.Length);
            Check.True(!header.Encrypted && !header.Signed);
            using var bundle = QuireBundle.Open(fixture.Output);
            using var data = bundle.Read("common/data/content");
            Check.Bytes([1, 2, 3], data.Data.Span);
        }
        using var invalid = new Fixture(false);
        foreach (string oldSetting in new[] { "enabled: false", "enabled: true", "password: secret", "key-environment: QUIRE_KEY" })
        {
            WriteConfig(invalid, "", "jobs: 1", null);
            File.AppendAllText(invalid.ProjectPath, $"encryption:\n  {oldSetting}\n");
            Check.Throws<InvalidDataException>(() => invalid.Config());
        }
    }

    private static async Task DiffWithoutBuildRequirementsAsync()
    {
        using var fixture = new Fixture(true, true);
        fixture.Put("content", [1, 2, 3]);
        fixture.Put("metadata", [4]);
        fixture.Text("metadata.meta", "value=1");
        fixture.Put("gone", [5]);
        fixture.Put("unchanged", [6]);
        await CaptureAsync(["package", "-f", fixture.ProjectPath, "-tag", "encrypted-baseline"]);
        await CaptureAsync(["commit", "encrypted-baseline", "-f", fixture.ProjectPath]);
        var project = fixture.Config();
        var baseline = TagStore.LoadBaseline(project.StateDirectory);
        fixture.RemoveKeys();


        fixture.Put("content", [3, 2, 1]);
        fixture.Text("metadata.meta", "value=2");
        fixture.Put("new", [7]);
        File.Delete(Path.Combine(fixture.Input, "gone"));
        string diff = await CaptureAsync(["diff", "-f", fixture.ProjectPath]);
        string[] lines = diff.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        Check.Equal(5, lines.Length);
        foreach (string expected in new[]
        {
            "A  main/common/data/new",
            "M  main/common/data/content",
            "M  main/common/data/metadata",
            "D  main/common/data/gone",
            "4 changes; 1 added; 2 updated; 1 deleted."
        })
            Check.True(lines.Contains(expected, StringComparer.Ordinal), $"Missing change: {expected}");
        await Check.ThrowsAsync<FileNotFoundException>(() => CommandLine.RunAsync(["package", "-f", fixture.ProjectPath], default));

        WriteConfig(fixture, "", "jobs: 2\n  memory-mib: 15\n  block-size: 1048576",
            fixture.KeyName);
        Check.Equal(diff, await CaptureAsync(["diff", "-f", fixture.ProjectPath]));
        fixture.InitializeKeys();
        await Check.ThrowsAsync<ArgumentException>(() => CommandLine.RunAsync(["package", "-f", fixture.ProjectPath], default));
        Check.Equal(baseline.TagId, TagStore.LoadBaseline(project.StateDirectory).TagId);
        Check.Equal(1, TagStore.List(project.StateDirectory).Count());
    }

    private static async Task JobsAsync()
    {
        var defaults = new BuildOptions();
        Check.Equal(0, defaults.Jobs);
        defaults.Validate();
        Check.True(defaults.EffectiveJobs >= 1 && defaults.EffectiveJobs <= Math.Min(Environment.ProcessorCount, 256));
        Check.True(((long)defaults.EffectiveJobs * 7 + defaults.IoConcurrency + 4) * defaults.BlockSize <= (long)defaults.MemoryMiB * 1024 * 1024);
        var constrained = defaults with { MemoryMiB = 15, BlockSize = 1048576 };
        constrained.Validate();
        Check.Equal(1, constrained.EffectiveJobs);
        Check.Throws<ArgumentException>(() => (constrained with { Jobs = 2 }).Validate());
        Check.Throws<ArgumentException>(() => (constrained with { MemoryMiB = 8 }).Validate());
        Check.Throws<ArgumentException>(() => (defaults with { Jobs = -1 }).Validate());
        Check.Throws<ArgumentException>(() => (defaults with { Jobs = 257 }).Validate());
        var explicitJobs = defaults with { Jobs = 256, MemoryMiB = 512 };
        explicitJobs.Validate();
        Check.Equal(256, explicitJobs.EffectiveJobs);

        using var fixture = new Fixture(false);
        fixture.Put("a", [1]);
        WriteConfig(fixture, "", "jobs: 2\n  memory-mib: 15\n  block-size: 1048576", "none");
        Check.Equal(2, fixture.Config().BuildOptions.Jobs);
        await Check.ThrowsAsync<ArgumentException>(() => CommandLine.RunAsync(["package", "-f", fixture.ProjectPath], default));
        await CaptureAsync(["package", "-f", fixture.ProjectPath, "-jobs", "1"]);
        await CaptureAsync(["package", "-f", fixture.ProjectPath, "-jobs", "0"]);
        Check.Equal(2, TagStore.List(fixture.Config().StateDirectory).Count());
    }

    private static async Task SuffixAsync()
    {
        using var fixture = new Fixture(false);
        fixture.Put("a", [1, 2]);
        Check.Equal("asset", fixture.Config().Suffix);
        Check.Equal("asset", new ReadOptions().Suffix);
        Check.Equal("pak", PackageSuffix.Normalize(".pak"));
        Check.Equal("pak", PackageSuffix.Normalize("pak"));
        Check.Equal("bundle.pak", PackageSuffix.Normalize(".bundle.pak"));
        Directory.CreateDirectory(fixture.Output);
        foreach (string suffix in new[] { "", ".", "..pak", "pak.", "pa..k", "../pak", "a/b", "a\\b", "a*b", "pak ", "包", "a:pak" })
        {
            Check.Throws<ArgumentException>(() => PackageSuffix.Normalize(suffix));
            Check.Throws<ArgumentException>(() => QuireBundle.Open(fixture.Output, new ReadOptions { Suffix = suffix }));
            WriteConfig(fixture, $"suffix: '{suffix}'\n", "jobs: 1", "none");
            Check.Throws<ArgumentException>(() => fixture.Config());
        }
        Check.Throws<ArgumentNullException>(() => PackageSuffix.Normalize(null!));
        Check.Throws<ArgumentNullException>(() => QuireBundle.Open(fixture.Output, new ReadOptions { Suffix = null! }));

        WriteConfig(fixture, "suffix: .pak\n", "jobs: 1", "none");
        await CaptureAsync(["package", "-f", fixture.ProjectPath]);
        var baseline = Latest(fixture.Config());
        WriteConfig(fixture, "suffix: .bundle.pak\n", "jobs: 1", "none");
        await CaptureAsync(["commit", baseline.Name, "-f", fixture.ProjectPath]);
        await CaptureAsync(["package", "-f", fixture.ProjectPath]);
        var settingsOnly = Latest(fixture.Config());
        Check.Equal(0, settingsOnly.ContentChanges);
        Check.Equal(0, settingsOnly.Outputs.Length);
        fixture.Put("a", [3, 4, 5]);
        await CaptureAsync(["commit", settingsOnly.Name, "-f", fixture.ProjectPath]);
        await CaptureAsync(["package", "-f", fixture.ProjectPath]);
        var changed = Latest(fixture.Config());
        Check.Equal(1, changed.ContentChanges);
        Check.True(changed.Outputs.Single().EndsWith(".bundle.pak", StringComparison.Ordinal));
        using var bundle = QuireBundle.Open(Path.Combine(fixture.Output, changed.TagId), new ReadOptions { Suffix = "bundle.pak" });
        using var data = bundle.Read("common/data/a");
        Check.Bytes([3, 4, 5], data.Data.Span);
    }

    private static Task RequiredSourcesAsync()
    {
        using var fixture = new Fixture(false);
        File.WriteAllText(fixture.ProjectPath, "version: 1\nproject: test-project\nsuffix: pak\nbuild:\n  jobs: 0\nsecret-key: none\n");
        Check.Throws<InvalidDataException>(() => fixture.Config());
        File.AppendAllText(fixture.ProjectPath, "packages:\n  - name: main\n");
        Check.Throws<InvalidDataException>(() => fixture.Config());
        return Task.CompletedTask;
    }

    private static void WriteConfig(Fixture fixture, string settings, string build, string? secretKey, string output = "./dist/{tag}")
        => File.WriteAllText(fixture.ProjectPath, $"version: 1\nproject: test-project\noutput: {output}\n{settings}compression: lz4\nbuild:\n  {build}\n{(secretKey is null ? "" : $"secret-key: {secretKey}\n")}packages:\n  - name: main\n    sources:\n      - directory: ./input\n        prefix: common/data\n");

    private static BuildTag Latest(ProjectConfig config) => TagStore.List(config.StateDirectory).OrderBy(tag => tag.BuildTimeUtc).Last();

    private static async Task<string> CaptureAsync(string[] arguments)
    {
        var originalOutput = Console.Out;
        var originalError = Console.Error;
        using var output = new StringWriter();
        using var error = new StringWriter();
        try
        {
            Console.SetOut(output);
            Console.SetError(error);
            Check.Equal(0, await CommandLine.RunAsync(arguments, default));
            return output.ToString() + error.ToString();
        }
        finally
        {
            Console.SetOut(originalOutput);
            Console.SetError(originalError);
        }
    }
}
