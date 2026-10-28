using System.Buffers;

namespace Quire.Package.Internal;
// Shared ownership keeps copied struct enumerators from returning the same pooled buffers twice.
// The only allocation proportional to the result count is the returned path strings.
internal sealed class EntryEnumeration : IDisposable
{
    private PackageSet? _owner;
    private MergedIndex? _index;
    private int _nextIndex;
    private int _endIndex;
    private Cursor[]? _heap;
    private int _count;
    private readonly int _prefixLength;
    private readonly bool _recursive;
    private readonly bool _skipDescendantFiles;
    private byte[]? _directoryPath;
    private int _directoryPathLength;
    private int _nextSlash;
    private int _previousParentLength = -1;
    internal AssetInfo CurrentFile
    {
        get;
        private set;
    }
    internal string CurrentDirectory
    {
        get;
        private set;
    } = string.Empty;

    internal EntryEnumeration(PackageSet owner, PackageReader[] packages, string directory, bool recursive, bool directories)
    {
        ArgumentNullException.ThrowIfNull(owner);
        owner.AcquireReference();
        _owner = owner;
        _recursive = recursive;
        _skipDescendantFiles = !directories && !recursive;
        try
        {
            _index = owner.MergedIndex;
            if (_index is not null)
            {
                InitializeIndex(directory, directories);
                return;
            }

            Span<byte> prefix = stackalloc byte[4097];
            _prefixLength = AssetPath.Utf8.GetBytes(directory, prefix);
            if (_prefixLength != 0)
            {
                prefix[_prefixLength++] = (byte)'/';
            }

            prefix = prefix[.._prefixLength];
            if (packages.Length != 0)
            {
                _heap = ArrayPool<Cursor>.Shared.Rent(packages.Length);
            }

            for (int order = 0; order < packages.Length; order++)
            {
                var package = packages[order];
                long start = 0;
                long end = package.Header.EntryCount;
                if (!prefix.IsEmpty)
                {
                    start = package.LowerBound(prefix, 0, end);
                    prefix[^1]++;
                    end = package.LowerBound(prefix, start, end);
                    prefix[^1]--;
                }

                if (start == end)
                {
                    continue;
                }

                var cursor = new Cursor(package, order, end);
                try
                {
                    if (cursor.MoveTo(start, _prefixLength, _skipDescendantFiles))
                    {
                        Push(cursor);
                    }
                    else
                    {
                        cursor.Dispose();
                    }
                }
                catch
                {
                    cursor.Dispose();
                    throw;
                }
            }

            if (_count == 0)
            {
                Dispose();
            }
            else if (directories && recursive)
            {
                _directoryPath = ArrayPool<byte>.Shared.Rent(4096);
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    internal bool MoveNextFile()
    {
        return _index is not null ? MoveNextIndexedFile() : MoveNextPackageFile();
    }

    private bool MoveNextPackageFile()
    {
        if (_owner is null)
        {
            return false;
        }

        try
        {
            Span<byte> path = stackalloc byte[4096];
            if (MoveNextEntry(path, out int pathLength, out long length, out bool hasMetadata))
            {
                CurrentFile = new AssetInfo(AssetPath.Utf8.GetString(path[..pathLength]), length, hasMetadata);
                return true;
            }

            Dispose();
            return false;
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    internal bool MoveNextDirectory()
    {
        return _index is not null ? MoveNextIndexedDirectory() : MoveNextPackageDirectory();
    }

    private bool MoveNextPackageDirectory()
    {
        if (_owner is null)
        {
            return false;
        }

        try
        {
            Span<byte> path = stackalloc byte[4096];
            while (true)
            {
                if (_recursive && _nextSlash < _directoryPathLength)
                {
                    var pending = _directoryPath.AsSpan(0, _directoryPathLength);
                    int slash = pending[_nextSlash..].IndexOf((byte)'/');
                    if (slash >= 0)
                    {
                        slash += _nextSlash;
                        _nextSlash = slash + 1;
                        CurrentDirectory = AssetPath.Utf8.GetString(pending[..slash]);
                        return true;
                    }

                    _nextSlash = _directoryPathLength;
                }

                if (!MoveNextEntry(path, out int pathLength, out _, out _))
                {
                    Dispose();
                    return false;
                }

                var selected = path[..pathLength];
                if (!_recursive)
                {
                    int slash = selected[_prefixLength..].IndexOf((byte)'/');
                    if (slash < 0)
                    {
                        continue;
                    }

                    slash += _prefixLength;
                    SkipSubtree(selected[..(slash + 1)]);
                    CurrentDirectory = AssetPath.Utf8.GetString(selected[..slash]);
                    return true;
                }

                int common = selected.CommonPrefixLength(_directoryPath.AsSpan(0, _directoryPathLength));
                _nextSlash = Math.Max(_prefixLength, Math.Min(common, _previousParentLength + 1));
                _previousParentLength = selected.LastIndexOf((byte)'/');
                selected.CopyTo(_directoryPath);
                _directoryPathLength = pathLength;
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    private void InitializeIndex(string directory, bool directories)
    {
        Span<byte> path = stackalloc byte[AssetPath.Utf8.GetByteCount(directory)];
        AssetPath.Utf8.GetBytes(directory, path);
        int selected = _index!.FindDirectory(path);
        if (selected < 0)
        {
            Dispose();
            return;
        }

        ref readonly var node = ref _index.Directories[selected];
        if (directories)
        {
            _nextIndex = _recursive ? selected + 1 : node.FirstChild;
            _endIndex = node.SubtreeEnd;
        }
        else
        {
            _nextIndex = _recursive ? node.FileStart : node.FirstDirectFile;
            _endIndex = node.FileEnd;
        }

        if (_nextIndex < 0 || _nextIndex >= _endIndex)
            Dispose();
    }

    private bool MoveNextIndexedFile()
    {
        try
        {
            if (_nextIndex < 0 || _nextIndex >= _endIndex)
            {
                Dispose();
                return false;
            }

            int selected = _nextIndex;
            CurrentFile = _index!.GetFileInfo(selected);
            _nextIndex = _recursive ? selected + 1 : _index.NextFile[selected];
            return true;
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    private bool MoveNextIndexedDirectory()
    {
        try
        {
            if (_nextIndex < 0 || _nextIndex >= _endIndex)
            {
                Dispose();
                return false;
            }

            int selected = _nextIndex;
            CurrentDirectory = _index!.GetDirectoryPath(selected);
            _nextIndex = _recursive ? selected + 1 : _index.Directories[selected].NextSibling;
            return true;
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    private bool MoveNextEntry(Span<byte> path, out int pathLength, out long length, out bool hasMetadata)
    {
        while (_count > 0)
        {
            var selected = _heap![0].Path;
            pathLength = selected.Length;
            selected.CopyTo(path);
            int contentOrder = -1;
            int metadataOrder = -1;
            length = 0;
            hasMetadata = false;
            do
            {
                ref var cursor = ref _heap[0];
                if (cursor.Order > metadataOrder)
                {
                    metadataOrder = cursor.Order;
                    hasMetadata = cursor.Entry.HasMetadata;
                }

                if (!cursor.Entry.MetadataOnly && cursor.Order > contentOrder)
                {
                    contentOrder = cursor.Order;
                    length = cursor.Entry.Length;
                }

                AdvanceRoot(cursor.Index + 1);
            }
            while (_count > 0 && _heap[0].Path.SequenceEqual(path[..pathLength]));
            if (contentOrder >= 0)
            {
                return true;
            }
        }

        pathLength = 0;
        length = 0;
        hasMetadata = false;
        return false;
    }

    private void SkipSubtree(ReadOnlySpan<byte> prefix)
    {
        Span<byte> upperBound = stackalloc byte[prefix.Length];
        prefix.CopyTo(upperBound);
        upperBound[^1]++;
        while (_count > 0 && _heap![0].Path.StartsWith(prefix))
        {
            ref var cursor = ref _heap[0];
            long next = cursor.Package.LowerBound(upperBound, cursor.Index + 1, cursor.End);
            AdvanceRoot(next);
        }
    }

    private void Push(Cursor cursor)
    {
        int index = _count++;
        while (index > 0)
        {
            int parent = (index - 1) / 2;
            if (_heap![parent].Path.SequenceCompareTo(cursor.Path) <= 0)
            {
                break;
            }

            _heap[index] = _heap[parent];
            index = parent;
        }

        _heap![index] = cursor;
    }

    private void AdvanceRoot(long next)
    {
        ref var root = ref _heap![0];
        if (!root.MoveTo(next, _prefixLength, _skipDescendantFiles))
        {
            root.Dispose();
            _count--;
            if (_count == 0)
            {
                root = default;
                return;
            }

            root = _heap[_count];
            _heap[_count] = default;
        }

        var cursor = root;
        int index = 0;
        while (index < _count / 2)
        {
            int child = index * 2 + 1;
            if (child + 1 < _count && _heap[child + 1].Path.SequenceCompareTo(_heap[child].Path) < 0)
            {
                child++;
            }

            if (cursor.Path.SequenceCompareTo(_heap[child].Path) <= 0)
            {
                break;
            }

            _heap[index] = _heap[child];
            index = child;
        }

        _heap[index] = cursor;
    }

    public void Dispose()
    {
        if (_owner is null)
        {
            return;
        }

        var owner = _owner;
        _owner = null;
        _index = null;
        if (_heap is not null)
        {
            for (int index = 0; index < _count; index++)
            {
                _heap[index].Dispose();
            }

            ArrayPool<Cursor>.Shared.Return(_heap, clearArray: true);
            _heap = null;
            _count = 0;
        }

        if (_directoryPath is not null)
        {
            ArrayPool<byte>.Shared.Return(_directoryPath, clearArray: true);
            _directoryPath = null;
        }

        owner.ReleaseReference();
    }

    private struct Cursor : IDisposable
    {
        internal readonly PackageReader Package;
        internal readonly int Order;
        internal readonly long End;
        internal long Index;
        internal EntryRecord Entry;
        private byte[]? _path;
        internal Cursor(PackageReader package, int order, long end)
        {
            Package = package;
            Order = order;
            End = end;
            _path = package.Directory.Resident.IsEmpty ? ArrayPool<byte>.Shared.Rent(4096) : null;
        }

        internal readonly ReadOnlySpan<byte> Path => _path is null ? Package.Directory.Resident.Slice((int)Entry.PathOffset, Entry.PathLength) : _path.AsSpan(0, Entry.PathLength);

        internal bool MoveTo(long index, int prefixLength, bool skipDescendants)
        {
            while (index < End)
            {
                Index = index;
                Entry = Package.ReadEntry(index);
                if (_path is not null)
                {
                    Package.Directory.Read(_path.AsSpan(0, Entry.PathLength), Entry.PathOffset);
                }

                if (!skipDescendants)
                {
                    return true;
                }

                var path = Path;
                int slash = path[prefixLength..].IndexOf((byte)'/');
                if (slash < 0)
                {
                    return true;
                }

                slash += prefixLength;
                index = SeekPastSubtree(path[..(slash + 1)]);
            }

            return false;
        }

        private readonly long SeekPastSubtree(ReadOnlySpan<byte> prefix)
        {
            Span<byte> upperBound = stackalloc byte[prefix.Length];
            prefix.CopyTo(upperBound);
            upperBound[^1]++;
            return Package.LowerBound(upperBound, Index + 1, End);
        }

        public void Dispose()
        {
            if (_path is not null)
            {
                ArrayPool<byte>.Shared.Return(_path, clearArray: true);
                _path = null;
            }
        }
    }
}
