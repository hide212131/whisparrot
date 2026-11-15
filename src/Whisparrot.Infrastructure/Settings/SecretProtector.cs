using System.Security.Cryptography;
using System.Text;

namespace Whisparrot.Infrastructure.Settings;

public interface ISecretProtector
{
    /// <summary>平文を暗号化して Base64 にする。null / 空は null（未設定）に統一する。</summary>
    string? Protect(string? plaintext);

    /// <summary>復号する。失敗（別ユーザー・破損）は false で、呼び出し側は暗号文を消さない。</summary>
    bool TryUnprotect(string? protectedBase64, out string? plaintext);
}

/// <summary>設計 §8.2: UTF-8 → CurrentUser DPAPI → Base64。固定のアプリ識別子を entropy にする（秘密ではない）。</summary>
public sealed class DpapiSecretProtector : ISecretProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("whisparrot.secret.v1");

    private readonly byte[] _entropy;

    public DpapiSecretProtector() : this(Entropy) { }

    internal DpapiSecretProtector(byte[] entropy) => _entropy = entropy;

    public string? Protect(string? plaintext)
    {
        if (string.IsNullOrEmpty(plaintext)) return null;
        var bytes = Encoding.UTF8.GetBytes(plaintext);
        try
        {
            return Convert.ToBase64String(ProtectedData.Protect(bytes, _entropy, DataProtectionScope.CurrentUser));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    public bool TryUnprotect(string? protectedBase64, out string? plaintext)
    {
        plaintext = null;
        if (string.IsNullOrEmpty(protectedBase64)) return true;
        try
        {
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(protectedBase64), _entropy, DataProtectionScope.CurrentUser);
            try { plaintext = Encoding.UTF8.GetString(bytes); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
            return true;
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return false;
        }
    }
}
