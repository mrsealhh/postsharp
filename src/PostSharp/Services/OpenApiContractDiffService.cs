using PostSharp.Models;

namespace PostSharp.Services;

/// <summary>One contract change between two OpenAPI documents.</summary>
public sealed record OpenApiContractChange(
    string Kind,
    string Operation,
    string Location,
    string Before,
    string After,
    bool Breaking,
    string Detail
);

/// <summary>Structured comparison of two OpenAPI / Swagger documents.</summary>
public sealed record OpenApiContractDiff(
    string BaseTitle,
    string BaseVersion,
    string TargetTitle,
    string TargetVersion,
    int AddedOperations,
    int RemovedOperations,
    int BreakingChanges,
    int NonBreakingChanges,
    string Conclusion,
    IReadOnlyList<OpenApiContractChange> Changes
);

/// <summary>
/// Compares operations, parameters, request bodies and response schemas. Changes are classified as
/// breaking when an existing client could stop working; additions are reported separately.
/// </summary>
public static class OpenApiContractDiffService
{
    public const string OperationAdded = "operation.added";
    public const string OperationRemoved = "operation.removed";
    public const string ParameterAdded = "parameter.added";
    public const string ParameterRemoved = "parameter.removed";
    public const string ParameterRequired = "parameter.required";
    public const string ParameterType = "parameter.type";
    public const string BodyAdded = "body.added";
    public const string BodyRemoved = "body.removed";
    public const string BodyRequired = "body.required";
    public const string BodyMediaType = "body.mediaType";
    public const string FieldAdded = "field.added";
    public const string FieldRemoved = "field.removed";
    public const string FieldType = "field.type";
    public const string FieldRequired = "field.required";
    public const string ResponseAdded = "response.added";
    public const string ResponseRemoved = "response.removed";

    public static OpenApiContractDiff Compare(
        OpenApiImportDocument baseDocument,
        OpenApiImportDocument targetDocument
    )
    {
        var baseOperations = Index(baseDocument);
        var targetOperations = Index(targetDocument);
        var changes = new List<OpenApiContractChange>();

        foreach (var (key, baseOperation) in baseOperations)
        {
            if (!targetOperations.TryGetValue(key, out var targetOperation))
            {
                changes.Add(
                    new OpenApiContractChange(
                        OperationRemoved,
                        key,
                        string.Empty,
                        baseOperation.Summary,
                        string.Empty,
                        true,
                        "目标文档中删除了该操作"
                    )
                );
                continue;
            }

            CompareOperation(key, baseOperation, targetOperation, changes);
        }

        foreach (var (key, targetOperation) in targetOperations)
        {
            if (baseOperations.ContainsKey(key))
            {
                continue;
            }

            changes.Add(
                new OpenApiContractChange(
                    OperationAdded,
                    key,
                    string.Empty,
                    string.Empty,
                    targetOperation.Summary,
                    false,
                    "目标文档新增了该操作"
                )
            );
        }

        var removedOperations = changes.Count(change => change.Kind == OperationRemoved);
        var addedOperations = changes.Count(change => change.Kind == OperationAdded);
        var breaking = changes.Count(change => change.Breaking);
        var nonBreaking = changes.Count - breaking;
        return new OpenApiContractDiff(
            baseDocument.Title,
            baseDocument.Version,
            targetDocument.Title,
            targetDocument.Version,
            addedOperations,
            removedOperations,
            breaking,
            nonBreaking,
            Conclusion(addedOperations, removedOperations, breaking),
            changes
                .OrderBy(change => change.Operation, StringComparer.Ordinal)
                .ThenBy(change => change.Kind, StringComparer.Ordinal)
                .ToArray()
        );
    }

    private static void CompareOperation(
        string operation,
        OpenApiOperationDoc baseOperation,
        OpenApiOperationDoc targetOperation,
        ICollection<OpenApiContractChange> changes
    )
    {
        CompareParameters(operation, baseOperation, targetOperation, changes);
        CompareRequestBody(operation, baseOperation, targetOperation, changes);
        CompareResponses(operation, baseOperation, targetOperation, changes);
    }

