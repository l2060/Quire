using System.Buffers;

namespace Quire.Package.Internal;
/// <summary>A compact immutable overlay index. Paths remain in the original package indexes.</summary>
internal sealed class MergedIndex
{
    private readonly PackageReader[] _packages;
    private readonly bool _hasPagedPaths;
    internal FileRow[] Files
    {
        get;
    }
    internal DirectoryNode[] Directories
    {
        get;
    }
    internal int[] NextFile
    {
        get;
    }
    internal int FileCount => Files.Length;
    internal int DirectoryCount => Directories.Length - 1;
    internal long MemoryBytes => 28L * Files.Length + 40L * Directories.Length;

    private MergedIndex(PackageReader[] packages, int files, int directories)
    {
        _packages = packages;
        Files = GC.AllocateUninitializedArray<FileRow>(files);
        Directories = GC.AllocateUninitializedArray<DirectoryNode>(directories);
        NextFile = GC.AllocateUninitializedArray<int>(files);
        foreach (var package in packages)
        {
            _hasPagedPaths |= package.Directory.Resident.IsEmpty && package.Header.EntryCount != 0;
        }
    }

    internal static MergedIndex? TryBuild(PackageReader[] packages, long maxBytes, CancellationToken token, IOperationProgress? progress = null)
    {
        token.ThrowIfCancellationRequested();
        if (maxBytes < 40)
        {
            return null;
        }

        if (!TryCount(packages, maxBytes, token, progress, out int fileCount, out int directoryCount))
        {
            return null;
        }

        token.ThrowIfCancellationRequested();
        var result = new MergedIndex(packages, fileCount, directoryCount);
        result.Populate(token, progress);
        return result;
    }

    private static bool TryCount(PackageReader[] packages, long maxBytes, CancellationToken token, IOperationProgress? progress, out int fileCount, out int directoryCount)
    {
        long files = 0;
        long directories = 1;
        Span<byte> path = stackalloc byte[4096];
        Span<byte> previous = stackalloc byte[4096];
        int previousLength = 0;
        int previousParentLength = -1;
        using var merge = new MergeCursor(packages, token, progress);
        while (merge.MoveNext(path, out int length, out _))
        {
            files++;
            var current = path[..length];
            int common = current.CommonPrefixLength(previous[..previousLength]);
            int start = Math.Min(common, previousParentLength + 1);
            int slash;
            while ((slash = current[start..].IndexOf((byte)'/')) >= 0)
            {
                directories++;
                start += slash + 1;
            }

            if (files > Array.MaxLength || directories > Array.MaxLength || 28L * files + 40L * directories > maxBytes)
            {
                fileCount = 0;
                directoryCount = 0;
                return false;
            }

            previousParentLength = current.LastIndexOf((byte)'/');
            current.CopyTo(previous);
            previousLength = length;
        }

        fileCount = (int)files;
        directoryCount = (int)directories;
        return true;
    }

    private void Populate(CancellationToken token, IOperationProgress? progress)
    {
        Directories[0] = new DirectoryNode(-1, 0, -1, 0);
        Span<int> parents = stackalloc int[2049];
        Span<int> lastChildren = stackalloc int[2049];
        parents[0] = 0;
        lastChildren[0] = -1;
        int depth = 1;
        int nextDirectory = 1;
        int nextFile = 0;
        int previousLength = 0;
        Span<byte> previous = stackalloc byte[4096];
        Span<byte> path = stackalloc byte[4096];
        using var merge = new MergeCursor(_packages, token, progress);
        while (merge.MoveNext(path, out int length, out var file))
        {
            var current = path[..length];
            int common = current.CommonPrefixLength(previous[..previousLength]);
            while (depth > 1 && Directories[parents[depth - 1]].PrefixBytes >= common)
            {
                ref var completed = ref Directories[parents[--depth]];
                completed.SubtreeEnd = nextDirectory;
                completed.FileEnd = nextFile;
            }

            int start = depth == 1 ? 0 : Directories[parents[depth - 1]].PrefixBytes + 1;
            int slash;
            while ((slash = current[start..].IndexOf((byte)'/')) >= 0)
            {
                slash += start;
                int parent = parents[depth - 1];
                int child = nextDirectory++;
                Directories[child] = new DirectoryNode(nextFile, slash, parent, nextFile);
                if (lastChildren[depth - 1] < 0)
                {
                    Directories[parent].FirstChild = child;
                }
                else
                {
                    Directories[lastChildren[depth - 1]].NextSibling = child;
                }

                lastChildren[depth - 1] = child;
                parents[depth] = child;
                lastChildren[depth++] = -1;
                start = slash + 1;
            }

            Files[nextFile] = file;
            NextFile[nextFile] = -1;
            ref var owner = ref Directories[parents[depth - 1]];
            if (owner.LastDirectFile < 0)
            {
                owner.FirstDirectFile = nextFile;
            }
            else
            {
                NextFile[owner.LastDirectFile] = nextFile;
            }

            owner.LastDirectFile = nextFile++;
            current.CopyTo(previous);
            previousLength = length;
        }

        while (depth > 0)
        {
            ref var completed = ref Directories[parents[--depth]];
            completed.SubtreeEnd = nextDirectory;
            completed.FileEnd = nextFile;
        }

        if (nextFile != Files.Length || nextDirectory != Directories.Length)
        {
            throw new InvalidDataException("Package indexes changed while the merged index was being built.");
        }

        token.ThrowIfCancellationRequested();
    }

