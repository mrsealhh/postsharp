using System.Collections.Concurrent;
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

    /// <summary>
    /// The file is locked or could not be moved aside, so writing it is blocked for this run; the value comes from
    /// the backup when that one is readable.
    /// </summary>
    Inaccessible,
}

/// <summary>The outcome of saving a data file.</summary>
public enum SaveResult
{
    Saved,

    /// <summary>Not written on purpose: the file could not be read at startup and must not be overwritten.</summary>
    Blocked,

    /// <summary>The write failed; the reason is in the log.</summary>
    Failed,
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
/// A file that could neither be read nor moved aside is never written again in this process.
/// </summary>
public static class DataFile
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private static readonly ConcurrentDictionary<string, bool> BlockedPaths = new(
        StringComparer.OrdinalIgnoreCase
    );

    public static string BackupPath(string path) => path + ".bak";

    /// <summary>True while writes to this file are blocked after an unreadable startup file.</summary>
    public static bool IsBlocked(string path) => BlockedPaths.ContainsKey(Path.GetFullPath(path));

    /// <summary>Serializes and writes a data file; a failure is logged and reported, never thrown.</summary>
    public static SaveResult Save(string path, Func<string> serialize)
    {
        try
        {
            return WriteAllText(path, serialize()) ? SaveResult.Saved : SaveResult.Blocked;
        }
        catch (Exception exception)
        {
            Log.Error(exception, "保存数据文件失败 {DataFile}", path);
            return SaveResult.Failed;
        }
    }

    /// <summary>Returns false without writing when the file is protected after a failed read.</summary>
    private static bool WriteAllText(string path, string content)
    {
        if (BlockedPaths.ContainsKey(Path.GetFullPath(path)))
        {
            return false;
        }

        // No fsync: saves run on every request, and the .bak covers the rare power loss mid-write.
        var temporaryPath = path + ".tmp";
        File.WriteAllText(temporaryPath, content, Utf8);

        if (!File.Exists(path))
        {
            File.Move(temporaryPath, path);
            return true;
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
            // Some file systems (network shares, certain sync folders) do not support File.Replace, and another
            // program may hold the backup open.
            Log.Warning(exception, "原子替换数据文件失败，改为备份后覆盖 {DataFile}", path);
            try
            {
                File.Copy(path, BackupPath(path), overwrite: true);
            }
            catch (Exception backupException)
                when (backupException is IOException or UnauthorizedAccessException)
            {
                // Keeping the save matters more than refreshing the backup.
                Log.Warning(
                    backupException,
                    "无法更新数据文件备份，本次保存不更新备份 {DataFile}",
                    path
                );
            }

            File.Move(temporaryPath, path, overwrite: true);
        }

        return true;
    }

    public static DataFileLoad<T> Load<T>(string path, Func<string, T?> parse)
        where T : class
    {
        if (!File.Exists(path))
        {
            // A backup without its main file means an earlier start moved the file aside or a save stopped midway.
            var orphan = ReadBackup(path, parse);
            if (orphan.Value is not null)
            {
                return new DataFileLoad<T>(orphan.Value, DataFileState.RestoredFromBackup, null);
            }

            if (orphan.Blocked)
            {
                return new DataFileLoad<T>(null, DataFileState.Inaccessible, null);
            }

            return orphan.SetAside is null
                ? new DataFileLoad<T>(null, DataFileState.Missing, null)
                : new DataFileLoad<T>(null, DataFileState.Unreadable, orphan.SetAside);
        }

        if (TryRead(path) is not { } text)
        {
            // Locked by antivirus or a sync client, not corrupt: leave it where it is and never overwrite it.
            return Protect(path, parse);
        }

        if (TryParse(path, text, parse) is { } value)
        {
            return new DataFileLoad<T>(value, DataFileState.Loaded, null);
        }

        var setAsidePath = SetAside(path);
        if (setAsidePath is null)
        {
            return Protect(path, parse);
        }

        var backup = ReadBackup(path, parse);
        if (backup.Value is not null)
        {
            return new DataFileLoad<T>(
                backup.Value,
                DataFileState.RestoredFromBackup,
                setAsidePath
            );
        }

        return backup.Blocked
            ? new DataFileLoad<T>(null, DataFileState.Inaccessible, setAsidePath)
            : new DataFileLoad<T>(null, DataFileState.Unreadable, setAsidePath);
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

    /// <summary>Blocks writes to a file that must stay untouched and opens its backup read-only instead.</summary>
    private static DataFileLoad<T> Protect<T>(string path, Func<string, T?> parse)
        where T : class
    {
        Block(path);
        return new DataFileLoad<T>(ReadBackup(path, parse).Value, DataFileState.Inaccessible, null);
    }

    private static void Block(string path) => BlockedPaths[Path.GetFullPath(path)] = true;

    /// <summary>
    /// Reads the backup of a file that is missing or unusable. A backup that exists but cannot be used is moved
    /// aside; when it is locked or cannot be moved, writing the main file is blocked, because the second save would
    /// replace this backup with whatever the first one wrote.
    /// </summary>
    private static (T? Value, bool Blocked, string? SetAside) ReadBackup<T>(
        string path,
        Func<string, T?> parse
    )
        where T : class
    {
        var backupPath = BackupPath(path);
        if (!File.Exists(backupPath))
        {
            return (null, false, null);
        }

        if (TryRead(backupPath) is not { } text)
        {
            Block(path);
            return (null, true, null);
        }

        if (TryParse(backupPath, text, parse) is { } value)
        {
            Log.Warning("数据文件不可用，已改用备份 {DataFile} {BackupFile}", path, backupPath);
            return (value, false, null);
        }

        if (SetAside(backupPath) is { } setAside)
        {
            return (null, false, setAside);
        }

        Block(path);
        return (null, true, null);
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
