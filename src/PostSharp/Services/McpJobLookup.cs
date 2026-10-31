using PostSharp.Models;

namespace PostSharp.Services;

/// <summary>Matches generic MCP job idempotency keys.</summary>
public static class McpJobLookup
{
    public static McpJob? FindByIdempotencyKey(IEnumerable<McpJob> jobs, string? idempotencyKey)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            return null;
        }

        var key = idempotencyKey.Trim();
        return jobs.FirstOrDefault(job =>
            string.Equals(job.IdempotencyKey, key, StringComparison.Ordinal)
        );
    }
}
