using PostSharp.Models;

namespace PostSharp.Services;

/// <summary>Matches MCP idempotency keys to the in-memory job that already satisfied them.</summary>
public static class SuiteRunJobLookup
{
    public static SuiteRunJob? FindByIdempotencyKey(
        IEnumerable<SuiteRunJob> jobs,
        string? idempotencyKey
    )
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
