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
        var settings = new AppSettings { CodeFont = "等线", McpReadOnly = true };

        var json = DataJson.Serialize(settings);
        var restored = DataJson.Deserialize<AppSettings>(json);

        Assert.Contains("等线", json);
        Assert.NotNull(restored);
        Assert.Equal("等线", restored.CodeFont);
        Assert.True(restored.McpReadOnly);
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
}
