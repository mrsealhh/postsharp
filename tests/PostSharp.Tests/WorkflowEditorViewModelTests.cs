using Avalonia;
using PostSharp.Models;
using PostSharp.ViewModels;

namespace PostSharp.Tests;

public sealed class WorkflowEditorViewModelTests
{
    [Fact]
    public void BuildsSuiteFromRequestNode()
    {
        var collection = new RequestCollection { Name = "Demo" };
        var request = new RequestItem
        {
            Name = "Health",
            Method = "GET",
            Url = "{{baseUrl}}/health",
            Owner = collection,
        };
        collection.Requests.Add(request);
        var viewModel = new WorkflowEditorViewModel(new[] { collection }, null);
        viewModel.AddDroppedSource(new SuiteSourceDragData(null, request), new Point(20, 30));
        var result = viewModel.BuildSuite();

        Assert.NotNull(result);
        var step = Assert.Single(result!.Suite.Steps);
        Assert.Equal(request.Id, step.RequestId);
        Assert.NotNull(result.Suite.Workflow);
        var node = Assert.Single(result.Suite.Workflow!.Nodes);
        Assert.Equal("step", node.Kind);
        Assert.Equal(20, node.X);
        Assert.Equal(30, node.Y);
    }

    [Fact]
    public void CollectionNodeExpandsItsRequests()
    {
        var collection = new RequestCollection { Name = "Demo" };
        var first = new RequestItem
        {
            Name = "One",
            Url = "https://example.com/one",
            Owner = collection,
        };
        var second = new RequestItem
        {
            Name = "Two",
            Url = "https://example.com/two",
            Owner = collection,
        };
        collection.Requests.Add(first);
        collection.Requests.Add(second);
        var viewModel = new WorkflowEditorViewModel(new[] { collection }, null);
        viewModel.AddDroppedSource(new SuiteSourceDragData(collection, null), new Point(10, 10));
        var result = viewModel.BuildSuite();

        Assert.NotNull(result);
        Assert.Equal(2, result!.Suite.Steps.Count);
        Assert.Equal(2, result.Suite.Workflow!.Nodes.Count);
    }

    [Fact]
    public void BuildSuiteIsBlockedWhenRequestWasRemoved()
    {
        var collection = new RequestCollection { Name = "Demo" };
        var request = new RequestItem
        {
            Name = "Removed",
            Url = "https://example.com/removed",
            Owner = collection,
        };
        collection.Requests.Add(request);
        var viewModel = new WorkflowEditorViewModel(new[] { collection }, null);
        viewModel.AddDroppedSource(new SuiteSourceDragData(null, request), new Point(10, 10));
        collection.Requests.Clear();

        var result = viewModel.BuildSuite();

        Assert.Null(result);
        Assert.True(viewModel.HasValidationIssues);
        Assert.Contains(viewModel.ValidationIssues, issue => issue.Code == "step.requestMissing");
    }
}
