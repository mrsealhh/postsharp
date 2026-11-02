using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PostSharp.Services;

namespace PostSharp.ViewModels;

/// <summary>图像编辑器里的一个节点：请求、循环或并发组。</summary>
public sealed partial class PipelineEditorNode : ObservableObject
{
    public ObservableCollection<PipelineEditorNode> Children { get; } = new();

    public PipelineEditorNode? Parent { get; set; }

    public JsonObject? Raw { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title))]
    [NotifyPropertyChangedFor(nameof(IsRequest))]
    [NotifyPropertyChangedFor(nameof(IsLoop))]
    [NotifyPropertyChangedFor(nameof(IsParallel))]
    [NotifyPropertyChangedFor(nameof(IsGroup))]
    private string _kind = "request";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title))]
    private string _id = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title))]
    private string _name = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title))]
    private string _method = "GET";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title))]
    private string _url = string.Empty;

    [ObservableProperty]
    private string _headersText = string.Empty;

    [ObservableProperty]
    private string _queryText = string.Empty;

    [ObservableProperty]
    private string _formText = string.Empty;

    [ObservableProperty]
    private string _bodyKind = "none";

    [ObservableProperty]
    private string _bodyText = string.Empty;

    [ObservableProperty]
    private string _expectStatus = string.Empty;

    [ObservableProperty]
    private string _bodyContains = string.Empty;

    [ObservableProperty]
    private string _jsonPathText = string.Empty;

    [ObservableProperty]
    private string _retryCountText = "0";

    [ObservableProperty]
    private string _retryDelayText = "500";

    [ObservableProperty]
    private string _onFailure = "stop";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title))]
    private string _loopOver = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title))]
    private string _loopAs = "item";

    [ObservableProperty]
    private string _loopIndex = string.Empty;

    [ObservableProperty]
    private string _loopMaxText = "1000";

    [ObservableProperty]
    private string _parallelMaxText = "0";

    public bool IsRequest => Kind == "request";

    public bool IsLoop => Kind == "loop";

    public bool IsParallel => Kind == "parallel";

    public bool IsGroup => !IsRequest;

    public string Title =>
        Kind switch
        {
            "loop" => $"循环 {LoopAs} ∈ {LoopOver}",
            "parallel" => $"并发组 max={ParallelMaxText}",
            _ => string.IsNullOrWhiteSpace(Name) ? $"{Method} {Url}" : Name,
        };

    public string Detail =>
        Kind switch
        {
            "request" => $"{Method} {Url}",
            "loop" => $"循环体 {Children.Count} 个节点",
            _ => $"并发 {Children.Count} 个节点",
        };

    public void RefreshDetail() => OnPropertyChanged(nameof(Detail));
}

/// <summary>
/// 流水线图像编辑器：把 YAML 解析成请求/循环/并发节点树，用树形画布增删改，
/// 保存时只改写 `steps` 和名称/地址，保留 data、vars、retry 等未编辑字段。
/// </summary>
public sealed partial class PipelineEditorViewModel : ObservableObject
{
    private JsonObject _root = new();

    public PipelineEditorViewModel(string yaml)
    {
        var node = ParseRoot(yaml);
        if (node is not JsonObject root)
        {
            throw new InvalidOperationException("流水线根节点必须是对象。");
        }

        _root = root;
        Name = Text(root["name"]) ?? "未命名流水线";
        BaseUrl = Text(root["baseUrl"]) ?? string.Empty;
        foreach (var step in root["steps"] as JsonArray ?? new JsonArray())
        {
            if (step is JsonObject map)
            {
                Nodes.Add(FromJson(map));
            }
        }

        SelectedNode = Nodes.FirstOrDefault();
    }

    public ObservableCollection<PipelineEditorNode> Nodes { get; } = new();

    [ObservableProperty]
    private PipelineEditorNode? _selectedNode;

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _baseUrl = string.Empty;

    [ObservableProperty]
    private string _statusText = "选择左侧节点编辑；分组可以继续添加子步骤。";

    partial void OnSelectedNodeChanged(PipelineEditorNode? value) =>
        OnPropertyChanged(nameof(HasSelection));

