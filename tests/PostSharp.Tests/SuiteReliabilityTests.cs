using PostSharp.Models;
using PostSharp.Services;

namespace PostSharp.Tests;

public sealed class SuiteReliabilityTests
{
    [Fact]
    public void PreflightBlocksMissingRequest()
    {
        var suite = new TestSuite();
        var step = AddStep(
            suite,
            new RequestCollection { Name = "Demo" },
            "缺失",
            "https://example.test"
        );
        step.RequestId = Guid.NewGuid();

        var result = SuitePreflight.Validate(suite, Array.Empty<RequestCollection>(), null);

        Assert.True(result.HasErrors);
        Assert.Contains(result.Issues, issue => issue.Code == "step.requestMissing");
    }

    [Fact]
    public void PreflightToleratesNestedCollectionsInFlatWorkspaceList()
    {
        var root = new RequestCollection { Name = "根集合" };
        var child = new RequestCollection { Name = "子集合", Parent = root };
        root.Children.Add(child);
        var suite = new TestSuite();
        AddStep(suite, root, "根接口", "https://example.test/root");
        AddStep(suite, child, "子接口", "https://example.test/child");
        suite.Workflow = BuildWorkflow(suite);

        // 主窗口的 Collections 同时包含父集合和子集合，子集合的请求会被遍历两次。
        var result = SuitePreflight.Validate(suite, new[] { root, child }, null);

        Assert.False(result.HasErrors);
        Assert.DoesNotContain(result.Issues, issue => issue.Code == "step.requestMissing");
    }

    [Fact]
    public void PreflightDetectsCycleAndDanglingEdge()
    {
        var collection = new RequestCollection { Name = "Demo" };
        var suite = new TestSuite();
        var first = AddStep(suite, collection, "一", "https://example.test/one");
        var second = AddStep(suite, collection, "二", "https://example.test/two");
        suite.Workflow = BuildWorkflow(suite);
        Connect(suite.Workflow, first, second);
        Connect(suite.Workflow, second, first);
        suite.Workflow.Edges.Add(
            new SuiteWorkflowEdge { SourceId = first.Id.ToString(), TargetId = "missing-node" }
        );

        var result = SuitePreflight.Validate(suite, new[] { collection }, null);

        Assert.True(result.HasErrors);
        Assert.Contains(result.Issues, issue => issue.Code == "workflow.cycle");
        Assert.Contains(result.Issues, issue => issue.Code == "workflow.edge.dangling");
    }

    [Fact]
    public void PreflightWarnsWhenEnvironmentVariableIsMissing()
    {
        var collection = new RequestCollection { Name = "Demo" };
        var suite = new TestSuite();
        AddStep(suite, collection, "健康检查", "{{baseUrl}}/health");
        suite.Workflow = BuildWorkflow(suite);

        var result = SuitePreflight.Validate(suite, new[] { collection }, null);

        Assert.False(result.HasErrors);
        Assert.Contains(result.Issues, issue => issue.Code == "environment.missingVariable");
    }

    [Fact]
    public void PreflightRejectsMappingSourceThatIsNotUpstream()
    {
        var collection = new RequestCollection { Name = "Demo" };
        var suite = new TestSuite();
        var root = AddStep(suite, collection, "根", "https://example.test/root");
        var target = AddStep(suite, collection, "目标", "https://example.test/target");
        var other = AddStep(suite, collection, "其他", "https://example.test/other");
        target.InputMappings.Add(
            new InputMapping
            {
                SourceStepId = other.Id,
                SourcePath = "data.id",
                TargetKind = "Body",
                TargetPath = "id",
            }
        );
        suite.Workflow = BuildWorkflow(suite);
        Connect(suite.Workflow, root, target);

        var result = SuitePreflight.Validate(suite, new[] { collection }, null);

        Assert.True(result.HasErrors);
        Assert.Contains(result.Issues, issue => issue.Code == "mapping.notUpstream");
    }

