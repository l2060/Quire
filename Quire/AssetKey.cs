using System;
using System.Security.Cryptography;

namespace Quire;

/// <summary>客户端密钥：32 字节 AES 主密钥及 64 字节可信 ECDSA P-256 验签公钥。</summary>
/// <remarks>
/// 必须由可信渠道取得，不能自动信任资源包内附带的密钥。
/// 此对象不包含密码或签名私钥；提供给读取选项后，所有加载的包都必须通过签名验证。
/// </remarks>
public sealed class AssetKey
{
    /// <summary>导入、导出的二进制密钥长度。</summary>
    public const int Size = 96;
    internal const int SignatureSize = 64;
    private readonly byte[] _bytes;
    private readonly ECParameters _publicParameters;

    /// <summary>复制并验证 96 字节的客户端密钥，不保留对调用方缓冲区的引用。</summary>
    public AssetKey(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != Size)
            throw new ArgumentException("An asset key must contain exactly 96 bytes.", nameof(bytes));
        _bytes = bytes.ToArray();
        _publicParameters = ValidatePublicKey(_bytes, nameof(bytes));
    }

    /// <summary>从标准 Base64 字符串导入客户端密钥；解码后必须恰好为 96 字节。</summary>
    /// <remarks>接受标准 Base64 空白字符。该字符串是密钥的编码，不是密码或十六进制字符串。</remarks>
    /// <exception cref="ArgumentNullException">字符串为 null。</exception>
    /// <exception cref="FormatException">Base64 格式无效，或解码后的密钥长度不是 96 字节。</exception>
    /// <exception cref="ArgumentException">密钥包含无效的 ECDSA P-256 公钥。</exception>
    public AssetKey(string base64)
    {
        ArgumentNullException.ThrowIfNull(base64);
        _bytes = new byte[Size];
        if (!Convert.TryFromBase64String(base64, _bytes, out int written) || written != Size)
        {
            CryptographicOperations.ZeroMemory(_bytes);
            throw new FormatException("An asset key must be valid Base64 encoding exactly 96 bytes.");
        }
        _publicParameters = ValidatePublicKey(_bytes, nameof(base64));
    }

    private static ECParameters ValidatePublicKey(byte[] bytes, string parameterName)
    {
        try
        {
            var parameters = new ECParameters
            {
                Curve = ECCurve.NamedCurves.nistP256,
                Q = new ECPoint { X = bytes.AsSpan(32, 32).ToArray(), Y = bytes.AsSpan(64, 32).ToArray() }
            };
            using var verifier = ECDsa.Create(parameters);
            return parameters;
        }
        // CNG reports malformed points as a platform error wrapping its cryptographic
        // error. Preserve a genuine unsupported platform, but reject invalid input
        // consistently with other providers and the binary/string constructors.
        catch (Exception error) when (error is CryptographicException or
            PlatformNotSupportedException { InnerException: CryptographicException })
        {
            CryptographicOperations.ZeroMemory(bytes);
            throw new ArgumentException("The asset key contains an invalid ECDSA P-256 public key.", parameterName, error);
        }
        catch
        {
            CryptographicOperations.ZeroMemory(bytes);
            throw;
        }
    }

    /// <summary>返回密钥材料的独立副本。</summary>
    public byte[] Export() => (byte[])_bytes.Clone();

    /// <summary>返回标准 Base64 编码的客户端密钥，共 128 个字符，不含换行或填充。</summary>
    public string ExportBase64() => Convert.ToBase64String(_bytes);

    internal ReadOnlySpan<byte> EncryptionKey => _bytes.AsSpan(0, 32);
    internal ReadOnlySpan<byte> SigningPublicKey => _bytes.AsSpan(32, 64);
    internal bool Verify(ReadOnlySpan<byte> digest, ReadOnlySpan<byte> signature)
    {
        if (digest.Length != 32 || signature.Length != SignatureSize) return false;
        // A verifier is needed only while opening a package. No native handles escape
        // this scope, and concurrent opens never share a mutable crypto provider.
        using var verifier = ECDsa.Create(_publicParameters);
        return verifier.VerifyHash(digest, signature, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    }
}
