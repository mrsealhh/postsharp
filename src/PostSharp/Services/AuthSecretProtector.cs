namespace PostSharp.Services;

/// <summary>Protects authorization secrets with the current platform's local secret store.</summary>
public static class AuthSecretProtector
{
    public static string Protect(string value) =>
        LocalSecretProtector.Protect(value, "Authorization");

    public static string Unprotect(string value) =>
        LocalSecretProtector.Unprotect(value, "Authorization");
}
