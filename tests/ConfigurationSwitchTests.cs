using System.Globalization;
using Quire;
using Quire.Cli;
using Quire.Cli.Configuration;
using Quire.Cli.Tags;
using Quire.Internal;

internal static class ConfigurationSwitchTests
{

    internal static void Register(Action<string, Func<Task>> test)
    {
        foreach (bool encryptedPatch in new[] { false, true })
        {
            bool encrypted = encryptedPatch;
            test($"Alternate YAML packaging changes only logical assets, encrypted patch={encrypted}", () => SwitchAsync(encrypted));
        }
    }

    private static async Task SwitchAsync(bool encryptedPatch)
    {
        using var fixture = new Fixture(false, true);
        string patchFile = Path.Combine(fixture.Root, "quire.patch.yml");
        File.WriteAllText(fixture.ProjectPath, FullConfiguration(fixture.KeyName));
        File.WriteAllText(patchFile, PatchConfiguration(encryptedPatch ? fixture.KeyName : "none"));
        fixture.Put("core/keep.bin", [1, 2, 3]);
        fixture.Put("core/change.bin", [4, 5]);
        fixture.Put("core/meta.bin", [6, 7]);
        fixture.Text("core/meta.bin.meta", "revision=1");
        fixture.Put("media/delete.bin", [8]);
        fixture.Put("media/stay.bin", [9]);
        var fullConfig = ProjectConfig.Load(fixture.ProjectPath);
        var patchConfig = ProjectConfig.Load(patchFile);
        Check.Equal(fullConfig.StateDirectory, patchConfig.StateDirectory);
        Check.Equal(fullConfig.Output, patchConfig.Output);
        Check.Equal(CompressionAlgorithm.Zlib, fullConfig.Packages[0].Compression);
        Check.Equal(CompressionAlgorithm.Lz4, patchConfig.Packages.Single().Compression);
        Check.True(fullConfig.Encrypted);
        Check.Equal(encryptedPatch, patchConfig.Encrypted);
        string state = fullConfig.StateDirectory;

        var a = await Build(fixture, fixture.ProjectPath, "A-full");
        Check.Equal(2, a.Outputs.Length);
        Check.Equal(5, a.ContentChanges);
        await Run(patchFile, "commit", a.Name); // Both YAML paths address the same formal baseline.
        var original = TagStore.LoadBaseline(state);
        var originalMeta = Find(original, "assets/core/meta.bin");
        var originalKeep = Find(original, "assets/core/keep.bin");
        byte[] acceptedA = File.ReadAllBytes(Path.Combine(state, "baseline.idx"));
        string differences = await Run(patchFile, "diff");
        Check.True(differences.Contains("0 changes", StringComparison.Ordinal));
        var b = await Build(fixture, patchFile, "B-settings-only");
        AssertEmpty(b);
        Check.Equal(a.TagId, b.BaseTag);
        Check.Equal(0, TagStore.LoadDelta(state, b.TagId).Upserts.Length);
        Check.Equal(0, TagStore.LoadDelta(state, b.TagId).Removed.Length);
        Check.Bytes(acceptedA, File.ReadAllBytes(Path.Combine(state, "baseline.idx")));
        await Run(fixture.ProjectPath, "commit", b.Name);
        Check.Equal(originalMeta, Find(TagStore.LoadBaseline(state), originalMeta.Path));

        byte[] changed = Enumerable.Repeat((byte)23, 150001).ToArray();
        fixture.Put("core/change.bin", changed);
        fixture.Text("core/meta.bin.meta", "revision=2");
        File.Delete(Path.Combine(fixture.Input, "media/delete.bin"));
        fixture.Put("media/added.bin", [10, 11, 12]);
        var c = await Build(fixture, patchFile, "C-patch");
        Check.Equal(b.TagId, c.BaseTag);
        Check.Equal(2, c.ContentChanges);
        Check.Equal(1, c.MetadataChanges);
        Check.Equal(1, c.DeletedChanges);
        Check.Equal(1, c.AddedFiles);
        Check.Equal(2, c.UpdatedFiles);
        Check.Equal(1, c.RemovedFiles);
        Check.Equal(1, c.Outputs.Length);
        Check.Equal("Patch.asset", Path.GetFileName(c.Outputs.Single()));
        string candidateDirectory = Path.Combine(fixture.Output, c.Name);
        Check.Equal(1, Directory.GetFiles(candidateDirectory, "*.asset", SearchOption.AllDirectories).Length);
        var changes = TagStore.ReadChanges(state, c.TagId);
        Check.Equal(4, changes.Length);
        Check.True(changes.All(change => change.Package == "Patch"));
        Check.True(changes.Contains(new FileChange("Patch", "assets/media/delete.bin", ChangeKind.Deleted)));
        var delta = TagStore.LoadDelta(state, c.TagId);
        var metadataUpdate = delta.Upserts.Single(file => file.Path == originalMeta.Path);
        Check.Equal(originalMeta.Package, metadataUpdate.Package);
        Check.Equal(originalMeta.Encoding, metadataUpdate.Encoding);
        Check.Equal(originalMeta.ContentHash, metadataUpdate.ContentHash);
        Check.True(originalMeta.MetadataHash != metadataUpdate.MetadataHash);
        var contentUpdate = delta.Upserts.Single(file => file.Path == "assets/core/change.bin");
        Check.Equal("Patch", contentUpdate.Package);
        Check.True(contentUpdate.Encoding.Contains(":Lz4:", StringComparison.Ordinal));
        using (var package = await PackageReader.OpenAsync(Path.Combine(fixture.Root, c.Outputs.Single()),
            new ReadOptions { Key = encryptedPatch ? TestKeys.Client : null }, true, default))
        {
            Check.Equal(131072, package.Header.BlockSize);
            Check.Equal(encryptedPatch, package.Header.Encrypted);
            Check.Equal(encryptedPatch, package.Header.Signed);
            Check.Equal(4L, package.Header.EntryCount);
            Check.Equal(EntryKind.MetadataOnly, package.Find(originalMeta.Path)!.Value.Kind);
            Check.Equal(EntryKind.Deleted, package.Find("assets/media/delete.bin")!.Value.Kind);
        }

        await Run(fixture.ProjectPath, "commit", c.Name);
        var acceptedC = TagStore.LoadBaseline(state);
        Check.Equal(originalKeep, Find(acceptedC, originalKeep.Path));
        Check.Equal(originalMeta.Encoding, Find(acceptedC, originalMeta.Path).Encoding);
        Check.Equal(originalMeta.Package, Find(acceptedC, originalMeta.Path).Package);
        Check.Equal(contentUpdate, Find(acceptedC, contentUpdate.Path));
        Check.True(acceptedC.Files.Values.All(file => file.Path != "assets/media/delete.bin"));
        var d = await Build(fixture, fixture.ProjectPath, "D-back-to-full");
        AssertEmpty(d);
        Check.Equal(c.TagId, d.BaseTag);
        Check.True((await Run(fixture.ProjectPath, "diff")).Contains("0 changes", StringComparison.Ordinal));
        Check.Equal(0, TagStore.LoadDelta(state, d.TagId).Upserts.Length);
        Check.Equal(0, TagStore.LoadDelta(state, d.TagId).Removed.Length);
        await Run(patchFile, "commit", d.Name);

        if (!encryptedPatch)
        {
            // A deliberate unsigned patch remains valid for indexing, but cannot
            // downgrade a collection opened with a pinned signing identity.
            var trusted = new ReadOptions { Key = TestKeys.Client };
            Check.Throws<InvalidDataException>(() => { using var bundle = QuireBundle.Open(fixture.Output, trusted); });
            await Check.ThrowsAsync<InvalidDataException>(async () => { using var bundle = await QuireBundle.OpenAsync(fixture.Output, trusted); });
            return;
        }

        foreach (bool asynchronous in new[] { false, true })
        {
            var keysRequested = new HashSet<string>(StringComparer.Ordinal);
            var options = new ReadOptions
            {
                VerifyPackageChain = true,
                KeyProvider = info =>
                {
                    keysRequested.Add(info.TagName);
                    return info.TagName == a.Name || info.TagName == c.Name ? TestKeys.Client : null;
                }
            };
            using var bundle = asynchronous ? await QuireBundle.OpenAsync(fixture.Output, options) : QuireBundle.Open(fixture.Output, options);
            Check.Equal(3, bundle.Packages.Count);
            Check.Equal(c.Name, bundle.LatestTag!.Name); // Empty candidates emit no package identity.
            Check.Equal(5, bundle.EnumerateFiles().Count());
            Check.True(!bundle.FileExists("assets/media/delete.bin"));
            using (var keep = bundle.Read(originalKeep.Path)) Check.Bytes([1, 2, 3], keep.Data.Span);
            using (var updated = await bundle.ReadAsync(contentUpdate.Path)) Check.Bytes(changed, updated.Data.Span);
            using (var metadata = bundle.Read(originalMeta.Path))
            {
                Check.Bytes([6, 7], metadata.Data.Span);
                Check.Equal(2L, metadata.Metadata.GetInt64("revision"));
            }
            using (var added = bundle.Read("assets/media/added.bin")) Check.Bytes([10, 11, 12], added.Data.Span);
            using (var stay = bundle.Read("assets/media/stay.bin")) Check.Bytes([9], stay.Data.Span);
            if (asynchronous) await bundle.VerifyAsync(); else bundle.Verify();
            Check.True(keysRequested.Contains(a.Name));
            Check.True(keysRequested.Contains(c.Name));
        }
    }

