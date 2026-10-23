using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;
using TKCode.Arc;

namespace PostSharp.Models;

/// <summary>A saved automation suite: an ordered list of requests with optional assertions.</summary>
public partial class TestSuite : ObservableObject
{
    public Guid Id { get; init; } = Guid.NewGuid();

    [ObservableProperty]
    private string _name = "新建套件";

    [ObservableProperty]
    private string _lastSummary = "尚未运行";

    public bool StopOnError { get; set; } = true;

    public List<TestStep> Steps { get; } = new();

    public override string ToString() => Name;
}

public sealed class TestStep
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid RequestId { get; set; }

    public string RequestName { get; set; } = string.Empty;

    public string Method { get; set; } = "GET";

    public string Url { get; set; } = string.Empty;

    /// <summary>Delay before the following step, in milliseconds.</summary>
    public int DelayMs { get; set; } = 200;

    public List<InputMapping> InputMappings { get; } = new();

    public int? ExpectedStatus { get; set; }

    public string? BodyContains { get; set; }

    public BadgeTone MethodTone => Method.ToUpperInvariant() switch
    {
        "GET" => BadgeTone.Info,
        "POST" => BadgeTone.Success,
        "PUT" or "PATCH" => BadgeTone.Warning,
        "DELETE" => BadgeTone.Danger,
        _ => BadgeTone.Neutral
    };

    public string ExpectationText => ExpectedStatus is null && string.IsNullOrWhiteSpace(BodyContains)
        ? "仅检查网络成功"
        : string.Join(" · ", new[]
        {
            ExpectedStatus is null ? null : $"状态 {ExpectedStatus}",
            string.IsNullOrWhiteSpace(BodyContains) ? null : $"包含 {BodyContains}"
        }.Where(value => value is not null));
}

/// <summary>A custom mapping from a previous step's response into this request's input.</summary>
public sealed class InputMapping
{
    public Guid SourceStepId { get; set; }

    /// <summary>Dotted JSON path, for example <c>data.token</c> or <c>items[0].id</c>.</summary>
    public string SourcePath { get; set; } = string.Empty;

    /// <summary>Body, Header, Query or Path.</summary>
    public string TargetKind { get; set; } = "Body";

    /// <summary>Target body path, header name, query name, or URL placeholder name.</summary>
    public string TargetPath { get; set; } = string.Empty;

    /// <summary>Optional template; <c>{value}</c> is replaced with the extracted value.</summary>
    public string Template { get; set; } = "{value}";
}

public sealed class TestStepResult
{
    public Guid StepId { get; set; }

    public Guid RequestId { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Method { get; set; } = "GET";

    public string Url { get; set; } = string.Empty;

    public int StatusCode { get; set; }

    public string StatusText { get; set; } = string.Empty;

    public long ElapsedMs { get; set; }

    public bool Passed { get; set; }

    public string? Error { get; set; }

    public string ResponsePreview { get; set; } = string.Empty;

    public string ResultLabel => Passed ? "通过" : "失败";

    public BadgeTone ResultTone => Passed ? BadgeTone.Success : BadgeTone.Danger;

    [JsonIgnore]
    public object ResultIcon => new ArcGlyph
    {
        Data = Passed ? ArcIcons.CircleCheck : ArcIcons.CircleX,
        Size = 12
    };
}

public sealed class TestRunResult
{
    public Guid SuiteId { get; set; }

    public string SuiteName { get; set; } = string.Empty;

    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.Now;

    public int Total { get; set; }

    public int Passed { get; set; }

    public int Failed { get; set; }

    public bool Stopped { get; set; }

    public List<TestStepResult> Steps { get; set; } = new();

    public string Summary => $"{Passed}/{Total} 通过 · {Timestamp.LocalDateTime:HH:mm:ss}";
}
