using CommunityToolkit.Mvvm.Input;
using TKCode.Arc;

namespace PostSharp.Models;

public sealed class HistoryEntry
{
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.Now;

    public string Name { get; init; } = string.Empty;

    public string Method { get; init; } = "GET";

    public string Url { get; init; } = string.Empty;

    public string BodyMode { get; init; } = "None";

    public string Body { get; init; } = string.Empty;

    public List<RowSnapshot> Params { get; init; } = new();

    public List<RowSnapshot> Headers { get; init; } = new();

    public int StatusCode { get; init; }

    public long DurationMs { get; init; }

    public IRelayCommand? OpenCommand { get; set; }

    public IRelayCommand? DeleteCommand { get; set; }

    public IRelayCommand? RestoreCommand { get; set; }

    public IRelayCommand? PurgeCommand { get; set; }

    public string DisplayTime => Timestamp.LocalDateTime.ToString("HH:mm:ss");

    public string TimestampText => Timestamp.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss");

    public string StatusLabel => StatusCode > 0 ? StatusCode.ToString() : "失败";

    public BadgeTone StatusTone =>
        StatusCode switch
        {
            >= 200 and < 300 => BadgeTone.Success,
            >= 300 and < 400 => BadgeTone.Info,
            >= 400 and < 500 => BadgeTone.Warning,
            _ => BadgeTone.Danger,
        };

    public object StatusIcon =>
        new ArcGlyph
        {
            Data = StatusCode switch
            {
                >= 200 and < 300 => ArcIcons.CircleCheck,
                >= 300 and < 400 => ArcIcons.Info,
                >= 400 and < 500 => ArcIcons.TriangleAlert,
                _ => ArcIcons.CircleX,
            },
            Size = 12,
        };

    public BadgeTone MethodTone =>
        Method.ToUpperInvariant() switch
        {
            "GET" => BadgeTone.Info,
            "POST" => BadgeTone.Success,
            "PUT" or "PATCH" => BadgeTone.Warning,
            "DELETE" => BadgeTone.Danger,
            _ => BadgeTone.Neutral,
        };

    public RequestItem ToRequest()
    {
        var request = new RequestItem
        {
            Name = string.IsNullOrWhiteSpace(Name) ? "历史请求" : $"{Name}（历史）",
            Method = Method,
            Url = Url,
            BodyMode = BodyMode,
            Body = Body,
        };

        foreach (var row in Params)
        {
            request.Params.AddRow(row.Key, row.Value, row.IsEnabled);
        }

        foreach (var row in Headers)
        {
            request.Headers.AddRow(row.Key, row.Value, row.IsEnabled);
        }

        request.Params.EnsureTrailingRow();
        request.Headers.EnsureTrailingRow();
        return request;
    }
}

public sealed record RowSnapshot(string Key, string Value, bool IsEnabled);

public sealed record ResponseHeaderView(string Key, string Value);
