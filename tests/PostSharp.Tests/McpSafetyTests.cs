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

    [Theory]
    [InlineData("list_collections", McpToolScope.Read)]
    [InlineData("send_request", McpToolScope.Send)]
    [InlineData("delete_request", McpToolScope.Write)]
    [InlineData("update_settings", McpToolScope.Admin)]
    [InlineData("start_sequence_run", McpToolScope.Send)]
    [InlineData("start_import", McpToolScope.Write)]
    [InlineData("start_storage_sync", McpToolScope.Admin)]
    public void ToolsHaveFineGrainedScopes(string tool, McpToolScope scope)
    {
        Assert.Equal(scope, McpToolAccess.ScopeOf(tool));
    }

    [Fact]
    public void PolicyAllowsOnlyEnabledScopes()
    {
        var policy = new McpAccessPolicy(false, string.Empty, false, false, true, false);

        Assert.True(policy.Allows("list_collections"));
        Assert.True(policy.Allows("delete_request"));
        Assert.False(policy.Allows("send_request"));
        Assert.False(policy.Allows("update_settings"));
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

    [Fact]
    public void GenericJobLookupFindsTheMatchingKey()
    {
        var first = new McpJob { Kind = "run_sequence", IdempotencyKey = "seq-1" };
        var second = new McpJob { Kind = "import_content", IdempotencyKey = "import-1" };

        Assert.Same(first, McpJobLookup.FindByIdempotencyKey([first, second], "seq-1"));
        Assert.Same(second, McpJobLookup.FindByIdempotencyKey([first, second], "import-1"));
        Assert.Null(McpJobLookup.FindByIdempotencyKey([first, second], "missing"));
    }
}
