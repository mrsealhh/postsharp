using PostSharp.Models;
using PostSharp.Services;
using Xunit;

namespace PostSharp.Tests;

public sealed class RedirectPolicyTests
{
    [Fact]
    public void RequestComposerAndCloneKeepRedirectPolicy()
    {
        var request = new RequestItem
        {
            Name = "redirect",
            Url = "https://example.com/start",
            FollowRedirects = false,
        };

        var composed = RequestComposer.Compose(request, null);
        var clone = request.Clone();

        Assert.False(composed.FollowRedirects);
        Assert.False(clone.FollowRedirects);
    }

    [Fact]
    public void WorkspaceCaptureAndHistoryKeepRedirectPolicy()
    {
        var collection = new RequestCollection { Name = "redirects" };
        var request = new RequestItem
        {
            Name = "redirect",
            Url = "https://example.com/start",
            FollowRedirects = false,
            Owner = collection,
        };
        collection.Requests.Add(request);
        var history = new HistoryEntry
        {
            Name = "redirect",
            Url = "https://example.com/start",
            FollowRedirects = false,
        };

        var workspace = WorkspaceStore.Capture(
            [collection],
            Array.Empty<EnvironmentModel>(),
            [history],
            Array.Empty<HistoryEntry>(),
            Array.Empty<TestSuite>(),
            Array.Empty<TrashedNode>(),
            null,
            includeSuites: false
        );

        var storedRequest = workspace.Collections[0].Requests.Single();
        var storedHistory = workspace.History.Single();

        Assert.False(storedRequest.FollowRedirects);
        Assert.False(storedHistory.FollowRedirects);
        Assert.False(WorkspaceStore.ToModel(storedRequest).FollowRedirects);
        Assert.False(WorkspaceStore.ToHistory(storedHistory).ToRequest().FollowRedirects);
    }
}
