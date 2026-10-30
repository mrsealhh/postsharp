using PostSharp.Models;
using PostSharp.Services;
using Xunit;

namespace PostSharp.Tests;

public sealed class WorkspaceStateTests
{
    [Fact]
    public void CaptureKeepsExpansionAndSelection()
    {
        var collapsed = new RequestCollection { Name = "折叠集合", IsExpanded = false };
        var expanded = new RequestCollection { Name = "展开集合", IsExpanded = true };
        var request = new RequestItem
        {
            Name = "健康检查",
            Method = "GET",
            Url = "https://example.com/health",
            Owner = expanded,
        };
        expanded.Requests.Add(request);

        var workspace = WorkspaceStore.Capture(
            [collapsed, expanded],
            [],
            [],
            [],
            [],
            [],
            null,
            includeSuites: false,
            selectedCollectionId: collapsed.Id,
            selectedRequestId: null
        );
        var json = DataJson.Serialize(workspace);
        var restored = DataJson.Deserialize<WorkspaceDto>(json);

        Assert.NotNull(restored);
        Assert.False(restored.Collections[0].IsExpanded);
        Assert.True(restored.Collections[1].IsExpanded);
        Assert.Equal(collapsed.Id, restored.SelectedCollectionId);
        Assert.Null(restored.SelectedRequestId);

        var model = WorkspaceStore.ToCollectionModel(restored.Collections[0]);
        Assert.False(model.IsExpanded);
    }

    [Fact]
    public void OlderWorkspaceWithoutExpansionOpensExpanded()
    {
        const string json = """
            {
              "Collections": [
                { "Id": "1a9c8f6e-0f6e-4e6e-9a4c-9c0b8b7f2b11", "Name": "旧集合" }
              ]
            }
            """;

        var restored = DataJson.Deserialize<WorkspaceDto>(json);

        Assert.NotNull(restored);
        Assert.True(restored.Collections[0].IsExpanded);
        Assert.Null(restored.SelectedCollectionId);
        Assert.Null(restored.SelectedRequestId);
    }

    [Fact]
    public void SelectedRequestRoundTrips()
    {
        var collection = new RequestCollection { Name = "集合" };
        var request = new RequestItem { Name = "请求", Owner = collection };
        collection.Requests.Add(request);

        var workspace = WorkspaceStore.Capture(
            [collection],
            [],
            [],
            [],
            [],
            [],
            null,
            includeSuites: false,
            selectedCollectionId: collection.Id,
            selectedRequestId: request.Id
        );
        var restored = DataJson.Deserialize<WorkspaceDto>(DataJson.Serialize(workspace));

        Assert.NotNull(restored);
        Assert.Equal(collection.Id, restored.SelectedCollectionId);
        Assert.Equal(request.Id, restored.SelectedRequestId);
    }
}
