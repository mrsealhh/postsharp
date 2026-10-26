using System.Text;

namespace PostSharp.Services;

/// <summary>How a data file was found when it was read.</summary>
public enum DataFileState
{
    /// <summary>Neither the file nor a backup exists: a first run.</summary>
    Missing,

    /// <summary>The file was read normally.</summary>
    Loaded,

    /// <summary>The file was missing or unreadable, and the last good backup was used instead.</summary>
    RestoredFromBackup,

    /// <summary>The file was unreadable, no backup could be used, and the file was moved aside.</summary>
    Unreadable,

    /// <summary>The file is locked or could not be moved aside, so saving is blocked to protect it.</summary>
    Inaccessible,
}

/// <summary>
/// The outcome of reading a data file. <see cref="SetAsidePath"/> names the copy an unreadable file was moved to.
/// </summary>
public sealed record DataFileLoad<T>(T? Value, DataFileState State, string? SetAsidePath)
    where T : class
{
    public bool IsProblem => State is not (DataFileState.Missing or DataFileState.Loaded);
}

/// <summary>
/// Reads and writes the local JSON data files. A save goes through a temporary file and keeps the previous
/// version as <c>.bak</c>, so a crash mid-write never leaves a half-written file. A file that cannot be read is
/// moved aside instead of being overwritten by the next save, and the backup is used when it is readable.
/// </summary>
public static class DataFile
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    public static string BackupPath(string path) => path + ".bak";

    public static void WriteAllText(string path, string content)
    {
        // No fsync: saves run on every request, and the .bak covers the rare power loss mid-write.
        var temporaryPath = path + ".tmp";
        File.WriteAllText(temporaryPath, content, Utf8);

        if (!File.Exists(path))
        {
            File.Move(temporaryPath, path);
            return;
        }

        try
        {
            File.Replace(temporaryPath, path, BackupPath(path), ignoreMetadataErrors: true);
        }
        catch (Exception exception)
            when (exception
                    is IOException
                        or UnauthorizedAccessException
                        or PlatformNotSupportedException
            )
        {
            // Some file systems (network shares, certain sync folders) do not support File.Replace.
            Log.Warning(exception, "原子替换数据文件失败，改为备份后覆盖 {DataFile}", path);
            File.Copy(path, BackupPath(path), overwrite: true);
            File.Move(temporaryPath, path, overwrite: true);
        }
    }

    public static DataFileLoad<T> Load<T>(string path, Func<string, T?> parse)
        where T : class
    {
        if (!File.Exists(path))
        {
            // A backup without its main file means an earlier start moved the file aside or a save stopped midway.
            return TryLoadBackup(path, parse, setAsidePath: null)
                ?? new DataFileLoad<T>(null, DataFileState.Missing, null);
        }

        if (TryRead(path) is not { } text)
        {
            // Locked by antivirus or a sync client, not corrupt: leave it where it is and never overwrite it.
            return new DataFileLoad<T>(null, DataFileState.Inaccessible, null);
        }

        if (TryParse(path, text, parse) is { } value)
        {
            return new DataFileLoad<T>(value, DataFileState.Loaded, null);
        }

        var setAsidePath = SetAside(path);
        if (setAsidePath is null)
        {
            return new DataFileLoad<T>(null, DataFileState.Inaccessible, null);
        }

        return TryLoadBackup(path, parse, setAsidePath)
            ?? new DataFileLoad<T>(null, DataFileState.Unreadable, setAsidePath);
    }

    /// <summary>Removes the previous version, for example after secrets were scrubbed from the file.</summary>
    public static bool DeleteBackup(string path)
    {
        try
        {
            File.Delete(BackupPath(path));
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Log.Warning(exception, "删除数据文件备份失败 {DataFile}", BackupPath(path));
            return false;
        }
    }

    private static DataFileLoad<T>? TryLoadBackup<T>(
        string path,
        Func<string, T?> parse,
        string? setAsidePath
    )
        where T : class
    {
        var backupPath = BackupPath(path);
        if (!File.Exists(backupPath))
        {
            return null;
        }

        if (
            TryRead(backupPath) is not { } text
            || TryParse(backupPath, text, parse) is not { } value
        )
        {
            return null;
        }

        Log.Warning(
            "数据文件不可用，已改用备份 {DataFile} {BackupFile} {SetAsideFile}",
            path,
            backupPath,
            setAsidePath
        );
        return new DataFileLoad<T>(value, DataFileState.RestoredFromBackup, setAsidePath);
    }

    /// <summary>Reads the file, retrying briefly because scanners and sync clients hold files for a moment.</summary>
    private static string? TryRead(string path)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return File.ReadAllText(path, Utf8);
            }
            catch (Exception exception)
                when ((exception is IOException or UnauthorizedAccessException) && attempt < 3)
            {
                Log.Debug(
                    exception,
                    "读取数据文件暂时失败，稍后重试 {DataFile} {Attempt}",
                    path,
                    attempt
                );
                Thread.Sleep(200);
            }
            catch (Exception exception)
                when (exception is IOException or UnauthorizedAccessException)
            {
                Log.Error(exception, "数据文件无法读取，本次不会移动或覆盖它 {DataFile}", path);
                return null;
            }
        }
    }

    /// <summary>Returns null when the content is not valid; only this case counts as a damaged file.</summary>
    private static T? TryParse<T>(string path, string text, Func<string, T?> parse)
        where T : class
    {
        try
        {
            return parse(text);
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "数据文件内容无效 {DataFile}", path);
            return null;
        }
    }

    private static string? SetAside(string path)
    {
        var directory = Path.GetDirectoryName(path) ?? string.Empty;
        var target = Path.Combine(
            directory,
            $"{Path.GetFileNameWithoutExtension(path)}.unreadable-{DateTime.Now:yyyyMMdd-HHmmss}{Path.GetExtension(path)}"
        );

        try
        {
            File.Move(path, target, overwrite: false);
            Log.Warning("数据文件无法读取，已移到旁边保留 {DataFile} {SetAsideFile}", path, target);
            return target;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Log.Error(exception, "无法移走读取失败的数据文件，本次不会覆盖它 {DataFile}", path);
            return null;
        }
    }
}
