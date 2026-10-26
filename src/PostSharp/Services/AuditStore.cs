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

    /// <summary>Never throws: auditing must not break a request.</summary>
    public SaveResult Save(IEnumerable<AuditEntry> entries) =>
        DataFile.Save(FilePath, () => JsonSerializer.Serialize(entries, JsonOptions));

    public SaveResult SaveTrash(IEnumerable<AuditEntry> entries) =>
        DataFile.Save(TrashFilePath, () => JsonSerializer.Serialize(entries, JsonOptions));

    /// <summary>
    /// Drops the previous versions, so scrubbed secrets do not survive in the backups. Returns the backups that
    /// could not be deleted.
    /// </summary>
    public IReadOnlyList<string> DeleteBackups()
    {
        return new[] { FilePath, TrashFilePath }
            .Where(path =>
                File.Exists(path) && !DataFile.IsBlocked(path) && !DataFile.DeleteBackup(path)
            )
            .Select(DataFile.BackupPath)
            .ToList();
    }

    private static DataFileLoad<List<AuditEntry>> LoadFile(string path) =>
        DataFile.Load(
            path,
            text => JsonSerializer.Deserialize<List<AuditEntry>>(text, JsonOptions)
        );
}
