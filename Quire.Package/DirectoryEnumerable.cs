using System.Collections;
using Quire.Package.Internal;

namespace Quire.Package;
/// <summary>A repeatable, lazy enumeration of directories implied by visible file paths.</summary>
/// <remarks>Results are complete package-relative paths without a trailing slash. The requested
/// directory is omitted. Ordering compares UTF-8 directory prefixes including their trailing slash;
/// recursive enumeration emits parents before children. Empty directories are not stored in packages.</remarks>
public readonly struct DirectoryEnumerable : IEnumerable<string>
{
    private readonly PackageSet _owner;
    private readonly PackageReader[] _packages;
    private readonly string _directory;
    private readonly bool _recursive;
    internal DirectoryEnumerable(PackageSet owner, PackageReader[] packages, string directory, bool recursive)
    {
        _owner = owner;
        _packages = packages;
        _directory = directory;
        _recursive = recursive;
    }

    public Enumerator GetEnumerator() => new(_owner, _packages, _directory, _recursive);
    IEnumerator<string> IEnumerable<string>.GetEnumerator() => GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    /// <summary>Owns a package lease until enumeration finishes or is disposed.</summary>
    public struct Enumerator : IEnumerator<string>
    {
        private EntryEnumeration? _state;
        internal Enumerator(PackageSet owner, PackageReader[] packages, string directory, bool recursive)
        {
            _state = new EntryEnumeration(owner, packages, directory, recursive, directories: true);
        }

        public readonly string Current => _state?.CurrentDirectory ?? string.Empty;
        readonly object IEnumerator.Current => Current;

        public bool MoveNext() => _state?.MoveNextDirectory() ?? false;
        public void Dispose()
        {
            _state?.Dispose();
            _state = null;
        }

        readonly void IEnumerator.Reset() => throw new NotSupportedException();
    }
}
