using System.Text.Json;
using PostSharp.Models;
using Xunit;

namespace PostSharp.Tests;

public sealed class ResidentSettingsTests
{
    [Fact]
    public void KeepMcpResidentDefaultsToTrueAndRoundTrips()
    {
        Assert.True(new AppSettings().KeepMcpResident);

        var json = JsonSerializer.Serialize(new AppSettings { KeepMcpResident = false });
        var restored = JsonSerializer.Deserialize<AppSettings>(json);

        Assert.NotNull(restored);
        Assert.False(restored.KeepMcpResident);
    }
}
