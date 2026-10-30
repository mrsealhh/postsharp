using System.Globalization;
using PostSharp.Models;

namespace PostSharp.Services;

/// <summary>One actionable failure finding, tied to a step or assertion where possible.</summary>
public sealed record SuiteFailureFinding(
    Guid? StepId,
    string StepName,
    string Category,
    string Message,
    string Evidence,
    string Hint
);

/// <summary>An intent-level explanation for one run, suitable for showing to a person unchanged.</summary>
public sealed record SuiteFailureExplanation(
    Guid SuiteId,
    Guid RunId,
    string SuiteName,
    bool Passed,
    string Conclusion,
    string NextAction,
    IReadOnlyList<SuiteFailureFinding> Findings
);

/// <summary>
/// Classifies why a run failed without another round of tool calls: preflight, network, status,
/// response-body and assertion failures each produce a finding that names the step and the evidence.
/// </summary>
public static class SuiteFailureExplainer
{
    public static SuiteFailureExplanation Explain(TestRunResult run, Guid? stepId = null)
    {
        var findings = new List<SuiteFailureFinding>();
        if (run.Error is not null)
        {
            findings.Add(
                new SuiteFailureFinding(
                    null,
                    string.Empty,
                    "Preflight",
                    run.Error,
                    $"运行 {run.Id} 在发送前被预检拦下",
                    "运行 validate_suite 获取 code、stepId 和 hint，按提示修复后再运行。"
                )
            );
        }

        var steps = stepId is { } onlyStep
            ? run.Steps.Where(step => step.StepId == onlyStep).ToList()
            : run.Steps;
        foreach (var step in steps)
        {
            if (step.Skipped)
            {
                findings.Add(
                    new SuiteFailureFinding(
                        step.StepId,
                        step.Name,
                        "Dependency",
                        "步骤被跳过，没有发送请求",
                        step.Error ?? "上游条件或分支未命中",
                        "检查上游步骤的结果和分支条件，或用 continue_suite_from_step 从该步继续。"
                    )
                );
                continue;
            }

            if (step.Passed)
            {
                continue;
            }

            AddStepFailure(findings, step);
        }

        if (stepId is null)
        {
            foreach (var node in run.NodeStates.Where(node => node.State == "Failed"))
            {
                if (findings.Any(finding => finding.StepId == node.StepId))
                {
                    continue;
                }

                findings.Add(
                    new SuiteFailureFinding(
                        node.StepId,
                        node.Name,
                        "Workflow",
                        "节点失败但没有步骤结果",
                        node.Detail ?? "节点状态为 Failed",
                        "重新运行该套件；若仍失败，用 get_suite 检查节点与连线的绑定。"
                    )
                );
            }

            if (run.NotRun > 0 && run.Error is null)
            {
                findings.Add(
                    new SuiteFailureFinding(
                        null,
                        string.Empty,
                        "Dependency",
                        $"{run.NotRun} 个步骤没有运行",
                        run.Cancelled ? "运行被取消" : "运行在上游失败后停止",
                        "用 rerun_failed_suite 重跑失败及依赖，或把 stopOnError 设为 false 后重跑。"
                    )
                );
            }
        }

        var conclusion = Conclusion(run, findings, stepId);
        return new SuiteFailureExplanation(
            run.SuiteId,
            run.Id,
            run.SuiteName,
            findings.Count == 0,
            conclusion,
            NextAction(run, findings),
            findings
        );
    }