    public bool HasSelection => SelectedNode is not null;

    [RelayCommand]
    private void AddRequest() => Add(new PipelineEditorNode { Kind = "request" });

    [RelayCommand]
    private void AddLoop()
    {
        var node = new PipelineEditorNode
        {
            Kind = "loop",
            LoopOver = "${list}",
            LoopAs = "item",
        };
        node.Children.Add(new PipelineEditorNode { Kind = "request", Name = "循环步骤" });
        Add(node);
    }

    [RelayCommand]
    private void AddParallel()
    {
        var node = new PipelineEditorNode { Kind = "parallel", ParallelMaxText = "0" };
        node.Children.Add(new PipelineEditorNode { Kind = "request", Name = "并行步骤 A" });
        node.Children.Add(new PipelineEditorNode { Kind = "request", Name = "并行步骤 B" });
        Add(node);
    }

    [RelayCommand]
    private void RemoveNode()
    {
        if (SelectedNode is not { } node)
        {
            return;
        }

        var list = ListOf(node);
        var index = list.IndexOf(node);
        list.Remove(node);
        SelectedNode = list.Count == 0 ? null : list[Math.Clamp(index, 0, list.Count - 1)];
        StatusText = "已删除节点。";
    }

    [RelayCommand]
    private void MoveUp()
    {
        if (SelectedNode is not { } node)
        {
            return;
        }

        var list = ListOf(node);
        var index = list.IndexOf(node);
        if (index > 0)
        {
            list.Move(index, index - 1);
        }
    }

    [RelayCommand]
    private void MoveDown()
    {
        if (SelectedNode is not { } node)
        {
            return;
        }

        var list = ListOf(node);
        var index = list.IndexOf(node);
        if (index >= 0 && index < list.Count - 1)
        {
            list.Move(index, index + 1);
        }
    }

    [RelayCommand]
    private void AddChildRequest()
    {
        if (SelectedNode is { IsGroup: true } group)
        {
            group.Children.Add(new PipelineEditorNode { Kind = "request", Name = "子步骤" });
            group.RefreshDetail();
        }
    }

    [RelayCommand]
    private void AddChildLoop()
    {
        if (SelectedNode is { IsGroup: true } group)
        {
            group.Children.Add(
                new PipelineEditorNode
                {
                    Kind = "loop",
                    LoopOver = "${list}",
                    LoopAs = "item",
                }
            );
            group.RefreshDetail();
        }
    }

    [RelayCommand]
    private void AddChildParallel()
    {
        if (SelectedNode is { IsGroup: true } group)
        {
            group.Children.Add(new PipelineEditorNode { Kind = "parallel" });
            group.RefreshDetail();
        }
    }

    public string ToYaml()
    {
        try
        {
            var root = (JsonObject)_root.DeepClone();
            root["name"] = Name.Trim();
            var baseUrl = BaseUrl.Trim();
            if (baseUrl.Length == 0)
            {
                root.Remove("baseUrl");
            }
            else
            {
                root["baseUrl"] = baseUrl;
            }

            var steps = new JsonArray();
            foreach (var node in Nodes)
            {
                steps.Add((JsonNode)ToJson(node));
            }

            root["steps"] = steps;
            var yaml = PipelineYamlWriter.Write(root);
            var parsed = PipelineParser.Parse(yaml);
            if (parsed.HasErrors)
            {
                StatusText =
                    "保存失败："
                    + string.Join(
                        "；",
                        parsed
                            .Issues.Where(issue => issue.IsError)
                            .Take(5)
                            .Select(issue => issue.Message)
                    );
                throw new InvalidOperationException(StatusText);
            }

            return yaml;
        }
        catch (JsonException exception)
        {
            StatusText = $"保存失败：JSON 格式无效（{exception.Message}）。";
            throw new InvalidOperationException(StatusText, exception);
        }
    }

    private void Add(PipelineEditorNode node)
    {
        var target = SelectedNode switch
        {
            { IsGroup: true } group => group,
            { } selected => selected.Parent,
            _ => null,
        };
        if (target is null)
        {
            Nodes.Add(node);
        }
        else
        {
            target.Children.Add(node);
            target.RefreshDetail();
        }

        SelectedNode = node;
        StatusText = $"已添加{node.Title}。";
    }

