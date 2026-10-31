namespace PostSharp.Services;

/// <summary>
/// Tools that change or remove workspace data. They accept a shared <c>dryRun</c> flag so an AI
/// client can inspect the impact before running the real operation.
/// </summary>
public static class McpDestructiveTools
{
    private static readonly HashSet<string> Names = new(StringComparer.Ordinal)
    {
        "delete_collection",
        "delete_request",
        "delete_suite",
        "delete_audit",
        "delete_history",
        "delete_environment",
        "delete_variable",
        "delete_storage_profile",
        "clear_audit",
        "clear_history",
        "purge_audit",
        "purge_history",
        "empty_audit_trash",
        "empty_history_trash",
        "purge_collection_trash",
        "empty_collection_trash",
        "restore_audit",
        "restore_history",
        "restore_collection_trash",
        "switch_storage",
    };

    public static bool IsDestructive(string tool) => Names.Contains(tool);
}
