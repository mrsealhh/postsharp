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
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public string FilePath { get; }

    public AuditStore()
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "PostSharp");
        Directory.CreateDirectory(root);
        FilePath = Path.Combine(root, "audit.json");
    }

    public List<AuditEntry> Load()
    {
        try
        {
            return File.Exists(FilePath)
                ? JsonSerializer.Deserialize<List<AuditEntry>>(File.ReadAllText(FilePath), JsonOptions) ?? new()
                : new List<AuditEntry>();
        }
        catch
        {
            return new List<AuditEntry>();
        }
    }

    public void Save(IEnumerable<AuditEntry> entries)
    {
        try
        {
            File.WriteAllText(FilePath, JsonSerializer.Serialize(entries, JsonOptions));
        }
        catch
        {
            // Auditing must never break a request.
        }
    }
}