    private ObservableCollection<PipelineEditorNode> ListOf(PipelineEditorNode node) =>
        node.Parent is { } parent ? parent.Children : Nodes;

    private static JsonObject ToJson(PipelineEditorNode node)
    {
        if (node.Kind == "loop")
        {
            var loop = new JsonObject
            {
                ["over"] = node.LoopOver.Trim(),
                ["as"] = node.LoopAs.Trim(),
                ["steps"] = BuildSteps(node.Children),
            };
            var index = node.LoopIndex.Trim();
            if (index.Length > 0)
            {
                loop["index"] = index;
            }

            var max = ParseInt(node.LoopMaxText, 0);
            if (max > 0 && max != 1000)
            {
                loop["max"] = max;
            }

            var step = new JsonObject { ["loop"] = loop };
            ApplyCommon(step, node);
            return step;
        }

        if (node.Kind == "parallel")
        {
            var group = new JsonObject { ["steps"] = BuildSteps(node.Children) };
            var max = ParseInt(node.ParallelMaxText, 0);
            if (max > 0)
            {
                group["max"] = max;
            }

            var step = new JsonObject { ["parallel"] = group };
            ApplyCommon(step, node);
            return step;
        }

        var request = (JsonObject?)node.Raw?.DeepClone() ?? new JsonObject();
        ApplyCommon(request, node);
        request.Remove("saveAs");
        request.Remove("as");
        request["method"] = node.Method.Trim().ToUpperInvariant();
        request["url"] = node.Url.Trim();

        SetMap(request, "headers", node.HeadersText, ':');
        SetMap(request, "query", node.QueryText, ':');
        SetMap(request, "form", node.FormText, ':');

        request.Remove("json");
        request.Remove("body");
        if (node.BodyKind == "json" && !string.IsNullOrWhiteSpace(node.BodyText))
        {
            request["json"] = JsonNode.Parse(node.BodyText);
        }
        else if (node.BodyKind == "raw" && node.BodyText.Length > 0)
        {
            request["body"] = node.BodyText;
        }

        ApplyExpectation(request, node);
        ApplyRetry(request, node);
        return request;
    }

    private static void ApplyCommon(JsonObject step, PipelineEditorNode node)
    {
        var id = node.Id.Trim();
        if (id.Length == 0)
        {
            step.Remove("id");
        }
        else
        {
            step["id"] = id;
        }

        var name = node.Name.Trim();
        if (name.Length == 0)
        {
            step.Remove("name");
        }
        else
        {
            step["name"] = name;
        }

        var onFailure = node.OnFailure.Trim();
        if (onFailure is "continue" or "stop")
        {
            step["onFailure"] = onFailure;
        }
        else
        {
            step.Remove("onFailure");
        }
    }

    private static void ApplyExpectation(JsonObject step, PipelineEditorNode node)
    {
        var expect = (JsonObject?)step["expect"]?.DeepClone() ?? new JsonObject();
        var status = node.ExpectStatus.Trim();
        if (status.Length == 0)
        {
            expect.Remove("status");
        }
        else if (status.EndsWith("xx", StringComparison.OrdinalIgnoreCase))
        {
            expect["status"] = status;
        }
        else if (status.Contains(',', StringComparison.Ordinal))
        {
            expect["status"] = new JsonArray(
                status
                    .Split(
                        ',',
                        StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
                    )
                    .Select(part => (JsonNode)JsonValue.Create(ParseInt(part, 200))!)
                    .ToArray()
            );
        }
        else
        {
            expect["status"] = ParseInt(status, 200);
        }

        var contains = node.BodyContains.Trim();
        if (contains.Length == 0)
        {
            expect.Remove("bodyContains");
        }
        else
        {
            expect["bodyContains"] = contains;
        }

        var paths = ParseJsonAssertions(node.JsonPathText);
        if (paths.Count == 0)
        {
            expect.Remove("jsonPath");
        }
        else
        {
            expect["jsonPath"] = paths;
        }

        if (expect.Count == 0)
        {
            step.Remove("expect");
        }
        else
        {
            step["expect"] = expect;
        }
    }

