using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using Quire.Cli.Build;
using Quire.Internal;

namespace Quire.Cli.Tags;

internal sealed record FileState(string Package, string Path, long Length, ContentDigest ContentHash, ContentDigest? MetadataHash, string Encoding);
internal sealed record BuildTag(string TagId, DateTimeOffset BuildTimeUtc, string? BaseTag, string ConfigHash, string[] Outputs, int ContentChanges, int MetadataChanges, int DeletedChanges)
{
    internal string Name { get; init; } = TagId;
    internal int AddedFiles { get; init; }
    internal int UpdatedFiles { get; init; }
    internal int RemovedFiles { get; init; }
}
internal enum ChangeKind : byte { Added = 1, Updated = 2, Deleted = 3 }
internal sealed record FileChange(string Package, string Path, ChangeKind Kind);
internal sealed record FileKey(string Package, string Path);
internal sealed record StateDelta(FileState[] Upserts, FileKey[] Removed);
internal sealed record BaselineState(string[] CommittedTags, Dictionary<(string Package, string Path), FileState> Files)
{
    internal string? TagId => CommittedTags.Length == 0 ? null : CommittedTags[^1];
}

internal static class TagStore
{
    private const int TagHeaderSize = 100, BaselineHeaderSize = 24, DeltaHeaderSize = 32;
    private const string UpgradeMessage = "Unsupported build state format. Archive the old .quire directory and create a new build state.";

    internal static BuildTag Load(string state, string id)
    {
        id = NormalizeId(id);
        using var stream = Open(TagPath(state, id), 4096);
        using var reader = new BinaryReader(stream, AssetPath.Utf8);
        try { return ReadTagHeader(reader, id).Tag; }
        catch (Exception e) when (Malformed(e)) { throw Invalid("Tag header", e); }
    }

