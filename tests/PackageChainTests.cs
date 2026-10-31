using System.Buffers.Binary;
using System.Security.Cryptography;
using Quire;
using Quire.Cli.Build;
using Quire.Cli.Configuration;
using Quire.Cli.Tags;
using Quire.Internal;

internal static class PackageChainTests
{
    internal static void Register(Action<string, Func<Task>> test)
    {
        foreach (bool encrypted in new[] { false, true })
        {
            bool useEncryption = encrypted;
            test($"Package chain verification checks each present Tag by UID, encrypted={encrypted}", () => MembershipAsync(useEncryption));
            test($"Package identities and manifests reject corruption independently of chain verification, encrypted={encrypted}", () => IntegrityAsync(useEncryption));
        }
        test("Empty package roots have no latest Tag with either verification setting", EmptyAsync);
    }

    private static async Task MembershipAsync(bool encrypted)
    {
        using var f = new Fixture(false, encrypted);
        foreach (string name in new[] { "first", "second", "third" }) f.Put(name + "/item.bin", [1]);
        var project = Project(f, "first", "second", "third");
        var a = await ProjectBuilder.BuildAsync(project, "A", f.BuildOptions, default);
        TagStore.Commit(project.StateDirectory, a.TagId);
        f.Put("first/item.bin", [2]);
        f.Put("second/item.bin", [3]);
        var b = await ProjectBuilder.BuildAsync(project, "B", f.BuildOptions, default);
        Check.Equal(3, a.Outputs.Length);
        Check.Equal(2, b.Outputs.Length);
        string a1 = Output(f, a, "first"), a2 = Output(f, a, "second"), a3 = Output(f, a, "third");
        string b1 = Output(f, b, "first"), b2 = Output(f, b, "second");
        var checkedOptions = f.ReadOptions with { VerifyPackageChain = true };
        Check.True(!new ReadOptions().VerifyPackageChain);

        string single = CopySet(f, "single", (b1, "first.asset"));
        await MissingAsync(single, checkedOptions, b.Name, b2);
        await OpensAsync(single, f.ReadOptions, b, 1);
        using (var incomplete = QuireBundle.Open(single, f.ReadOptions))
        {
            using var data = incomplete.Read("assets/first/item.bin");
            Check.Bytes([2], data.Data.Span);
            Check.True(!incomplete.FileExists("assets/second/item.bin"));
        }

        string bOnly = CopySet(f, "b-only", (b1, "first.asset"), (b2, "second.asset"));
        await OpensAsync(bOnly, checkedOptions, b, 2); // B does not require any of its parent's A packages.
        string partialA = CopySet(f, "partial-a", (b1, "B/first.asset"), (b2, "B/second.asset"), (a1, "A/first.asset"));
        await MissingAsync(partialA, checkedOptions, a.Name, a2, a3);
        await OpensAsync(partialA, f.ReadOptions, b, 3);
        await OpensAsync(f.Output, checkedOptions, b, 5);
        using (var complete = QuireBundle.Open(f.Output, checkedOptions))
        {
            Check.Equal(5, complete.Packages.Select(package => package.PackageUid).Distinct().Count());
            Check.Equal(2, complete.Packages.Select(package => package.TagUid).Distinct().Count());
            Check.True(complete.Packages.All(package => package.PackageUid != Guid.Empty && package.TagUid != Guid.Empty));
            Check.True(complete.Packages.Where(package => package.TagName == a.Name).All(package => package.TagId == a.TagId));
            Check.True(complete.Packages.Where(package => package.TagName == b.Name).All(package => package.TagId == b.TagId));
        }

        string renamed = CopySet(f, "renamed", (b1, "z/random-name.asset"), (b2, "a/another-name.asset"));
        await OpensAsync(renamed, checkedOptions, b, 2);
        using (var before = QuireBundle.Open(bOnly, checkedOptions))
        using (var after = QuireBundle.Open(renamed, checkedOptions))
            Check.True(before.Packages.Select(package => package.PackageUid).ToHashSet().SetEquals(after.Packages.Select(package => package.PackageUid)));

        // All of A is present, but A's second.asset cannot stand in for B's same-named member.
        string impostor = CopySet(f, "impostor", (a1, "A/first.asset"), (a3, "A/third.asset"),
            (b1, "B/first.asset"), (a2, "B/second.asset"));
        await MissingAsync(impostor, checkedOptions, b.Name, b2);
        string duplicates = CopySet(f, "duplicates", (b1, "first.asset"), (b2, "second.asset"), (b1, "copied.asset"));
        await RejectsAsync(duplicates, checkedOptions);
        await OpensAsync(duplicates, f.ReadOptions, b, 3);
    }

