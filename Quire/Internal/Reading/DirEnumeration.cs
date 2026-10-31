using System;
using System.Buffers;
using System.Threading;
using System.Threading.Tasks;

namespace Quire.Internal;

// One directory lookup and child traversal, or one bounded merge of package ranges.
// Shared ownership also makes copies of the public struct enumerators safe to dispose.
internal sealed class DirEnumeration : IDisposable
{
    private QuireBundle? _owner;
    private MergedIndex? _index;
    private readonly PackageReader[] _packages;
    private readonly string _directory;
    private readonly CancellationToken _token;
    private readonly bool _synchronousReads;
    private bool _initialized;
    private int _nextDirectory = -1;
    private int _nextFile = -1;
    private Cursor[]? _heap;
    private int _count;
    private int _prefixLength;
    private byte[]? _scratch, _path, _upperBound;

    internal AssetEntryInfo Current { get; private set; }

    internal DirEnumeration(QuireBundle owner, PackageReader[] packages, string directory, bool asynchronous, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(owner);
        owner.AcquireReference();
        _owner = owner;
        _packages = packages;
        _directory = directory;
        _token = token;
        _index = owner.MergedIndex;
        _synchronousReads = !asynchronous || (_index is not null ? !_index.HasPagedPaths : AllResident(packages));
        if (!asynchronous)
        {
            try { Initialize(); }
            catch { Dispose(); throw; }
        }
    }

    private static bool AllResident(PackageReader[] packages)
    {
        foreach (var package in packages)
            if (package.Header.EntryCount != 0 && package.Directory.Resident.IsEmpty)
                return false;
        return true;
    }

    private void Initialize()
    {
        _initialized = true;
        Span<byte> prefix = stackalloc byte[4097];
        _prefixLength = AssetPath.Utf8.GetBytes(_directory, prefix);
        if (_index is not null)
        {
            InitializeChildren(_index.FindDirectory(prefix[.._prefixLength]));
            return;
        }
        if (_prefixLength != 0)
            prefix[_prefixLength++] = (byte)'/';
        prefix = prefix[.._prefixLength];
        if (_packages.Length != 0)
            _heap = ArrayPool<Cursor>.Shared.Rent(_packages.Length);
        for (int order = 0; order < _packages.Length; order++)
        {
            _token.ThrowIfCancellationRequested();
            var package = _packages[order];
            long start = 0, end = package.Header.EntryCount;
            if (!prefix.IsEmpty)
            {
                start = package.LowerBound(prefix, 0, end);
                prefix[^1]++;
                end = package.LowerBound(prefix, start, end);
                prefix[^1]--;
            }
            if (start == end)
                continue;
            int slot = _count++;
            _heap![slot] = new Cursor(package, order, end);
            ReadCursor(slot, start);
            SiftUp(slot);
        }
        if (_count == 0)
            Dispose();
    }

    private async ValueTask InitializeAsync()
    {
        _initialized = true;
        _scratch = ArrayPool<byte>.Shared.Rent(4096);
        _upperBound = ArrayPool<byte>.Shared.Rent(4097);
        _prefixLength = AssetPath.Utf8.GetBytes(_directory, _upperBound);
        if (_index is not null)
        {
            int directory = await _index.FindDirectoryAsync(_upperBound.AsMemory(0, _prefixLength), _scratch, _token).ConfigureAwait(false);
            ReturnBuffer(ref _upperBound);
            InitializeChildren(directory);
            return;
        }
        _path = ArrayPool<byte>.Shared.Rent(4096);
        if (_prefixLength != 0)
            _upperBound[_prefixLength++] = (byte)'/';
        if (_packages.Length != 0)
            _heap = ArrayPool<Cursor>.Shared.Rent(_packages.Length);
        for (int order = 0; order < _packages.Length; order++)
        {
            _token.ThrowIfCancellationRequested();
            var package = _packages[order];
            long start = 0, end = package.Header.EntryCount;
            if (_prefixLength != 0)
            {
                var prefix = _upperBound.AsMemory(0, _prefixLength);
                start = await package.LowerBoundAsync(prefix, 0, end, _scratch, _token).ConfigureAwait(false);
                _upperBound[_prefixLength - 1]++;
                end = await package.LowerBoundAsync(prefix, start, end, _scratch, _token).ConfigureAwait(false);
                _upperBound[_prefixLength - 1]--;
            }
            if (start == end)
                continue;
            int slot = _count++;
            _heap![slot] = new Cursor(package, order, end);
            await ReadCursorAsync(slot, start).ConfigureAwait(false);
            SiftUp(slot);
        }
        if (_count == 0)
            Dispose();
    }

