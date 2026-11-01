using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using PostSharp.Models;
using PostSharp.ViewModels;

namespace PostSharp.Services;

/// <summary>
/// Source-generated JSON metadata for local documents. NativeAOT disables reflection-based
/// serialization, so settings, workspace, suites, audit and storage must use this context.
/// </summary>
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true, WriteIndented = true)]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(WorkspaceDto))]
[JsonSerializable(typeof(List<SuiteDto>))]
[JsonSerializable(typeof(List<AuditEntry>))]
[JsonSerializable(typeof(StorageConfig))]
[JsonSerializable(typeof(PendingUpdate))]
[JsonSerializable(typeof(OpenApiOperationDoc))]
internal partial class DataJsonContext : JsonSerializerContext { }

public static class DataJson
{
    private static readonly JsonSerializerOptions Options = new(DataJsonContext.Default.Options)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Options for <see cref="JsonNode"/> trees: indented and readable for Chinese text.</summary>
    public static JsonSerializerOptions NodeOptions { get; } = Options;

    /// <summary>Same as <see cref="NodeOptions"/> but keeps JSON on one line.</summary>
    public static JsonSerializerOptions CompactNodeOptions { get; } =
        new(Options) { WriteIndented = false };

    public static string Serialize<T>(T value) =>
        JsonSerializer.Serialize(value, Options.GetTypeInfo(typeof(T)));

    public static T? Deserialize<T>(string text) =>
        (T?)JsonSerializer.Deserialize(text, Options.GetTypeInfo(typeof(T)));

    /// <summary>Re-indents or minifies an already parsed document without reflection.</summary>
    public static string Format(JsonElement element, bool indented, bool relaxed)
    {
        using var stream = new MemoryStream();
        using (
            var writer = new Utf8JsonWriter(
                stream,
                new JsonWriterOptions
                {
                    Indented = indented,
                    Encoder = relaxed
                        ? JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                        : JavaScriptEncoder.Default,
                }
            )
        )
        {
            element.WriteTo(writer);
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }
}

/// <summary>
/// Source-generated metadata for MCP tool results. Every payload type must be registered here,
/// because NativeAOT cannot fall back to reflection when the runtime type is unknown.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true
)]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(McpCollectionInfo))]
[JsonSerializable(typeof(List<McpCollectionInfo>))]
[JsonSerializable(typeof(McpRequestInfo))]
[JsonSerializable(typeof(McpSendResult))]
[JsonSerializable(typeof(McpSequenceResult))]
[JsonSerializable(typeof(SoapDescription))]
[JsonSerializable(typeof(McpSettingsInfo))]
[JsonSerializable(typeof(UpdateCheckResult))]
[JsonSerializable(typeof(McpStorageConfigInfo))]
[JsonSerializable(typeof(McpStorageProfileInfo))]
[JsonSerializable(typeof(McpStorageSyncResult))]
[JsonSerializable(typeof(StorageTestResult))]
[JsonSerializable(typeof(ImportSummary))]
[JsonSerializable(typeof(AuditEntry))]
[JsonSerializable(typeof(List<AuditEntry>))]
[JsonSerializable(typeof(McpAuditSummary))]
[JsonSerializable(typeof(List<McpAuditSummary>))]
[JsonSerializable(typeof(TestSuite))]
[JsonSerializable(typeof(List<TestSuite>))]
[JsonSerializable(typeof(McpSuiteInfo))]
[JsonSerializable(typeof(List<McpSuiteInfo>))]
[JsonSerializable(typeof(McpSuiteDetail))]
[JsonSerializable(typeof(TestRunResult))]
[JsonSerializable(typeof(McpSuiteRunJobInfo))]
[JsonSerializable(typeof(McpJobInfo))]
[JsonSerializable(typeof(McpSuiteValidationInfo))]
[JsonSerializable(typeof(McpSuiteRunInfo))]
[JsonSerializable(typeof(List<McpSuiteRunInfo>))]
[JsonSerializable(typeof(EndpointVerification))]
[JsonSerializable(typeof(EndpointCheck))]
[JsonSerializable(typeof(List<EndpointCheck>))]
[JsonSerializable(typeof(SuiteGenerationResult))]
[JsonSerializable(typeof(SuiteDraftStep))]
[JsonSerializable(typeof(List<SuiteDraftStep>))]
[JsonSerializable(typeof(SuiteReportExport))]
[JsonSerializable(typeof(SuiteRunDiff))]
[JsonSerializable(typeof(SuiteRunStepDiff))]
[JsonSerializable(typeof(List<SuiteRunStepDiff>))]
[JsonSerializable(typeof(SuiteRunNodeDiff))]
[JsonSerializable(typeof(List<SuiteRunNodeDiff>))]
[JsonSerializable(typeof(SuiteFailureExplanation))]
[JsonSerializable(typeof(SuiteFailureFinding))]
[JsonSerializable(typeof(List<SuiteFailureFinding>))]
[JsonSerializable(typeof(McpHistoryInfo))]
[JsonSerializable(typeof(List<McpHistoryInfo>))]
[JsonSerializable(typeof(McpHistoryDetail))]
[JsonSerializable(typeof(McpEnvironmentInfo))]
[JsonSerializable(typeof(List<McpEnvironmentInfo>))]
[JsonSerializable(typeof(McpTrashInfo))]
[JsonSerializable(typeof(List<McpTrashInfo>))]
internal partial class McpJsonContext : JsonSerializerContext { }

public static class McpJson
{
    private static readonly JsonSerializerOptions Options = new(McpJsonContext.Default.Options)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static JsonSerializerOptions SerializerOptions => Options;

    public static string Serialize(object? payload) =>
        payload switch
        {
            JsonNode node => node.ToJsonString(Options),
            null => "null",
            _ => JsonSerializer.Serialize(payload, Info(payload.GetType())),
        };

    public static JsonObject ToObject(object? payload) =>
        payload is JsonObject existing
            ? existing
            : JsonNode.Parse(Serialize(payload)) as JsonObject ?? new JsonObject();

    private static System.Text.Json.Serialization.Metadata.JsonTypeInfo Info(Type type) =>
        Options.GetTypeInfo(type);
}
