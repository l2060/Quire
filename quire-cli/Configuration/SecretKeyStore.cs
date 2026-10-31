using System.Buffers.Binary;
using System.Security.Cryptography;
using Quire.Cli.Build;
using Quire.Internal;

namespace Quire.Cli.Configuration;

internal sealed record KeyEntry(string Name, AssetKey ClientKey);

/// <summary>Named publisher credentials owned by the current user, independent of any project.</summary>
internal static class SecretKeyStore
{
    private const int HeaderLength = 20;
    private const int StoreVersion = 1;
    private const int MaxStoredLength = 16384;
    private static ReadOnlySpan<byte> Magic => "QUIRENAM"u8;
    private static ReadOnlySpan<byte> EntropyLabel => "Quire named publisher credentials v2/"u8;

    // Tests use an isolated absolute directory and restore it after the suite.
    internal static string? StorageDirectoryOverride { get; set; }

    internal static void ValidateName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.Length > 128 || name[0] == '-' || char.IsWhiteSpace(name[0]) || char.IsWhiteSpace(name[^1]) ||
            name.Equals("none", StringComparison.OrdinalIgnoreCase) || name.Any(char.IsControl) || AssetPath.Utf8.GetByteCount(name) > 255)
            throw new ArgumentException("Key names must be 1–128 characters and at most 255 UTF-8 bytes; 'none', control characters, leading '-', and surrounding whitespace are not allowed.", nameof(name));
    }

    internal static string GetStoragePath(string name)
    {
        ValidateName(name);
        return Path.Combine(VaultDirectory(), FileName(name));
    }

    internal static void Add(string name, PublisherKey publisher, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        ValidateName(name);
        ArgumentNullException.ThrowIfNull(publisher);
        string vault = VaultDirectory();
        CreatePrivateDirectory(vault);
        using var vaultLock = BuildLock.Acquire(vault);
        string path = Path.Combine(vault, FileName(name));
        if (File.Exists(path) || Directory.Exists(path))
            throw new IOException($"Key '{name}' already exists. Existing keys are never overwritten.");
        byte[] material = publisher.Export();
        byte[]? encoded = null;
        try
        {
            encoded = Encode(name, material);
            WriteNew(path, encoded, token);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(material);
            if (encoded is not null) CryptographicOperations.ZeroMemory(encoded);
        }
    }

    internal static PublisherKey Load(string name)
    {
        string path = GetStoragePath(name);
        if (!File.Exists(path)) throw new FileNotFoundException($"Key '{name}' does not exist. Add it with 'quire-cli keys add KEYNAME PASSWORD'.", path);
        var loaded = ReadKey(path);
        if (!loaded.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
        {
            loaded.Publisher.Dispose();
            throw new InvalidDataException("Stored key name does not match the requested name.");
        }
        return loaded.Publisher;
    }

    internal static KeyEntry[] List()
    {
        string vault = VaultDirectory();
        if (!Directory.Exists(vault)) return [];
        RejectLink(vault);
        using var vaultLock = BuildLock.Acquire(vault);
        var entries = new List<KeyEntry>();
        foreach (string path in Directory.EnumerateFiles(vault, "*.key", SearchOption.TopDirectoryOnly))
        {
            var loaded = ReadKey(path);
            using (loaded.Publisher)
                entries.Add(new(loaded.Name, loaded.Publisher.ClientKey));
        }
        entries.Sort((left, right) => StringComparer.OrdinalIgnoreCase.Compare(left.Name, right.Name));
        return entries.ToArray();
    }

    internal static void Remove(string name, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        string path = GetStoragePath(name);
        string vault = Path.GetDirectoryName(path)!;
        if (!Directory.Exists(vault)) throw new FileNotFoundException($"Key '{name}' does not exist.", path);
        RejectLink(vault);
        using var vaultLock = BuildLock.Acquire(vault);
        if (!File.Exists(path)) throw new FileNotFoundException($"Key '{name}' does not exist.", path);
        RejectLink(path);
        token.ThrowIfCancellationRequested();
        File.Delete(path);
    }

    internal static string ExportClient(string name, string destination, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var publisher = Load(name);
        destination = Path.GetFullPath(destination);
        byte[] client = publisher.ClientKey.Export();
        try
        {
            if (CheckExistingClient(destination, client)) return destination;
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            WriteNew(destination, client, token);
            return destination;
        }
        finally { CryptographicOperations.ZeroMemory(client); }
    }

    internal static AssetKey LoadClientFile(string path)
    {
        byte[] bytes = ReadLimited(path, AssetKey.Size);
        try
        {
            if (bytes.Length != AssetKey.Size) throw new InvalidDataException($"Client key files must contain exactly {AssetKey.Size} bytes.");
            return new AssetKey(bytes);
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    internal static string Fingerprint(AssetKey key) => Convert.ToHexStringLower(SHA256.HashData(key.SigningPublicKey));

    private static string VaultDirectory()
    {
        if (StorageDirectoryOverride is { } overridden)
        {
            if (!Path.IsPathFullyQualified(overridden))
                throw new InvalidOperationException("The key storage override must be an absolute directory.");
            return Path.GetFullPath(overridden);
        }
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(local)) throw new InvalidOperationException("A local user application-data directory is required for publisher credentials.");
        return Path.Combine(local, "Quire", "keys", "named-v2");
    }

    private static string FileName(string name) =>
        Convert.ToHexString(SHA256.HashData(AssetPath.Utf8.GetBytes(name.ToUpperInvariant()))) + ".key";

    private static byte[] Entropy(string name)
    {
        byte[] normalized = AssetPath.Utf8.GetBytes(name.ToUpperInvariant());
        byte[] context = new byte[EntropyLabel.Length + normalized.Length];
        EntropyLabel.CopyTo(context);
        normalized.CopyTo(context, EntropyLabel.Length);
        return SHA256.HashData(context);
    }

    private static byte[] Encode(string name, byte[] material)
    {
        byte[] nameBytes = AssetPath.Utf8.GetBytes(name);
        byte[] clear = new byte[nameBytes.Length + PublisherKey.MaterialSize];
        byte[]? payload = null;
        try
        {
            // The protected copy binds the displayed spelling to the secret material.
            nameBytes.CopyTo(clear, 0);
            material.CopyTo(clear, nameBytes.Length);
            payload = OperatingSystem.IsWindows()
                ? ProtectedData.Protect(clear, Entropy(name), DataProtectionScope.CurrentUser)
                : clear.ToArray();
            byte[] encoded = new byte[HeaderLength + nameBytes.Length + payload.Length];
            Magic.CopyTo(encoded);
            BinaryPrimitives.WriteInt32LittleEndian(encoded.AsSpan(8), StoreVersion);
            BinaryPrimitives.WriteInt32LittleEndian(encoded.AsSpan(12), PublisherKey.ProfileVersion);
            encoded[16] = OperatingSystem.IsWindows() ? (byte)1 : (byte)0;
            BinaryPrimitives.WriteUInt16LittleEndian(encoded.AsSpan(18), checked((ushort)nameBytes.Length));
            nameBytes.CopyTo(encoded, HeaderLength);
            payload.CopyTo(encoded, HeaderLength + nameBytes.Length);
            return encoded;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(clear);
            if (payload is not null) CryptographicOperations.ZeroMemory(payload);
        }
    }

    private static (string Name, PublisherKey Publisher) ReadKey(string path)
    {
        RejectLink(Path.GetDirectoryName(path)!);
        RejectLink(path);
        byte[] encoded = ReadLimited(path, MaxStoredLength);
        byte[]? clear = null;
        try
        {
            if (encoded.Length < HeaderLength || !encoded.AsSpan(0, 8).SequenceEqual(Magic) ||
                BinaryPrimitives.ReadInt32LittleEndian(encoded.AsSpan(8)) != StoreVersion ||
                BinaryPrimitives.ReadInt32LittleEndian(encoded.AsSpan(12)) != PublisherKey.ProfileVersion || encoded[17] != 0)
                throw new InvalidDataException("Invalid named publisher credential header.");
            int nameLength = BinaryPrimitives.ReadUInt16LittleEndian(encoded.AsSpan(18));
            if (nameLength is < 1 or > 255 || encoded.Length <= HeaderLength + nameLength)
                throw new InvalidDataException("Invalid stored key name length.");
            string name;
            try
            {
                name = AssetPath.Utf8.GetString(encoded, HeaderLength, nameLength);
                ValidateName(name);
            }
            catch (ArgumentException error) { throw new InvalidDataException("Invalid stored key name.", error); }
            if (!Path.GetFileName(path).Equals(FileName(name), StringComparison.Ordinal))
                throw new InvalidDataException("Stored key name does not match its filename.");
            if (OperatingSystem.IsWindows())
            {
                if (encoded[16] != 1) throw new InvalidDataException("Named publisher credentials must use Windows user protection.");
                clear = ProtectedData.Unprotect(encoded.AsSpan(HeaderLength + nameLength).ToArray(), Entropy(name), DataProtectionScope.CurrentUser);
            }
            else
            {
                if (encoded[16] != 0) throw new InvalidDataException("Named publisher credentials belong to a different operating system. Add the key again using the original password.");
                const UnixFileMode otherAccess = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
                    UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
                if ((File.GetUnixFileMode(path) & otherAccess) != 0)
                    throw new UnauthorizedAccessException("Publisher credentials must be accessible only to their owner (mode 0600).");
                clear = encoded.AsSpan(HeaderLength + nameLength).ToArray();
            }
            if (clear.Length != nameLength + PublisherKey.MaterialSize ||
                !clear.AsSpan(0, nameLength).SequenceEqual(encoded.AsSpan(HeaderLength, nameLength)))
                throw new InvalidDataException("Stored key name or private material length is invalid.");
            return (name, PublisherKey.Import(clear.AsSpan(nameLength)));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encoded);
            if (clear is not null) CryptographicOperations.ZeroMemory(clear);
        }
    }

    private static bool CheckExistingClient(string path, ReadOnlySpan<byte> expected)
    {
        if (Directory.Exists(path)) throw new IOException($"Client key destination is a directory: {path}");
        if (!File.Exists(path)) return false;
        RejectLink(path);
        byte[] existing = ReadLimited(path, AssetKey.Size);
        try
        {
            if (!CryptographicOperations.FixedTimeEquals(existing, expected))
                throw new IOException($"Client key destination contains different data and will not be overwritten: {path}");
            return true;
        }
        finally { CryptographicOperations.ZeroMemory(existing); }
    }

    private static byte[] ReadLimited(string path, int maximum)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1);
        if (stream.Length > maximum) throw new InvalidDataException($"Key file exceeds its permitted length: {path}");
        byte[] bytes = new byte[(int)stream.Length];
        try { stream.ReadExactly(bytes); return bytes; }
        catch { CryptographicOperations.ZeroMemory(bytes); throw; }
    }

    private static void CreatePrivateDirectory(string path)
    {
        if (Directory.Exists(path)) RejectLink(path);
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(path);
        else
        {
            Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        RejectLink(path);
    }

    private static void WriteNew(string destination, byte[] bytes, CancellationToken token)
    {
        string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, BufferSize = 1 };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var output = new FileStream(temporary, options))
            {
                output.Write(bytes);
                output.Flush(true);
            }
            token.ThrowIfCancellationRequested();
            File.Move(temporary, destination);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static void RejectLink(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException($"Key storage cannot use symbolic links: {path}");
    }
}
