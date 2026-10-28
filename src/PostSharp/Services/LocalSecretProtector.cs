using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace PostSharp.Services;

/// <summary>
/// Protects local secrets with the current user's DPAPI on Windows and with an AES-GCM key file
/// on other platforms. The key file is created with user-only permissions when the platform
/// supports Unix file modes.
/// </summary>
public static class LocalSecretProtector
{
    private const string DpapiPrefix = "dpapi:v1:";
    private const string AesPrefix = "aesgcm:v1:";
    private const int KeySize = 32;
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private static readonly object KeyLock = new();
    private static byte[]? _aesKey;

    public static string Protect(string value, string purpose)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        if (OperatingSystem.IsWindows())
        {
            return ProtectWithDpapi(value, purpose);
        }

        return ProtectWithAes(value, purpose);
    }

    public static string Unprotect(string value, string purpose)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        if (value.StartsWith(DpapiPrefix, StringComparison.Ordinal))
        {
            // A DPAPI ciphertext cannot be decrypted on another operating system.
            return OperatingSystem.IsWindows() ? UnprotectWithDpapi(value, purpose) : string.Empty;
        }

        if (value.StartsWith(AesPrefix, StringComparison.Ordinal))
        {
            return UnprotectWithAes(value, purpose);
        }

        return value;
    }

    [SupportedOSPlatform("windows")]
    private static string ProtectWithDpapi(string value, string purpose)
    {
        try
        {
            var cipher = ProtectedData.Protect(
                Encoding.UTF8.GetBytes(value),
                Entropy(purpose),
                DataProtectionScope.CurrentUser
            );
            return DpapiPrefix + Convert.ToBase64String(cipher);
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "本机机密加密失败，本次以明文保存 {Purpose}", purpose);
            return value;
        }
    }

    [SupportedOSPlatform("windows")]
    private static string UnprotectWithDpapi(string value, string purpose)
    {
        try
        {
            var cipher = Convert.FromBase64String(value[DpapiPrefix.Length..]);
            var plain = ProtectedData.Unprotect(
                cipher,
                Entropy(purpose),
                DataProtectionScope.CurrentUser
            );
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "本机机密无法解密，需要重新输入 {Purpose}", purpose);
            return string.Empty;
        }
    }

    private static string ProtectWithAes(string value, string purpose)
    {
        try
        {
            var key = GetAesKey();
            var plain = Encoding.UTF8.GetBytes(value);
            var nonce = RandomNumberGenerator.GetBytes(NonceSize);
            var cipher = new byte[plain.Length];
            var tag = new byte[TagSize];
            using var aes = new AesGcm(key, TagSize);
            aes.Encrypt(nonce, plain, cipher, tag, Entropy(purpose));
            var payload = new byte[NonceSize + TagSize + cipher.Length];
            nonce.CopyTo(payload, 0);
            tag.CopyTo(payload, NonceSize);
            cipher.CopyTo(payload, NonceSize + TagSize);
            return AesPrefix + Convert.ToBase64String(payload);
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "本机机密加密失败，本次以明文保存 {Purpose}", purpose);
            return value;
        }
    }

    private static string UnprotectWithAes(string value, string purpose)
    {
        try
        {
            var payload = Convert.FromBase64String(value[AesPrefix.Length..]);
            if (payload.Length < NonceSize + TagSize)
            {
                return string.Empty;
            }

            var nonce = payload.AsSpan(0, NonceSize);
            var tag = payload.AsSpan(NonceSize, TagSize);
            var cipher = payload.AsSpan(NonceSize + TagSize);
            var plain = new byte[cipher.Length];
            using var aes = new AesGcm(GetAesKey(), TagSize);
            aes.Decrypt(nonce, cipher, tag, plain, Entropy(purpose));
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "本机机密无法解密，需要重新输入 {Purpose}", purpose);
            return string.Empty;
        }
    }

    private static byte[] Entropy(string purpose) =>
        Encoding.UTF8.GetBytes($"PostSharp.{purpose}.v1");

    private static byte[] GetAesKey()
    {
        if (_aesKey is { } cached)
        {
            return cached;
        }

        lock (KeyLock)
        {
            if (_aesKey is { } existing)
            {
                return existing;
            }

            AppPaths.EnsureRoot();
            var path = Path.Combine(AppPaths.Root, ".secret-key");
            if (File.Exists(path))
            {
                var bytes = File.ReadAllBytes(path);
                if (bytes.Length != KeySize)
                {
                    throw new CryptographicException("本机密钥文件长度不正确。");
                }

                _aesKey = bytes;
                return bytes;
            }

            var key = RandomNumberGenerator.GetBytes(KeySize);
            File.WriteAllBytes(path, key);
            TryRestrictPermissions(path);
            _aesKey = key;
            return key;
        }
    }

    private static void TryRestrictPermissions(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "无法限制本机密钥文件权限 {Path}", path);
        }
    }
}