    internal static IEnumerable<BuildTag> List(string state)
    {
        RejectLegacyState(state);
        EnsureCommittedTagsPresent(state, ReadCommittedTags(state));
        string directory = Path.Combine(state, "tags");
        if (!Directory.Exists(directory)) yield break;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in Directory.EnumerateFiles(directory))
        {
            string filename = Path.GetFileName(path);
            if (filename.StartsWith('.') && filename.EndsWith(".tmp", StringComparison.Ordinal)) continue;
            if (filename.Length != 12 || !filename.EndsWith(".idx", StringComparison.Ordinal) ||
                !ValidId(filename[..8])) throw new InvalidDataException(UpgradeMessage);
            var tag = Load(state, filename[..8]);
            if (!names.Add(tag.Name)) throw new InvalidDataException("Duplicate build Tag name.");
            yield return tag;
        }
    }

    internal static string Resolve(string state, string nameOrId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nameOrId);
        string? named = null, exact = null, prefix = null;
        int prefixCount = 0;
        bool hexadecimal = nameOrId.Length <= 8 && nameOrId.All(char.IsAsciiHexDigit);
        foreach (var tag in List(state))
        {
            if (tag.Name.Equals(nameOrId, StringComparison.OrdinalIgnoreCase)) named = tag.TagId;
            if (!hexadecimal || !tag.TagId.StartsWith(nameOrId, StringComparison.OrdinalIgnoreCase)) continue;
            prefix ??= tag.TagId;
            prefixCount++;
            if (nameOrId.Length == 8) exact = tag.TagId;
        }
        return named ?? exact ?? (prefixCount switch
        {
            0 => throw new FileNotFoundException($"Build Tag '{nameOrId}' was not found."),
            1 => prefix!,
            _ => throw new ArgumentException($"Tag prefix '{nameOrId}' is ambiguous. Specify a full name or more ID characters.", nameof(nameOrId))
        });
    }

    internal static void ValidateName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (name.Length is < 1 or > 128 || name[0] == '-' || name is "." or ".." ||
            char.IsWhiteSpace(name[0]) || char.IsWhiteSpace(name[^1]) || name[^1] == '.' ||
            name.Equals(".quire", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) ||
            name.Any(c => char.IsControl(c) || c is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|') ||
            AssetPath.Utf8.GetByteCount(name) > 255)
            throw new ArgumentException("Tag names must be valid directory names of 1-128 characters and at most 255 UTF-8 bytes, without leading '-', surrounding whitespace, '.quire', or a '.tmp' suffix.", nameof(name));
        var stem = name.AsSpan();
        int dot = stem.IndexOf('.');
        if (dot >= 0) stem = stem[..dot];
        bool reserved = stem.Equals("CON", StringComparison.OrdinalIgnoreCase) || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
            (stem.Length == 4 && (stem[3] is >= '1' and <= '9' or '\u00b9' or '\u00b2' or '\u00b3') &&
                (stem[..3].Equals("COM", StringComparison.OrdinalIgnoreCase) || stem[..3].Equals("LPT", StringComparison.OrdinalIgnoreCase)));
        if (reserved) throw new ArgumentException("Tag names cannot use reserved device names.", nameof(name));
    }

    internal static void EnsureNameAvailable(string state, string name)
    {
        ValidateName(name);
        foreach (var tag in List(state))
            if (tag.Name.Equals(name, StringComparison.OrdinalIgnoreCase) || tag.TagId.Equals(name, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException($"Build Tag name '{name}' is already in use.", nameof(name));
    }

    internal static string CreateId(IEnumerable<string> existing, Func<uint>? generate = null)
    {
        var used = existing.ToHashSet(StringComparer.OrdinalIgnoreCase);
        Span<byte> random = stackalloc byte[sizeof(uint)];
        while (true)
        {
            uint value;
            if (generate is not null) value = generate();
            else { RandomNumberGenerator.Fill(random); value = BinaryPrimitives.ReadUInt32LittleEndian(random); }
            string id = PackageTag.Format(value);
            if (!used.Contains(id)) return id;
        }
    }

    // The commit ledger precedes the large file table, so listing history never reads it.
    internal static string[] ReadCommittedTags(string state)
    {
        RejectLegacyState(state);
        string path = Path.Combine(state, "baseline.idx");
        if (!File.Exists(path)) { EnsureNoLostBaseline(state); return []; }
        using var stream = Open(path, 4096);
        using var reader = new BinaryReader(stream, AssetPath.Utf8);
        try { return ReadBaselineHeader(reader).Committed; }
        catch (Exception e) when (Malformed(e)) { throw Invalid("baseline header", e); }
    }

    internal static BaselineState LoadBaseline(string state, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        RejectLegacyState(state);
        string path = Path.Combine(state, "baseline.idx");
        if (!File.Exists(path)) { EnsureNoLostBaseline(state); return new([], new()); }
        using var stream = Open(path);
        using var reader = new BinaryReader(stream, AssetPath.Utf8);
        try
        {
            long payloadEnd = VerifyChecksum(stream, token);
            var header = ReadBaselineHeader(reader, token);
            var files = new Dictionary<(string, string), FileState>(header.Count);
            StateIndexCodec.ReadFiles(reader, header.Count, header.Tables, files, null, token, payloadEnd);
            RequireEnd(stream, payloadEnd);
            return new(header.Committed, files);
        }
        catch (Exception e) when (Malformed(e)) { throw Invalid("baseline", e); }
    }

    internal static FileChange[] ReadChanges(string state, string id)
    {
        id = NormalizeId(id);
        using var stream = Open(TagPath(state, id));
        using var reader = new BinaryReader(stream, AssetPath.Utf8);
        try
        {
            var header = ReadTagHeader(reader, id);
            var changes = new FileChange[header.Count];
            StateIndexCodec.ReadKeys(reader, header.Count, header.Packages, changes, null, default);
            RequireEnd(stream);
            ValidateChangeCounts(header.Tag, changes);
            return changes;
        }
        catch (Exception e) when (Malformed(e)) { throw Invalid("Tag changes", e); }
    }

    internal static StateDelta LoadDelta(string state, string id) => ReadDelta(state, NormalizeId(id), out _, default);

    private static StateDelta ReadDelta(string state, string id, out string? parent, CancellationToken token)
    {
        using var stream = Open(PendingPath(state, id));
        using var reader = new BinaryReader(stream, AssetPath.Utf8);
        try
        {
            long payloadEnd = VerifyChecksum(stream, token);
            Span<byte> header = stackalloc byte[DeltaHeaderSize];
            stream.ReadExactly(header);
            RequireMagic(header[..8], "ASDELT01"u8);
            ulong parentCode = BinaryPrimitives.ReadUInt64LittleEndian(header[12..]);
            int count = BinaryPrimitives.ReadInt32LittleEndian(header[20..]);
            int removedCount = BinaryPrimitives.ReadInt32LittleEndian(header[24..]);
            int tables = BinaryPrimitives.ReadInt32LittleEndian(header[28..]);
            if (PackageTag.Format(BinaryPrimitives.ReadUInt32LittleEndian(header[8..])) != id || parentCode > (ulong)uint.MaxValue + 1 ||
                count < 0 || removedCount < 0 || count > Array.MaxLength || removedCount > Array.MaxLength ||
                (long)count * StateIndexCodec.FileRecordSize + (long)removedCount * StateIndexCodec.KeyRecordSize + 4 > payloadEnd - stream.Position)
                throw new InvalidDataException("Invalid candidate delta header.");
            parent = DecodeParent(parentCode);
            var upserts = new FileState[count];
            StateIndexCodec.ReadFiles(reader, count, tables, null, upserts, token, payloadEnd);
            int packages = reader.ReadInt32();
            var removed = new FileKey[removedCount];
            StateIndexCodec.ReadKeys(reader, removedCount, packages, null, removed, token, payloadEnd);
            RequireEnd(stream, payloadEnd);
            ValidateNoOverlap(upserts, removed);
            return new(upserts, removed);
        }
        catch (Exception e) when (Malformed(e)) { throw Invalid("candidate delta", e); }
    }

    internal static void WriteCandidate(string state, string tagDestination, string deltaDestination, BuildTag tag,
        IEnumerable<FileChange> changes, StateDelta delta, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        RejectLegacyState(state);
        ValidateTag(tag);
        EnsureNameAvailable(state, tag.Name);
        if (!tag.Name.Equals(tag.TagId, StringComparison.OrdinalIgnoreCase)) EnsureNameAvailable(state, tag.TagId);
        var sortedChanges = changes.ToArray();
        Array.Sort(sortedChanges, static (a, b) => StateIndexCodec.Compare(a.Package, a.Path, b.Package, b.Path));
        var sortedFiles = StateIndexCodec.SortFiles(delta.Upserts, token);
        var sortedRemoved = delta.Removed.ToArray();
        Array.Sort(sortedRemoved, static (a, b) => StateIndexCodec.Compare(a.Package, a.Path, b.Package, b.Path));
        ValidateChangeCounts(tag, sortedChanges);
        ValidateCandidate(sortedChanges, sortedFiles, sortedRemoved);
        var tables = StateIndexCodec.CreateFileTable(sortedFiles);
        var changePackages = StateIndexCodec.CreateKeyTable(sortedChanges, null);
        var removedPackages = StateIndexCodec.CreateKeyTable(null, sortedRemoved);
        bool tagOwned = false, deltaOwned = false;
        try
        {
            using (var stream = Create(deltaDestination))
            {
                deltaOwned = true;
                using var writer = new BinaryWriter(stream, AssetPath.Utf8, true);
                writer.Write("ASDELT01"u8);
                writer.Write(PackageTag.Parse(tag.TagId));
                writer.Write(EncodeParent(tag.BaseTag));
                writer.Write(sortedFiles.Length); writer.Write(sortedRemoved.Length); writer.Write(tables.Count);
                StateIndexCodec.WriteFiles(writer, sortedFiles, tables, token);
                writer.Write(removedPackages.Count);
                StateIndexCodec.WriteKeys(writer, null, sortedRemoved, removedPackages, token);
                writer.Flush(); AppendChecksum(stream, token); stream.Flush(true);
            }
            token.ThrowIfCancellationRequested();
            using (var stream = Create(tagDestination))
            {
                tagOwned = true;
                using var writer = new BinaryWriter(stream, AssetPath.Utf8, true);
                WriteTagHeader(writer, tag, sortedChanges.Length, changePackages.Count);
                StateIndexCodec.WriteKeys(writer, sortedChanges, null, changePackages, token);
                writer.Flush(); stream.Flush(true);
            }
            token.ThrowIfCancellationRequested();
        }
        catch
        {
            if (tagOwned) File.Delete(tagDestination);
            if (deltaOwned) File.Delete(deltaDestination);
            throw;
        }
    }

    internal static BuildTag Commit(string state, string id, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        id = NormalizeId(id);
        state = Path.TrimEndingDirectorySeparator(Path.GetFullPath(state));
        using var buildLock = BuildLock.Acquire(state);
        var committed = ReadCommittedTags(state);
        EnsureCommittedTagsPresent(state, committed);
        var tag = Load(state, id);
        string? head = committed.Length == 0 ? null : committed[^1];
        if (head == id)
        {
            TryCleanPending(state, id);
            return tag;
        }
        if (committed.Contains(id, StringComparer.Ordinal))
            throw new InvalidOperationException("This Tag was already committed before the current baseline; it cannot be committed again.");
        if (tag.BaseTag != head)
            throw new InvalidOperationException("The candidate was built from a different baseline. Build a new candidate from the current baseline.");
        string root = Path.GetDirectoryName(state)!;
        // Output records are relative to the YAML directory, above .quire/<project>/.
        if (Path.GetFileName(root).Equals(".quire", StringComparison.OrdinalIgnoreCase))
            root = Path.GetDirectoryName(root)!;
        foreach (string output in tag.Outputs)
        {
            token.ThrowIfCancellationRequested();
            string path = Path.GetFullPath(output, root);
            if (!File.Exists(path)) throw new FileNotFoundException("A candidate output is missing; restore it before committing.", path);
        }
        var delta = ReadDelta(state, id, out string? parent, token);
        if (parent != head) throw new InvalidDataException("The candidate delta references a different baseline.");
        var changes = ReadChanges(state, id);
        var candidate = ValidateCandidate(changes, delta.Upserts, delta.Removed);
        var baseline = LoadBaseline(state, token);
        foreach (var file in baseline.Files.Values)
        {
            token.ThrowIfCancellationRequested();
            if (!candidate.TryGetValue(file.Path, out var change)) continue;
            if (change.Kind == ChangeKind.Added || change.Existed)
                throw new InvalidDataException("Candidate changes do not match the current baseline.");
            // Moving changed content to a different output package must remove its old
            // storage key. Metadata-only upserts may retain the original content owner.
            if ((change.Kind == ChangeKind.Deleted || change.UpsertPackage != file.Package) &&
                !ContainsRemoval(delta.Removed, file.Package, file.Path))
                throw new InvalidDataException("The candidate leaves a stale baseline record for a changed resource.");
            candidate[file.Path] = change with { Existed = true };
        }
        foreach (var change in candidate.Values)
            if ((change.Kind != ChangeKind.Added) != change.Existed)
                throw new InvalidDataException("Candidate changes do not match the current baseline.");
        foreach (var key in delta.Removed)
        {
            token.ThrowIfCancellationRequested();
            if (!baseline.Files.Remove((key.Package, key.Path)))
                throw new InvalidDataException("A candidate removal does not exist in the current baseline.");
        }
        foreach (var file in delta.Upserts) baseline.Files[(file.Package, file.Path)] = file;
        string[] nextCommitted = new string[committed.Length + 1];
        committed.CopyTo(nextCommitted, 0); nextCommitted[^1] = id;
        WriteBaseline(state, new(nextCommitted, baseline.Files), token);
        TryCleanPending(state, id);
        return tag;
    }

    internal static void Remove(string state, string id)
    {
        id = NormalizeId(id);
        using var buildLock = BuildLock.Acquire(state);
        var committed = ReadCommittedTags(state);
        EnsureCommittedTagsPresent(state, committed);
        if (committed.Contains(id, StringComparer.Ordinal))
            throw new InvalidOperationException("Committed Tags cannot be removed.");
        Load(state, id);
        // Removing the discoverable Tag is the removal point. Failed cleanup may leave only an orphan delta.
        File.Delete(TagPath(state, id));
        TryCleanPending(state, id, removed: true);
    }

    private static void WriteBaseline(string state, BaselineState baseline, CancellationToken token)
    {
        var files = StateIndexCodec.SortFiles(baseline.Files.Values, token);
        var tables = StateIndexCodec.CreateFileTable(files);
        string target = Path.Combine(state, "baseline.idx");
        string temporary = Path.Combine(state, ".baseline-" + Guid.NewGuid().ToString("N") + ".tmp");
        bool owned = false;
        try
        {
            using (var stream = Create(temporary))
            {
                owned = true;
                using var writer = new BinaryWriter(stream, AssetPath.Utf8, true);
                writer.Write("ASBASE01"u8); writer.Write(baseline.CommittedTags.Length);
                writer.Write(files.Length); writer.Write(tables.Count); writer.Write(0);
                foreach (string id in baseline.CommittedTags) writer.Write(PackageTag.Parse(id));
                writer.Flush(); AppendChecksum(stream, token);
                StateIndexCodec.WriteFiles(writer, files, tables, token);
                writer.Flush(); AppendChecksum(stream, token); stream.Flush(true);
            }
            token.ThrowIfCancellationRequested();
            // This replacement is the sole commit point. History files never change afterwards.
            File.Move(temporary, target, true);
            owned = false;
        }
        finally { if (owned) File.Delete(temporary); }
    }

    private static (string[] Committed, int Count, int Tables) ReadBaselineHeader(BinaryReader reader, CancellationToken token = default)
    {
        var stream = reader.BaseStream;
        Span<byte> header = stackalloc byte[BaselineHeaderSize];
        stream.ReadExactly(header);
        RequireMagic(header[..8], "ASBASE01"u8);
        int committedCount = BinaryPrimitives.ReadInt32LittleEndian(header[8..]);
        int count = BinaryPrimitives.ReadInt32LittleEndian(header[12..]);
        int tables = BinaryPrimitives.ReadInt32LittleEndian(header[16..]);
        if (committedCount <= 0 || committedCount > Array.MaxLength || count < 0 || count > Array.MaxLength ||
            tables < 0 || tables > count || (tables == 0) != (count == 0) || BinaryPrimitives.ReadInt32LittleEndian(header[20..]) != 0 ||
            (long)committedCount * 4 + 32 + (long)count * StateIndexCodec.FileRecordSize + (long)tables * 10 > stream.Length - 32 - stream.Position)
            throw new InvalidDataException("Invalid baseline dimensions.");
        // The small ledger has its own digest, independently verifiable without reading file records.
        // Verify before allocating from its counts, including when the large baseline is not decoded.
        Span<byte> actual = stackalloc byte[32], expected = stackalloc byte[32];
        long ledgerEnd = BaselineHeaderSize + (long)committedCount * sizeof(uint);
        HashPayload((FileStream)stream, ledgerEnd, actual, token);
        stream.ReadExactly(expected);
        if (!CryptographicOperations.FixedTimeEquals(actual, expected))
            throw new InvalidDataException("Baseline commit ledger checksum mismatch.");
        stream.Position = BaselineHeaderSize;
        var ids = new string[committedCount];
        var unique = new HashSet<uint>();
        for (int i = 0; i < ids.Length; i++)
        {
            uint id = reader.ReadUInt32();
            if (!unique.Add(id)) throw new InvalidDataException("Duplicate committed Tag ID in baseline.");
            ids[i] = PackageTag.Format(id);
        }
        stream.Position = ledgerEnd + 32;
        return (ids, count, tables);
    }

    private static (BuildTag Tag, int Count, int Packages) ReadTagHeader(BinaryReader reader, string expectedId)
    {
        var stream = reader.BaseStream;
        Span<byte> bytes = stackalloc byte[TagHeaderSize];
        stream.ReadExactly(bytes[..8]);
        RequireMagic(bytes[..8], "ASTAG004"u8);
        stream.ReadExactly(bytes[8..]);
        string id = PackageTag.Format(BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]));
        long ticks = BinaryPrimitives.ReadInt64LittleEndian(bytes[12..]);
        ulong parentCode = BinaryPrimitives.ReadUInt64LittleEndian(bytes[20..]);
        int content = BinaryPrimitives.ReadInt32LittleEndian(bytes[60..]), metadata = BinaryPrimitives.ReadInt32LittleEndian(bytes[64..]);
        int deleted = BinaryPrimitives.ReadInt32LittleEndian(bytes[68..]), added = BinaryPrimitives.ReadInt32LittleEndian(bytes[72..]);
        int updated = BinaryPrimitives.ReadInt32LittleEndian(bytes[76..]), removed = BinaryPrimitives.ReadInt32LittleEndian(bytes[80..]);
        int outputs = BinaryPrimitives.ReadInt32LittleEndian(bytes[84..]), count = BinaryPrimitives.ReadInt32LittleEndian(bytes[88..]);
        int packages = BinaryPrimitives.ReadInt32LittleEndian(bytes[92..]), nameBytes = BinaryPrimitives.ReadInt32LittleEndian(bytes[96..]);
        if (id != expectedId || ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks || parentCode > (ulong)uint.MaxValue + 1 ||
            content < 0 || metadata < 0 || deleted < 0 || added < 0 || updated < 0 || removed < 0 ||
            outputs < 0 || outputs > Array.MaxLength || count < 0 || count > Array.MaxLength || packages < 0 || packages > count ||
            (count == 0) != (packages == 0) || (long)added + updated + removed != count || nameBytes is < 1 or > 512 ||
            nameBytes + (long)outputs * 5 + (long)packages * 5 + (long)count * StateIndexCodec.KeyRecordSize > stream.Length - stream.Position)
            throw new InvalidDataException("Invalid Tag header dimensions.");
        byte[] buffer = ArrayPool<byte>.Shared.Rent(StateIndexCodec.MaxStringBytes);
        try
        {
            stream.ReadExactly(buffer.AsSpan(0, nameBytes));
            string name = AssetPath.Utf8.GetString(buffer.AsSpan(0, nameBytes));
            ValidateName(name);
            var outputPaths = new string[outputs];
            for (int i = 0; i < outputs; i++) outputPaths[i] = StateIndexCodec.ReadString(reader, buffer, StateIndexCodec.MaxStringBytes);
            var tag = new BuildTag(id, new DateTimeOffset(ticks, TimeSpan.Zero), DecodeParent(parentCode), Convert.ToHexString(bytes.Slice(28, 32)),
                outputPaths, content, metadata, deleted) { Name = name, AddedFiles = added, UpdatedFiles = updated, RemovedFiles = removed };
            ValidateTag(tag);
            if ((long)packages * 5 + (long)count * StateIndexCodec.KeyRecordSize > stream.Length - stream.Position)
                throw new InvalidDataException("Truncated Tag changes.");
            return (tag, count, packages);
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    private static void WriteTagHeader(BinaryWriter writer, BuildTag tag, int count, int packages)
    {
        writer.Write("ASTAG004"u8); writer.Write(PackageTag.Parse(tag.TagId)); writer.Write(tag.BuildTimeUtc.UtcTicks);
        writer.Write(EncodeParent(tag.BaseTag)); writer.Write(Convert.FromHexString(tag.ConfigHash));
        writer.Write(tag.ContentChanges); writer.Write(tag.MetadataChanges); writer.Write(tag.DeletedChanges);
        writer.Write(tag.AddedFiles); writer.Write(tag.UpdatedFiles); writer.Write(tag.RemovedFiles);
        writer.Write(tag.Outputs.Length); writer.Write(count); writer.Write(packages);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(StateIndexCodec.MaxStringBytes);
        try
        {
            StateIndexCodec.WriteString(writer, tag.Name, buffer);
            foreach (string output in tag.Outputs) StateIndexCodec.WriteString(writer, output, buffer);
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    private static void ValidateTag(BuildTag tag)
    {
        if (!ValidId(tag.TagId) || (tag.BaseTag is not null && !ValidId(tag.BaseTag)) || tag.BaseTag == tag.TagId ||
            tag.ConfigHash.Length != 64 || !tag.ConfigHash.All(char.IsAsciiHexDigit) || tag.ContentChanges < 0 ||
            tag.MetadataChanges < 0 || tag.DeletedChanges < 0 || tag.AddedFiles < 0 || tag.UpdatedFiles < 0 || tag.RemovedFiles < 0)
            throw new ArgumentException("Invalid Tag metadata.", nameof(tag));
        ValidateName(tag.Name);
        foreach (string output in tag.Outputs)
            if (string.IsNullOrEmpty(output) || output.Contains('\0') || Path.IsPathRooted(output) || AssetPath.Utf8.GetByteCount(output) > StateIndexCodec.MaxStringBytes)
                throw new ArgumentException("Tag outputs must be nonempty relative paths.", nameof(tag));
    }

    private static void ValidateChangeCounts(BuildTag tag, FileChange[] changes)
    {
        int added = 0, updated = 0, removed = 0;
        foreach (var change in changes)
            switch (change.Kind)
            {
                case ChangeKind.Added: added++; break;
                case ChangeKind.Updated: updated++; break;
                case ChangeKind.Deleted: removed++; break;
                default: throw new InvalidDataException("Invalid change kind.");
            }
        if (added != tag.AddedFiles || updated != tag.UpdatedFiles || removed != tag.RemovedFiles)
            throw new InvalidDataException("Tag change counts do not match its records.");
    }

    private readonly record struct CandidateChange(ChangeKind Kind, string? UpsertPackage = null, bool HasRemoval = false, bool Existed = false);

    private static Dictionary<string, CandidateChange> ValidateCandidate(FileChange[] changes, FileState[] files, FileKey[] removed)
    {
        // History records the logical change and its new output package. Pending state
        // also carries physical key removals when a changed resource moves packages.
        var paths = new Dictionary<string, CandidateChange>(changes.Length, StringComparer.Ordinal);
        for (int i = 0; i < changes.Length; i++)
        {
            var change = changes[i];
            StateIndexCodec.ValidateKey(change.Package, change.Path);
            if (i != 0 && StateIndexCodec.Compare(changes[i - 1].Package, changes[i - 1].Path, change.Package, change.Path) >= 0)
                throw new InvalidDataException("Candidate changes must be unique and sorted.");
            if (!Enum.IsDefined(change.Kind) || !paths.TryAdd(change.Path, new(change.Kind)))
                throw new InvalidDataException("Candidate changes must have unique logical resource paths.");
        }
        foreach (var file in files)
        {
            if (!paths.TryGetValue(file.Path, out var change) || change.Kind == ChangeKind.Deleted || change.UpsertPackage is not null)
                throw new InvalidDataException("Candidate upserts do not match its logical changes.");
            paths[file.Path] = change with { UpsertPackage = file.Package };
        }
        for (int i = 0; i < removed.Length; i++)
        {
            var key = removed[i];
            StateIndexCodec.ValidateKey(key.Package, key.Path);
            if (i != 0 && StateIndexCodec.Compare(removed[i - 1].Package, removed[i - 1].Path, key.Package, key.Path) >= 0)
                throw new InvalidDataException("Candidate removals must be unique and sorted.");
            if (!paths.TryGetValue(key.Path, out var change) || change.Kind == ChangeKind.Added)
                throw new InvalidDataException("Candidate removals do not match its logical changes.");
            paths[key.Path] = change with { HasRemoval = true };
        }
        foreach (var change in paths.Values)
            if (change.Kind == ChangeKind.Deleted ? !change.HasRemoval : change.UpsertPackage is null)
                throw new InvalidDataException("Missing candidate details.");
        ValidateNoOverlap(files, removed);
        return paths;
    }

    private static bool ContainsRemoval(FileKey[] removed, string package, string path)
    {
        int low = 0, high = removed.Length - 1;
        while (low <= high)
        {
            int middle = low + ((high - low) >> 1);
            int order = StateIndexCodec.Compare(removed[middle].Package, removed[middle].Path, package, path);
            if (order == 0) return true;
            if (order < 0) low = middle + 1; else high = middle - 1;
        }
        return false;
    }

    private static void ValidateNoOverlap(FileState[] files, FileKey[] removed)
    {
        int i = 0, j = 0;
        while (i < files.Length && j < removed.Length)
        {
            int order = StateIndexCodec.Compare(files[i].Package, files[i].Path, removed[j].Package, removed[j].Path);
            if (order == 0) throw new InvalidDataException("A candidate cannot both upsert and remove the same file.");
            if (order < 0) i++; else j++;
        }
    }

    private static void TryCleanPending(string state, string id, bool removed = false)
    {
        try { File.Delete(PendingPath(state, id)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine(removed
                ? $"Candidate {id} was removed; pending-state cleanup could not finish: {e.Message}"
                : $"Tag {id} is committed; pending-state cleanup could not finish. Commit it again to retry cleanup: {e.Message}");
        }
    }

    private static void EnsureNoLostBaseline(string state)
    {
        string directory = Path.Combine(state, "tags");
        if (!Directory.Exists(directory)) return;
        foreach (string path in Directory.EnumerateFiles(directory, "*.idx"))
        {
            string id = Path.GetFileNameWithoutExtension(path);
            if (!File.Exists(PendingPath(state, id)))
                throw new InvalidDataException($"The baseline is missing or a candidate is incomplete. Restore '{Path.Combine(state, "baseline.idx")}' and its matching Tag/pending records before changing build state.");
        }
    }

    private static void EnsureCommittedTagsPresent(string state, string[] ids)
    {
        foreach (string id in ids)
            if (!File.Exists(TagPath(state, id)))
                throw new InvalidDataException($"Committed Tag '{id}' is missing. Restore its history record '{TagPath(state, id)}'.");
    }

    private static void RejectLegacyState(string state)
    {
        string tags = Path.Combine(state, "tags");
        if (File.Exists(Path.Combine(state, "paths.idx")) ||
            (Directory.Exists(tags) && Directory.EnumerateDirectories(tags).Any()))
            throw new InvalidDataException(UpgradeMessage);
    }

    private static void RequireMagic(ReadOnlySpan<byte> actual, ReadOnlySpan<byte> expected)
    { if (!actual.SequenceEqual(expected)) throw new InvalidDataException(UpgradeMessage); }
    private static void RequireEnd(Stream stream)
    { if (stream.Position != stream.Length) throw new InvalidDataException("Trailing state index bytes."); }
    private static void RequireEnd(Stream stream, long payloadEnd)
    { if (stream.Position != payloadEnd) throw new InvalidDataException("Trailing state index bytes."); }

    private static long VerifyChecksum(FileStream stream, CancellationToken token)
    {
        long payloadEnd = stream.Length - 32;
        if (payloadEnd < 8) throw new InvalidDataException("Truncated state index checksum.");
        Span<byte> actual = stackalloc byte[32], expected = stackalloc byte[32];
        HashPayload(stream, payloadEnd, actual, token);
        stream.ReadExactly(expected);
        if (!CryptographicOperations.FixedTimeEquals(actual, expected))
            throw new InvalidDataException("State index checksum mismatch.");
        stream.Position = 0;
        return payloadEnd;
    }

    private static void AppendChecksum(FileStream stream, CancellationToken token)
    {
        long length = stream.Position;
        stream.Flush();
        Span<byte> digest = stackalloc byte[32];
        HashPayload(stream, length, digest, token);
        stream.Write(digest);
    }

    private static void HashPayload(FileStream stream, long length, Span<byte> digest, CancellationToken token)
    {
        stream.Position = 0;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(65536);
        try
        {
            while (length > 0)
            {
                token.ThrowIfCancellationRequested();
                int count = (int)Math.Min(length, 65536);
                stream.ReadExactly(buffer.AsSpan(0, count));
                hash.AppendData(buffer.AsSpan(0, count));
                length -= count;
            }
            hash.GetHashAndReset(digest);
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }
    private static bool Malformed(Exception e) => e is EndOfStreamException or ArgumentException or OverflowException;
    private static InvalidDataException Invalid(string kind, Exception e) => new($"Invalid or truncated {kind} index.", e);
    private static bool ValidId(string id) => PackageTag.IsValid(id) && id == id.ToLowerInvariant();
    private static string NormalizeId(string id) => PackageTag.Format(PackageTag.Parse(id));
    private static ulong EncodeParent(string? id) => id is null ? 0 : (ulong)PackageTag.Parse(id) + 1;
    private static string? DecodeParent(ulong code) => code == 0 ? null : PackageTag.Format((uint)(code - 1));
    private static string TagPath(string state, string id) => Path.Combine(state, "tags", id + ".idx");
    private static string PendingPath(string state, string id) => Path.Combine(state, "pending", id + ".idx");
    private static FileStream Open(string path, int bufferSize = 65536) => new(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, bufferSize, FileOptions.SequentialScan);
    private static FileStream Create(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        return new(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 65536, FileOptions.SequentialScan);
    }
}
