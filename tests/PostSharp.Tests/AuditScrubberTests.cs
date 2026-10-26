using PostSharp.Models;
using PostSharp.ViewModels;

namespace PostSharp.Tests;

public sealed class AuditScrubberTests
{
    private const string Secret = "secret-token-1";

    private readonly List<AuditEntry> _audit = new()
    {
        new AuditEntry { Url = $"https://api.example.com/?key={Secret}" },
    };

    private readonly List<EnvironmentModel> _environments = new();
    private readonly DataNoticeViewModel _notice = new();
    private readonly Queue<bool> _persistResults = new();
    private int _persistCalls;
    private int _deleteCalls;

    public AuditScrubberTests()
    {
        var environment = new EnvironmentModel();
        environment.Variables.AddRow("token", Secret, isSecret: true);
        _environments.Add(environment);
    }

    [Fact]
    public void RetriesTheWriteAfterAFailedSave()
    {
        var scrubber = CreateScrubber();
        _persistResults.Enqueue(false);
        _persistResults.Enqueue(true);

        scrubber.Scrub();

        Assert.DoesNotContain(Secret, _audit[0].Url);
        Assert.True(_notice.IsOpen);
        Assert.Equal(0, _deleteCalls);

        scrubber.Scrub();

        Assert.Equal(2, _persistCalls);
        Assert.Equal(1, _deleteCalls);
    }

    [Fact]
    public void SkipsWhenTheSecretsHaveNotChanged()
    {
        var scrubber = CreateScrubber();
        _persistResults.Enqueue(true);

        scrubber.Scrub();
        scrubber.Scrub();

        Assert.Equal(1, _persistCalls);
    }

    [Fact]
    public void RepeatedWarningReopensTheDismissedBanner()
    {
        _notice.Warn("审计备份无法删除");
        _notice.IsOpen = false;

        _notice.Warn("审计备份无法删除");

        Assert.True(_notice.IsOpen);
    }

    private AuditScrubber CreateScrubber() =>
        new(
            _audit,
            new List<AuditEntry>(),
            _environments,
            () =>
            {
                _persistCalls++;
                return _persistResults.Dequeue();
            },
            () =>
            {
                _deleteCalls++;
                return [];
            },
            _notice
        );
}
