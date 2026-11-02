using PostSharp.Services;
using PostSharp.ViewModels;

namespace PostSharp.Tests;

public sealed class PipelineEditorTests
{
    [Fact]
    public void RoundTripsNestedPipelinesAndKeepsAdvancedFields()
    {
        const string yaml = """
            name: 编辑测试
            baseUrl: http://mock.local
            vars:
              token: abc
            data:
              rows:
                - { item: book }
            steps:
              - id: list
                method: GET
                url: /api/list
                expect: { status: 200 }
              - loop:
                  over: ${list}
                  as: item
                  steps:
                    - method: GET
                      url: /api/items/${item.id}
              - parallel:
                  max: 2
                  steps:
                    - { method: GET, url: /api/a }
                    - { method: GET, url: /api/b }
            """;

        var editor = new PipelineEditorViewModel(yaml);
        Assert.Equal(3, editor.Nodes.Count);
        var loop = editor.Nodes[1];
        Assert.Equal("loop", loop.Kind);
        Assert.Single(loop.Children);

        var output = editor.ToYaml();
        var parsed = PipelineParser.Parse(output);

        Assert.False(parsed.HasErrors, string.Join("; ", parsed.Issues.Select(i => i.Message)));
        Assert.Equal(3, parsed.Pipeline.Steps.Count);
        Assert.Equal("abc", parsed.Pipeline.Variables["token"]);
        Assert.Single(parsed.Pipeline.Data);
        Assert.Equal("item", parsed.Pipeline.Steps[1].LoopAlias);
    }

    [Fact]
    public void EditsRequestFieldsAndBody()
    {
        const string yaml = """
            name: 单项编辑
            baseUrl: http://mock.local
            steps:
              - method: GET
                url: /api/one
            """;

        var editor = new PipelineEditorViewModel(yaml);
        var node = Assert.Single(editor.Nodes);
        node.Name = "创建";
        node.Method = "POST";
        node.Url = "/api/orders";
        node.Id = "created";
        node.BodyKind = "json";
        node.BodyText = """{"item":"book"}""";
        node.ExpectStatus = "201";
        node.JsonPathText = "id exists";

        var output = editor.ToYaml();
        var parsed = PipelineParser.Parse(output);

        Assert.False(parsed.HasErrors, string.Join("; ", parsed.Issues.Select(i => i.Message)));
        var step = parsed.Pipeline.Steps[0];
        Assert.Equal("POST", step.Method);
        Assert.Equal("/api/orders", step.Url);
        Assert.Equal("created", step.Id);
        Assert.Equal(201, step.Expectation.Statuses[0]);
        Assert.Single(step.Expectation.JsonPath);
        Assert.NotNull(step.JsonBody);
    }

    [Fact]
    public void WriterProducesParseableYaml()
    {
        var text = PipelineYamlWriter.Write(
            System.Text.Json.Nodes.JsonNode.Parse(
                """{"name":"x","steps":[{"method":"GET","url":"/a","json":{"a":1,"b":"t"}}]}"""
            )!
        );

        var parsed = PipelineParser.Parse(text);

        Assert.False(parsed.HasErrors, string.Join("; ", parsed.Issues.Select(i => i.Message)));
        Assert.Single(parsed.Pipeline.Steps);
        Assert.Contains("json:", text, StringComparison.Ordinal);
    }
}
