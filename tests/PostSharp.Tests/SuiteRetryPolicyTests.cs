using PostSharp.Models;
using PostSharp.Services;

namespace PostSharp.Tests;

public sealed class SuiteRetryPolicyTests
{
    [Theory]
    [InlineData(0, 1)]
    [InlineData(2, 3)]
    [InlineData(10, 11)]
    [InlineData(20, 11)]
    public void MaxAttemptsClampsRetries(int retryCount, int expected)
    {
        Assert.Equal(expected, SuiteRetryPolicy.MaxAttempts(retryCount));
    }

    [Fact]
    public void ShouldRetryOnlyWhileFailedAndAttemptsRemain()
    {
        Assert.False(SuiteRetryPolicy.ShouldRetry(passed: true, attempt: 1, retryCount: 2));
        Assert.True(SuiteRetryPolicy.ShouldRetry(passed: false, attempt: 1, retryCount: 2));
        Assert.True(SuiteRetryPolicy.ShouldRetry(passed: false, attempt: 2, retryCount: 2));
        Assert.False(SuiteRetryPolicy.ShouldRetry(passed: false, attempt: 3, retryCount: 2));
    }

    [Fact]
    public void StepExpectationMentionsRetry()
    {
        var step = new TestStep { RetryCount = 2 };

        Assert.Contains("重试 2 次", step.ExpectationText);
    }
}
