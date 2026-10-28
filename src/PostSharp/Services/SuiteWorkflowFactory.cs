using PostSharp.Models;

namespace PostSharp.Services;

/// <summary>Builds the canonical DAG for suites created from an ordered step list.</summary>
public static class SuiteWorkflowFactory
{
    public static SuiteWorkflow CreateLinear(IReadOnlyList<TestStep> steps)
    {
        var workflow = new SuiteWorkflow();
        for (var index = 0; index < steps.Count; index++)
        {
            workflow.Nodes.Add(
                new SuiteWorkflowNode
                {
                    Id = steps[index].Id.ToString(),
                    StepId = steps[index].Id,
                    Kind = "step",
                    X = 80,
                    Y = 60 + index * 110,
                    JoinMode = steps[index].JoinMode,
                }
            );
        }

        var groups = new List<List<TestStep>>();
        foreach (var step in steps)
        {
            if (
                groups.Count > 0
                && step.ParallelGroup != 0
                && groups[^1][0].ParallelGroup == step.ParallelGroup
            )
            {
                groups[^1].Add(step);
            }
            else
            {
                groups.Add(new List<TestStep> { step });
            }
        }

        for (var index = 1; index < groups.Count; index++)
        {
            foreach (var source in groups[index - 1])
            {
                foreach (var target in groups[index])
                {
                    workflow.Edges.Add(
                        new SuiteWorkflowEdge
                        {
                            SourceId = source.Id.ToString(),
                            TargetId = target.Id.ToString(),
                            Condition = "Always",
                        }
                    );
                }
            }
        }

        return workflow;
    }

    public static void EnsureLinear(TestSuite suite)
    {
        if (suite.Workflow is { HasStepGraph: true })
        {
            return;
        }

        suite.Workflow = CreateLinear(suite.Steps);
    }
}
