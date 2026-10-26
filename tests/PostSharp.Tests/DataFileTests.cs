using PostSharp.Services;

namespace PostSharp.Tests;

public sealed class DataFileTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "postsharp-tests-" + Guid.NewGuid().ToString("N")
    );

    public DataFileTests()
    {
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void SecondWriteKeepsPreviousVersionAsBackup()
    {
        var path = Path.Combine(_directory, "workspace.json");

        DataFile.Save(path, () => "{\"v\":1}");
        DataFile.Save(path, () => "{\"v\":2}");

        Assert.Equal("{\"v\":2}", File.ReadAllText(path));
        Assert.Equal("{\"v\":1}", File.ReadAllText(DataFile.BackupPath(path)));
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void MissingFileIsFirstRun()
    {
        var load = DataFile.Load(Path.Combine(_directory, "workspace.json"), Parse);

        Assert.Equal(DataFileState.Missing, load.State);
        Assert.Null(load.Value);
        Assert.False(load.IsProblem);
    }

    [Fact]
    public void UnreadableFileIsMovedAsideAndBackupIsUsed()
    {
        var path = Path.Combine(_directory, "workspace.json");
        DataFile.Save(path, () => "{\"v\":1}");
        DataFile.Save(path, () => "{\"v\":2}");
        File.WriteAllText(path, "{\"v\":");

        var load = DataFile.Load(path, Parse);

        Assert.Equal(DataFileState.RestoredFromBackup, load.State);
        Assert.Equal("{\"v\":1}", load.Value);
        Assert.NotNull(load.SetAsidePath);
        Assert.Equal("{\"v\":", File.ReadAllText(load.SetAsidePath));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void UnreadableFileWithoutBackupIsKeptAside()
    {
        var path = Path.Combine(_directory, "workspace.json");
        File.WriteAllText(path, "not json");

        var load = DataFile.Load(path, Parse);
        DataFile.Save(path, () => "{\"v\":3}");

        Assert.Equal(DataFileState.Unreadable, load.State);
        Assert.Null(load.Value);
        Assert.NotNull(load.SetAsidePath);
        Assert.Equal("not json", File.ReadAllText(load.SetAsidePath));
        Assert.Equal("{\"v\":3}", File.ReadAllText(path));
    }

    [Fact]
    public void LockedFileIsLeftInPlace()
    {
        var path = Path.Combine(_directory, "workspace.json");
        DataFile.Save(path, () => "{\"v\":1}");
        DataFile.Save(path, () => "{\"v\":2}");

        DataFileLoad<string> load;
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            load = DataFile.Load(path, Parse);
        }

        var written = DataFile.Save(path, () => "{\"v\":3}");

        Assert.Equal(DataFileState.Inaccessible, load.State);
        Assert.Equal("{\"v\":1}", load.Value);
        Assert.Equal(SaveResult.Blocked, written);
        Assert.Equal("{\"v\":2}", File.ReadAllText(path));
        Assert.Empty(Directory.GetFiles(_directory, "workspace.unreadable-*"));
    }

    [Fact]
    public void LockedBackupOfMissingFileIsNeverOverwritten()
    {
        var path = Path.Combine(_directory, "workspace.json");
        File.WriteAllText(DataFile.BackupPath(path), "{\"v\":1}");

        DataFileLoad<string> load;
        using (
            new FileStream(
                DataFile.BackupPath(path),
                FileMode.Open,
                FileAccess.Read,
                FileShare.None
            )
        )
        {
            load = DataFile.Load(path, Parse);
        }

        var first = DataFile.Save(path, () => "{\"v\":2}");
        var second = DataFile.Save(path, () => "{\"v\":3}");

        Assert.Equal(DataFileState.Inaccessible, load.State);
        Assert.Equal(SaveResult.Blocked, first);
        Assert.Equal(SaveResult.Blocked, second);
        Assert.Equal("{\"v\":1}", File.ReadAllText(DataFile.BackupPath(path)));
    }

    [Fact]
    public void CorruptBackupOfMissingFileIsMovedAside()
    {
        var path = Path.Combine(_directory, "workspace.json");
        File.WriteAllText(DataFile.BackupPath(path), "{\"v\":");

        var load = DataFile.Load(path, Parse);

        Assert.Equal(DataFileState.Unreadable, load.State);
        Assert.NotNull(load.SetAsidePath);
        Assert.Equal("{\"v\":", File.ReadAllText(load.SetAsidePath));
        Assert.False(File.Exists(DataFile.BackupPath(path)));
    }

    [Fact]
    public void SaveSucceedsWhileTheBackupIsHeldOpen()
    {
        var path = Path.Combine(_directory, "workspace.json");
        DataFile.Save(path, () => "{\"v\":1}");
        DataFile.Save(path, () => "{\"v\":2}");

        SaveResult saved;
        using (
            new FileStream(
                DataFile.BackupPath(path),
                FileMode.Open,
                FileAccess.Read,
                FileShare.None
            )
        )
        {
            saved = DataFile.Save(path, () => "{\"v\":3}");
        }

        Assert.Equal(SaveResult.Saved, saved);
        Assert.Equal("{\"v\":3}", File.ReadAllText(path));
    }

    [Fact]
    public void FailedWriteIsReportedNotThrown()
    {
        var path = Path.Combine(_directory, "missing-folder", "workspace.json");

        Assert.Equal(SaveResult.Failed, DataFile.Save(path, () => "{}"));
    }

    [Fact]
    public void BackupIsUsedWhenMainFileIsMissing()
    {
        var path = Path.Combine(_directory, "workspace.json");
        File.WriteAllText(DataFile.BackupPath(path), "{\"v\":1}");

        var load = DataFile.Load(path, Parse);

        Assert.Equal(DataFileState.RestoredFromBackup, load.State);
        Assert.Equal("{\"v\":1}", load.Value);
        Assert.Null(load.SetAsidePath);
    }

    private static string? Parse(string text)
    {
        using var document = System.Text.Json.JsonDocument.Parse(text);
        return document.RootElement.GetRawText();
    }
}
