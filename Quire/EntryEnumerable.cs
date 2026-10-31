using Quire.Internal;
using System;
using System.Collections;
using System.Collections.Generic;

namespace Quire;

/// <summary>A repeatable, lazy listing of the direct files and directories in a virtual directory.</summary>
/// <remarks>Entries are ordered by UTF-8 path, comparing directories with an appended '/'.
/// Direct foreach avoids interface boxing. Each enumerator owns a package lease.</remarks>
public readonly struct EntryEnumerable : IEnumerable<AssetEntryInfo>
{
    private readonly QuireBundle _owner;
    private readonly PackageReader[] _packages;
    private readonly string _directory;

    internal EntryEnumerable(QuireBundle owner, PackageReader[] packages, string directory)
    {
        _owner = owner;
        _packages = packages;
        _directory = directory;
    }

    public Enumerator GetEnumerator() => new(_owner, _packages, _directory);
    IEnumerator<AssetEntryInfo> IEnumerable<AssetEntryInfo>.GetEnumerator() => GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public struct Enumerator : IEnumerator<AssetEntryInfo>
    {
        private DirEnumeration? _state;
        internal Enumerator(QuireBundle owner, PackageReader[] packages, string directory)
            => _state = new(owner, packages, directory, asynchronous: false, default);

        public readonly AssetEntryInfo Current => _state?.Current ?? default;
        readonly object IEnumerator.Current => Current;
        public bool MoveNext() => _state?.MoveNext() ?? false;
        public void Dispose()
        {
            _state?.Dispose();
            _state = null;
        }
        readonly void IEnumerator.Reset() => throw new NotSupportedException();
    }
}