    private static void CompareParameters(
        string operation,
        OpenApiOperationDoc baseOperation,
        OpenApiOperationDoc targetOperation,
        ICollection<OpenApiContractChange> changes
    )
    {
        var baseParameters = IndexParameters(baseOperation.Parameters);
        var targetParameters = IndexParameters(targetOperation.Parameters);
        foreach (var (key, before) in baseParameters)
        {
            if (!targetParameters.TryGetValue(key, out var after))
            {
                changes.Add(
                    new OpenApiContractChange(
                        ParameterRemoved,
                        operation,
                        key,
                        Describe(before),
                        string.Empty,
                        false,
                        "目标文档不再声明该参数"
                    )
                );
                continue;
            }

            if (before.Required != after.Required)
            {
                changes.Add(
                    new OpenApiContractChange(
                        ParameterRequired,
                        operation,
                        key,
                        before.Required ? "必填" : "可选",
                        after.Required ? "必填" : "可选",
                        after.Required,
                        after.Required ? "参数从可选变为必填" : "参数从必填变为可选"
                    )
                );
            }

            if (
                !string.Equals(before.Type, after.Type, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(after.Type)
            )
            {
                changes.Add(
                    new OpenApiContractChange(
                        ParameterType,
                        operation,
                        key,
                        before.Type,
                        after.Type,
                        true,
                        "参数类型发生变化"
                    )
                );
            }
        }

        foreach (var (key, after) in targetParameters)
        {
            if (baseParameters.ContainsKey(key))
            {
                continue;
            }

            changes.Add(
                new OpenApiContractChange(
                    ParameterAdded,
                    operation,
                    key,
                    string.Empty,
                    Describe(after),
                    after.Required,
                    after.Required ? "新增必填参数" : "新增可选参数"
                )
            );
        }
    }

    private static void CompareRequestBody(
        string operation,
        OpenApiOperationDoc baseOperation,
        OpenApiOperationDoc targetOperation,
        ICollection<OpenApiContractChange> changes
    )
    {
        var before = baseOperation.RequestBody;
        var after = targetOperation.RequestBody;
        if (before is null && after is null)
        {
            return;
        }

        if (before is null)
        {
            changes.Add(
                new OpenApiContractChange(
                    BodyAdded,
                    operation,
                    "body",
                    string.Empty,
                    after!.MediaType,
                    after.Required,
                    after.Required ? "新增必填请求体" : "新增可选请求体"
                )
            );
            CompareFields(operation, "body", [], after.Fields, changes, requestSide: true);
            return;
        }

        if (after is null)
        {
            changes.Add(
                new OpenApiContractChange(
                    BodyRemoved,
                    operation,
                    "body",
                    before.MediaType,
                    string.Empty,
                    false,
                    "目标文档不再声明请求体"
                )
            );
            return;
        }

        if (before.Required != after.Required)
        {
            changes.Add(
                new OpenApiContractChange(
                    BodyRequired,
                    operation,
                    "body",
                    before.Required ? "必填" : "可选",
                    after.Required ? "必填" : "可选",
                    after.Required,
                    after.Required ? "请求体从可选变为必填" : "请求体从必填变为可选"
                )
            );
        }

        var baseMedia = NormalizeMediaType(before.MediaType);
        var targetMedia = NormalizeMediaType(after.MediaType);
        if (
            baseMedia.Length > 0
            && targetMedia.Length > 0
            && !string.Equals(baseMedia, targetMedia, StringComparison.OrdinalIgnoreCase)
        )
        {
            changes.Add(
                new OpenApiContractChange(
                    BodyMediaType,
                    operation,
                    "body",
                    before.MediaType,
                    after.MediaType,
                    true,
                    "请求体媒体类型发生变化"
                )
            );
        }

        CompareFields(operation, "body", before.Fields, after.Fields, changes, requestSide: true);
    }

    private static void CompareResponses(
        string operation,
        OpenApiOperationDoc baseOperation,
        OpenApiOperationDoc targetOperation,
        ICollection<OpenApiContractChange> changes
    )
    {
        var baseResponses = baseOperation.Responses.ToDictionary(
            response => response.StatusCode,
            StringComparer.OrdinalIgnoreCase
        );
        var targetResponses = targetOperation.Responses.ToDictionary(
            response => response.StatusCode,
            StringComparer.OrdinalIgnoreCase
        );
        foreach (var (status, before) in baseResponses)
        {
            if (!targetResponses.TryGetValue(status, out var after))
            {
                changes.Add(
                    new OpenApiContractChange(
                        ResponseRemoved,
                        operation,
                        $"response.{status}",
                        before.Description,
                        string.Empty,
                        true,
                        "目标文档删除了该响应状态"
                    )
                );
                continue;
            }

            CompareFields(
                operation,
                $"response.{status}",
                before.Fields,
                after.Fields,
                changes,
                requestSide: false
            );
        }

        foreach (var (status, after) in targetResponses)
        {
            if (baseResponses.ContainsKey(status))
            {
                continue;
            }

            changes.Add(
                new OpenApiContractChange(
                    ResponseAdded,
                    operation,
                    $"response.{status}",
                    string.Empty,
                    after.Description,
                    false,
                    "目标文档新增了该响应状态"
                )
            );
        }
    }

    private static void CompareFields(
        string operation,
        string location,
        IReadOnlyList<OpenApiSchemaFieldDoc> baseFields,
        IReadOnlyList<OpenApiSchemaFieldDoc> targetFields,
        ICollection<OpenApiContractChange> changes,
        bool requestSide
    )
    {
        var baseByName = baseFields.ToDictionary(field => field.Name, StringComparer.Ordinal);
        var targetByName = targetFields.ToDictionary(field => field.Name, StringComparer.Ordinal);
        foreach (var (name, before) in baseByName)
        {
            if (!targetByName.TryGetValue(name, out var after))
            {
                changes.Add(
                    new OpenApiContractChange(
                        FieldRemoved,
                        operation,
                        $"{location}.{name}",
                        Describe(before),
                        string.Empty,
                        !requestSide,
                        requestSide
                            ? "请求字段已移除"
                            : "响应字段已移除，读取该字段的客户端可能失败"
                    )
                );
                continue;
            }

            if (
                !string.Equals(before.Type, after.Type, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(after.Type)
            )
            {
                changes.Add(
                    new OpenApiContractChange(
                        FieldType,
                        operation,
                        $"{location}.{name}",
                        before.Type,
                        after.Type,
                        true,
                        "字段类型发生变化"
                    )
                );
            }

            if (before.Required != after.Required && requestSide && after.Required)
            {
                changes.Add(
                    new OpenApiContractChange(
                        FieldRequired,
                        operation,
                        $"{location}.{name}",
                        "可选",
                        "必填",
                        true,
                        "请求字段从可选变为必填"
                    )
                );
            }
        }

        foreach (var (name, after) in targetByName)
        {
            if (baseByName.ContainsKey(name))
            {
                continue;
            }

            var breaking = requestSide && after.Required;
            changes.Add(
                new OpenApiContractChange(
                    FieldAdded,
                    operation,
                    $"{location}.{name}",
                    string.Empty,
                    Describe(after),
                    breaking,
                    breaking ? "请求体新增必填字段"
                        : requestSide ? "请求体新增可选字段"
                        : "响应新增字段"
                )
            );
        }
    }

    private static Dictionary<string, OpenApiOperationDoc> Index(OpenApiImportDocument document) =>
        document
            .Groups.SelectMany(group => group.Requests)
            .GroupBy(request => request.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.First().Doc,
                StringComparer.OrdinalIgnoreCase
            );

    private static Dictionary<string, OpenApiParameterDoc> IndexParameters(
        IReadOnlyList<OpenApiParameterDoc> parameters
    ) =>
        parameters
            .GroupBy(
                parameter => $"{parameter.Location}:{parameter.Name}",
                StringComparer.OrdinalIgnoreCase
            )
            .ToDictionary(
                group => group.Key,
                group => group.First(),
                StringComparer.OrdinalIgnoreCase
            );

    private static string Describe(OpenApiParameterDoc parameter) =>
        $"{parameter.Location} {parameter.Name} · {(string.IsNullOrWhiteSpace(parameter.Type) ? "未声明类型" : parameter.Type)} · {(parameter.Required ? "必填" : "可选")}";

    private static string Describe(OpenApiSchemaFieldDoc field) =>
        $"{(string.IsNullOrWhiteSpace(field.Type) ? "未声明类型" : field.Type)} · {(field.Required ? "必填" : "可选")}";

    private static string NormalizeMediaType(string mediaType) => mediaType.Split(';', 2)[0].Trim();

    private static string Conclusion(int added, int removed, int breaking)
    {
        var parts = new List<string>();
        if (breaking > 0)
        {
            parts.Add($"发现 {breaking} 处破坏性变更");
        }

        if (removed > 0)
        {
            parts.Add($"删除 {removed} 个操作");
        }

        if (added > 0)
        {
            parts.Add($"新增 {added} 个操作");
        }

        return parts.Count == 0
            ? "未发现破坏性变更，操作、参数和响应结构一致。"
            : string.Join("；", parts) + "。";
    }
}
