using System.Security.Cryptography;
using System.Text;

namespace PostSharp.Services;

/// <summary>用 Windows DPAPI 加密落盘的授权令牌，只允许当前用户解密。</summary>
public static class AuthSecretProtector
{
    private const string Prefix = "dpapi:v1:";

    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("PostSharp.Authorization.v1");

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
            Log.Warning(exception, "授权令牌加密失败，本次以明文保存");
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
            Log.Warning(exception, "授权令牌无法解密，需要重新登录");
            return string.Empty;
        }
    }
}
