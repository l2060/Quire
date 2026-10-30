using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Quire.Cli;
using Quire.Cli.Build;
using Quire.Cli.Configuration;
using Quire.Cli.Extraction;
using Quire.Cli.Sources;
using Quire.Cli.Tags;
using Quire.Package;
using Quire.Package.Internal;

internal static class CliTests
{
    internal static void Register(Action<string, Func<Task>> test)
    {
        test("metadata export preserves all types, escaping and exact floating-point values", () =>
        {
            const string source = """
                text="  中文 😀 \"quoted\"\nline\t\\=#  "
                code:string=001
                truth:string=true
                tiny=5e-324
                maximum=1.7976931348623157E+308
                negativeZero:double=-0.0
                integer=-9223372036854775808
                boolean=false
                strings=["x","","中","\n"]
                integers=[-9223372036854775808,9223372036854775807]
                doubles:double[]=[1.0,0.0,-0.0,9007199254740994.0,9223372036854775808.0,1.7976931348623157E+308,5e-324]
                booleans=[true,false]
                emptyStrings:string[]=[]
                emptyIntegers:int64[]=[]
                emptyDoubles:double[]=[]
                emptyBooleans:bool[]=[]
                key:with:colon:string=value
                """;
            CultureInfo previous = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
                byte[] encoded = PropertyMetadata.Parse(source);
                Check.Bytes(encoded, PropertyMetadata.Parse(PropertyMetadata.Format(new Metadata(encoded))));
                Check.Equal("", PropertyMetadata.Format(Metadata.Empty));
            }
            finally
            {
                CultureInfo.CurrentCulture = previous;
            }

            return Task.CompletedTask;
        });
        test("CLI package replaces build and help advertises unpackage", async () =>
        {
            using var f = new Fixture();
            f.Put("a", [1, 2, 3]);
            await Check.ThrowsAsync<ArgumentException>(() => CommandLine.RunAsync(["build", "-project", f.ProjectPath], default));
            string workingDirectory = Environment.CurrentDirectory;
            try
            {
                Environment.CurrentDirectory = f.Root;
                Check.Equal(0, await CommandLine.RunAsync(["package", "-jobs", "5"], default));
            }
            finally
            {
                Environment.CurrentDirectory = workingDirectory;
            }

            Check.Equal(1, TagStore.List(f.Config().StateDirectory).Count());
            var original = Console.Out;
            using var help = new StringWriter(CultureInfo.InvariantCulture);
            try
            {
                Console.SetOut(help);
                Check.Equal(0, await CommandLine.RunAsync(["--help"], default));
            }
            finally
            {
                Console.SetOut(original);
            }

            Check.True(help.ToString().Contains("quire-cli package", StringComparison.Ordinal));
            Check.True(help.ToString().Contains("quire-cli unpackage", StringComparison.Ordinal));
            Check.True(help.ToString().Contains("-base TAG", StringComparison.Ordinal));
            Check.True(!help.ToString().Contains("-base-tag", StringComparison.Ordinal));
            Check.True(!help.ToString().Contains("quire-cli build", StringComparison.Ordinal));
            foreach (string option in new[]
            {
                "-io-concurrency",
                "-write-concurrency",
                "-memory-mib",
                "-block-size"
            }

            )
            {
                Check.True(!help.ToString().Contains(option, StringComparison.Ordinal));
                await Check.ThrowsAsync<ArgumentException>(() => CommandLine.RunAsync(["package", "-project", f.ProjectPath, option, "2"], default));
            }
        });
        test("CLI package and diff resolve unique prefixes and eight-character Tags", async () =>
        {
            string[] ids = ["72745fcb", "cb7d4e65", "7fcb7655"];
            (string Selector, string Expected)[] selections =
            [
                ("72", ids[0]), ("7f", ids[2]), ("c", ids[1]),
                ("C", ids[1]), ("7F", ids[2]),
                ("72745fcb", ids[0]), ("CB7D4E65", ids[1])
            ];
            foreach (var selection in selections)
            {
                using var f = new Fixture();
                f.Put("a", [1, 2, 3]);
                string state = f.Config().StateDirectory;
                foreach (string id in ids)
                {
                    var tag = new BuildTag(id, DateTimeOffset.UnixEpoch, null, "unused", [], 0, 0);
                    TagStore.Write(Path.Combine(state, "tags", id), tag, []);
                }

                string diff = await CaptureOutputAsync(["diff", "-project", f.ProjectPath, "-base=" + selection.Selector]);
                Check.True(diff.Contains("CONTENT  main/common/data/a", StringComparison.Ordinal));
                Check.True(diff.Contains("1 changes;", StringComparison.Ordinal));
                Check.Equal(3, TagStore.List(state).Count());
                Check.True(!Directory.Exists(f.Output));
                string output = await CaptureOutputAsync(["package", "-project", f.ProjectPath, "-base", selection.Selector]);
                var built = TagStore.List(state).Single(tag => !ids.Contains(tag.TagId));
                Check.Equal("Tag: " + built.TagId, output.Split('\n')[0].TrimEnd('\r'));
                Check.Equal(8, built.TagId.Length);
                Check.True(built.TagId.All(char.IsAsciiHexDigit));
                Check.Equal(built.TagId.ToLowerInvariant(), built.TagId);
                Check.Equal(selection.Expected, built.BaseTag);
                Check.Equal(1, built.ContentChanges);
                Check.Equal(1, built.Outputs.Length);
                Check.True(File.Exists(Path.Combine(f.Root, built.Outputs[0])));
            }
        });
        test("Tag generation retries collisions against every reserved ID", () =>
        {
            var candidates = new Queue<uint>([0x72745fcb, 0xcb7d4e65, 0x1234abcd]);
            string id = TagStore.CreateId(["72745FCB", "cb7d4e65"], candidates.Dequeue);
            Check.Equal("1234abcd", id);
            Check.Equal(0, candidates.Count);
            Check.Equal("00000000", TagStore.CreateId([], () => 0));
            return Task.CompletedTask;
        });
        test("build Tag generation reserves history, orphan outputs and staging IDs", () =>
        {
            using var f = new Fixture();
            var project = f.Config();
            var history = new BuildTag("11111111", DateTimeOffset.UnixEpoch, null, "unused", [], 0, 0);
            string[] reserved =
            [
                Path.Combine(f.Output, "22222222"),
                Path.Combine(f.Output, ".33333333.tmp"),
                Path.Combine(project.StateDirectory, "tags", ".44444444.tmp"),
                Path.Combine(project.StateDirectory, "tags", "55555555")
            ];
            foreach (string directory in reserved)
                Directory.CreateDirectory(directory);
            var candidates = new Queue<uint>([0x11111111, 0x22222222, 0x33333333, 0x44444444, 0x55555555, 0xabcdef12]);
            Check.Equal("abcdef12", ProjectBuilder.CreateTagId(project, [history], candidates.Dequeue));
            Check.Equal(0, candidates.Count);
            Check.True(reserved.All(Directory.Exists));
            return Task.CompletedTask;
        });
        test("a locked shared output rejects builds and releases the project lock", async () =>
        {
            using var f = new Fixture();
            f.Put("a", [1, 2, 3]);
            Directory.CreateDirectory(f.Output);
            using (new FileStream(Path.Combine(f.Output, ".quire-build.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                await Check.ThrowsAsync<IOException>(() => f.Build());
                Check.Equal(0, TagStore.List(f.Config().StateDirectory).Count());
                Check.Equal(0, Directory.GetDirectories(f.Output).Length);
            }

            var built = await f.Build();
            Check.Equal(1, TagStore.List(f.Config().StateDirectory).Count());
            Check.Equal(1, built.Outputs.Length);
            Check.True(File.Exists(Path.Combine(f.Root, built.Outputs[0])));
        });
        test("CLI rejects ambiguous, missing and invalid base Tags before publishing", async () =>
        {
            using var f = new Fixture();
            f.Put("a", [1, 2, 3]);
            string state = f.Config().StateDirectory;
            string[] ids = ["72745fcb", "7fcb7655"];
            foreach (string id in ids)
            {
                var tag = new BuildTag(id, DateTimeOffset.UnixEpoch, null, "unused", [], 0, 0);
                TagStore.Write(Path.Combine(state, "tags", id), tag, []);
            }

            foreach (string command in new[] { "package", "diff" })
            {
                foreach (string selector in new[] { "7", "", " ", "g", "72-", "72745fcb0", "72745fcb765d4e00000000000000000001", "../72" })
                    await Check.ThrowsAsync<ArgumentException>(() => CommandLine.RunAsync([command, "-project", f.ProjectPath, "-base", selector], default));
                foreach (string selector in new[] { "ab", "aaaaaaaa" })
                    await Check.ThrowsAsync<FileNotFoundException>(() => CommandLine.RunAsync([command, "-project", f.ProjectPath, "-base", selector], default));
                await Check.ThrowsAsync<ArgumentException>(() => CommandLine.RunAsync([command, "-project", f.ProjectPath, "-base-tag", ids[0]], default));
                await Check.ThrowsAsync<ArgumentException>(() => CommandLine.RunAsync([command, "-project", f.ProjectPath, "-base"], default));
            }

            await Check.ThrowsAsync<ArgumentException>(() => CommandLine.RunAsync(["diff", "-project", f.ProjectPath], default));
            Check.True(TagStore.List(state).Select(tag => tag.TagId).Order().SequenceEqual(ids.Order()));
            Check.True(!Directory.Exists(f.Output));
        });
        test("CLI rejects malformed Tag directories before writing a new build", async () =>
        {
            using var f = new Fixture();
            f.Put("a", [1, 2, 3]);
            string state = f.Config().StateDirectory;
            string invalidDirectory = Path.Combine(state, "tags", "deadbeef765d4e00000000000000000001");
            Directory.CreateDirectory(invalidDirectory);
            string record = "{\"TagId\":\"deadbeef-765d-4e00-0000-000000000001\"}";
            File.WriteAllText(Path.Combine(invalidDirectory, "tag.json"), record);
            foreach (string command in new[] { "package", "diff" })
                await Check.ThrowsAsync<InvalidDataException>(() => CommandLine.RunAsync([command, "-project", f.ProjectPath, "-base", "deadbeef"], default));
            await Check.ThrowsAsync<InvalidDataException>(() => CommandLine.RunAsync(["package", "-project", f.ProjectPath], default));
            Check.Equal(record, File.ReadAllText(Path.Combine(invalidDirectory, "tag.json")));
            Check.Equal(1, Directory.GetDirectories(Path.Combine(state, "tags")).Length);
            Check.True(!Directory.Exists(f.Output));
        });
        test("CLI, package headers, output directories and state use eight-character Tags", async () =>
        {
            using var f = new Fixture();
            f.Put("a", [1, 2, 3]);
            string orphan = Path.Combine(f.Output, "1234abcd");
            Directory.CreateDirectory(orphan);
            File.WriteAllText(Path.Combine(orphan, "keep.txt"), "orphan output");
            string output = await CaptureOutputAsync(["package", "-project", f.ProjectPath]);
            var built = TagStore.List(f.Config().StateDirectory).Single();
            Check.Equal(8, built.TagId.Length);
            Check.True(built.TagId != "1234abcd");
            Check.Equal("orphan output", File.ReadAllText(Path.Combine(orphan, "keep.txt")));
            Check.Equal("Tag: " + built.TagId, output.Split('\n')[0].TrimEnd('\r'));
            string record = Path.Combine(f.Config().StateDirectory, "tags", built.TagId, "tag.json");
            Check.True(File.ReadAllText(record).Contains("\"" + built.TagId + "\"", StringComparison.Ordinal));
            Check.True(Directory.Exists(Path.Combine(f.Output, built.TagId)));
            using (var packages = PackageSet.Open(f.Output))
                Check.Equal(built.TagId, packages.Packages.Single().TagId);
            foreach (string command in new[] { "inspect", "verify" })
            {
                string listing = await CaptureOutputAsync([command, f.Output]);
                Check.Equal(built.TagId, listing.Split('\n')[0].Split("  ", StringSplitOptions.None)[1]);
            }
        });
        test("CLI tags aligns columns and shows local build time, output counts and base Tags", async () =>
        {
            using var f = new Fixture();
            string[] headers = ["TAG", "BUILD TIME", "OUTPUT FILES", "CONTENT", "META ONLY", "BASE"];
            int[] starts = [0, 10, 36, 50, 62, 74];
            string empty = await CaptureOutputAsync(["tags", "-project", f.ProjectPath]);
            string[] emptyLines = empty.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
            Check.Equal(1, emptyLines.Length);
            CheckColumns(emptyLines[0], headers);

            var full = new BuildTag("72745fcb",
                new DateTimeOffset(2026, 10, 30, 5, 22, 46, 666, TimeSpan.Zero), null, "unused", ["a.asset", "b.asset", "c.asset"], 2, 0);
            var patch = new BuildTag("cb7d4e65",
                full.BuildTimeUtc.AddHours(1).AddMilliseconds(1), full.TagId, "unused", [], 0, 12);
            string state = f.Config().StateDirectory;
            foreach (var tag in new[] { patch, full })
                TagStore.Write(Path.Combine(state, "tags", tag.TagId), tag, []);

            string output = await CaptureOutputAsync(["tags", "-project", f.ProjectPath]);
            string[] lines = output.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
            Check.Equal(3, lines.Length);
            Check.Equal(emptyLines[0], lines[0]);
            CheckColumns(lines[1], ["72745fcb", full.BuildTimeUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture), "3", "2", "0", "NULL"]);
            CheckColumns(lines[2], ["cb7d4e65", patch.BuildTimeUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture), "0", "0", "12", "72745fcb"]);

            void CheckColumns(string line, string[] values)
            {
                for (int i = 0; i < starts.Length; i++)
                {
                    string column = i + 1 < starts.Length ? line[starts[i]..starts[i + 1]] : line[starts[i]..];
                    Check.True(column.StartsWith(values[i], StringComparison.Ordinal), $"Column {headers[i]} must be left aligned.");
                    Check.Equal(values[i], column.TrimEnd());
                }
            }
        });
        foreach (bool compression in new[]
        {
            false,
            true
        }

        )
            foreach (bool encryption in new[]
            {
                false,
                true
            }

            )
            {
                bool compressed = compression, encrypted = encryption;
                test($"CLI unpackage and repackage compression={compressed} encryption={encrypted}", async () =>
                {
                    using var f = new Fixture(compressed, encrypted);
                    byte[] body = new byte[700123];
                    new Random(317).NextBytes(body);
                    body.AsSpan(300000, 200000).Clear();
                    const string metadata = "name:string=001\nd:double=1.0\nvalues:double[]=[1.0,9223372036854775808.0]\nempty:bool[]=[]";
                    f.Put("sub/中文😀.bin", body);
                    f.Text("sub/中文😀.bin.meta", metadata);
                    f.Put("empty", []);
                    f.Text("empty.meta", "");
                    f.Put("plain", [7]);
                    await f.Build();
                    string destination = Path.Combine(f.Root, "unpacked");
                    Directory.CreateDirectory(destination);
                    var arguments = new List<string>
                    {
                        "unpackage",
                        f.Output,
                        destination,
                        "-jobs",
                        "5"
                    };
                    if (encrypted)
                        arguments.AddRange(compressed ? ["-pwd-env", f.Config().KeyEnvironment!] : ["-pwd", f.ReadOptions.Password!]);
                    Check.Equal(0, await CommandLine.RunAsync(arguments.ToArray(), default));
                    byte[] unpackedBody = await File.ReadAllBytesAsync(Path.Combine(destination, "common/data/sub/中文😀.bin"));
                    string unpackedMetadata = await File.ReadAllTextAsync(Path.Combine(destination, "common/data/sub/中文😀.bin.meta"));
                    Check.Bytes(body, unpackedBody);
                    Check.Bytes(PropertyMetadata.Parse(metadata), PropertyMetadata.Parse(unpackedMetadata));
                    Check.Equal(0L, new FileInfo(Path.Combine(destination, "common/data/empty")).Length);
                    Check.Equal(0L, new FileInfo(Path.Combine(destination, "common/data/empty.meta")).Length);
                    Check.True(!File.Exists(Path.Combine(destination, "common/data/plain.meta")));
                    Check.Equal(5, Directory.GetFiles(destination, "*", SearchOption.AllDirectories).Length);
                    File.WriteAllText(f.ProjectPath, File.ReadAllText(f.ProjectPath).Replace("directory: ./input", "directory: ./unpacked").Replace("prefix: common/data", "prefix: \"\""));
                    Check.Equal(3, (await f.Build()).ContentChanges);
                    using var set = PackageSet.Open(f.Output, f.ReadOptions);
                    using var restored = set.Read("common/data/sub/中文😀.bin");
                    Check.Bytes(body, restored.Data.Span);
                    Check.Equal("001", restored.Metadata.GetString("name"));
                    Check.Equal(1d, restored.Metadata.GetDouble("d"));
                });
            }