    private static FileState Find(BaselineState baseline, string path) => baseline.Files.Values.Single(file => file.Path == path);
    private static void AssertEmpty(BuildTag tag)
    {
        Check.Equal(0, tag.Outputs.Length);
        Check.Equal(0, tag.ContentChanges + tag.MetadataChanges + tag.DeletedChanges);
        Check.Equal(0, tag.AddedFiles + tag.UpdatedFiles + tag.RemovedFiles);
    }
    private static async Task<BuildTag> Build(Fixture fixture, string project, string name)
    {
        await Run(project, "package", "-tag", name);
        return TagStore.List(ProjectConfig.LoadStateDirectory(project)).Single(tag => tag.Name == name);
    }
    private static async Task<string> Run(string project, params string[] arguments)
    {
        var previous = Console.Out;
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        try
        {
            Console.SetOut(output);
            Check.Equal(0, await CommandLine.RunAsync([.. arguments, "-f", project], default));
            return output.ToString();
        }
        finally { Console.SetOut(previous); }
    }
    private static string FullConfiguration(string keyName) => $$"""
        version: 1
        project: test-project
        output: ./dist/{tag}
        suffix: asset
        compression: zlib
        compression-policy: always
        build:
          jobs: 1
          memory-mib: 32
          block-size: 65536
        secret-key: {{keyName}}
        packages:
          - name: Core
            sources:
              - directory: ./input/core
                prefix: assets/core
          - name: Media
            sources:
              - directory: ./input/media
                prefix: assets/media
        """;
    private static string PatchConfiguration(string keyName) => $$"""
        version: 1
        project: test-project
        output: ./dist/{tag}
        suffix: asset
        compression: lz4
        compression-policy: adaptive
        build:
          jobs: 1
          memory-mib: 32
          block-size: 131072
        secret-key: {{keyName}}
        packages:
          - name: Patch
            sources:
              - directory: ./input
                prefix: assets
        """;
}
