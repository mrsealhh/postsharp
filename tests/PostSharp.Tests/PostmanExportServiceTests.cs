using PostSharp.Models;
using PostSharp.Services;

namespace PostSharp.Tests;

public sealed class PostmanExportServiceTests
{
    [Fact]
    public void ExportsAndReimportsNestedRequests()
    {
        var root = new RequestCollection { Name = "导出测试" };
        var child = new RequestCollection { Name = "子集合", Parent = root };
        root.Children.Add(child);

        var query = new RequestItem
        {
            Name = "查询",
            Method = "GET",
            Url = "https://api.example.com/items",
            BodyMode = "None",
            Owner = root,
        };
        query.Params.AddRow("page", "2");
        query.Headers.AddRow("X-Trace", "demo", false);
        root.Requests.Add(query);

        var upload = new RequestItem
        {
            Name = "上传",
            Method = "POST",
            Url = "https://api.example.com/upload",
            BodyMode = "Multipart",
            UploadFilePath = "C:\\tmp\\demo.txt",
            UploadFieldName = "file",
            Owner = child,
        };
        upload.FormFields.AddRow("title", "demo");
        child.Requests.Add(upload);

        var json = PostmanExportService.ExportCollection(root);
        var imported = PostmanImportService.Parse(json, "export.json");

        Assert.Contains("v2.1.0", json);
        Assert.Equal("导出测试", imported.Root.Name);
        Assert.Equal(2, imported.RequestCount);
        Assert.Equal(1, imported.FolderCount);

        var importedQuery = Assert.Single(imported.Root.Requests);
        Assert.Contains("page=2", importedQuery.Url);
        Assert.Contains(
            importedQuery.Headers,
            header => header.Key == "X-Trace" && !header.IsEnabled
        );

        var importedChild = Assert.Single(imported.Root.Children);
        var importedUpload = Assert.Single(importedChild.Requests);
        Assert.Equal("Multipart", importedUpload.BodyMode);
        Assert.Equal("C:\\tmp\\demo.txt", importedUpload.UploadFilePath);
        Assert.Equal("title", Assert.Single(importedUpload.FormFields).Key);
    }

    [Fact]
    public void ExportsWorkspaceAsOneCollectionWithFolders()
    {
        var first = new RequestCollection { Name = "集合一" };
        var second = new RequestCollection { Name = "集合二" };
        first.Requests.Add(
            new RequestItem
            {
                Name = "请求一",
                Method = "GET",
                Url = "https://api.example.com/one",
                BodyMode = "None",
                Owner = first,
            }
        );

        var json = PostmanExportService.ExportWorkspace(new[] { first, second });
        var imported = PostmanImportService.Parse(json, "workspace.json");

        Assert.Equal("PostSharp 工作区", imported.Root.Name);
        Assert.Equal(2, imported.Root.Children.Count);
        Assert.Equal("集合一", imported.Root.Children[0].Name);
        Assert.Equal("请求一", Assert.Single(imported.Root.Children[0].Requests).Name);
    }

    [Fact]
    public void SuggestsSafeCollectionFileName()
    {
        Assert.Equal(
            "My-Collection.postman_collection.json",
            PostmanExportService.SuggestFileName("My:Collection")
        );
        Assert.Equal(
            "postsharp-collection.postman_collection.json",
            PostmanExportService.SuggestFileName("...")
        );
    }
}
