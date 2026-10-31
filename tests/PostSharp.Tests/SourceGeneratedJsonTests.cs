using System.Text.Json.Nodes;
using PostSharp.Models;
using PostSharp.Services;
using Xunit;

namespace PostSharp.Tests;

/// <summary>
/// NativeAOT turns reflection-based serialization off, so every local document and every MCP tool
/// result must resolve through a source-generated context. These tests fail if a type is missing.
/// </summary>
public sealed class SourceGeneratedJsonTests
{
    [Fact]
    public void WorkspaceRoundTripsThroughSourceGeneratedContext()
    {
        var workspace = new WorkspaceDto
        {
            ActiveEnvironmentId = Guid.NewGuid(),
            Collections = { new CollectionDto { Name = "接口集合" } },
        };

        var json = DataJson.Serialize(workspace);
        var restored = DataJson.Deserialize<WorkspaceDto>(json);

        Assert.NotNull(restored);
        Assert.Equal(workspace.ActiveEnvironmentId, restored.ActiveEnvironmentId);
        Assert.Single(restored.Collections);
        Assert.Equal("接口集合", restored.Collections[0].Name);
    }

    [Fact]
    public void SettingsRoundTripKeepsChineseTextUnescaped()
    {
        var settings = new AppSettings
        {
            CodeFont = "等线",
            McpReadOnly = true,
            McpAllowSend = false,
            McpAllowWrite = false,
            McpAllowAdmin = true,
        };

        var json = DataJson.Serialize(settings);
        var restored = DataJson.Deserialize<AppSettings>(json);

        Assert.Contains("等线", json);
        Assert.NotNull(restored);
        Assert.Equal("等线", restored.CodeFont);
        Assert.True(restored.McpReadOnly);
        Assert.False(restored.McpAllowSend);
        Assert.False(restored.McpAllowWrite);
        Assert.True(restored.McpAllowAdmin);
    }

    [Fact]
    public void SuitesAndAuditUseTheSameContext()
    {
        var suites = new List<SuiteDto>
        {
            new() { Id = Guid.NewGuid(), Name = "回归套件" },
        };
        var audits = new List<AuditEntry>
        {
            new()
            {
                RequestName = "登录",
                Method = "POST",
                Url = "https://example.com/login",
            },
        };

        Assert.Equal(
            "回归套件",
            DataJson.Deserialize<List<SuiteDto>>(DataJson.Serialize(suites))![0].Name
        );
        Assert.Equal(
            "登录",
            DataJson.Deserialize<List<AuditEntry>>(DataJson.Serialize(audits))![0].RequestName
        );
    }

    [Fact]
    public void FormatIndentsAndMinifiesWithoutReflection()
    {
        using var document = System.Text.Json.JsonDocument.Parse("{\"a\":[1,2]}");

        var indented = DataJson.Format(document.RootElement, indented: true, relaxed: true);
        var compact = DataJson.Format(document.RootElement, indented: false, relaxed: true);

        Assert.Contains("\n", indented);
        Assert.Equal("{\"a\":[1,2]}", compact);
    }

    [Fact]
    public void McpPayloadsSerializeForEveryRegisteredShape()
    {
        var rows = new List<McpRowInfo> { new("Authorization", "Bearer x", true) };
        var send = new McpSendResult(
            Guid.NewGuid(),
            "登录",
            "POST",
            "https://example.com/login",
            200,
            "OK",
            null,
            12,
            34,
            true,
            "application/json",
            "{\"ok\":true}",
            rows
        );
        var job = new McpSuiteRunJobInfo(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "套件",
            "run-1",
            "Running",
            DateTimeOffset.Now,
            null,
            "1/3",
            null,
            null,
            3,
            1,
            [],
            [],
            []
        );

        Assert.Contains("\"requestId\"", McpJson.Serialize(send));
        Assert.Contains("\"statusCode\": 200", McpJson.Serialize(send));
        Assert.Contains("\"suiteName\": \"套件\"", McpJson.Serialize(job));
        Assert.Contains("deleted", McpJson.Serialize(new JsonObject { ["deleted"] = true }));
        Assert.Equal("true", McpJson.Serialize(true));
        Assert.Equal("\"文本\"", McpJson.Serialize("文本"));
    }

