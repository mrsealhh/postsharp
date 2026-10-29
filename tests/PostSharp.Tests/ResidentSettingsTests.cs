using System.Text.Json;
using PostSharp.Models;
using Xunit;

namespace PostSharp.Tests;

public sealed class ResidentSettingsTests
{
    [Fact]
    public void McpServiceModeDefaultsToPageAndRoundTrips()
    {
        Assert.Equal(McpServiceModes.Page, new AppSettings().McpServiceMode);
        Assert.True(new AppSettings().KeepMcpResident);

        var json = JsonSerializer.Serialize(
            new AppSettings { McpServiceMode = McpServiceModes.Background }
        );
        var restored = JsonSerializer.Deserialize<AppSettings>(json);

        Assert.NotNull(restored);
        Assert.Equal(McpServiceModes.Background, restored.McpServiceMode);
        Assert.True(restored.KeepMcpResident);
    }
}
