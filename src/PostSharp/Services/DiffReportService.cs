using System.Net;
using System.Text;
using PostSharp.Models;

namespace PostSharp.Services;

/// <summary>Result of writing a diff report to the reports directory.</summary>
public sealed record DiffReportExport(
    string Format,
    string FileName,
    string Content,
    int Bytes,
    bool Truncated,
    string? Path
);

/// <summary>Renders run diffs and OpenAPI contract diffs as Markdown, HTML or JSON files.</summary>
public static class DiffReportService
{
    public const int DefaultContentLimit = 60_000;

    public static DiffReportExport ExportRunDiff(
        SuiteRunDiff diff,
        string? format,
        int contentLimit = DefaultContentLimit
    )
    {
        var normalized = NormalizeFormat(format);
        var content = normalized switch
        {
            "html" => RunDiffHtml(diff),
            "json" => McpJson.Serialize(diff),
            _ => RunDiffMarkdown(diff),
        };
        return Write("run-diff", normalized, content, contentLimit);
    }

    public static DiffReportExport ExportOpenApiDiff(
        OpenApiContractDiff diff,
        string? format,
        int contentLimit = DefaultContentLimit
    )
    {
        var normalized = NormalizeFormat(format);
        var content = normalized switch
        {
            "html" => ContractDiffHtml(diff),
            "json" => McpJson.Serialize(diff),
            _ => ContractDiffMarkdown(diff),
        };
        return Write("contract-diff", normalized, content, contentLimit);
    }

    public static string NormalizeFormat(string? format) =>
        format?.Trim().ToLowerInvariant() switch
        {
            "html" => "html",
            "json" => "json",
            _ => "markdown",
        };

    private static DiffReportExport Write(
        string prefix,
        string format,
        string content,
        int contentLimit
    )
    {
        var extension = format switch
        {
            "html" => "html",
            "json" => "json",
            _ => "md",
        };
        var fileName = $"{prefix}-{DateTime.Now:yyyyMMdd-HHmmss}.{extension}";
        Directory.CreateDirectory(AppPaths.ReportDirectory);
        var path = Path.Combine(AppPaths.ReportDirectory, fileName);
        File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        var limit = Math.Clamp(
            contentLimit <= 0 ? DefaultContentLimit : contentLimit,
            1_000,
            500_000
        );
        var truncated = content.Length > limit;
        return new DiffReportExport(
            format,
            fileName,
            truncated ? content[..limit] : content,
            Encoding.UTF8.GetByteCount(content),
            truncated,
            path
        );
    }

    private static string RunDiffMarkdown(SuiteRunDiff diff)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# PostSharp 运行对比");
        builder.AppendLine();
        builder.AppendLine($"- 套件：{diff.SuiteName}");
        builder.AppendLine(
            $"- 基准运行：{diff.BaseRunId} · {diff.BaseTimestamp.LocalDateTime:yyyy-MM-dd HH:mm:ss}"
        );
        builder.AppendLine(
            $"- 目标运行：{diff.TargetRunId} · {diff.TargetTimestamp.LocalDateTime:yyyy-MM-dd HH:mm:ss}"
        );
        builder.AppendLine($"- 结论：{diff.Conclusion}");
        builder.AppendLine(
            $"- 统计：新增失败 {diff.NewFailures} · 修复 {diff.Fixed} · 状态码变化 {diff.StatusChanged} · 耗时回归 {diff.Slower} · 耗时改善 {diff.Faster} · 目标未运行 {diff.NotInTarget} · 目标新增 {diff.NewInTarget}"
        );
        builder.AppendLine();
        builder.AppendLine("| 类型 | 步骤 | 状态码 | 耗时 | 明细 |");
        builder.AppendLine("| --- | --- | --- | --- | --- |");
        foreach (var step in diff.Steps)
        {
            builder.AppendLine(
                $"| {ChangeLabel(step.Change)} | {Escape(step.Name)} | {Status(step.BaseStatus)} → {Status(step.TargetStatus)} | {step.BaseElapsedMs} → {step.TargetElapsedMs} ms | {Escape(step.Detail)} |"
            );
        }

        if (diff.Nodes.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("## 节点状态");
            builder.AppendLine();
            builder.AppendLine("| 节点 | 基准 | 目标 | 耗时 |");
            builder.AppendLine("| --- | --- | --- | --- |");
            foreach (var node in diff.Nodes)
            {
                builder.AppendLine(
                    $"| {Escape(node.Name)} | {Escape(node.BaseState)} | {Escape(node.TargetState)} | {node.BaseElapsedMs} → {node.TargetElapsedMs} ms |"
                );
            }
        }

