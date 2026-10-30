using System.Globalization;
using PostSharp.Models;

namespace PostSharp.Services;

/// <summary>One generated suite step and the evidence used to decide its expectation.</summary>
public sealed record SuiteDraftStep(
    Guid RequestId,
    string Name,
    string Method,
    string Url,
    int? ExpectedStatus,
    string Evidence
);

/// <summary>The generated suite, the inference evidence and the preflight result in one answer.</summary>
public sealed record SuiteGenerationResult(
    bool Saved,
    Guid? SuiteId,
    string Name,
    int StepCount,
    IReadOnlyList<SuiteDraftStep> Steps,
    IReadOnlyList<string> Notes,
    bool HasValidationErrors,
    int ValidationErrorCount,
    string ValidationSummary
);

/// <summary>
/// Builds a suite draft from requests. Expectations come from the latest send of the same method and
/// URL, because pinning the last successful status catches regressions without inventing assertions
/// the user never reviewed.
/// </summary>
public static class SuiteGenerationPlanner
{
    public static IReadOnlyList<SuiteDraftStep> Plan(
        IReadOnlyList<RequestItem> requests,
        IReadOnlyList<HistoryEntry> history,
        bool assertObservedStatus
    )
    {
        var planned = new List<SuiteDraftStep>(requests.Count);
        foreach (var request in requests)
        {
            var observed = LatestObservation(request, history);
            int? expectedStatus = null;
            var evidence = "没有发送历史，只检查网络成功（2xx/3xx）";
            if (observed is not null)
            {
                var observedText =
                    observed.StatusCode > 0
                        ? observed.StatusCode.ToString(CultureInfo.InvariantCulture)
                        : "失败";
                evidence =
                    $"最近一次发送 {observed.Timestamp.LocalDateTime:yyyy-MM-dd HH:mm:ss} 返回 {observedText}";
                if (assertObservedStatus && observed.StatusCode is >= 200 and < 400)
                {
                    expectedStatus = observed.StatusCode;
                    evidence += "，已固定为期望状态";
                }
                else if (observed.StatusCode >= 400 || observed.StatusCode == 0)
                {
                    evidence += "，失败状态不作为期望值";
                }
            }

            planned.Add(
                new SuiteDraftStep(
                    request.Id,
                    string.IsNullOrWhiteSpace(request.Name) ? "未命名请求" : request.Name,
                    string.IsNullOrWhiteSpace(request.Method) ? "GET" : request.Method,
                    request.Url,
                    expectedStatus,
                    evidence
                )
            );
        }

        return planned;
    }

    private static HistoryEntry? LatestObservation(
        RequestItem request,
        IReadOnlyList<HistoryEntry> history
    )
    {
        HistoryEntry? latest = null;
        foreach (var entry in history)
        {
            if (
                !string.Equals(entry.Url, request.Url, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(entry.Method, request.Method, StringComparison.OrdinalIgnoreCase)
            )
            {
                continue;
            }

            if (latest is null || entry.Timestamp > latest.Timestamp)
            {
                latest = entry;
            }
        }

        return latest;
    }
}