    private void InitializeChildren(int directory)
    {
        if (directory >= 0)
        {
            ref readonly var node = ref _index!.Directories[directory];
            _nextDirectory = node.FirstChild;
            _nextFile = node.FirstDirectFile;
        }
        if (_nextDirectory < 0 && _nextFile < 0)
            Dispose();
    }

    // Directory SourceFile positions already interleave with direct file positions in UTF-8 order.
    // No path decoding/comparison, extra sorting or second directory lookup is needed.
    private bool DirectoryIsNext => _nextDirectory >= 0 &&
        (_nextFile < 0 || _index!.Directories[_nextDirectory].SourceFile < _nextFile);

    internal AssetEntryInfo[] ReadAll()
    {
        AssetEntryInfo[]? buffer = null;
        try
        {
            _token.ThrowIfCancellationRequested();
            if (_owner is null)
                return Array.Empty<AssetEntryInfo>();
            if (!_initialized)
                Initialize();
            if (_owner is null)
                return Array.Empty<AssetEntryInfo>();
            if (_index is not null)
            {
                // Count only integer child links. Paths are read once, while filling the
                // final array; no temporary collection or enumeration wrapper is needed.
                int count = CountIndexedEntries();
                if (count == 0)
                    return Array.Empty<AssetEntryInfo>();
                var result = new AssetEntryInfo[count];
                for (int position = 0; position < count; position++)
                {
                    if (!MoveNext())
                        throw new InvalidOperationException("The directory listing ended before its indexed child count.");
                    result[position] = Current;
                }
                _token.ThrowIfCancellationRequested();
                return result;
            }

            int written = 0;
            while (MoveNext())
                AppendEntry(ref buffer, ref written, Current);
            _token.ThrowIfCancellationRequested();
            return CopyEntries(buffer, written);
        }
        finally
        {
            if (buffer is not null)
                ArrayPool<AssetEntryInfo>.Shared.Return(buffer, clearArray: true);
            Dispose();
        }
    }

    internal ValueTask<AssetEntryInfo[]> ReadAllAsync()
    {
        if (!_synchronousReads)
            return ReadAllAsyncCore();
        try { return new(ReadAll()); }
        catch (Exception exception) { return ValueTask.FromException<AssetEntryInfo[]>(exception); }
    }

    private async ValueTask<AssetEntryInfo[]> ReadAllAsyncCore()
    {
        AssetEntryInfo[]? buffer = null;
        try
        {
            _token.ThrowIfCancellationRequested();
            if (_owner is null)
                return Array.Empty<AssetEntryInfo>();
            if (!_initialized)
                await InitializeAsync().ConfigureAwait(false);
            if (_owner is null)
                return Array.Empty<AssetEntryInfo>();
            if (_index is not null)
            {
                int count = CountIndexedEntries();
                if (count == 0)
                    return Array.Empty<AssetEntryInfo>();
                var result = new AssetEntryInfo[count];
                for (int position = 0; position < count; position++)
                {
                    if (!await MoveNextAsync().ConfigureAwait(false))
                        throw new InvalidOperationException("The directory listing ended before its indexed child count.");
                    result[position] = Current;
                }
                _token.ThrowIfCancellationRequested();
                return result;
            }

            int written = 0;
            while (await MoveNextAsync().ConfigureAwait(false))
                AppendEntry(ref buffer, ref written, Current);
            _token.ThrowIfCancellationRequested();
            return CopyEntries(buffer, written);
        }
        finally
        {
            if (buffer is not null)
                ArrayPool<AssetEntryInfo>.Shared.Return(buffer, clearArray: true);
            Dispose();
        }
    }

