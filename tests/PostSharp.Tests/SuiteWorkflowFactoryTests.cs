using PostSharp.Models;
using PostSharp.Services;

namespace PostSharp.Tests;

public sealed class SuiteWorkflowFactoryTests
{
    [Fact]
    public void CreateFromSpecBuildsBranchesAndConditions()
    {
        var root = Step("root");
        var success = Step("success");
        var failure = Step("failure");
        var join = Step("join");
        var steps = new[] { root, success, failure, join };
        var spec = new SuiteWorkflowSpec(
            new[]
            {
                new SuiteWorkflowNodeSpec(null, 0, "root-node", 100, 120, "All"),
                new SuiteWorkflowNodeSpec(null, 1, "success-node", 260, 40, "All"),
                new SuiteWorkflowNodeSpec(null, 2, "failure-node", 260, 200, "All"),
                new SuiteWorkflowNodeSpec(null, 3, "join-node", 420, 120, "Any"),
            },
            new[]
            {
                new SuiteWorkflowEdgeSpec("root-node", "success-node", "OnSuccess"),
                new SuiteWorkflowEdgeSpec("root-node", "failure-node", "OnFailure"),
                new SuiteWorkflowEdgeSpec(
                    "failure-node",
                    "join-node",
                    "Expression",
                    new SuiteConditionSpec(
                        "Any",
                        new[]
                        {
                            new SuiteConditionRuleSpec("StatusCode", null, "Equals", "500"),
                            new SuiteConditionRuleSpec("Result", null, "NotEquals", "Passed"),
                        }
                    )
                ),
            },
            8
        );

        var workflow = SuiteWorkflowFactory.CreateFromSpec(steps, spec);

        Assert.Equal(4, workflow.Nodes.Count);
        Assert.Equal(2, workflow.Edges.Count(edge => edge.SourceId == "root-node"));
        var expression = Assert.Single(workflow.Edges, edge => edge.Condition == "Expression");
        Assert.Equal("Any", expression.Rules.Mode);
        Assert.Equal(2, expression.Rules.Rules.Count);
        Assert.Equal("StatusCode", expression.Rules.Rules[0].Source);
        Assert.Equal("join-node", workflow.Nodes[3].Id);
        Assert.Equal("Any", workflow.Nodes[3].JoinMode);
    }

    [Fact]
    public void CreateFromSpecRequiresEveryStep()
    {
        var steps = new[] { Step("root"), Step("next") };
        var spec = new SuiteWorkflowSpec(
            new[] { new SuiteWorkflowNodeSpec(null, 0, "root-node", null, null, "All") },
            Array.Empty<SuiteWorkflowEdgeSpec>()
        );

        var exception = Assert.Throws<InvalidOperationException>(() =>
            SuiteWorkflowFactory.CreateFromSpec(steps, spec)
        );

        Assert.Contains("必须覆盖全部", exception.Message);
    }

    [Fact]
    public void CreateFromSpecRejectsExpressionWithoutRules()
    {
        var steps = new[] { Step("root"), Step("next") };
        var spec = new SuiteWorkflowSpec(
            new[]
            {
                new SuiteWorkflowNodeSpec(null, 0, "root-node", null, null, "All"),
                new SuiteWorkflowNodeSpec(null, 1, "next-node", null, null, "All"),
            },
            new[] { new SuiteWorkflowEdgeSpec("root-node", "next-node", "Expression") }
        );

        var exception = Assert.Throws<InvalidOperationException>(() =>
            SuiteWorkflowFactory.CreateFromSpec(steps, spec)
        );

        Assert.Contains("至少配置一条规则", exception.Message);
    }

    private static TestStep Step(string name) =>
        new() { RequestName = name, Url = $"https://example.test/{name}" };
}
