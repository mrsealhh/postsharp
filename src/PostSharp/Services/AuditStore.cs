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
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "PostSharp"
        );
        Directory.CreateDirectory(root);
        FilePath = Path.Combine(root, "audit.json");
        TrashFilePath = Path.Combine(root, "audit-trash.json");
    }

    public List<AuditEntry> Load() => LoadFile(FilePath);

    public List<AuditEntry> LoadTrash() => LoadFile(TrashFilePath);

    public void Save(IEnumerable<AuditEntry> entries) => SaveFile(FilePath, entries);

    public void SaveTrash(IEnumerable<AuditEntry> entries) => SaveFile(TrashFilePath, entries);

    private static List<AuditEntry> LoadFile(string path)
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<List<AuditEntry>>(File.ReadAllText(path), JsonOptions)
                    ?? new()
                : new List<AuditEntry>();
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "读取审计文件失败 {AuditFile}", path);
            return new List<AuditEntry>();
        }
    }

    private static void SaveFile(string path, IEnumerable<AuditEntry> entries)
    {
        try
        {
            File.WriteAllText(path, JsonSerializer.Serialize(entries, JsonOptions));
        }
        catch (Exception exception)
        {
            // Auditing must never break a request, but the failure stays visible in the log.
            Log.Error(exception, "保存审计文件失败 {AuditFile}", path);
        }
    }
}