    private int CountIndexedEntries()
    {
        int count = 0;
        for (int directory = _nextDirectory; directory >= 0; directory = _index!.Directories[directory].NextSibling)
        {
            _token.ThrowIfCancellationRequested();
            CheckArrayCapacity(count);
            count++;
        }
        for (int file = _nextFile; file >= 0; file = _index!.NextFile[file])
        {
            _token.ThrowIfCancellationRequested();
            CheckArrayCapacity(count);
            count++;
        }
        return count;
    }

    private static void AppendEntry(ref AssetEntryInfo[]? buffer, ref int count, AssetEntryInfo entry)
    {
        CheckArrayCapacity(count);
        if (buffer is null || count == buffer.Length)
        {
            int capacity = buffer is null ? 16 : (int)Math.Min((long)buffer.Length * 2, Array.MaxLength);
            var grown = ArrayPool<AssetEntryInfo>.Shared.Rent(capacity);
            if (buffer is not null)
            {
                buffer.AsSpan(0, count).CopyTo(grown);
                ArrayPool<AssetEntryInfo>.Shared.Return(buffer, clearArray: true);
            }
            buffer = grown;
        }
        buffer[count++] = entry;
    }

    private static AssetEntryInfo[] CopyEntries(AssetEntryInfo[]? buffer, int count)
    {
        if (count == 0)
            return Array.Empty<AssetEntryInfo>();
        var result = new AssetEntryInfo[count];
        buffer.AsSpan(0, count).CopyTo(result);
        return result;
    }

    private static void CheckArrayCapacity(int count)
    {
        if (count >= Array.MaxLength)
            throw new InvalidOperationException("The directory listing exceeds the supported array capacity. Use streaming enumeration instead.");
    }

    internal bool MoveNext()
    {
        if (_owner is null)
            return false;
        try
        {
            _token.ThrowIfCancellationRequested();
            if (!_initialized)
                Initialize();
            if (_owner is null)
                return false;
            if (_index is not null)
            {
                if (DirectoryIsNext)
                {
                    Current = new(_index.GetDirectoryPath(_nextDirectory), AssetEntryKind.Directory, 0, false);
                    _nextDirectory = _index.Directories[_nextDirectory].NextSibling;
                    return true;
                }
                if (_nextFile >= 0)
                {
                    Current = _index.GetFileInfo(_nextFile);
                    _nextFile = _index.NextFile[_nextFile];
                    return true;
                }
            }
            else if (MoveNextPackage())
                return true;
            Dispose();
            return false;
        }
        catch { Dispose(); throw; }
    }

    internal ValueTask<bool> MoveNextAsync()
    {
        if (_owner is null)
            return new(false);
        if (!_synchronousReads)
            return MoveNextAsyncCore();
        try { return new(MoveNext()); }
        catch (Exception exception) { return ValueTask.FromException<bool>(exception); }
    }

    private async ValueTask<bool> MoveNextAsyncCore()
    {
        try
        {
            _token.ThrowIfCancellationRequested();
            if (!_initialized)
                await InitializeAsync().ConfigureAwait(false);
            if (_owner is null)
                return false;
            if (_index is not null)
            {
                if (DirectoryIsNext)
                {
                    string path = await _index.GetDirectoryPathAsync(_nextDirectory, _scratch, _token).ConfigureAwait(false);
                    Current = new(path, AssetEntryKind.Directory, 0, false);
                    _nextDirectory = _index.Directories[_nextDirectory].NextSibling;
                    return true;
                }
                if (_nextFile >= 0)
                {
                    Current = await _index.GetFileInfoAsync(_nextFile, _scratch, _token).ConfigureAwait(false);
                    _nextFile = _index.NextFile[_nextFile];
                    return true;
                }
            }
            else if (await MoveNextPackageAsync().ConfigureAwait(false))
                return true;
            Dispose();
            return false;
        }
        catch { Dispose(); throw; }
    }

