using System.Collections.ObjectModel;
using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using PostSharp.Models;
using TKCode.Arc;

namespace PostSharp.ViewModels;

public sealed record WorkflowPaletteItem(
    string Id,
    string Kind,
    Guid CollectionId,
    Guid RequestId,
    string Name,
    string Description,
    int Indent,
    bool IsCollection
)
{
    public Thickness IndentMargin => new(Indent * 14, 0, 0, 0);
}

public sealed record WorkflowSuiteResult(TestSuite Suite);

public sealed partial class WorkflowEditorViewModel : ObservableObject
{
    private readonly Dictionary<string, RequestItem> _requests = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, RequestCollection> _collections = new();
    private readonly Dictionary<string, WorkflowNode> _nodes = new(StringComparer.Ordinal);

    public WorkflowEditorViewModel(IEnumerable<RequestCollection> collections)
    {
        foreach (var collection in collections)
        {
            AddCollection(collection, 0);
        }

        SuiteName = "工作流套件";
    }

    public ObservableCollection<WorkflowPaletteItem> Palette { get; } = new();

    public WorkflowGraph Graph { get; } = new();

    [ObservableProperty]
    private string _suiteName = "工作流套件";

    [ObservableProperty]
    private string _statusText = "从左侧拖入集合或接口，再拖动节点端口连接流程";

    public WorkflowNode AddNode(WorkflowPaletteItem item, Point position)
    {
        if (_nodes.TryGetValue(item.Id, out var existing) && Graph.Nodes.Contains(existing))
        {
            existing.Position = position;
            return existing;
        }

        var node = new WorkflowNode(item.Id, item.Name)
        {
            Kind = item.Kind,
            Description = item.Description,
            Position = position,
        };
        _nodes[item.Id] = node;
        Graph.Nodes.Add(node);
        StatusText = $"已添加：{item.Name}";
        return node;
    }

    public void Clear()
    {
        Graph.Nodes.Clear();
        Graph.Edges.Clear();
        _nodes.Clear();
        StatusText = "画布已清空";
    }

    public WorkflowSuiteResult? BuildSuite()
    {
        var ordered = TopologicalOrder();
        if (ordered.Count == 0)
        {
            StatusText = "至少拖入一个节点后才能创建套件";
            return null;
        }

        var suite = new TestSuite
        {
            Name = string.IsNullOrWhiteSpace(SuiteName) ? "工作流套件" : SuiteName.Trim(),
            StopOnError = true,
        };
        var stepRequests = new HashSet<Guid>();

        foreach (var node in ordered)
        {
            if (!_paletteById.TryGetValue(node.Id, out var item))
            {
                continue;
            }

            if (item.IsCollection)
            {
                if (_collections.TryGetValue(item.CollectionId, out var collection))
                {
                    AddCollectionSteps(collection, suite, stepRequests);
                }
            }
            else if (_requests.TryGetValue(item.Id, out var request))
            {
                AddStep(request, suite, stepRequests);
            }
        }

        if (suite.Steps.Count == 0)
        {
            StatusText = "工作流中没有可执行的请求";
            return null;
        }

        suite.Workflow = CaptureWorkflow();
        StatusText = $"已生成 {suite.Steps.Count} 个执行步骤";
        return new WorkflowSuiteResult(suite);
    }

    private readonly Dictionary<string, WorkflowPaletteItem> _paletteById = new(
        StringComparer.Ordinal
    );

    private void AddCollection(RequestCollection collection, int indent)
    {
        _collections[collection.Id] = collection;
        var collectionItem = new WorkflowPaletteItem(
            $"collection:{collection.Id}",
            "collection",
            collection.Id,
            Guid.Empty,
            collection.Name,
            $"{collection.TotalRequestCount} 个请求",
            indent,
            true
        );
        Palette.Add(collectionItem);
        _paletteById[collectionItem.Id] = collectionItem;

        foreach (var request in collection.Requests)
        {
            _requests[$"request:{request.Id}"] = request;
            var requestItem = new WorkflowPaletteItem(
                $"request:{request.Id}",
                "request",
                collection.Id,
                request.Id,
                request.Name,
                $"{request.Method} {request.Url}",
                indent + 1,
                false
            );
            Palette.Add(requestItem);
            _paletteById[requestItem.Id] = requestItem;
        }

        foreach (var child in collection.Children)
        {
            AddCollection(child, indent + 1);
        }
    }

    private void AddCollectionSteps(
        RequestCollection collection,
        TestSuite suite,
        HashSet<Guid> stepRequests
    )
    {
        foreach (var request in collection.Requests)
        {
            AddStep(request, suite, stepRequests);
        }

        foreach (var child in collection.Children)
        {
            AddCollectionSteps(child, suite, stepRequests);
        }
    }

    private static void AddStep(RequestItem request, TestSuite suite, HashSet<Guid> stepRequests)
    {
        if (!stepRequests.Add(request.Id))
        {
            return;
        }

        suite.Steps.Add(
            new TestStep
            {
                RequestId = request.Id,
                RequestName = request.Name,
                Method = request.Method,
                Url = request.Url,
                DelayMs = 200,
            }
        );
    }

    private IReadOnlyList<WorkflowNode> TopologicalOrder()
    {
        var nodes = Graph.Nodes.ToList();
        var byId = nodes.ToDictionary(node => node.Id, StringComparer.Ordinal);
        var incoming = nodes.ToDictionary(node => node.Id, _ => 0, StringComparer.Ordinal);
        var outgoing = nodes.ToDictionary(
            node => node.Id,
            _ => new List<WorkflowNode>(),
            StringComparer.Ordinal
        );

        foreach (var edge in Graph.Edges)
        {
            if (!byId.ContainsKey(edge.Source.Id) || !byId.ContainsKey(edge.Target.Id))
            {
                continue;
            }

            outgoing[edge.Source.Id].Add(edge.Target);
            incoming[edge.Target.Id]++;
        }

        var result = new List<WorkflowNode>(nodes.Count);
        var queue = nodes.Where(node => incoming[node.Id] == 0).ToList();
        while (queue.Count > 0)
        {
            var node = queue[0];
            queue.RemoveAt(0);
            result.Add(node);
            foreach (var target in outgoing[node.Id])
            {
                incoming[target.Id]--;
                if (incoming[target.Id] == 0)
                {
                    queue.Add(target);
                }
            }
        }

        foreach (var node in nodes.Where(node => !result.Contains(node)))
        {
            result.Add(node);
        }

        return result;
    }

    private SuiteWorkflow CaptureWorkflow()
    {
        var workflow = new SuiteWorkflow();
        foreach (var node in Graph.Nodes)
        {
            if (!_paletteById.TryGetValue(node.Id, out var item))
            {
                continue;
            }

            workflow.Nodes.Add(
                new SuiteWorkflowNode
                {
                    Id = node.Id,
                    Kind = item.Kind,
                    CollectionId = item.CollectionId,
                    RequestId = item.RequestId,
                    X = node.Position.X,
                    Y = node.Position.Y,
                }
            );
        }

        foreach (var edge in Graph.Edges)
        {
            workflow.Edges.Add(
                new SuiteWorkflowEdge { SourceId = edge.Source.Id, TargetId = edge.Target.Id }
            );
        }

        return workflow;
    }
}
