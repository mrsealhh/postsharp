using PostSharp.Models;
using PostSharp.ViewModels;

namespace PostSharp.Tests;

public sealed class ImportViewModelTests
{
    [Fact]
    public void OpenImportDialogResetsState()
    {
        var viewModel = Create(out _);
        viewModel.ImportSource = "OpenApi";
        viewModel.ImportUrl = "https://example.com/openapi.json";
        viewModel.PostmanJson = "{}";
        viewModel.CurlCommand = "curl https://example.com";
        viewModel.HarJson = "{}";
        viewModel.InsomniaJson = "{}";
        viewModel.HoppscotchJson = "{}";
        viewModel.ImportWarnings.Add("旧提示");
        viewModel.ImportReport.Add("旧结果");
        viewModel.ImportStatus = "旧状态";
        viewModel.HasImportError = true;

        viewModel.OpenImportDialogCommand.Execute(null);

        Assert.Equal("PostmanCollection", viewModel.ImportSource);
        Assert.Equal(string.Empty, viewModel.ImportUrl);
        Assert.Equal(string.Empty, viewModel.PostmanJson);
        Assert.Equal(string.Empty, viewModel.CurlCommand);
        Assert.Equal(string.Empty, viewModel.HarJson);
        Assert.Equal(string.Empty, viewModel.InsomniaJson);
        Assert.Equal(string.Empty, viewModel.HoppscotchJson);
        Assert.Empty(viewModel.ImportWarnings);
        Assert.Empty(viewModel.ImportReport);
        Assert.False(viewModel.HasImportError);
        Assert.True(viewModel.IsImportDialogOpen);
        Assert.Contains("Postman", viewModel.ImportStatus);
    }

    [Fact]
    public void SelectImportSourceUpdatesVisibilityAndStatus()
    {
        var viewModel = Create(out _);

        viewModel.SelectImportSourceCommand.Execute("Har");

        Assert.Equal("Har", viewModel.ImportSource);
        Assert.True(viewModel.IsHarImport);
        Assert.False(viewModel.IsPostmanCollectionImport);
        Assert.Contains("HAR", viewModel.ImportStatus);
    }

    [Fact]
    public void ToggleCommandsFlipExpansionState()
    {
        var viewModel = Create(out _);

        viewModel.ToggleImportSourceCommand.Execute(null);
        viewModel.ToggleImportReportCommand.Execute(null);
        viewModel.ToggleImportWarningsCommand.Execute(null);

        Assert.False(viewModel.IsImportSourceExpanded);
        Assert.True(viewModel.IsImportReportExpanded);
        Assert.True(viewModel.IsImportWarningsExpanded);
        Assert.Equal("展开来源", viewModel.ImportSourceToggleLabel);
        Assert.Equal("收起", viewModel.ImportReportToggleLabel);
        Assert.Equal("收起", viewModel.ImportWarningsToggleLabel);
    }

    [Fact]
    public void CloseImportDialogWhenIdleCloses()
    {
        var viewModel = Create(out _);
        viewModel.OpenImportDialogCommand.Execute(null);

        viewModel.CloseImportDialogCommand.Execute(null);

        Assert.False(viewModel.IsImportDialogOpen);
    }

    [Fact]
    public void ImportCurlAppliesRequestThroughHost()
    {
        var viewModel = Create(out var host);
        viewModel.CurlCommand =
            "curl -X POST 'https://example.com/api/orders' -H 'Accept: application/json'";

        viewModel.ImportCurlCommand.Execute(null);

        Assert.NotNull(host.CurlResult);
        Assert.True(host.Completed);
        Assert.NotEmpty(host.CurlResult.Request.Name);
        Assert.Equal("https://example.com/api/orders", host.CurlResult.Request.Url);
        Assert.False(viewModel.HasImportError);
        Assert.False(viewModel.IsImportDialogOpen);
        Assert.Contains(
            viewModel.ImportReport,
            line => line.Contains("cURL", StringComparison.Ordinal)
        );
        Assert.NotNull(host.Toast);
    }

    private static ImportViewModel Create(out FakeImportHost host)
    {
        host = new FakeImportHost();
        return new ImportViewModel(host);
    }

    private sealed class FakeImportHost : IImportHost
    {
        public CurlImportResult? CurlResult { get; private set; }

        public bool Completed { get; private set; }

        public string? Toast { get; private set; }

        public CollectionImportOutcome ApplyCollectionImport(ImportResult result)
        {
            return new CollectionImportOutcome(result.Root.Name, result.RequestCount, 0, null);
        }

        public CollectionImportOutcome ApplyOpenApiImport(OpenApiImportDocument document)
        {
            return new CollectionImportOutcome(document.Title, document.RequestCount, 0, null);
        }

        public CollectionImportOutcome ApplyEnvironmentImport(EnvironmentImportResult result)
        {
            return new CollectionImportOutcome(
                result.Environment.Name,
                0,
                0,
                result.Environment.Name
            );
        }

        public string? ApplyImportedEnvironment(ImportedEnvironment? imported)
        {
            return imported?.Name;
        }

        public RequestItem ApplyCurlImport(CurlImportResult result)
        {
            CurlResult = result;
            return new RequestItem
            {
                Name = "cURL",
                Method = result.Request.Method,
                Url = result.Request.Url,
            };
        }

        public void CompleteImport() => Completed = true;

        public void ShowToast(string title) => Toast = title;
    }
}
