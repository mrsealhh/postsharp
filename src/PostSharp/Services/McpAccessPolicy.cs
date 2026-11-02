using System.Security.Cryptography;
using System.Text;
using PostSharp.Models;

namespace PostSharp.Services;

/// <summary>Immutable MCP request policy; replaced atomically when settings change.</summary>
public sealed record McpAccessPolicy(
    bool RequireToken,
    string Token,
    bool ReadOnly,
    bool AllowSend = true,
    bool AllowWrite = true,
    bool AllowAdmin = true,
    IReadOnlyList<McpClientToken>? ClientTokens = null
)
{
    public static McpAccessPolicy Local { get; } = new(false, string.Empty, false);

    public string PermissionLabel => ReadOnly ? "ReadOnly" : "ReadWrite";

    /// <summary>尝试用客户端 token 解析出受限策略；匹配不到返回 false。</summary>
    public bool TryResolveClient(string token, out McpAccessPolicy policy)
    {
        policy = this;
        if (!RequireToken || string.IsNullOrEmpty(token) || ClientTokens is null)
        {
            return false;
        }

        var hash = McpTokenService.Hash(token);
        foreach (var client in ClientTokens)
        {
            if (
                !string.IsNullOrEmpty(client.TokenHash)
                && McpTokenService.FixedEquals(client.TokenHash, hash)
            )
            {
                policy = this with
                {
                    Token = string.Empty,
                    ReadOnly = false,
                    AllowSend = client.AllowSend,
                    AllowWrite = client.AllowWrite,
                    AllowAdmin = client.AllowAdmin,
                };
                return true;
            }
        }

        return false;
    }

    public bool Allows(string tool)
    {
        var scope = McpToolAccess.ScopeOf(tool);
        if (ReadOnly)
        {
            return scope == McpToolScope.Read;
        }

        return scope switch
        {
            McpToolScope.Read => true,
            McpToolScope.Send => AllowSend,
            McpToolScope.Write => AllowWrite,
            McpToolScope.Admin => AllowAdmin,
            _ => false,
        };
    }
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

    /// <summary>客户端 token 只落盘哈希，用于比对而不保存明文。</summary>
    public static string Hash(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

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

/// <summary>Fine-grained MCP tool scopes used by the access policy.</summary>
public enum McpToolScope
{
    Read,
    Send,
    Write,
    Admin,
}

/// <summary>Classifies every MCP tool so read/send/write/admin can be toggled independently.</summary>
public static class McpToolAccess
{
    private static readonly HashSet<string> ReadTools = new(StringComparer.Ordinal)
    {
        "list_collections",
        "get_request",
        "get_settings",
        "get_storage_config",
        "list_suites",
        "get_suite",
        "validate_suite",
        "validate_pipeline",
        "visualize_pipeline",
        "list_clients",
        "get_client_activity",
        "list_client_tokens",
        "list_suite_runs",
        "get_suite_run",
        "get_suite_run_job",
        "get_sequence_run_job",
        "get_import_job",
        "get_storage_sync_job",
        "list_history",
        "get_history",
        "list_environments",
        "list_audit",
        "get_audit",
        "list_audit_trash",
        "list_history_trash",
        "list_collection_trash",
        "get_curl",
        "export_workspace",
        "soap_describe",
        "grpc_describe",
        "export_report",
        "diff_runs",
        "diff_openapi",
        "export_run_diff",
        "export_openapi_diff",
        "explain_failure",
    };

    private static readonly HashSet<string> SendTools = new(StringComparer.Ordinal)
    {
        "send_request",
        "run_sequence",
        "start_sequence_run",
        "run_suite",
        "run_pipeline",
        "start_suite_run",
        "rerun_failed_suite",
        "continue_suite_from_step",
        "verify_endpoint",
        "cancel_suite_run",
        "cancel_sequence_run",
    };

    private static readonly HashSet<string> AdminTools = new(StringComparer.Ordinal)
    {
        "update_settings",
        "check_updates",
        "create_client_token",
        "revoke_client_token",
        "upsert_storage_profile",
        "delete_storage_profile",
        "set_storage_binding",
        "test_storage_profile",
        "sync_storage",
        "start_storage_sync",
        "cancel_storage_sync",
        "switch_storage",
    };

    public static McpToolScope ScopeOf(string tool) =>
        ReadTools.Contains(tool) ? McpToolScope.Read
        : SendTools.Contains(tool) ? McpToolScope.Send
        : AdminTools.Contains(tool) ? McpToolScope.Admin
        : McpToolScope.Write;

    public static bool IsReadOnly(string tool) => ScopeOf(tool) == McpToolScope.Read;
}
