using PostSharp.Models;
using PostSharp.Services;
using Xunit;

namespace PostSharp.Tests;

public sealed class CollectionDuplicatorTests
{
    private static RequestCollection BuildTree(out RequestItem originalRequest)
    {
        var root = new RequestCollection
        {
            Name = "订单",
            IsFavorite = true,
            IsExpanded = false,
        };
        root.Auth.AuthType = AuthKinds.Bearer;
        root.Auth.Token = "root-token";

        originalRequest = new RequestItem
        {
            Name = "创建订单",
            Method = "POST",
            Url = "{{baseUrl}}/orders",
            BodyMode = "Json",
            Body = "{\"paid\":true}",
            Owner = root,
        };
        originalRequest.Params.AddRow("page", "1");
        originalRequest.Headers.AddRow("X-Trace", "abc");
        originalRequest.FormFields.AddRow("note", "hello");
        originalRequest.UploadFiles.Add(
            new UploadFileItem { FilePath = @"C:\tmp\a.bin", FieldName = "file" }
        );
        originalRequest.Auth.AuthType = AuthKinds.ApiKey;
        originalRequest.Auth.ApiKeyValue = "request-key";
        root.Requests.Add(originalRequest);

        var child = new RequestCollection
        {
            Name = "子集合",
            IsFavorite = true,
            IsExpanded = false,
            OpenApiSource = new OpenApiSource { Url = "https://example.com/openapi.json" },
        };
        child.Parent = root;
        child.Auth.AuthType = AuthKinds.Basic;
        child.Auth.Username = "user";
        child.Auth.Password = "pass";
        child.Requests.Add(
            new RequestItem
            {
                Name = "查询订单",
                Method = "GET",
                Url = "{{baseUrl}}/orders/1",
                Owner = child,
            }
        );
        root.Children.Add(child);
        return root;
    }

    [Fact]
    public void CopiesTheWholeSubtreeWithNewIds()
    {
        var root = BuildTree(out var originalRequest);

        var copy = CollectionDuplicator.Duplicate(root);

        Assert.Equal("订单 副本", copy.Name);
        Assert.NotEqual(root.Id, copy.Id);
        Assert.False(copy.IsFavorite);
        Assert.True(copy.IsExpanded);
        Assert.Null(copy.OpenApiSource);
        Assert.Equal(AuthKinds.Bearer, copy.Auth.AuthType);
        Assert.Equal("root-token", copy.Auth.Token);

        var copiedRequest = Assert.Single(copy.Requests);
        Assert.NotEqual(originalRequest.Id, copiedRequest.Id);
        Assert.Equal(originalRequest.Name, copiedRequest.Name);
        Assert.Same(copy, copiedRequest.Owner);
        Assert.Equal("POST", copiedRequest.Method);
        Assert.Equal("{{baseUrl}}/orders", copiedRequest.Url);
        Assert.Equal("{\"paid\":true}", copiedRequest.Body);
        Assert.Contains(copiedRequest.Params, row => row.Key == "page" && row.Value == "1");
        Assert.Contains(copiedRequest.Headers, row => row.Key == "X-Trace" && row.Value == "abc");
        Assert.Contains(copiedRequest.FormFields, row => row.Key == "note" && row.Value == "hello");
        Assert.Equal(@"C:\tmp\a.bin", Assert.Single(copiedRequest.UploadFiles).FilePath);
        Assert.Equal(AuthKinds.ApiKey, copiedRequest.Auth.AuthType);
        Assert.Equal("request-key", copiedRequest.Auth.ApiKeyValue);

        var copiedChild = Assert.Single(copy.Children);
        Assert.NotEqual(root.Children[0].Id, copiedChild.Id);
        Assert.Equal("子集合", copiedChild.Name);
        Assert.Same(copy, copiedChild.Parent);
        Assert.False(copiedChild.IsFavorite);
        Assert.Null(copiedChild.OpenApiSource);
        Assert.Equal(AuthKinds.Basic, copiedChild.Auth.AuthType);
        Assert.Equal("user", copiedChild.Auth.Username);
        Assert.Single(copiedChild.Requests);
        Assert.NotEqual(root.Children[0].Requests[0].Id, copiedChild.Requests[0].Id);
    }

    [Fact]
    public void AuthorizationIsIndependentFromTheOriginal()
    {
        var root = BuildTree(out var originalRequest);
        var copy = CollectionDuplicator.Duplicate(root);

        copy.Auth.Token = "copy-token";
        copy.Requests[0].Auth.ApiKeyValue = "copy-key";
        copy.Children[0].Auth.Password = "copy-pass";

        Assert.Equal("root-token", root.Auth.Token);
        Assert.Equal("request-key", originalRequest.Auth.ApiKeyValue);
        Assert.Equal("pass", root.Children[0].Auth.Password);
    }

    [Fact]
    public void OriginalTreeKeepsItsIdsAndChildren()
    {
        var root = BuildTree(out var originalRequest);
        var originalChildId = root.Children[0].Id;

        CollectionDuplicator.Duplicate(root);

        Assert.Equal(originalRequest.Id, root.Requests[0].Id);
        Assert.Equal(originalChildId, root.Children[0].Id);
        Assert.True(root.IsFavorite);
        Assert.Null(root.OpenApiSource);
        Assert.Same(root, root.Requests[0].Owner);
    }
}