    private static void ApplyRetry(JsonObject step, PipelineEditorNode node)
    {
        var count = ParseInt(node.RetryCountText, 0);
        var delay = ParseInt(node.RetryDelayText, 500);
        if (count <= 0)
        {
            step.Remove("retry");
            return;
        }

        var retry = (JsonObject?)step["retry"]?.DeepClone() ?? new JsonObject();
        retry["count"] = count;
        retry["delayMs"] = delay;
        step["retry"] = retry;
    }

    private static JsonArray BuildSteps(IEnumerable<PipelineEditorNode> nodes)
    {
        var array = new JsonArray();
        foreach (var node in nodes)
        {
            array.Add((JsonNode)ToJson(node));
        }

        return array;
    }

    private static JsonObject ParseJsonAssertions(string text)
    {
        var result = new JsonObject();
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (line.EndsWith(" exists", StringComparison.OrdinalIgnoreCase))
            {
                result[line[..^7].Trim()] = new JsonObject { ["exists"] = true };
                continue;
            }

            var contains = line.IndexOf(" ~ ", StringComparison.Ordinal);
            if (contains > 0)
            {
                result[line[..contains].Trim()] = new JsonObject
                {
                    ["contains"] = ScalarNode(line[(contains + 3)..].Trim()),
                };
                continue;
            }

            var equals = line.IndexOf(" = ", StringComparison.Ordinal);
            if (equals > 0)
            {
                result[line[..equals].Trim()] = new JsonObject
                {
                    ["equals"] = ScalarNode(line[(equals + 3)..].Trim()),
                };
            }
        }

