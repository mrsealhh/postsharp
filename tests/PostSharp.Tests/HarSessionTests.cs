using System.Text.Json.Nodes;
using PostSharp.Models;
using PostSharp.ViewModels;

namespace PostSharp.Tests;

public sealed class HarSessionTests
{
    private const string Har = """
        {
          "log": {
            "version": "1.2",
            "entries": [
              {
                "_resourceType": "xhr",
                "startedDateTime": "2026-11-01T10:00:00Z",
                "time": 12.5,
                "request": { "method": "GET", "url": "https://api.example.com/users" },
                "response": {
                  "status": 200,
                  "content": { "mimeType": "application/json" }
                }
              },
              {
                "_resourceType": "xhr",
                "startedDateTime": "2026-11-01T10:00:01Z",
                "time": 30,
                "request": { "method": "POST", "url": "https://api.example.com/orders" },
                "response": {
                  "status": 500,
                  "content": { "mimeType": "application/json" }
                }
              },
              {
                "_resourceType": "image",
                "time": 4,
                "request": { "method": "GET", "url": "https://api.example.com/logo.png" },
                "response": { "status": 200, "content": { "mimeType": "image/png" } }
              },
              {
                "_resourceType": "stylesheet",
                "time": 2,
                "request": { "method": "GET", "url": "https://api.example.com/app.css" },
                "response": { "status": 200, "content": { "mimeType": "text/css" } }
              }
            ]
          }
        }
        """;

    [Fact]
    public void ParsesEntriesAndClassifiesStaticResources()
    {
        var entries = HarSessionService.Parse(Har);

        Assert.Equal(4, entries.Count);
        Assert.Equal("GET", entries[0].Method);
        Assert.Equal(200, entries[0].Status);
        Assert.Equal(12.5, entries[0].ElapsedMs);
        Assert.False(entries[0].IsStatic);
        Assert.Equal(500, entries[1].Status);
        Assert.True(entries[2].IsStatic);
        Assert.True(entries[3].IsStatic);
        Assert.Equal("api.example.com/users", entries[0].DisplayName);
    }

    [Fact]
    public void RejectsInvalidHar()
    {
        Assert.Throws<InvalidOperationException>(() => HarSessionService.Parse("{}"));
        Assert.Throws<InvalidOperationException>(() => HarSessionService.Parse("not json"));
    }

    [Fact]
    public void MasksSensitiveQueryValuesForDisplay()
    {
        var entry = new HarSessionEntry
        {
            Index = 1,
            Method = "GET",
            Url = "https://api.example.com/users?token=top-secret&page=1",
            Raw = new JsonObject(),
        };

        Assert.Equal("https://api.example.com/users?token=***&page=1", entry.DisplayUrl);
        Assert.DoesNotContain("top-secret", entry.DisplayName, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildHarKeepsSelectedEntries()
    {
        var entries = HarSessionService.Parse(Har);

        var document = JsonNode.Parse(HarSessionService.BuildHar([entries[0], entries[1]]));

        var rebuilt = document!["log"]!["entries"]!.AsArray();
        Assert.Equal(2, rebuilt.Count);
        Assert.Equal(500, rebuilt[1]!["response"]!["status"]!.GetValue<int>());
    }

    [Fact]
    public void SessionFiltersAndSelectsRequests()
    {
        var entries = HarSessionService.Parse(Har);
        var applied = new List<(int Count, bool Suite)>();
        var viewModel = new RecordingSessionViewModel(
            entries,
            (selected, suite) => applied.Add((selected.Count, suite)),
            () => { }
        );

        Assert.Equal(2, viewModel.SelectedCount);
        Assert.Equal(2, viewModel.VisibleCount);
        Assert.DoesNotContain(viewModel.VisibleEntries, entry => entry.IsStatic);

        viewModel.SelectedMethod = "POST";
        Assert.Single(viewModel.VisibleEntries);

        viewModel.SelectedMethod = RecordingSessionViewModel.AllMethods;
        viewModel.SelectedStatus = RecordingSessionViewModel.ServerErrorStatuses;
        Assert.Equal("POST", Assert.Single(viewModel.VisibleEntries).Method);

        viewModel.SelectedStatus = RecordingSessionViewModel.AllStatuses;
        viewModel.IncludeStaticResources = true;
        Assert.Equal(4, viewModel.VisibleCount);
        Assert.Equal(2, viewModel.SelectedCount);

        viewModel.SelectAllCommand.Execute(null);
        Assert.Equal(4, viewModel.SelectedCount);

        viewModel.ImportSelectedCommand.Execute(null);
        Assert.Single(applied);
        Assert.Equal(4, applied[0].Count);
        Assert.False(applied[0].Suite);

        viewModel.ImportAndGenerateSuiteCommand.Execute(null);
        Assert.Equal(2, applied.Count);
        Assert.True(applied[1].Suite);
    }

    [Fact]
    public void EmptySelectionReportsAnError()
    {
        var entries = HarSessionService.Parse(Har);
        var applied = 0;
        var viewModel = new RecordingSessionViewModel(entries, (_, _) => applied++, () => { });

        viewModel.ClearSelectionCommand.Execute(null);
        viewModel.ImportSelectedCommand.Execute(null);

        Assert.Equal(0, applied);
        Assert.True(viewModel.HasError);
        Assert.Contains("至少选择一个请求", viewModel.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void FilterHidesStaticEntriesAndClearsTheirSelection()
    {
        var entries = HarSessionService.Parse(Har);
        var viewModel = new RecordingSessionViewModel(entries, (_, _) => { }, () => { });

        viewModel.IncludeStaticResources = true;
        viewModel.SelectAllCommand.Execute(null);
        Assert.Equal(4, viewModel.SelectedCount);

        viewModel.IncludeStaticResources = false;

        Assert.Equal(2, viewModel.VisibleCount);
        Assert.Equal(2, viewModel.SelectedCount);
    }
}