    [Fact]
    public void McpPayloadListsResolveConcreteRuntimeTypes()
    {
        var collections = new List<McpCollectionInfo>();
        var audits = new List<McpAuditSummary>();
        var histories = new List<McpHistoryInfo>();
        var environments = new List<McpEnvironmentInfo>();
        var suites = new List<TestSuite>();
        object[] payloads = [collections, audits, histories, environments, suites];

        foreach (var payload in payloads)
        {
            var json = McpJson.Serialize(payload);
            Assert.StartsWith("[", json);
        }
    }

    [Fact]
    public void IntentToolPayloadsSerializeThroughMcpMetadata()
    {
        var verification = new EndpointVerification(
            Guid.NewGuid(),
            "健康检查",
            "GET",
            "https://example.com/health",
            true,
            true,
            200,
            "OK",
            12,
            "通过",
            [new EndpointCheck("Status", "2xx/3xx", "200", true, "状态 200 符合预期")],
            "{\"ok\":true}",
            null
        );
        var generation = new SuiteGenerationResult(
            Saved: false,
            SuiteId: null,
            Name: "生成套件",
            StepCount: 1,
            [
                new SuiteDraftStep(
                    Guid.NewGuid(),
                    "健康检查",
                    "GET",
                    "https://example.com",
                    200,
                    "证据"
                ),
            ],
            ["dryRun"],
            false,
            0,
            "通过"
        );
        var diff = new SuiteRunDiff(
            "套件",
            Guid.NewGuid(),
            DateTimeOffset.Now,
            Guid.NewGuid(),
            DateTimeOffset.Now,
            1,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            "新增失败 1 步。",
            [
                new SuiteRunStepDiff(
                    Guid.NewGuid(),
                    "步骤",
                    "NewFailure",
                    true,
                    false,
                    200,
                    500,
                    1,
                    2,
                    1,
                    "明细"
                ),
            ],
            [new SuiteRunNodeDiff(Guid.NewGuid(), "节点", "Done", "Failed", 1, 2, "Done → Failed")]
        );
        var explanation = new SuiteFailureExplanation(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "套件",
            false,
            "失败",
            "下一步",
            [new SuiteFailureFinding(null, string.Empty, "Preflight", "消息", "证据", "提示")]
        );
        var report = new SuiteReportExport("markdown", "report.md", "# 报告", 10, false, null);

        Assert.Contains("\"requestId\"", McpJson.Serialize(verification), StringComparison.Ordinal);
        Assert.Contains(
            "\"expectedStatus\": 200",
            McpJson.Serialize(generation),
            StringComparison.Ordinal
        );
        Assert.Contains("\"newFailures\": 1", McpJson.Serialize(diff), StringComparison.Ordinal);
        Assert.Contains("\"findings\"", McpJson.Serialize(explanation), StringComparison.Ordinal);
        Assert.Contains(
            "\"fileName\": \"report.md\"",
            McpJson.Serialize(report),
            StringComparison.Ordinal
        );

        var job = new McpJobInfo(
            Guid.NewGuid(),
            "run_sequence",
            "Completed",
            DateTimeOffset.Now,
            DateTimeOffset.Now,
            "已完成",
            null,
            "seq-1",
            2,
            2,
            new JsonObject { ["total"] = 2 }
        );
        Assert.Contains("\"jobId\"", McpJson.Serialize(job), StringComparison.Ordinal);
        Assert.Contains(
            "\"kind\": \"run_sequence\"",
            McpJson.Serialize(job),
            StringComparison.Ordinal
        );
    }
}
