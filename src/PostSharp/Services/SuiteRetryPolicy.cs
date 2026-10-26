namespace PostSharp.Services;

/// <summary>Retry limits and delays for one suite step.</summary>
public static class SuiteRetryPolicy
{
    public const int MaxRetries = 10;

    public static int MaxAttempts(int retryCount) => Math.Clamp(retryCount, 0, MaxRetries) + 1;

    public static bool ShouldRetry(bool passed, int attempt, int retryCount) =>
        !passed && attempt < MaxAttempts(retryCount);

    public static TimeSpan Delay(int retryDelayMs) =>
        TimeSpan.FromMilliseconds(Math.Clamp(retryDelayMs, 0, 60_000));
}