    [Fact]
    public void RetryPlanAddsAncestorsAndMappingSources()
    {
        var collection = new RequestCollection { Name = "Demo" };
        var suite = new TestSuite();
        var root = AddStep(suite, collection, "根", "https://example.test/root");
        var middle = AddStep(suite, collection, "中间", "https://example.test/middle");
        var failed = AddStep(suite, collection, "失败", "https://example.test/fail");
        failed.InputMappings.Add(
            new InputMapping
            {
                SourceStepId = root.Id,
                SourcePath = "data.token",
                TargetKind = "Header",
                TargetPath = "Authorization",
            }
        );
        suite.Workflow = BuildWorkflow(suite);
        Connect(suite.Workflow, root, middle);
        Connect(suite.Workflow, middle, failed);
        var run = new TestRunResult
        {
            Steps =
            {
                new TestStepResult { StepId = root.Id, Passed = true },
                new TestStepResult { StepId = middle.Id, Passed = true },
                new TestStepResult { StepId = failed.Id, Passed = false },
            },
        };

        var selected = SuiteRunPlanner.SelectFailedWithDependencies(suite, run);

        Assert.Equal(3, selected.Count);
        Assert.Contains(failed.Id, selected);
        Assert.Contains(middle.Id, selected);
        Assert.Contains(root.Id, selected);
    }

    [Fact]
    public void ContinuePlanIncludesDescendantsAndAncestors()
    {
        var collection = new RequestCollection { Name = "Demo" };
        var suite = new TestSuite();
        var root = AddStep(suite, collection, "根", "https://example.test/root");
        var middle = AddStep(suite, collection, "中间", "https://example.test/middle");
        var leaf = AddStep(suite, collection, "末", "https://example.test/leaf");
        suite.Workflow = BuildWorkflow(suite);
        Connect(suite.Workflow, root, middle);
        Connect(suite.Workflow, middle, leaf);

        var selected = SuiteRunPlanner.SelectFromStepWithDependencies(suite, middle.Id);
        var subset = SuiteRunPlanner.CreateSubset(suite, selected);

        Assert.Equal(3, selected.Count);
        Assert.Equal(3, subset.Steps.Count);
        Assert.Equal(2, subset.Workflow!.Edges.Count);
    }

    [Fact]
    public void RunHistoryRoundTripsThroughSuiteDto()
    {
        var suite = new TestSuite { Name = "回归" };
        var stepId = Guid.NewGuid();
        var run = new TestRunResult
        {
            SuiteId = suite.Id,
            SuiteName = suite.Name,
            Total = 1,
            Failed = 1,
            Steps =
            {
                new TestStepResult
                {
                    StepId = stepId,
                    Name = "失败步骤",
                    Passed = false,
                    Error = "模拟失败",
                },
            },
            NodeStates =
            {
                new SuiteRunNodeState
                {
                    StepId = stepId,
                    Name = "失败步骤",
                    State = "Failed",
                    ElapsedMs = 42,
                    Detail = "模拟失败",
                },
            },
            EdgeStates =
            {
                new SuiteRunEdgeState
                {
                    SourceId = "a",
                    TargetId = "b",
                    Active = true,
                },
            },
        };
        suite.Runs.Add(run);

        var dto = Assert.Single(WorkspaceStore.CaptureSuites(new[] { suite }));
        var restored = WorkspaceStore.ToSuiteModel(dto);

        var restoredRun = Assert.Single(restored.Runs);
        Assert.Equal(run.Id, restoredRun.Id);
        Assert.Equal("失败步骤", Assert.Single(restoredRun.Steps).Name);
        Assert.Equal("Failed", Assert.Single(restoredRun.NodeStates).State);
        Assert.True(Assert.Single(restoredRun.EdgeStates).Active);
    }

    private static TestStep AddStep(
        TestSuite suite,
        RequestCollection collection,
        string name,
        string url
    )
    {
        var request = new RequestItem
        {
            Name = name,
            Url = url,
            Owner = collection,
        };
        collection.Requests.Add(request);
        var step = new TestStep
        {
            RequestId = request.Id,
            RequestName = name,
            Url = url,
        };
        suite.Steps.Add(step);
        return step;
    }

    private static SuiteWorkflow BuildWorkflow(TestSuite suite)
    {
        var workflow = new SuiteWorkflow();
        foreach (var step in suite.Steps)
        {
            workflow.Nodes.Add(
                new SuiteWorkflowNode
                {
                    Id = step.Id.ToString(),
                    StepId = step.Id,
                    X = suite.Steps.IndexOf(step) * 180,
                    Y = 40,
                }
            );
        }

        return workflow;
    }

    private static void Connect(SuiteWorkflow workflow, TestStep source, TestStep target)
    {
        workflow.Edges.Add(
            new SuiteWorkflowEdge
            {
                SourceId = source.Id.ToString(),
                TargetId = target.Id.ToString(),
            }
        );
    }
}
