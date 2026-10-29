using System.Security.Cryptography;
using System.Text;

namespace PostSharp.Services;

/// <summary>Immutable MCP request policy; replaced atomically when settings change.</summary>
public sealed record McpAccessPolicy(bool RequireToken, string Token, bool ReadOnly)
{
    public static McpAccessPolicy Local { get; } = new(false, string.Empty, false);

    public string PermissionLabel => ReadOnly ? "ReadOnly" : "ReadWrite";
}

/// <summary>Generates and compares local MCP bearer tokens.</summary>
public static class McpTokenService
{
    private const string Purpose = "MCP token";

    public static string Generate()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static bool FixedEquals(string expected, string provided)
    {
        if (string.IsNullOrEmpty(expected) || string.IsNullOrEmpty(provided))
        {
            return false;
        }

        var left = Encoding.UTF8.GetBytes(expected);
        var right = Encoding.UTF8.GetBytes(provided);
        return left.Length == right.Length && CryptographicOperations.FixedTimeEquals(left, right);
    }

    public static string Protect(string token) => LocalSecretProtector.Protect(token, Purpose);

    public static string Unprotect(string cipher) =>
        LocalSecretProtector.Unprotect(cipher, Purpose);
}

/// <summary>Read-only MCP tools are allowed when the local access level is read-only.</summary>
public static class McpToolAccess
{
    private static readonly HashSet<string> ReadOnlyTools = new(StringComparer.Ordinal)
    {
        "list_collections",
        "get_request",
        "get_settings",
        "get_storage_config",
        "list_suites",
        "get_suite",
        "validate_suite",
        "list_suite_runs",
        "get_suite_run",
        "get_suite_run_job",
        "list_history",
        "get_history",
        "list_environments",
        "list_audit",
        "get_audit",
        "list_audit_trash",
        "list_collection_trash",
        "get_curl",
        "export_workspace",
        "soap_describe",
        "grpc_describe",
    };

    public static bool IsReadOnly(string tool) => ReadOnlyTools.Contains(tool);
}
