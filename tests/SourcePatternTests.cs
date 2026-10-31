using Quire;
using Quire.Cli.Sources;

internal static class SourcePatternTests
{
    internal static void Register(Action<string, Func<Task>> test)
    {
        test("source patterns accept ./ while keeping root and recursive matches distinct", async () =>
        {
            using var fixture = new Fixture(false);
            File.WriteAllBytes(Path.Combine(fixture.Root, "image.png"), [1, 2, 3]);
            File.WriteAllBytes(Path.Combine(fixture.Root, "skip.png"), [4]);
            File.WriteAllText(Path.Combine(fixture.Root, "image.png.meta"), "label=root\n");
            fixture.Put("nested.png", [5]);
            string config = "version: 1\nproject: test-project\noutput: ./dist\ncompression: none\npackages:\n" +
                "  - name: Root\n    sources:\n      - directory: ./\n        prefix: assets\n" +
                "        include:\n          - \"./*.png\"\n        exclude:\n          - \"./skip.png\"\n";
            File.WriteAllText(fixture.ProjectPath, config);
            var tag = await fixture.Build();
            Check.Equal(1, tag.Outputs.Length);
            Check.True(tag.Outputs.Single().EndsWith("/Root.asset", StringComparison.Ordinal));
            using (var bundle = QuireBundle.Open(fixture.Output))
            {
                Check.True(bundle.FileExists("assets/image.png"));
                Check.True(!bundle.FileExists("assets/skip.png"));
                Check.True(!bundle.FileExists("assets/input/nested.png"));
                using var data = bundle.Read("assets/image.png");
                Check.Bytes([1, 2, 3], data.Data.Span);
                Check.Equal("root", bundle.ReadMetadata("assets/image.png").GetString("label"));
            }

            File.WriteAllText(fixture.ProjectPath, config.Replace("./*.png", "./**/*.png"));
            var sources = await SourceScanner.ScanAsync(fixture.Config(), 4, default);
            Check.True(sources.Select(source => source.Path).Order().SequenceEqual(new[] { "assets/image.png", "assets/input/nested.png" }));
            File.WriteAllText(fixture.ProjectPath, config.Replace("./*.png", "*.png"));
            Check.Equal("assets/image.png", (await SourceScanner.ScanAsync(fixture.Config(), 4, default)).Single().Path);
        });

        test("relative rooted ignore rules preserve anchoring and negation", async () =>
        {
            using var fixture = new Fixture(false);
            fixture.Put("skip.png", [1]);
            fixture.Put("keep.png", [2]);
            fixture.Put("sub/skip.png", [3]);
            fixture.Text(".assetignore", "./*.png\n!./keep.png\n");
            var sources = await SourceScanner.ScanAsync(fixture.Config(), 4, default);
            Check.True(sources.Select(source => source.Path).Order().SequenceEqual(new[] { "common/data/keep.png", "common/data/sub/skip.png" }));
        });

        test("case-sensitive scans retain resources differing only in case from the project file", async () =>
        {
            using var fixture = new Fixture(false);
            string variant = Path.Combine(fixture.Root, "QUIRE.PROJECT.YML");
            // Probe the actual filesystem before writing: Windows and common macOS volumes
            // treat this spelling as the existing configuration and must not overwrite it.
            if (File.Exists(variant)) return;
            File.WriteAllText(fixture.ProjectPath,
                File.ReadAllText(fixture.ProjectPath).Replace("directory: ./input", "directory: ./", StringComparison.Ordinal));
            File.WriteAllBytes(variant, [1, 2, 3]);

            var sources = await SourceScanner.ScanAsync(fixture.Config(), 4, default);
            var asset = sources.Single();
            Check.Equal("common/data/QUIRE.PROJECT.YML", asset.Path);
            Check.Equal(variant, asset.SourcePath);
            Check.Equal(3L, asset.Length);
        });
    }
}