    internal int FindFile(ReadOnlySpan<byte> path)
    {
        Span<byte> scratch = _hasPagedPaths ? stackalloc byte[4096] : Span<byte>.Empty;
        int start = 0;
        int end = Files.Length;
        while (start < end)
        {
            int middle = start + (end - start) / 2;
            int comparison = ReadFilePath(middle, scratch).SequenceCompareTo(path);
            if (comparison == 0)
            {
                return middle;
            }

            if (comparison < 0)
            {
                start = middle + 1;
            }
            else
            {
                end = middle;
            }
        }

        return -1;
    }

    internal int FindDirectory(ReadOnlySpan<byte> path)
    {
        if (path.IsEmpty)
        {
            return 0;
        }

        Span<byte> wanted = stackalloc byte[path.Length + 1];
        path.CopyTo(wanted);
        wanted[^1] = (byte)'/';
        Span<byte> scratch = _hasPagedPaths ? stackalloc byte[4096] : Span<byte>.Empty;
        int start = 1;
        int end = Directories.Length;
        while (start < end)
        {
            int middle = start + (end - start) / 2;
            ref readonly var node = ref Directories[middle];
            var candidate = ReadFilePath(node.SourceFile, scratch)[..(node.PrefixBytes + 1)];
            int comparison = candidate.SequenceCompareTo(wanted);
            if (comparison == 0)
            {
                return middle;
            }

            if (comparison < 0)
            {
                start = middle + 1;
            }
            else
            {
                end = middle;
            }
        }

        return -1;
    }

    internal (PackageReader ContentPackage, EntryRecord Content, PackageReader MetadataPackage, EntryRecord Metadata) ReadFile(int file)
    {
        ref readonly var row = ref Files[file];
        var contentPackage = _packages[row.ContentPackage];
        var content = contentPackage.ReadEntry(row.ContentEntry);
        var metadataPackage = _packages[row.MetadataPackage];
        var metadata = row.ContentPackage == row.MetadataPackage && row.ContentEntry == row.MetadataEntry ? content : metadataPackage.ReadEntry(row.MetadataEntry);
        return (contentPackage, content, metadataPackage, metadata);
    }

    internal AssetInfo GetFileInfo(int file, string path)
    {
        ref readonly var row = ref Files[file];
        // A separate metadata source is necessarily a newer metadata-only record.
        bool metadataOverride = row.ContentPackage != row.MetadataPackage || row.ContentEntry != row.MetadataEntry;
        return _packages[row.ContentPackage].ReadFileInfo(row.ContentEntry, path, metadataOverride);
    }

    internal AssetInfo GetFileInfo(int file)
    {
        ref readonly var row = ref Files[file];
        bool metadataOverride = row.ContentPackage != row.MetadataPackage || row.ContentEntry != row.MetadataEntry;
        return _packages[row.ContentPackage].ReadFileInfo(row.ContentEntry, null, metadataOverride);
    }

    internal string GetDirectoryPath(int directory)
    {
        if (directory == 0)
        {
            return string.Empty;
        }

        ref readonly var node = ref Directories[directory];
        Span<byte> scratch = _hasPagedPaths ? stackalloc byte[4096] : Span<byte>.Empty;
        return AssetPath.Utf8.GetString(ReadFilePath(node.SourceFile, scratch)[..node.PrefixBytes]);
    }

    internal ReadOnlySpan<byte> ReadFilePath(int file, Span<byte> scratch)
    {
        ref readonly var row = ref Files[file];
        return _packages[row.ContentPackage].ReadPathBytes(row.ContentEntry, scratch);
    }