        test("unpackage resolves content and metadata-only overlays without deleting inherited files", async () =>
        {
            using var f = new Fixture();
            f.Put("a", [1]);
            f.Text("a.meta", "old=true");
            f.Put("metadata", [2]);
            f.Text("metadata.meta", "version=1\nold=true");
            f.Put("inherited", [3]);
            var baseline = await f.Build();
            f.Put("a", [9]);
            File.Delete(Path.Combine(f.Input, "a.meta"));
            f.Text("metadata.meta", "version=2");
            File.Delete(Path.Combine(f.Input, "inherited"));
            await f.Build(baseline.TagId);
            string destination = Path.Combine(f.Root, "unpacked");
            var result = await PackageExtractor.ExtractAsync(f.Output, destination, f.ReadOptions with
            {
                MaxBufferedAssetBytes = 0
            }, default);
            Check.Equal(3L, result.Assets);
            Check.Equal(1L, result.MetadataFiles);
            Check.Bytes([9], File.ReadAllBytes(Path.Combine(destination, "common/data/a")));
            Check.True(!File.Exists(Path.Combine(destination, "common/data/a.meta")));
            Check.Bytes([3], File.ReadAllBytes(Path.Combine(destination, "common/data/inherited")));
            var metadata = new Metadata(PropertyMetadata.Parse(File.ReadAllText(Path.Combine(destination, "common/data/metadata.meta"))));
            Check.Equal(2L, metadata.GetInt64("version"));
            Check.True(!metadata.Contains("old"));
        });
        test("unpackage rejects nonempty, overlapping, missing and empty directories", async () =>
        {
            using var f = new Fixture();
            f.Put("a", [1]);
            await f.Build();
            string destination = Path.Combine(f.Root, "unpacked");
            Directory.CreateDirectory(destination);
            string existing = Path.Combine(destination, "existing");
            File.WriteAllText(existing, "keep");
            await Check.ThrowsAsync<IOException>(() => PackageExtractor.ExtractAsync(f.Output, destination, f.ReadOptions, default));
            Check.Equal("keep", File.ReadAllText(existing));
            foreach (string overlapping in new[]
            {
                f.Output,
                f.Root,
                Path.Combine(f.Output, "nested")
            }

            )
                await Check.ThrowsAsync<ArgumentException>(() => PackageExtractor.ExtractAsync(f.Output, overlapping, f.ReadOptions, default));
            await Check.ThrowsAsync<DirectoryNotFoundException>(() => PackageExtractor.ExtractAsync(Path.Combine(f.Root, "missing"), Path.Combine(f.Root, "other"), f.ReadOptions, default));
            string empty = Path.Combine(f.Root, "empty");
            Directory.CreateDirectory(empty);
            await Check.ThrowsAsync<InvalidDataException>(() => PackageExtractor.ExtractAsync(empty, Path.Combine(f.Root, "other"), f.ReadOptions, default));
            Check.True(!Directory.Exists(Path.Combine(f.Root, "other")));
        });
        test("unpackage corruption and cancellation leave no output", async () =>
        {
            using var f = new Fixture(false);
            f.Put("a", [1, 2, 3]);
            var tag = await f.Build();
            string destination = Path.Combine(f.Root, "unpacked");
            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            await Check.ThrowsAsync<OperationCanceledException>(() => PackageExtractor.ExtractAsync(f.Output, destination, f.ReadOptions, canceled.Token));
            string package = Path.Combine(f.Root, tag.Outputs[0]);
            byte[] bytes = File.ReadAllBytes(package);
            bytes[^1] ^= 1;
            File.WriteAllBytes(package, bytes);
            await Check.ThrowsAsync<InvalidDataException>(() => PackageExtractor.ExtractAsync(f.Output, destination, f.ReadOptions, default));
            Check.True(!Directory.Exists(destination));
            Check.Equal(0, Directory.GetDirectories(f.Root, ".asset-unpackage-*.tmp").Length);
        });
        test("unpackage rejects incorrect encryption password", async () =>
        {
            using var f = new Fixture(true, true);
            f.Put("a", [1]);
            await f.Build();
            string destination = Path.Combine(f.Root, "unpacked");
            await Check.ThrowsAsync<InvalidDataException>(() => PackageExtractor.ExtractAsync(f.Output, destination, new ReadOptions { Password = "wrong" }, default));
            Check.True(!Directory.Exists(destination));
        });
        test("unpackage file-directory and metadata collisions clean staging without publishing", async () =>
        {
            foreach (string prefix in new[]
            {
                "common/data/a",
                "common/data/a.meta"
            }

            )
            {
                using var f = new Fixture();
                f.Put("a", [1]);
                f.Text("a.meta", "x=1");
                File.AppendAllText(f.ProjectPath, $"  - name: second\n    sources:\n      - directory: ./input\n        prefix: {prefix}\n");
                await f.Build();
                string destination = Path.Combine(f.Root, "unpacked");
                Directory.CreateDirectory(destination);
                bool rejected = false;
                try
                {
                    await PackageExtractor.ExtractAsync(f.Output, destination, f.ReadOptions, default);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    rejected = true;
                }

                Check.True(rejected, "Conflicting output paths must fail extraction.");
                Check.Equal(0, Directory.GetFileSystemEntries(destination).Length);
                Check.Equal(0, Directory.GetDirectories(f.Root, ".asset-unpackage-*.tmp").Length);
            }
        });
        if (OperatingSystem.IsWindows())
            test("unpackage rejects Windows path aliases and reserved device names", async () =>
            {
                foreach (string prefix in new[]
                {
                    ".. /escape",
                    "NUL",
                    "COM1.txt"
                }

                )
                {
                    using var f = new Fixture();
                    f.Put("a", [1]);
                    File.WriteAllText(f.ProjectPath, File.ReadAllText(f.ProjectPath).Replace("prefix: common/data", "prefix: " + prefix));
                    await f.Build();
                    string destination = Path.Combine(f.Root, "unpacked");
                    await Check.ThrowsAsync<InvalidDataException>(() => PackageExtractor.ExtractAsync(f.Output, destination, f.ReadOptions, default));
                    Check.True(!Directory.Exists(destination));
                    Check.True(!Directory.Exists(Path.Combine(f.Root, "escape")));
                    Check.Equal(0, Directory.GetDirectories(f.Root, ".asset-unpackage-*.tmp").Length);
                }
            });
        test("unpackage validates argument count, options and password environment variable", async () =>
        {
            using var f = new Fixture();
            foreach (string[] arguments in new string[][]
            {
                ["unpackage"],
                ["unpackage", f.Output],
                ["unpackage", f.Output, f.Root, "extra"],
                ["unpackage", f.Output, f.Root, "-project", f.ProjectPath],
                ["unpackage", f.Output, f.Root, "-jobs", "0"],
                ["unpackage", f.Output, f.Root, "-jobs", "-1"],
                ["unpackage", f.Output, f.Root, "-jobs", "257"],
                ["unpackage", f.Output, f.Root, "-pwd", ""],
                ["unpackage", f.Output, f.Root, "-pwd-env", ""],
                ["unpackage", f.Output, f.Root, "-pwd", "secret", "-pwd-env", "NAME"],
                ["unpackage", f.Output, f.Root, "-pwd-env", "ASSET_MISSING_" + Guid.NewGuid().ToString("N")]
            }

            )
                await Check.ThrowsAsync<ArgumentException>(() => CommandLine.RunAsync(arguments, default));
        });
        test("explicit password supports package, diff, inspect, verify and unpackage without configured env", async () =>
        {
            using var f = new Fixture(true, true);
            f.Put("a", [1, 2, 3]);
            f.Text("a.meta", "x=1");
            string variable = f.Config().KeyEnvironment!;
            const string password = "--command password 密码=value";
            Environment.SetEnvironmentVariable(variable, null);
            var original = Console.Out;
            using var log = new StringWriter(CultureInfo.InvariantCulture);
            try
            {
                Console.SetOut(log);
                Check.Equal(0, await CommandLine.RunAsync(["package", "-project", f.ProjectPath, "-pwd=" + password], default));
                var baseline = TagStore.List(f.Config().StateDirectory).Single();
                Environment.SetEnvironmentVariable(variable, "different configured password");
                Check.Equal(0, await CommandLine.RunAsync(["diff", "-project", f.ProjectPath, "-base", baseline.TagId, "-pwd=" + password], default));
                foreach (string command in new[]
                {
                    "inspect",
                    "verify"
                }

                )
                    Check.Equal(0, await CommandLine.RunAsync([command, f.Output, "-pwd=" + password], default));
                string destination = Path.Combine(f.Root, "unpacked");
                Check.Equal(0, await CommandLine.RunAsync(["unpackage", f.Output, destination, "-pwd=" + password, "-jobs", "4"], default));
                Check.Bytes([1, 2, 3], File.ReadAllBytes(Path.Combine(destination, "common/data/a")));
                Environment.SetEnvironmentVariable(variable, password);
                Check.Equal(0, await CommandLine.RunAsync(["package", "-project", f.ProjectPath, "-base", baseline.TagId, "-pwd-env", variable], default));
                Check.Equal(0, TagStore.List(f.Config().StateDirectory).OrderBy(t => t.BuildTimeUtc).Last().Outputs.Length);
            }
            finally
            {
                Console.SetOut(original);
            }

            Check.True(log.ToString().Contains("0 changes;", StringComparison.Ordinal));
            Check.True(!log.ToString().Contains(password, StringComparison.Ordinal));
            byte[] passwordBytes = Encoding.UTF8.GetBytes(password);
            foreach (string file in Directory.EnumerateFiles(f.Config().StateDirectory, "*", SearchOption.AllDirectories))
                Check.True(File.ReadAllBytes(file).AsSpan().IndexOf(passwordBytes) < 0, "Password must not be persisted in build state.");
        });
        test("direct password is rejected when project encryption is disabled", async () =>
        {
            using var f = new Fixture();
            f.Put("a", [1]);
            await Check.ThrowsAsync<ArgumentException>(() => CommandLine.RunAsync(["package", "-project", f.ProjectPath, "-pwd", "secret"], default));
            Check.True(!Directory.Exists(f.Output));
        });
        test("parallel unpackage matches sequential extraction across many encrypted resources", async () =>
        {
            using var f = new Fixture(true, true);
            for (int i = 0; i < 80; i++)
            {
                byte[] bytes = new byte[65536 + i * 127];
                new Random(i).NextBytes(bytes);
                f.Put($"sub{i % 7}/file{i:D3}.bin", bytes);
                f.Text($"sub{i % 7}/file{i:D3}.bin.meta", $"id={i}\nname=file{i}");
            }

            await f.Build();
            string serial = Path.Combine(f.Root, "serial"), parallel = Path.Combine(f.Root, "parallel");
            var serialResult = await PackageExtractor.ExtractAsync(f.Output, serial, f.ReadOptions with
            {
                MaxBufferedAssetBytes = 0
            }, default, 1);
            var parallelResult = await PackageExtractor.ExtractAsync(f.Output, parallel, f.ReadOptions with
            {
                MaxBufferedAssetBytes = 0
            }, default, 8);
            Check.Equal(serialResult, parallelResult);
            Check.Equal(80L, parallelResult.Assets);
            Check.Equal(80L, parallelResult.MetadataFiles);
            var serialFiles = Directory.GetFiles(serial, "*", SearchOption.AllDirectories);
            Check.Equal(serialFiles.Length, Directory.GetFiles(parallel, "*", SearchOption.AllDirectories).Length);
            foreach (string file in serialFiles)
            {
                string relative = Path.GetRelativePath(serial, file);
                Check.Bytes(SHA256.HashData(File.ReadAllBytes(file)), SHA256.HashData(File.ReadAllBytes(Path.Combine(parallel, relative))));
            }
        });
        test("init creates complete multi-package configuration and preserves existing files", async () =>
        {
            using var f = new Fixture();
            string root = Path.Combine(f.Root, "new project");
            Check.Equal(0, await CommandLine.RunAsync(["init", root], default));
            string path = Path.Combine(root, "quire.project.yml");
            string original = File.ReadAllText(path);
            var project = ProjectConfig.Load(path);
            Check.Equal(2, project.Packages.Length);
            Check.Equal(2, project.Packages[0].Sources.Length);
            Check.Equal(128, project.BuildOptions.MemoryMiB);
            Check.Equal(262144, project.BuildOptions.BlockSize);
            Check.True(project.Packages[0].Compression);
            Check.True(!project.Packages[1].Compression);
            foreach (var package in project.Packages)
                foreach (var source in package.Sources)
                {
                    Check.True(Directory.Exists(source.Directory));
                    Check.True(source.Include.Length > 0 && source.Exclude.Length > 0);
                    File.WriteAllText(Path.Combine(source.Directory, "hello.txt"), package.Name);
                }

            await Check.ThrowsAsync<IOException>(() => CommandLine.RunAsync(["init", root], default));
            Check.Equal(original, File.ReadAllText(path));
            Check.Equal(0, await CommandLine.RunAsync(["package", "-project", path, "-jobs", "5"], default));
            using var assets = PackageSet.Open(project.Output);
            Check.Equal(2, assets.Packages.Count);
            Check.Equal(3, assets.EnumerateFiles().Count());
            Check.True(assets.FileExists("common/data/hello.txt"));
            Check.True(assets.FileExists("common/shared/hello.txt"));
            Check.True(assets.FileExists("strings/hello.txt"));
        });
        test("init defaults to current directory and rejects conflicts and extra arguments", async () =>
        {
            using var f = new Fixture();
            string target = Path.Combine(f.Root, "current");
            Directory.CreateDirectory(target);
            string previous = Environment.CurrentDirectory;
            try
            {
                Environment.CurrentDirectory = target;
                Check.Equal(0, await CommandLine.RunAsync(["init"], default));
            }
            finally
            {
                Environment.CurrentDirectory = previous;
            }

            Check.True(File.Exists(Path.Combine(target, "quire.project.yml")));
            await Check.ThrowsAsync<ArgumentException>(() => CommandLine.RunAsync(["init", target, "extra"], default));
            string conflict = Path.Combine(f.Root, "conflict");
            Directory.CreateDirectory(Path.Combine(conflict, "assets"));
            string occupied = Path.Combine(conflict, "assets/common");
            File.WriteAllText(occupied, "keep");
            await Check.ThrowsAsync<IOException>(() => CommandLine.RunAsync(["init", conflict], default));
            Check.Equal("keep", File.ReadAllText(occupied));
            Check.True(!File.Exists(Path.Combine(conflict, "quire.project.yml")));
        });
        test("YAML build settings control package blocks and incrementals; PK.QUIRE magic is enforced", async () =>
        {
            using var f = new Fixture();
            f.Put("a", new byte[200001]);
            File.AppendAllText(f.ProjectPath, "build:\n  memory-mib: 16\n  block-size: 65536\n");
            Check.Equal(16, f.Config().BuildOptions.MemoryMiB);
            Check.Equal(0, await CommandLine.RunAsync(["package", "-project", f.ProjectPath, "-jobs", "5"], default));
            var first = TagStore.List(f.Config().StateDirectory).Single();
            byte[] bytes = File.ReadAllBytes(Path.Combine(f.Root, first.Outputs.Single()));
            Check.Bytes("PK.QUIRE"u8, bytes.AsSpan(0, 8));
            Check.Equal(65536, PackageHeader.Decode(bytes, bytes.Length).BlockSize);
            "GASSET01"u8.CopyTo(bytes);
            SHA256.HashData(bytes.AsSpan(0, 160), bytes.AsSpan(160, 32));
            Check.Throws<InvalidDataException>(() => PackageHeader.Decode(bytes, bytes.Length));
            File.WriteAllText(f.ProjectPath, File.ReadAllText(f.ProjectPath).Replace("block-size: 65536", "block-size: 131072"));
            Check.Equal(0, await CommandLine.RunAsync(["package", "-project", f.ProjectPath, "-base", first.TagId], default));
            var second = TagStore.List(f.Config().StateDirectory).OrderBy(t => t.BuildTimeUtc).Last();
            Check.Equal(1, second.ContentChanges);
            File.WriteAllText(f.ProjectPath, File.ReadAllText(f.ProjectPath).Replace("memory-mib: 16", "memory-mib: 32"));
            Check.Equal(0, await CommandLine.RunAsync(["package", "-project", f.ProjectPath, "-base", second.TagId], default));
            Check.Equal(0, TagStore.List(f.Config().StateDirectory).OrderBy(t => t.BuildTimeUtc).Last().Outputs.Length);
        });
        test("invalid YAML build settings are rejected", () =>
        {
            using var f = new Fixture();
            string original = File.ReadAllText(f.ProjectPath);
            foreach (string setting in new[]
            {
                "memory-mib: 0",
                "memory-mib: 65537",
                "memory-mib: text",
                "block-size: 100000",
                "block-size: 0",
                "jobs: 4",
                "io-concurrency: 4"
            }

            )
            {
                File.WriteAllText(f.ProjectPath, original + "build:\n  " + setting + "\n");
                Check.Throws<InvalidDataException>(() => f.Config());
            }

            return Task.CompletedTask;
        });
        test("baseline Tags with incompatible encoding cannot produce incomplete patches", async () =>
        {
            using var f = new Fixture();
            f.Put("a", [1]);
            var current = await f.Build();
            var old = current with
            {
                TagId = current.TagId == "1234abcd" ? "1234abce" : "1234abcd"
            };
            var files = TagStore.LoadFiles(f.Config().StateDirectory, current.TagId).Values.Select(file => file with { Encoding = "v1:262144:True:False:public" });
            TagStore.Write(Path.Combine(f.Config().StateDirectory, "tags", old.TagId), old, files);
            await Check.ThrowsAsync<InvalidDataException>(() => f.Build(old.TagId));
        });
    }

    private static async Task<string> CaptureOutputAsync(string[] arguments)
    {
        var original = Console.Out;
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        try
        {
            Console.SetOut(output);
            Check.Equal(0, await CommandLine.RunAsync(arguments, default));
            return output.ToString();
        }
        finally
        {
            Console.SetOut(original);
        }
    }
}
