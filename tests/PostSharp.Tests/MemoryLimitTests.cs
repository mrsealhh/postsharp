using PostSharp.Models;

namespace PostSharp.Tests;

public sealed class MemoryLimitTests
{
    [Fact]
    public void AuditAiAndCurlPreviewsAreBounded()
    {
        var ai = new string('a', AuditEntry.AiPreviewLimit * 3);
        var curl = new string('c', AuditEntry.CurlPreviewLimit * 3);

        Assert.True(AuditEntry.LimitAiText(ai).Length < ai.Length);
        Assert.Contains("内容已截断", AuditEntry.LimitAiText(ai));
        Assert.True(AuditEntry.LimitCurl(curl)!.Length < curl.Length);
        Assert.Contains("内容已截断", AuditEntry.LimitCurl(curl));
    }

    [Fact]
    public void HistoryBodyPreviewIsBounded()
    {
        var body = new string('b', HistoryEntry.BodyLimit * 3);

        Assert.True(HistoryEntry.LimitBody(body).Length < body.Length);
        Assert.Contains("内容已截断", HistoryEntry.LimitBody(body));
    }

    [Fact]
    public void SoftwareRenderingIsTheDefault()
    {
        Assert.Equal(RenderingModes.Software, new AppSettings().RenderingMode);
    }

    [Fact]
    public void AuditLimitDefaultsToUnlimited()
    {
        Assert.Equal(0, new AppSettings().AuditLimit);
    }
}
