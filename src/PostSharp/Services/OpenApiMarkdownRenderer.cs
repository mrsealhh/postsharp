using System.Text;
using PostSharp.Models;

namespace PostSharp.Services;

/// <summary>Renders a stored OpenAPI operation snapshot as offline Markdown documentation.</summary>
public static class OpenApiMarkdownRenderer
{
    public static string Render(OpenApiOperationDoc doc)
    {
        var builder = new StringBuilder();
        var title = doc.Summary.Length > 0 ? doc.Summary : $"{doc.Method} {doc.Path}";
        builder.Append("# ").Append(title).AppendLine().AppendLine();

        builder.Append("`").Append(doc.Method).Append(" ").Append(doc.Path).Append("`");
        if (doc.OperationId.Length > 0)
        {
            builder.Append(" · `").Append(doc.OperationId).Append("`");
        }

        if (doc.Deprecated)
        {
            builder.Append(" · **已弃用**");
        }

        builder.AppendLine().AppendLine();
        if (doc.TagsText.Length > 0)
        {
            builder.Append("**分组**：").Append(doc.TagsText).AppendLine().AppendLine();
        }

        if (doc.Description.Length > 0)
        {
            builder.Append(doc.Description).AppendLine().AppendLine();
        }

        if (doc.ServersText.Length > 0)
        {
            builder.AppendLine("## 服务器").AppendLine();
            foreach (var server in doc.ServersText.Split(" · "))
            {
                builder.Append("- `").Append(server).AppendLine("`");
            }

            builder.AppendLine();
        }

        if (doc.SecurityText.Length > 0)
        {
            builder
                .AppendLine("## 认证")
                .AppendLine()
                .Append("- ")
                .Append(doc.SecurityText)
                .AppendLine()
                .AppendLine();
        }

        RenderParameters(builder, doc.Parameters);
        RenderBody(builder, doc.RequestBody);
        RenderResponses(builder, doc.Responses);
        return builder.ToString().TrimEnd() + Environment.NewLine;
    }

    private static void RenderParameters(
        StringBuilder builder,
        IReadOnlyList<OpenApiParameterDoc> parameters
    )
    {
        builder.AppendLine("## 请求参数").AppendLine();
        if (parameters.Count == 0)
        {
            builder.AppendLine("无").AppendLine();
            return;
        }

        builder
            .AppendLine("| 名称 | 位置 | 类型 | 必填 | 说明 | 示例 |")
            .AppendLine("| --- | --- | --- | --- | --- | --- |");
        foreach (var parameter in parameters)
        {
            builder
                .Append("| `")
                .Append(Cell(parameter.Name))
                .Append("` | ")
                .Append(Cell(LocationLabel(parameter.Location)))
                .Append(" | ")
                .Append(Cell(parameter.Type.Length > 0 ? parameter.Type : "string"))
                .Append(" | ")
                .Append(parameter.Required ? "是" : "否")
                .Append(" | ")
                .Append(Cell(WithEnum(parameter.Description, parameter.EnumText)))
                .Append(" | ")
                .Append(Cell(parameter.Example))
                .AppendLine(" |");
        }

        builder.AppendLine();
    }

    private static void RenderBody(StringBuilder builder, OpenApiBodyDoc? body)
    {
        if (body is null)
        {
            return;
        }

        builder
            .Append("## 请求体")
            .Append(body.MediaType.Length > 0 ? $" `{body.MediaType}`" : string.Empty)
            .Append(body.Required ? "（必填）" : "（可选）")
            .AppendLine()
            .AppendLine();

        if (body.Sample.Length > 0)
        {
            builder
                .Append("```")
                .AppendLine(FenceLanguage(body.MediaType))
                .AppendLine(body.Sample)
                .AppendLine("```")
                .AppendLine();
        }

        if (body.Fields.Count == 0)
        {
            return;
        }

        builder
            .AppendLine("| 字段 | 类型 | 必填 | 说明 | 示例 |")
            .AppendLine("| --- | --- | --- | --- | --- |");
        foreach (var field in body.Fields)
        {
            builder
                .Append("| `")
                .Append(Cell(field.Name))
                .Append("` | ")
                .Append(Cell(field.Type.Length > 0 ? field.Type : "string"))
                .Append(" | ")
                .Append(field.Required ? "是" : "否")
                .Append(" | ")
                .Append(Cell(WithEnum(field.Description, field.EnumText)))
                .Append(" | ")
                .Append(Cell(field.Example))
                .AppendLine(" |");
        }

        builder.AppendLine();
    }