    private bool MoveNextPackage()
    {
        Span<byte> selected = stackalloc byte[4096];
        while (_count > 0)
        {
            _token.ThrowIfCancellationRequested();
            var path = _heap![0].Path;
            int length = path.Length;
            path.CopyTo(selected);
            int contentOrder = -1, metadataOrder = -1;
            long fileLength = 0;
            bool hasMetadata = false, deleted = false;
            do
            {
                MergeEntry(_heap[0], ref contentOrder, ref metadataOrder, ref fileLength, ref hasMetadata, ref deleted);
                AdvanceRoot(_heap[0].Index + 1);
            }
            while (_count > 0 && _heap[0].Path.SequenceEqual(selected[..length]));
            if (contentOrder >= 0 && !deleted)
            {
                // Only content that survives every package overlay proves this directory
                // exists. Once found, the rest of its subtree can be skipped in each package.
                int slash = selected[_prefixLength..length].IndexOf((byte)'/');
                if (slash >= 0)
                {
                    slash += _prefixLength;
                    Current = new(AssetPath.Utf8.GetString(selected[..slash]), AssetEntryKind.Directory, 0, false);
                    SkipSubtree(selected[..(slash + 1)]);
                    return true;
                }
                Current = new(AssetPath.Utf8.GetString(selected[..length]), AssetEntryKind.File, fileLength, hasMetadata);
                return true;
            }
        }
        return false;
    }

    private async ValueTask<bool> MoveNextPackageAsync()
    {
        while (_count > 0)
        {
            _token.ThrowIfCancellationRequested();
            int length = _heap![0].Path.Length;
            _heap[0].Path.CopyTo(_path);
            int contentOrder = -1, metadataOrder = -1;
            long fileLength = 0;
            bool hasMetadata = false, deleted = false;
            do
            {
                MergeEntry(_heap[0], ref contentOrder, ref metadataOrder, ref fileLength, ref hasMetadata, ref deleted);
                await AdvanceRootAsync(_heap[0].Index + 1).ConfigureAwait(false);
            }
            while (_count > 0 && _heap[0].Path.SequenceEqual(_path.AsSpan(0, length)));
            if (contentOrder >= 0 && !deleted)
            {
                int slash = _path.AsSpan(_prefixLength, length - _prefixLength).IndexOf((byte)'/');
                if (slash >= 0)
                {
                    slash += _prefixLength;
                    Current = new(AssetPath.Utf8.GetString(_path.AsSpan(0, slash)), AssetEntryKind.Directory, 0, false);
                    await SkipSubtreeAsync(slash + 1).ConfigureAwait(false);
                    return true;
                }
                Current = new(AssetPath.Utf8.GetString(_path.AsSpan(0, length)), AssetEntryKind.File, fileLength, hasMetadata);
                return true;
            }
        }
        return false;
    }

    private static void MergeEntry(Cursor cursor, ref int contentOrder, ref int metadataOrder, ref long length, ref bool hasMetadata, ref bool deleted)
    {
        if (cursor.Order > metadataOrder)
        {
            metadataOrder = cursor.Order;
            hasMetadata = cursor.Entry.HasMetadata;
        }
        if (!cursor.Entry.MetadataOnly && cursor.Order > contentOrder)
        {
            contentOrder = cursor.Order;
            length = cursor.Entry.Length;
            deleted = cursor.Entry.Deleted;
        }
    }

    private void SkipSubtree(ReadOnlySpan<byte> prefix)
    {
        Span<byte> bound = stackalloc byte[prefix.Length];
        prefix.CopyTo(bound);
        bound[^1]++;
        while (_count > 0 && _heap![0].Path.StartsWith(prefix))
        {
            _token.ThrowIfCancellationRequested();
            var cursor = _heap[0];
            long next = cursor.Package.LowerBound(bound, cursor.Index + 1, cursor.End);
            AdvanceRoot(next);
        }
    }

    private async ValueTask SkipSubtreeAsync(int prefixLength)
    {
        _path.AsSpan(0, prefixLength).CopyTo(_upperBound);
        _upperBound![prefixLength - 1]++;
        while (_count > 0 && _heap![0].Path.StartsWith(_path.AsSpan(0, prefixLength)))
        {
            _token.ThrowIfCancellationRequested();
            var cursor = _heap[0];
            long next = await cursor.Package.LowerBoundAsync(_upperBound.AsMemory(0, prefixLength), cursor.Index + 1, cursor.End, _scratch, _token).ConfigureAwait(false);
            await AdvanceRootAsync(next).ConfigureAwait(false);
        }
    }

