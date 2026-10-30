namespace Quire.Package.Internal;
/// <summary>Exclusive leases of GCM contexts, grouped by derived key. Never shares an active context.</summary>
internal sealed class CipherPool(byte[] key, string tag, int domain = 4) : IDisposable
{
    private readonly object _sync = new();
    private readonly BlockCipher?[] _items = new BlockCipher[32];
    private int _count;
    private bool _disposed;
    internal BlockCipher Rent(long sequence)
    {
        long group = sequence / 1024;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            for (int i = _count - 1; i >= 0; i--)
            {
                var cipher = _items[i]!;
                if (cipher.Group != group)
                    continue;
                _items[i] = _items[--_count];
                _items[_count] = null;
                return cipher;
            }
        }

        return new(key, tag, domain);
    }

    internal void Return(BlockCipher cipher)
    {
        lock (_sync)
        {
            if (!_disposed && _count < 32 && cipher.Group >= 0)
            {
                _items[_count++] = cipher;
                return;
            }
        }

        cipher.Dispose();
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _disposed = true;
            for (int i = 0; i < _count; i++)
                _items[i]!.Dispose();
            Array.Clear(_items);
            _count = 0;
        }
    }
}