    private static void AddStepFailure(List<SuiteFailureFinding> findings, TestStepResult step)
    {
        var failedAssertions = step.Assertions.Where(assertion => !assertion.Passed).ToList();
        foreach (var assertion in failedAssertions)
        {
            findings.Add(
                new SuiteFailureFinding(
                    step.StepId,
                    step.Name,
                    "Assertion",
                    $"断言未通过：{assertion.Label}",
                    assertion.Detail,
                    $"核对响应值后修正断言，或用 verify_endpoint 单独验证 {step.Name}。"
                )
            );
        }

        var error = step.Error ?? string.Empty;
        if (error.StartsWith("期望状态", StringComparison.Ordinal))
        {
            findings.Add(
                new SuiteFailureFinding(
                    step.StepId,
                    step.Name,
                    "Status",
                    error,
                    $"状态码 {StatusCodeText(step.StatusCode)}，耗时 {step.ElapsedMs} ms",
                    "确认状态码变化是否符合预期；如是新契约，用 update_suite 调整 expectedStatus。"
                )
            );
            return;
        }

        if (error.StartsWith("响应不包含", StringComparison.Ordinal))
        {
            findings.Add(
                new SuiteFailureFinding(
                    step.StepId,
                    step.Name,
                    "Body",
                    error,
                    $"响应预览：{Preview(step.ResponsePreview)}",
                    "检查接口返回内容是否变化；确认后修正 bodyContains 或改为 JSONPath 断言。"
                )
            );
            return;
        }

        if (step.StatusCode == 0)
        {
            findings.Add(
                new SuiteFailureFinding(
                    step.StepId,
                    step.Name,
                    "Network",
                    string.IsNullOrWhiteSpace(error) ? "没有收到响应" : error,
                    $"耗时 {step.ElapsedMs} ms，尝试 {step.Attempts} 次",
                    "检查服务是否可达、地址和超时；网络恢复后用 rerun_failed_suite 重跑。"
                )
            );
            return;
        }

        if (error == "套件已取消")
        {
            findings.Add(
                new SuiteFailureFinding(
                    step.StepId,
                    step.Name,
                    "Cancelled",
                    "运行在发送过程中被取消",
                    $"状态码 {StatusCodeText(step.StatusCode)}",
                    "确认取消原因后重新运行套件。"
                )
            );
            return;
        }

        if (failedAssertions.Count == 0)
        {
            findings.Add(
                new SuiteFailureFinding(
                    step.StepId,
                    step.Name,
                    "Unknown",
                    string.IsNullOrWhiteSpace(error) ? "步骤失败但未给出原因" : error,
                    $"状态码 {StatusCodeText(step.StatusCode)}，响应预览：{Preview(step.ResponsePreview)}",
                    "用 send_request 重放该请求，确认响应后再决定修正断言还是实现。"
                )
            );
        }
    }

    private static string Conclusion(
        TestRunResult run,
        IReadOnlyList<SuiteFailureFinding> findings,
        Guid? stepId
    )
    {
        if (findings.Count == 0)
        {
            return stepId is null
                ? $"通过：{run.Passed}/{run.Total} 步成功，没有失败步骤。"
                : $"步骤通过：{run.Steps.FirstOrDefault(step => step.StepId == stepId)?.Name}。";
        }

        var first = findings[0];
        var target = string.IsNullOrWhiteSpace(first.StepName)
            ? "运行"
            : $"步骤「{first.StepName}」";
        return $"失败：{target} · {first.Message}（共 {findings.Count} 条问题）。";
    }

    private static string NextAction(TestRunResult run, IReadOnlyList<SuiteFailureFinding> findings)
    {
        if (findings.Count == 0)
        {
            return "无需操作；如需对比历史，用 diff_runs，导出报告用 export_report。";
        }

        var preflight = findings.Any(finding => finding.Category == "Preflight");
        if (preflight)
        {
            return $"先调用 validate_suite {{\"id\":\"{run.SuiteId}\"}}，修复返回的 code/hint 后重新运行。";
        }

        var step = findings.FirstOrDefault(finding => finding.StepId is not null);
        if (step?.StepId is { } stepId)
        {
            return $"用 continue_suite_from_step {{\"suiteId\":\"{run.SuiteId}\",\"runId\":\"{run.Id}\",\"stepId\":\"{stepId}\"}} 从失败步骤继续，或用 rerun_failed_suite {{\"suiteId\":\"{run.SuiteId}\",\"runId\":\"{run.Id}\"}} 只重跑失败及依赖。";
        }

        return $"用 rerun_failed_suite {{\"suiteId\":\"{run.SuiteId}\",\"runId\":\"{run.Id}\"}} 重跑失败步骤。";
    }

    private static string StatusCodeText(int statusCode) =>
        statusCode > 0 ? statusCode.ToString(CultureInfo.InvariantCulture) : "无响应";

    private static string Preview(string preview) =>
        preview.Length <= 300 ? preview.ReplaceLineEndings(" ") : preview[..300] + "…";
}