    private void ReadCursor(int slot, long index)
    {
        ref var cursor = ref _heap![slot];
        cursor.Index = index;
        cursor.Entry = cursor.Package.ReadEntry(index);
        if (cursor.Buffer is not null)
            cursor.Package.Directory.Read(cursor.Buffer.AsSpan(0, cursor.Entry.PathLength), cursor.Entry.PathOffset);
    }

    private async ValueTask ReadCursorAsync(int slot, long index)
    {
        var cursor = _heap![slot];
        var entry = await cursor.Package.ReadEntryAsync(index, _scratch, _token).ConfigureAwait(false);
        if (cursor.Buffer is not null)
            await cursor.Package.Directory.ReadAsync(cursor.Buffer.AsMemory(0, entry.PathLength), entry.PathOffset, _token).ConfigureAwait(false);
        _heap[slot].Index = index;
        _heap[slot].Entry = entry;
    }

    private void AdvanceRoot(long next)
    {
        _token.ThrowIfCancellationRequested();
        if (next >= _heap![0].End)
            RemoveRoot();
        else
            ReadCursor(0, next);
        SiftDown();
    }

    private async ValueTask AdvanceRootAsync(long next)
    {
        _token.ThrowIfCancellationRequested();
        if (next >= _heap![0].End)
            RemoveRoot();
        else
            await ReadCursorAsync(0, next).ConfigureAwait(false);
        SiftDown();
    }

    private void RemoveRoot()
    {
        _heap![0].Dispose();
        _count--;
        _heap[0] = _heap[_count];
        _heap[_count] = default;
    }

    private void SiftUp(int slot)
    {
        var cursor = _heap![slot];
        while (slot > 0)
        {
            int parent = (slot - 1) / 2;
            if (_heap[parent].Path.SequenceCompareTo(cursor.Path) <= 0)
                break;
            _heap[slot] = _heap[parent];
            slot = parent;
        }
        _heap[slot] = cursor;
    }

    private void SiftDown()
    {
        if (_count == 0)
            return;
        var cursor = _heap![0];
        int slot = 0;
        while (slot < _count / 2)
        {
            int child = slot * 2 + 1;
            if (child + 1 < _count && _heap[child + 1].Path.SequenceCompareTo(_heap[child].Path) < 0)
                child++;
            if (cursor.Path.SequenceCompareTo(_heap[child].Path) <= 0)
                break;
            _heap[slot] = _heap[child];
            slot = child;
        }
        _heap[slot] = cursor;
    }

    public void Dispose()
    {
        var owner = _owner;
        if (owner is null)
            return;
        _owner = null;
        _index = null;
        if (_heap is not null)
        {
            for (int slot = 0; slot < _count; slot++)
                _heap[slot].Dispose();
            ArrayPool<Cursor>.Shared.Return(_heap, clearArray: true);
            _heap = null;
            _count = 0;
        }
        ReturnBuffer(ref _scratch);
        ReturnBuffer(ref _path);
        ReturnBuffer(ref _upperBound);
        owner.ReleaseReference();
    }

    private static void ReturnBuffer(ref byte[]? buffer)
    {
        if (buffer is null)
            return;
        ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        buffer = null;
    }

    private struct Cursor : IDisposable
    {
        internal readonly PackageReader Package;
        internal readonly int Order;
        internal readonly long End;
        internal long Index;
        internal EntryRecord Entry;
        internal byte[]? Buffer;

        internal Cursor(PackageReader package, int order, long end)
        {
            Package = package;
            Order = order;
            End = end;
            Buffer = package.Directory.Resident.IsEmpty ? ArrayPool<byte>.Shared.Rent(4096) : null;
        }

        internal readonly ReadOnlySpan<byte> Path => Buffer is null
            ? Package.Directory.Resident.Slice((int)Entry.PathOffset, Entry.PathLength)
            : Buffer.AsSpan(0, Entry.PathLength);

        public void Dispose() => ReturnBuffer(ref Buffer);
    }
}
