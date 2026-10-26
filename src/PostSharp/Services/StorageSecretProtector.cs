using System.Security.Cryptography;
using System.Text;

namespace PostSharp.Services;

/// <summary>Encrypts remote storage connection strings with the current Windows user's DPAPI key.</summary>
public static class StorageSecretProtector
{
    private const string Prefix = "dpapi:v1:";

    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("PostSharp.Storage.v1");

    public static string Protect(string value)
    {
        if (string.IsNullOrEmpty(value) || !OperatingSystem.IsWindows())
        {
            return value;
        }

        try
        {
            var cipher = ProtectedData.Protect(
                Encoding.UTF8.GetBytes(value),
                Entropy,
                DataProtectionScope.CurrentUser
            );
            return Prefix + Convert.ToBase64String(cipher);
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "存储连接串加密失败，本次以明文保存");
            return value;
        }
    }

    public static string Unprotect(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        if (!OperatingSystem.IsWindows() || !value.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return value;
        }

        try
        {
            var cipher = Convert.FromBase64String(value[Prefix.Length..]);
            var plain = ProtectedData.Unprotect(cipher, Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "存储连接串无法解密，需要重新填写");
            return string.Empty;
        }
    }
}
