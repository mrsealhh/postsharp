namespace PostSharp.Services;

/// <summary>Protects remote storage connection strings with the current platform's local secret store.</summary>
public static class StorageSecretProtector
{
    public static string Protect(string value) => LocalSecretProtector.Protect(value, "Storage");

    public static string Unprotect(string value) =>
        LocalSecretProtector.Unprotect(value, "Storage");
}