    private static async Task IntegrityAsync(bool encrypted)
    {
        using var f = new Fixture(false, encrypted);
        f.Put("first/item.bin", [1, 2]);
        f.Put("second/item.bin", [3, 4]);
        var tag = await ProjectBuilder.BuildAsync(Project(f, "first", "second"), "manifest-test", f.BuildOptions, default);
        string first = Output(f, tag, "first"), second = Output(f, tag, "second");
        string directory = CopySet(f, "integrity", (first, "first.asset"), (second, "second.asset"));
        string file = Path.Combine(directory, "first.asset");
        byte[] original = File.ReadAllBytes(file);
        var header = PackageHeader.Decode(original, original.Length);
        byte[] manifest = original.AsSpan(PackageHeader.Size, header.ManifestLength).ToArray();
        var parsed = PackageManifest.Decode(manifest, header.PackageUid, header.ManifestHash);
        Check.Equal(2, parsed.Entries.Length);
        byte[] key = BlockCipher.DeriveKey(header, f.ReadOptions.Key);
        using var signing = encrypted ? TestKeys.Create() : null;
        try
        {
            async Task RejectBoth(byte[] bytes)
            {
                File.WriteAllBytes(file, bytes);
                foreach (bool verify in new[] { false, true })
                    await RejectsAsync(directory, f.ReadOptions with { VerifyPackageChain = verify });
            }
            byte[] ChangedHeader(Func<PackageHeader, PackageHeader> change)
            {
                byte[] bytes = (byte[])original.Clone();
                change(header).Encode(key, signing).CopyTo(bytes, 0);
                return bytes;
            }
            byte[] ChangedManifest(Action<byte[]> change, bool refreshHash = true)
            {
                byte[] payload = (byte[])manifest.Clone(); change(payload);
                byte[] bytes = (byte[])original.Clone();
                payload.CopyTo(bytes, PackageHeader.Size);
                if (refreshHash)
                    (header with { ManifestHash = new ContentDigest(SHA256.HashData(payload)) }).Encode(key, signing).CopyTo(bytes, 0);
                return bytes;
            }

            byte[] badHeader = (byte[])original.Clone();
            badHeader[PackageHeader.TagNameOffset] ^= 1;
            await RejectBoth(badHeader);
            await RejectBoth(ChangedHeader(value => value with { PackageUid = Guid.Empty }));
            await RejectBoth(ChangedHeader(value => value with { TagUid = Guid.Empty }));
            await RejectBoth(ChangedManifest(bytes => bytes[^1] ^= 1, refreshHash: false));
            await RejectBoth(ChangedManifest(bytes => BinaryPrimitives.WriteInt32LittleEndian(bytes, 0)));
            await RejectBoth(ChangedManifest(bytes => BinaryPrimitives.WriteInt32LittleEndian(bytes, int.MaxValue)));
            await RejectBoth(ChangedManifest(bytes => bytes[4 + 18] = 0xff));
            int secondEntry = 4 + 18 + BinaryPrimitives.ReadUInt16LittleEndian(manifest.AsSpan(4 + 16));
            await RejectBoth(ChangedManifest(bytes => bytes.AsSpan(4, 16).CopyTo(bytes.AsSpan(secondEntry, 16))));
            int ownEntry = new Guid(manifest.AsSpan(4, 16)) == header.PackageUid ? 4 : secondEntry;
            await RejectBoth(ChangedManifest(bytes => Guid.NewGuid().TryWriteBytes(bytes.AsSpan(ownEntry, 16))));

            // The file is valid on its own, but peers declaring the same Tag must agree on its manifest and metadata.
            int otherEntry = ownEntry == 4 ? secondEntry : 4;
            await RejectBoth(ChangedManifest(bytes => Guid.NewGuid().TryWriteBytes(bytes.AsSpan(otherEntry, 16))));
            await RejectBoth(ChangedHeader(value => value with { TagName = "different-name" }));
            await RejectBoth(ChangedHeader(value => value with { BuildTicks = value.BuildTicks + 1 }));
            File.WriteAllBytes(file, original);
            // first.asset populates the shared manifest cache before second.asset is authenticated.
            string laterFile = Path.Combine(directory, "second.asset");
            byte[] laterOriginal = File.ReadAllBytes(laterFile);
            var laterHeader = PackageHeader.Decode(laterOriginal, laterOriginal.Length);
            byte[] laterCorrupted = (byte[])laterOriginal.Clone();
            laterCorrupted[PackageHeader.Size + laterHeader.ManifestLength - 1] ^= 1;
            File.WriteAllBytes(laterFile, laterCorrupted);
            foreach (bool verify in new[] { false, true })
                foreach (bool asynchronous in new[] { false, true })
                {
                    var error = await RejectionAsync(directory, f.ReadOptions with { VerifyPackageChain = verify }, asynchronous);
                    Check.True(error.Message.Contains(laterFile, StringComparison.Ordinal), "Manifest checksum failures must identify the damaged package path.");
                }
            File.WriteAllBytes(laterFile, laterOriginal);
            await OpensAsync(directory, f.ReadOptions with { VerifyPackageChain = true }, tag, 2);
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    private static async Task EmptyAsync()
    {
        using var f = new Fixture(false);
        Directory.CreateDirectory(f.Output);
        foreach (bool verify in new[] { false, true })
            foreach (bool asynchronous in new[] { false, true })
            {
                var options = new ReadOptions { VerifyPackageChain = verify };
                using var bundle = asynchronous ? await QuireBundle.OpenAsync(f.Output, options) : QuireBundle.Open(f.Output, options);
                Check.True(bundle.LatestTag is null);
                Check.Equal(0, bundle.Packages.Count);
            }
    }

    private static ProjectConfig Project(Fixture fixture, params string[] names) => fixture.Config() with
    {
        Packages = names.Select(name => new PackageConfig(name, CompressionAlgorithm.None,
            [new SourceConfig(Path.Combine(fixture.Input, name), "assets/" + name, [], [])])).ToArray()
    };
    private static string Output(Fixture fixture, BuildTag tag, string package) =>
        Path.Combine(fixture.Root, tag.Outputs.Single(path => Path.GetFileName(path) == package + ".asset"));
    private static string CopySet(Fixture fixture, string name, params (string Source, string Relative)[] files)
    {
        string directory = Path.Combine(fixture.Root, "sets", name);
        Directory.CreateDirectory(directory);
        foreach (var (source, relative) in files)
        {
            string destination = Path.Combine(directory, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(source, destination);
        }
        return directory;
    }
    private static async Task OpensAsync(string directory, ReadOptions options, BuildTag expected, int count)
    {
        foreach (bool asynchronous in new[] { false, true })
        {
            using var bundle = asynchronous ? await QuireBundle.OpenAsync(directory, options) : QuireBundle.Open(directory, options);
            Check.Equal(count, bundle.Packages.Count);
            if (bundle.LatestTag is not { } latest) throw new Exception("A nonempty package set must expose its latest Tag.");
            Check.Equal(expected.Name, latest.Name);
            Check.Equal(expected.BuildTimeUtc, latest.BuildTimeUtc);
            Check.True(latest.TagUid != Guid.Empty);
            Check.True(bundle.Packages.Where(package => package.TagName == expected.Name).All(package => package.TagUid == latest.TagUid));
        }
    }
    private static async Task MissingAsync(string directory, ReadOptions options, string tagName, params string[] files)
    {
        var missing = files.Select(file =>
        {
            byte[] bytes = File.ReadAllBytes(file);
            return (Name: Path.GetFileName(file), Uid: PackageHeader.Decode(bytes, bytes.Length).PackageUid);
        }).ToArray();
        foreach (bool asynchronous in new[] { false, true })
        {
            var error = await RejectionAsync(directory, options, asynchronous);
            Check.True(error.Message.Contains(tagName, StringComparison.Ordinal), "Missing-package errors must identify the affected Tag.");
            foreach (var (name, uid) in missing)
            {
                Check.True(error.Message.Contains(name, StringComparison.Ordinal), $"Missing-package errors must identify original filename {name}.");
                Check.True(error.Message.Contains(uid.ToString(), StringComparison.Ordinal), $"Missing-package errors must identify PackageUid {uid}.");
            }
        }
    }
    private static async Task RejectsAsync(string directory, ReadOptions options)
    {
        foreach (bool asynchronous in new[] { false, true })
            _ = await RejectionAsync(directory, options, asynchronous);
    }
    private static async Task<InvalidDataException> RejectionAsync(string directory, ReadOptions options, bool asynchronous)
    {
        try
        {
            using var bundle = asynchronous ? await QuireBundle.OpenAsync(directory, options) : QuireBundle.Open(directory, options);
            throw new Exception("An invalid package set was accepted.");
        }
        catch (InvalidDataException error) { return error; }
    }
}
