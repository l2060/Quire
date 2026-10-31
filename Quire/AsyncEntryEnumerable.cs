using Quire.Internal;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Quire;

/// <summary>A repeatable, streaming asynchronous listing of direct files and directories.</summary>
/// <remarks>Uses asynchronous I/O for paged indexes. Resident reads may complete synchronously.
/// Ordering and results match Dir. Dispose the enumerator when stopping early.</remarks>
public readonly struct AsyncEntryEnumerable : IAsyncEnumerable<AssetEntryInfo>
{
    private readonly QuireBundle _owner;
    private readonly PackageReader[] _packages;
    private readonly string _directory;
    private readonly CancellationToken _token;

    internal AsyncEntryEnumerable(QuireBundle owner, PackageReader[] packages, string directory, CancellationToken token)
    {
        _owner = owner;
        _packages = packages;
        _directory = directory;
        _token = token;
    }

    public Enumerator GetAsyncEnumerator(CancellationToken cancellationToken = default)
        => new(_owner, _packages, _directory, _token, cancellationToken);
    IAsyncEnumerator<AssetEntryInfo> IAsyncEnumerable<AssetEntryInfo>.GetAsyncEnumerator(CancellationToken cancellationToken)
        => GetAsyncEnumerator(cancellationToken);

    public struct Enumerator : IAsyncEnumerator<AssetEntryInfo>
    {
        private DirEnumeration? _state;
        private CancellationTokenSource? _linked;

        internal Enumerator(QuireBundle owner, PackageReader[] packages, string directory, CancellationToken token, CancellationToken enumerationToken)
        {
            _state = null;
            _linked = null;
            if (!token.CanBeCanceled)
                token = enumerationToken;
            else if (enumerationToken.CanBeCanceled && enumerationToken != token)
            {
                _linked = CancellationTokenSource.CreateLinkedTokenSource(token, enumerationToken);
                token = _linked.Token;
            }
            try
            {
                _state = new(owner, packages, directory, asynchronous: true, token);
            }
            catch
            {
                _linked?.Dispose();
                throw;
            }
        }

        public readonly AssetEntryInfo Current => _state?.Current ?? default;
        public ValueTask<bool> MoveNextAsync() => _state?.MoveNextAsync() ?? new(false);
        public ValueTask DisposeAsync()
        {
            _state?.Dispose();
            _state = null;
            _linked?.Dispose();
            _linked = null;
            return default;
        }
    }
}
