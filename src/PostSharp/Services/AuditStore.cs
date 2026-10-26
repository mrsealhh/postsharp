using System.Text.Encodings.Web;
using System.Text.Json;
using PostSharp.Models;

namespace PostSharp.Services;

/// <summary>
/// Audit entries live in their own file: the workspace stays small, while AI calls can be kept for a long
/// time and exported or inspected independently.
/// </summary>
public sealed class AuditStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly HashSet<string> _blockedFiles = new(StringComparer.OrdinalIgnoreCase);

    public string FilePath { get; }

    public string TrashFilePath { get; }

    public AuditStore()
    {
        AppPaths.EnsureRoot();
        FilePath = AppPaths.AuditFile;
        TrashFilePath = AppPaths.AuditTrashFile;
    }

    public DataFileLoad<List<AuditEntry>> Load() => LoadFile(FilePath);

    public DataFileLoad<List<AuditEntry>> LoadTrash() => LoadFile(TrashFilePath);

    /// <summary>Returns false when the file could not be written (or is protected after a failed read).</summary>
    public bool Save(IEnumerable<AuditEntry> entries) => SaveFile(FilePath, entries);

    public bool SaveTrash(IEnumerable<AuditEntry> entries) => SaveFile(TrashFilePath, entries);

    /// <summary>
    /// Drops the previous versions, so scrubbed secrets do not survive in the backups. Returns the backups that
    /// could not be deleted.
    /// </summary>
    public IReadOnlyList<string> DeleteBackups()
    {
        return new[] { FilePath, TrashFilePath }
            .Where(path => !DataFile.DeleteBackup(path))
            .Select(DataFile.BackupPath)
            .ToList();
    }

    private DataFileLoad<List<AuditEntry>> LoadFile(string path)
    {
        var load = DataFile.Load(
            path,
            text => JsonSerializer.Deserialize<List<AuditEntry>>(text, JsonOptions)
        );
        if (load.State == DataFileState.Inaccessible)
        {
            _blockedFiles.Add(path);
        }

        return load;
    }

    private bool SaveFile(string path, IEnumerable<AuditEntry> entries)
    {
        if (_blockedFiles.Contains(path))
        {
            // The original could not be read or moved aside at startup; the startup notice already says so.
            return false;
        }

        try
        {
            DataFile.WriteAllText(path, JsonSerializer.Serialize(entries, JsonOptions));
            return true;
        }
        catch (Exception exception)
        {
            // Auditing must never break a request, but the failure stays visible in the log.
            Log.Error(exception, "保存审计文件失败 {AuditFile}", path);
            return false;
        }
    }
}