    // These fields occupy exactly 24 bytes without references or per-entry objects.
    internal readonly record struct FileRow(long ContentEntry, long MetadataEntry, int ContentPackage, int MetadataPackage);
    // Preorder directory nodes occupy 40 bytes. SubtreeEnd and FileEnd are exclusive.
    internal struct DirectoryNode
    {
        internal int SourceFile;
        internal int PrefixBytes;
        internal int Parent;
        internal int FirstChild;
        internal int NextSibling;
        internal int SubtreeEnd;
        internal int FileStart;
        internal int FileEnd;
        internal int FirstDirectFile;
        internal int LastDirectFile;
        internal DirectoryNode(int sourceFile, int prefixBytes, int parent, int firstFile)
        {
            SourceFile = sourceFile;
            PrefixBytes = prefixBytes;
            Parent = parent;
            FirstChild = -1;
            NextSibling = -1;
            SubtreeEnd = -1;
            FileStart = firstFile;
            FileEnd = -1;
            FirstDirectFile = -1;
            LastDirectFile = -1;
        }
    }

    private sealed class MergeCursor : IDisposable
    {
        private Cursor[]? _heap;
        private int _count;
        private readonly CancellationToken _token;
        private readonly IOperationProgress? _progress;
        internal MergeCursor(PackageReader[] packages, CancellationToken token, IOperationProgress? progress)
        {
            _token = token;
            _progress = progress;
            if (packages.Length == 0)
            {
                return;
            }

            _heap = ArrayPool<Cursor>.Shared.Rent(packages.Length);
            try
            {
                for (int order = 0; order < packages.Length; order++)
                {
                    token.ThrowIfCancellationRequested();
                    var package = packages[order];
                    if (package.Header.EntryCount == 0)
                    {
                        continue;
                    }

                    var cursor = new Cursor(package, order);
                    try
                    {
                        cursor.MoveTo(0);
                        Push(cursor);
                    }
                    catch
                    {
                        cursor.Dispose();
                        throw;
                    }
                }
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        internal bool MoveNext(Span<byte> path, out int length, out FileRow file)
        {
            while (_count > 0)
            {
                _token.ThrowIfCancellationRequested();
                var selected = _heap![0].Path;
                length = selected.Length;
                selected.CopyTo(path);
                int contentPackage = -1;
                int metadataPackage = -1;
                long contentEntry = 0;
                long metadataEntry = 0;
                do
                {
                    _token.ThrowIfCancellationRequested();
                    ref var cursor = ref _heap[0];
                    if (cursor.Order > metadataPackage)
                    {
                        metadataPackage = cursor.Order;
                        metadataEntry = cursor.Index;
                    }

                    if (!cursor.Entry.MetadataOnly && cursor.Order > contentPackage)
                    {
                        contentPackage = cursor.Order;
                        contentEntry = cursor.Index;
                    }

                    _progress?.Advance(files: 1);
                    AdvanceRoot();
                }
                while (_count > 0 && _heap[0].Path.SequenceEqual(path[..length]));
                if (contentPackage >= 0)
                {
                    file = new FileRow(contentEntry, metadataEntry, contentPackage, metadataPackage);
                    return true;
                }
            }

            length = 0;
            file = default;
            return false;
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

        private void AdvanceRoot()
        {
            ref var root = ref _heap![0];
            if (!root.MoveTo(root.Index + 1))
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
            if (_heap is null)
            {
                return;
            }

            for (int index = 0; index < _count; index++)
            {
                _heap[index].Dispose();
            }

            ArrayPool<Cursor>.Shared.Return(_heap, clearArray: true);
            _heap = null;
            _count = 0;
        }

        private struct Cursor : IDisposable
        {
            internal readonly PackageReader Package;
            internal readonly int Order;
            internal long Index;
            internal EntryRecord Entry;
            private byte[]? _path;
            internal Cursor(PackageReader package, int order)
            {
                Package = package;
                Order = order;
                _path = package.Directory.Resident.IsEmpty ? ArrayPool<byte>.Shared.Rent(4096) : null;
            }

            internal readonly ReadOnlySpan<byte> Path => _path is null ? Package.Directory.Resident.Slice((int)Entry.PathOffset, Entry.PathLength) : _path.AsSpan(0, Entry.PathLength);

            internal bool MoveTo(long index)
            {
                if (index == Package.Header.EntryCount)
                {
                    return false;
                }

                Index = index;
                Entry = Package.ReadEntry(index);
                if (_path is not null)
                {
                    Package.Directory.Read(_path.AsSpan(0, Entry.PathLength), Entry.PathOffset);
                }

                return true;
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
}
