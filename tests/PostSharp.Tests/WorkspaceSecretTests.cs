using PostSharp.Models;
using PostSharp.Services;
using Xunit;

namespace PostSharp.Tests;

public class WorkspaceSecretTests
{
    [Fact]
    public void CaptureProtectsSecretEnvironmentValuesAndRoundTripsThem()
    {
        var environment = new EnvironmentModel { Name = "Test" };
        environment.Variables.AddRow("baseUrl", "http://localhost:5199", isSecret: false);
        environment.Variables.AddRow("token", "super-secret-value", isSecret: true);

        var workspace = WorkspaceStore.Capture(
            Array.Empty<RequestCollection>(),
            [environment],
            Array.Empty<HistoryEntry>(),
            Array.Empty<HistoryEntry>(),
            Array.Empty<TestSuite>(),
            Array.Empty<TrashedNode>(),
            environment.Id,
            includeSuites: false
        );

        var stored = workspace.Environments[0].Variables;
        var secret = stored.Single(row => row.Key == "token");
        var plain = stored.Single(row => row.Key == "baseUrl");

        Assert.True(EnvironmentSecretProtector.IsProtected(secret.Value));
        Assert.NotEqual("super-secret-value", secret.Value);
        Assert.Equal("http://localhost:5199", plain.Value);

        var restored = WorkspaceStore.ToEnvironmentModel(workspace.Environments[0]);
        Assert.Equal(
            "super-secret-value",
            restored.Variables.Single(row => row.Key == "token").Value
        );
        Assert.True(restored.Variables.Single(row => row.Key == "token").IsSecret);
    }

    [Fact]
    public void UnprotectKeepsLegacyPlainTextReadableForMigration()
    {
        Assert.Equal("legacy-value", EnvironmentSecretProtector.Unprotect("legacy-value"));
        Assert.False(EnvironmentSecretProtector.IsProtected("legacy-value"));
    }

    [Fact]
    public void LegacyPlainTextSecretIsMigratedAndStillReadable()
    {
        var workspace = new WorkspaceDto
        {
            Environments =
            [
                new EnvironmentDto
                {
                    Id = Guid.NewGuid(),
                    Name = "Legacy",
                    Variables =
                    [
                        new RowDto
                        {
                            Key = "token",
                            Value = "legacy-secret",
                            IsEnabled = true,
                            IsSecret = true,
                        },
                    ],
                },
            ],
        };

        Assert.True(WorkspaceStore.ProtectLegacyEnvironmentSecrets(workspace));
        Assert.True(
            EnvironmentSecretProtector.IsProtected(workspace.Environments[0].Variables[0].Value)
        );

        var restored = WorkspaceStore.ToEnvironmentModel(workspace.Environments[0]);
        Assert.Equal("legacy-secret", restored.Variables.Single(row => row.Key == "token").Value);
    }
}
