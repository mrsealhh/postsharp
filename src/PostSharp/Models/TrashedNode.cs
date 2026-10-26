using CommunityToolkit.Mvvm.Input;
using TKCode.Arc;

namespace PostSharp.Models;

/// <summary>
/// A deleted collection (with everything under it) or a deleted request, kept in the recycle bin so the delete can
/// be undone. The objects keep their ids, so suites that use them work again after a restore.
/// </summary>
public sealed class TrashedNode
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public DateTimeOffset DeletedAt { get; init; } = DateTimeOffset.Now;

    /// <summary>UI or MCP.</summary>
    public string Source { get; init; } = "UI";

    /// <summary>The deleted collection; its sub-collections and requests stay attached to it.</summary>
    public RequestCollection? Collection { get; init; }

    public RequestItem? Request { get; init; }

    /// <summary>The collection it was in: the parent of a collection, the owner of a request.</summary>
    public Guid? OriginalParentId { get; init; }

    public string OriginalLocation { get; init; } = "根目录";

    public int OriginalPosition { get; init; }

    public string Name => Collection?.Name ?? Request?.Name ?? string.Empty;

    public string KindLabel => Collection is not null ? "集合" : "请求";

    public string BadgeLabel => Request?.Method ?? "集合";

    public BadgeTone BadgeTone =>
        Request is null ? BadgeTone.Neutral : MethodTones.Of(Request.Method);

    public int RequestCount => Collection is null ? 1 : Collection.TotalRequestCount;

    public string Detail =>
        Collection is not null
            ? $"{RequestCount} 个请求 · 原位置：{OriginalLocation}"
            : $"原位置：{OriginalLocation} · {Request?.Url}";

    public string DeletedText => DeletedAt.LocalDateTime.ToString("MM-dd HH:mm");

    public string DeletedTooltip =>
        $"{DeletedAt.LocalDateTime:yyyy-MM-dd HH:mm:ss} 由{(Source == "MCP" ? " AI · MCP " : "界面")}删除";

    public IRelayCommand? RestoreCommand { get; set; }

    public IRelayCommand? PurgeCommand { get; set; }
}
