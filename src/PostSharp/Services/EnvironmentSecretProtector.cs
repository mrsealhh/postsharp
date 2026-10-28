namespace PostSharp.Services;

/// <summary>
/// Protects secret environment variable values with the current platform's local secret store.
/// The in-memory environment still holds plain text so request composition and MCP masking keep
/// working; only the persisted workspace document is encrypted.
/// </summary>
public static class EnvironmentSecretProtector
{
    public static string Protect(string value) =>
        LocalSecretProtector.Protect(value, "Environment");

    public static string Unprotect(string value) =>
        LocalSecretProtector.Unprotect(value, "Environment");

    public static bool IsProtected(string value) => LocalSecretProtector.IsProtected(value);
}