    private static void RenderResponses(
        StringBuilder builder,
        IReadOnlyList<OpenApiResponseDoc> responses
    )
    {
        builder.AppendLine("## 响应").AppendLine();
        if (responses.Count == 0)
        {
            builder.AppendLine("文档没有声明响应。").AppendLine();
            return;
        }

        foreach (var response in responses)
        {
            builder
                .Append("### ")
                .Append(response.StatusCode)
                .Append(" · ")
                .Append(StatusLabel(response.StatusCode))
                .AppendLine()
                .AppendLine();
            if (response.Description.Length > 0)
            {
                builder.AppendLine(response.Description).AppendLine();
            }

            if (response.HeadersText.Length > 0)
            {
                builder.AppendLine("**响应头**：" + response.HeadersText).AppendLine();
            }

            foreach (var sample in response.Samples)
            {
                if (sample.Sample.Length == 0)
                {
                    continue;
                }

                builder
                    .Append("`")
                    .Append(sample.MediaType)
                    .AppendLine("`")
                    .AppendLine()
                    .Append("```")
                    .AppendLine(FenceLanguage(sample.MediaType))
                    .AppendLine(sample.Sample)
                    .AppendLine("```")
                    .AppendLine();
            }

            if (response.Fields.Count == 0)
            {
                continue;
            }

            builder
                .AppendLine("| 字段 | 类型 | 必填 | 说明 | 示例 |")
                .AppendLine("| --- | --- | --- | --- | --- |");
            foreach (var field in response.Fields)
            {
                builder
                    .Append("| `")
                    .Append(Cell(field.Name))
                    .Append("` | ")
                    .Append(Cell(field.Type.Length > 0 ? field.Type : "string"))
                    .Append(" | ")
                    .Append(field.Required ? "是" : "否")
                    .Append(" | ")
                    .Append(Cell(WithEnum(field.Description, field.EnumText)))
                    .Append(" | ")
                    .Append(Cell(field.Example))
                    .AppendLine(" |");
            }

            builder.AppendLine();
        }
    }

    private static string FenceLanguage(string mediaType) =>
        mediaType.Contains("json", StringComparison.OrdinalIgnoreCase) ? "json"
        : mediaType.Contains("xml", StringComparison.OrdinalIgnoreCase) ? "xml"
        : mediaType.Contains("html", StringComparison.OrdinalIgnoreCase) ? "html"
        : mediaType.Contains("yaml", StringComparison.OrdinalIgnoreCase) ? "yaml"
        : string.Empty;

    private static string WithEnum(string description, string enumText)
    {
        var text = description.Trim();
        if (enumText.Length == 0)
        {
            return text;
        }

        var values = $"可选值：{enumText}";
        return text.Length == 0 ? values : $"{text}；{values}";
    }

    private static string LocationLabel(string location) =>
        location switch
        {
            "path" => "路径",
            "query" => "查询",
            "header" => "请求头",
            "cookie" => "Cookie",
            _ => location,
        };

    private static string StatusLabel(string statusCode) =>
        statusCode switch
        {
            "200" => "成功",
            "201" => "已创建",
            "202" => "已接受",
            "204" => "无内容",
            "301" or "302" or "307" or "308" => "重定向",
            "400" => "请求参数错误",
            "401" => "未认证",
            "403" => "无权限",
            "404" => "未找到",
            "405" => "方法不允许",
            "409" => "冲突",
            "422" => "校验失败",
            "429" => "请求过多",
            "500" => "服务器错误",
            "502" => "网关错误",
            "503" => "服务不可用",
            "504" => "网关超时",
            _ => "响应",
        };

    private static string Cell(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "—";
        }

        return value
            .Replace("|", "\\|", StringComparison.Ordinal)
            .Replace("\r\n", "<br>", StringComparison.Ordinal)
            .Replace("\n", "<br>", StringComparison.Ordinal)
            .Trim();
    }
}
