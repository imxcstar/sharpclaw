using System.Security.Cryptography;
using System.Text;

namespace sharpclaw.Core;

/// <summary>
/// 跨平台数据加密器：使用 AES-256-CBC 加密，密钥由 KeyStore 从 OS 凭据管理器获取。
/// 加密后格式：ENC: 前缀 + Base64(IV[16] + Ciphertext)
/// </summary>
public static class DataProtector
{
    private const string EncPrefix = "ENC:";
    private static readonly Lazy<byte[]> Key = new(KeyStore.GetOrCreateKey);

    public static string Encrypt(string plaintext)
    {
        if (string.IsNullOrEmpty(plaintext))
            return plaintext;

        using var aes = Aes.Create();
        aes.Key = Key.Value;
        aes.GenerateIV();

        var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
        var ciphertext = aes.EncryptCbc(plaintextBytes, aes.IV);

        // IV(16) + Ciphertext
        var result = new byte[aes.IV.Length + ciphertext.Length];
        aes.IV.CopyTo(result, 0);
        ciphertext.CopyTo(result, aes.IV.Length);

        return EncPrefix + Convert.ToBase64String(result);
    }

    public static string Decrypt(string encrypted)
    {
        if (string.IsNullOrEmpty(encrypted) || !encrypted.StartsWith(EncPrefix))
            return encrypted;

        var data = Convert.FromBase64String(encrypted[EncPrefix.Length..]);

        using var aes = Aes.Create();
        aes.Key = Key.Value;

        var plaintext = aes.DecryptCbc(data[16..], data[..16]);
        return Encoding.UTF8.GetString(plaintext);
    }

    public static bool IsEncrypted(string value) =>
        !string.IsNullOrEmpty(value) && value.StartsWith(EncPrefix);
}