        return builder.ToString();
    }

    private static string RunDiffHtml(SuiteRunDiff diff) =>
        Html(
            "PostSharp 运行对比",
            $"""
            <p><strong>套件：</strong>{Escape(diff.SuiteName)}</p>
            <p><strong>基准：</strong>{diff.BaseRunId} · {diff.BaseTimestamp.LocalDateTime:yyyy-MM-dd HH:mm:ss}<br />
            <strong>目标：</strong>{diff.TargetRunId} · {diff.TargetTimestamp.LocalDateTime:yyyy-MM-dd HH:mm:ss}</p>
            <p><strong>结论：</strong>{Escape(diff.Conclusion)}</p>
            <p>新增失败 {diff.NewFailures} · 修复 {diff.Fixed} · 状态码变化 {diff.StatusChanged} · 耗时回归 {diff.Slower} · 耗时改善 {diff.Faster}</p>
            <table>
            <thead><tr><th>类型</th><th>步骤</th><th>状态码</th><th>耗时</th><th>明细</th></tr></thead>
            <tbody>
            {string.Join(
                Environment.NewLine,
                diff.Steps.Select(step =>
                    $"<tr><td>{Escape(ChangeLabel(step.Change))}</td><td>{Escape(step.Name)}</td><td>{Status(step.BaseStatus)} → {Status(step.TargetStatus)}</td><td>{step.BaseElapsedMs} → {step.TargetElapsedMs} ms</td><td>{Escape(step.Detail)}</td></tr>"
                )
            )}
            </tbody></table>
            """
        );

    private static string ContractDiffMarkdown(OpenApiContractDiff diff)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# PostSharp 契约对比");
        builder.AppendLine();
        builder.AppendLine($"- 基准：{diff.BaseTitle} {diff.BaseVersion}");
        builder.AppendLine($"- 目标：{diff.TargetTitle} {diff.TargetVersion}");
        builder.AppendLine($"- 结论：{diff.Conclusion}");
        builder.AppendLine(
            $"- 统计：操作新增 {diff.AddedOperations} · 操作删除 {diff.RemovedOperations} · 破坏性 {diff.BreakingChanges} · 非破坏性 {diff.NonBreakingChanges}"
        );
        builder.AppendLine();
        builder.AppendLine("| 类型 | 操作 | 位置 | 前 | 后 | 破坏性 | 说明 |");
        builder.AppendLine("| --- | --- | --- | --- | --- | --- | --- |");
        foreach (var change in diff.Changes)
        {
            builder.AppendLine(
                $"| {Escape(change.Kind)} | {Escape(change.Operation)} | {Escape(change.Location)} | {Escape(change.Before)} | {Escape(change.After)} | {(change.Breaking ? "是" : "否")} | {Escape(change.Detail)} |"
            );
        }

        return builder.ToString();
    }

    private static string ContractDiffHtml(OpenApiContractDiff diff) =>
        Html(
            "PostSharp 契约对比",
            $"""
            <p><strong>基准：</strong>{Escape(diff.BaseTitle)} {Escape(diff.BaseVersion)}<br />
            <strong>目标：</strong>{Escape(diff.TargetTitle)} {Escape(diff.TargetVersion)}</p>
            <p><strong>结论：</strong>{Escape(diff.Conclusion)}</p>
            <p>操作新增 {diff.AddedOperations} · 操作删除 {diff.RemovedOperations} · 破坏性 {diff.BreakingChanges} · 非破坏性 {diff.NonBreakingChanges}</p>
            <table>
            <thead><tr><th>类型</th><th>操作</th><th>位置</th><th>前</th><th>后</th><th>破坏性</th><th>说明</th></tr></thead>
            <tbody>
            {string.Join(
                Environment.NewLine,
                diff.Changes.Select(change =>
                    $"<tr><td>{Escape(change.Kind)}</td><td>{Escape(change.Operation)}</td><td>{Escape(change.Location)}</td><td>{Escape(change.Before)}</td><td>{Escape(change.After)}</td><td>{(change.Breaking ? "是" : "否")}</td><td>{Escape(change.Detail)}</td></tr>"
                )
            )}
            </tbody></table>
            """
        );

    private static string Html(string title, string body) =>
        "<!doctype html>\n"
        + "<html lang=\"zh-CN\">\n"
        + "<head>\n"
        + "<meta charset=\"utf-8\" />\n"
        + $"<title>{Escape(title)}</title>\n"
        + "<style>\n"
        + "body { font-family: system-ui, sans-serif; margin: 24px; color: #1f2328; }\n"
        + "table { border-collapse: collapse; width: 100%; margin-top: 12px; }\n"
        + "th, td { border: 1px solid #d0d7de; padding: 6px 8px; text-align: left; vertical-align: top; }\n"
        + "th { background: #f6f8fa; }\n"
        + "</style>\n"
        + "</head>\n"
        + "<body>\n"
        + $"<h1>{Escape(title)}</h1>\n"
        + body
        + "\n</body>\n</html>";

    private static string ChangeLabel(string change) =>
        change switch
        {
            SuiteRunChange.NewFailure => "新增失败",
            SuiteRunChange.Fixed => "已修复",
            SuiteRunChange.StillFailing => "仍然失败",
            SuiteRunChange.StatusChanged => "状态码变化",
            SuiteRunChange.Slower => "耗时回归",
            SuiteRunChange.Faster => "耗时改善",
            SuiteRunChange.Skipped => "已跳过",
            SuiteRunChange.Removed => "目标未运行",
            SuiteRunChange.Added => "目标新增",
            _ => "无变化",
        };

    private static string Status(int statusCode) => statusCode > 0 ? statusCode.ToString() : "—";

    private static string Escape(string value) => WebUtility.HtmlEncode(value);
}