        return result;
    }

    private static JsonNode ScalarNode(string text)
    {
        try
        {
            return JsonNode.Parse(text) ?? JsonValue.Create(text)!;
        }
        catch (JsonException)
        {
            return JsonValue.Create(text)!;
        }
    }

    private static void SetMap(JsonObject step, string key, string text, char separator)
    {
        var map = new JsonObject();
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var index = line.IndexOf(separator);
            if (index <= 0)
            {
                continue;
            }

            map[line[..index].Trim()] = line[(index + 1)..].Trim();
        }

        if (map.Count == 0)
        {
            step.Remove(key);
        }
        else
        {
            step[key] = map;
        }
    }

    private static PipelineEditorNode FromJson(JsonObject step)
    {
        if (step["loop"] is JsonObject loop)
        {
            var node = new PipelineEditorNode
            {
                Kind = "loop",
                Raw = (JsonObject)step.DeepClone(),
                Id = Text(step["id"]) ?? string.Empty,
                Name = Text(step["name"]) ?? string.Empty,
                LoopOver = Text(loop["over"]) ?? string.Empty,
                LoopAs = Text(loop["as"]) ?? "item",
                LoopIndex = Text(loop["index"]) ?? string.Empty,
                LoopMaxText = loop["max"]?.ToJsonString() ?? "1000",
                OnFailure = Text(step["onFailure"]) ?? "stop",
            };
            foreach (var child in loop["steps"] as JsonArray ?? new JsonArray())
            {
                if (child is JsonObject map)
                {
                    var parsed = FromJson(map);
                    parsed.Parent = node;
                    node.Children.Add(parsed);
                }
            }

            return node;
        }

        if (step["parallel"] is JsonObject or JsonArray)
        {
            var container = step["parallel"] as JsonObject;
            var children =
                (container?["steps"] ?? step["parallel"] as JsonArray) as JsonArray
                ?? new JsonArray();
            var node = new PipelineEditorNode
            {
                Kind = "parallel",
                Raw = (JsonObject)step.DeepClone(),
                Id = Text(step["id"]) ?? string.Empty,
                Name = Text(step["name"]) ?? string.Empty,
                ParallelMaxText = container?["max"]?.ToJsonString() ?? "0",
                OnFailure = Text(step["onFailure"]) ?? "stop",
            };
            foreach (var child in children)
            {
                if (child is JsonObject map)
                {
                    var parsed = FromJson(map);
                    parsed.Parent = node;
                    node.Children.Add(parsed);
                }
            }

            return node;
        }

        var request = new PipelineEditorNode
        {
            Kind = "request",
            Raw = (JsonObject)step.DeepClone(),
            Id = Text(step["id"]) ?? Text(step["saveAs"]) ?? string.Empty,
            Name = Text(step["name"]) ?? string.Empty,
            Method = Text(step["method"]) ?? "GET",
            Url = Text(step["url"]) ?? string.Empty,
            HeadersText = MapText(step["headers"]),
            QueryText = MapText(step["query"]),
            FormText = MapText(step["form"]),
            ExpectStatus = StatusTextOf(step["expect"]),
            BodyContains = ContainsText(step["expect"]),
            JsonPathText = JsonPathText(step["expect"]),
            RetryCountText = RetryValue(step["retry"], "count") ?? "0",
            RetryDelayText = RetryValue(step["retry"], "delayMs") ?? "500",
            OnFailure = Text(step["onFailure"]) ?? "stop",
        };
        if (step["json"] is { } json)
        {
            request.BodyKind = "json";
            request.BodyText = json.ToJsonString(IndentedJson);
        }
        else if (Text(step["body"]) is { } body)
        {
            request.BodyKind = "raw";
            request.BodyText = body;
        }

        return request;
    }

    private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true };

    private static string MapText(JsonNode? node)
    {
        if (node is not JsonObject map)
        {
            return string.Empty;
        }

        return string.Join(
            "\n",
            map.Select(entry => $"{entry.Key}: {Text(entry.Value) ?? string.Empty}")
        );
    }

    private static string StatusTextOf(JsonNode? expect) =>
        expect is not JsonObject map ? string.Empty
        : map["status"] is JsonArray array
            ? string.Join(", ", array.Select(item => item?.ToJsonString() ?? string.Empty))
        : Text(map["status"]) ?? string.Empty;

    private static string ContainsText(JsonNode? expect) =>
        expect is not JsonObject map ? string.Empty
        : map["bodyContains"] is JsonArray array
            ? string.Join("\n", array.Select(item => Text(item) ?? string.Empty))
        : Text(map["bodyContains"]) ?? string.Empty;

    private static string JsonPathText(JsonNode? expect)
    {
        if (expect is not JsonObject map || map["jsonPath"] is not JsonObject paths)
        {
            return string.Empty;
        }

        var lines = new List<string>();
        foreach (var entry in paths)
        {
            if (entry.Value is not JsonObject rule)
            {
                lines.Add($"{entry.Key} = {entry.Value?.ToJsonString() ?? "null"}");
                continue;
            }

            if (rule["exists"] is JsonValue value && value.TryGetValue<bool>(out var exists))
            {
                lines.Add(exists ? $"{entry.Key} exists" : $"{entry.Key} not-exists");
                continue;
            }

            if (rule["contains"] is { } contains)
            {
                lines.Add($"{entry.Key} ~ {contains.ToJsonString().Trim('"')}");
                continue;
            }

            if (rule["equals"] is { } equals)
            {
                lines.Add($"{entry.Key} = {equals.ToJsonString()}");
            }
        }

        return string.Join("\n", lines);
    }

    private static string? RetryValue(JsonNode? retry, string key) =>
        retry is JsonObject map && map[key] is { } value ? value.ToJsonString() : null;

    private static int ParseInt(string text, int fallback) =>
        int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : fallback;

    private static string? Text(JsonNode? node)
    {
        if (node is not JsonValue value)
        {
            return null;
        }

        return value.TryGetValue<string>(out var text) ? text : value.ToJsonString();
    }

    private static JsonNode ParseRoot(string yaml)
    {
        var trimmed = yaml.TrimStart();
        if (trimmed.StartsWith('{'))
        {
            return JsonNode.Parse(yaml) ?? throw new InvalidOperationException("流水线内容为空。");
        }

        return YamlJson.Parse(yaml, "流水线");
    }
}
