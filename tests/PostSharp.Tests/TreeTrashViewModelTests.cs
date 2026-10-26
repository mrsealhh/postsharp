using System.Collections.ObjectModel;
using PostSharp.Models;
using PostSharp.ViewModels;

namespace PostSharp.Tests;

public sealed class TreeTrashViewModelTests
{
    private readonly ObservableCollection<RequestCollection> _collections = new();
    private readonly List<string> _toasts = new();
    private readonly TreeTrashViewModel _trash;
    private readonly RequestCollection _first = new() { Name = "A" };
    private readonly RequestCollection _child = new() { Name = "A1" };
    private readonly RequestCollection _second = new() { Name = "B" };

    public TreeTrashViewModelTests()
    {
        _trash = new TreeTrashViewModel(_collections, () => { }, _toasts.Add);
        _child.Parent = _first;
        _first.Children.Add(_child);
        _collections.Add(_first);
        _collections.Add(_child);
        _collections.Add(_second);
    }

    [Fact]
    public void RestoresCollectionWithItsSubtreeInPlace()
    {
        var request = AddRequest(_child, "登录");

        _trash.TrashCollection(_first, "MCP");

        Assert.Equal([_second], _collections);
        Assert.Equal("MCP", _trash.Items[0].Source);
        Assert.Equal(1, _trash.Items[0].RequestCount);

        _trash.Items[0].RestoreCommand!.Execute(null);

        Assert.Equal([_first, _child, _second], _collections);
        Assert.Same(_first, _child.Parent);
        Assert.Same(_child, request.Owner);
        Assert.Empty(_trash.Items);
    }

    [Fact]
    public void RestoresRequestAtItsPositionWithTheSameId()
    {
        AddRequest(_second, "一");
        var request = AddRequest(_second, "二");
        AddRequest(_second, "三");
        var id = request.Id;

        _trash.TrashRequest(request, "UI");
        _trash.Items[0].RestoreCommand!.Execute(null);

        Assert.Equal(1, _second.Requests.IndexOf(request));
        Assert.Equal(id, request.Id);
        Assert.Same(_second, request.Owner);
    }

    [Fact]
    public void RestoresRequestToTopLevelWhenItsCollectionIsGone()
    {
        var request = AddRequest(_child, "登录");

        _trash.TrashRequest(request, "UI");
        _trash.TrashCollection(_first, "UI");
        _trash.Items.Single(node => node.Request is not null).RestoreCommand!.Execute(null);

        Assert.Same(_second, request.Owner);
        Assert.Contains("已不存在", _toasts[^1]);
    }

    [Fact]
    public void KeepsOnlyTheNewestItemsUpToCapacity()
    {
        for (var index = 0; index <= TreeTrashViewModel.Capacity; index++)
        {
            _trash.TrashRequest(AddRequest(_second, $"请求 {index}"), "MCP");
        }

        Assert.Equal(TreeTrashViewModel.Capacity, _trash.Items.Count);
        Assert.Equal($"请求 {TreeTrashViewModel.Capacity}", _trash.Items[0].Name);
        Assert.DoesNotContain(_trash.Items, node => node.Name == "请求 0");
    }

    [Fact]
    public void RefusesARequestThatIsInNoCollection()
    {
        Assert.False(_trash.TrashRequest(new RequestItem { Name = "孤立" }, "UI"));
        Assert.Empty(_trash.Items);
    }

    private static RequestItem AddRequest(RequestCollection collection, string name)
    {
        var request = new RequestItem { Name = name, Owner = collection };
        collection.Requests.Add(request);
        return request;
    }
}
