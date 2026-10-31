using PostSharp.Models;
using PostSharp.Services;
using Xunit;

namespace PostSharp.Tests;

public sealed class McpSafetyTests
{
    [Theory]
    [InlineData("delete_request")]
    [InlineData("delete_collection")]
    [InlineData("clear_history")]
    [InlineData("empty_audit_trash")]
    [InlineData("switch_storage")]
    public void DestructiveToolsAreListed(string tool)
    {
        Assert.True(McpDestructiveTools.IsDestructive(tool));
    }

    [Theory]
    [InlineData("send_request")]
    [InlineData("list_collections")]
    [InlineData("start_suite_run")]
    [InlineData("verify_endpoint")]
    public void NonDestructiveToolsAreNotListed(string tool)
    {
        Assert.False(McpDestructiveTools.IsDestructive(tool));
    }

    [Fact]
    public void DestructiveToolsAreNotReadOnly()
    {
        Assert.False(McpToolAccess.IsReadOnly("delete_request"));
        Assert.False(McpToolAccess.IsReadOnly("clear_audit"));
        Assert.True(McpToolAccess.IsReadOnly("list_collections"));
    }

    [Fact]
    public void IdempotencyKeyFindsTheFirstMatchingJob()
    {
        var first = new SuiteRunJob { SuiteId = Guid.NewGuid(), IdempotencyKey = "order-1" };
        var second = new SuiteRunJob { SuiteId = Guid.NewGuid(), IdempotencyKey = "order-2" };

        Assert.Same(first, SuiteRunJobLookup.FindByIdempotencyKey([first, second], "order-1"));
        Assert.Null(SuiteRunJobLookup.FindByIdempotencyKey([first, second], "missing"));
        Assert.Null(SuiteRunJobLookup.FindByIdempotencyKey([first, second], null));
        Assert.Null(SuiteRunJobLookup.FindByIdempotencyKey([first], " "));
    }

    [Fact]
    public void IdempotencyKeyIsCaseSensitive()
    {
        var job = new SuiteRunJob { SuiteId = Guid.NewGuid(), IdempotencyKey = "Order-1" };

        Assert.Same(job, SuiteRunJobLookup.FindByIdempotencyKey([job], "Order-1"));
        Assert.Null(SuiteRunJobLookup.FindByIdempotencyKey([job], "order-1"));
    }
}
