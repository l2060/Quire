using Quire.Internal;
using System;
using System.Collections;
using System.Collections.Generic;

namespace Quire;
/// <summary>A repeatable, lazy enumeration of visible files in UTF-8 path order.</summary>
/// <remarks>Direct foreach avoids interface boxing. Each result allocates only its path string;
/// traversal uses the merged index when available, or bounded, pooled merge state otherwise.</remarks>
public readonly struct FileEnumerable : IEnumerable<AssetEntryInfo>
{
    private readonly QuireBundle _owner;
    private readonly PackageReader[] _packages;
    private readonly string _directory;
    private readonly bool _recursive;
    internal FileEnumerable(QuireBundle owner, PackageReader[] packages, string directory, bool recursive)
    {
        _owner = owner;
        _packages = packages;
        _directory = directory;
        _recursive = recursive;
    }

    public Enumerator GetEnumerator() => new(_owner, _packages, _directory, _recursive);
    IEnumerator<AssetEntryInfo> IEnumerable<AssetEntryInfo>.GetEnumerator() => GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    /// <summary>Owns a package lease until enumeration finishes or is disposed.</summary>
    public struct Enumerator : IEnumerator<AssetEntryInfo>
    {
        private EntryEnumeration? _state;
        internal Enumerator(QuireBundle owner, PackageReader[] packages, string directory, bool recursive)
        {
            _state = new EntryEnumeration(owner, packages, directory, recursive, directories: false);
        }

        public readonly AssetEntryInfo Current => _state?.CurrentFile ?? default;
        readonly object IEnumerator.Current => Current;

        public bool MoveNext() => _state?.MoveNextFile() ?? false;
        public void Dispose()
        {
            _state?.Dispose();
            _state = null;
        }

        readonly void IEnumerator.Reset() => throw new NotSupportedException();
    }
}
