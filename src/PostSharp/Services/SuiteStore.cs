using System.Text.Encodings.Web;
using System.Text.Json;
using PostSharp.Models;

namespace PostSharp.Services;

/// <summary>Stores automation suites separately so their storage profile can be switched independently.</summary>
public sealed class SuiteStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly StorageCoordinator _storage = StorageCoordinator.Current;

    public DataFileLoad<List<SuiteDto>> Load()
    {
        var document = _storage.LoadDocument(StorageCategories.Suites);
        if (document is null)
        {
            return new DataFileLoad<List<SuiteDto>>(null, DataFileState.Missing, null);
        }

        try
        {
            var suites = JsonSerializer.Deserialize<List<SuiteDto>>(document.Payload, JsonOptions);
            return suites is null
                ? new DataFileLoad<List<SuiteDto>>(null, DataFileState.Unreadable, null)
                : new DataFileLoad<List<SuiteDto>>(suites, DataFileState.Loaded, null);
        }
        catch (Exception exception)
        {
            Log.Error(exception, "读取 SQLite 套件文档失败");
            return new DataFileLoad<List<SuiteDto>>(null, DataFileState.Unreadable, null);
        }
    }

    public SaveResult Save(IEnumerable<TestSuite> suites) =>
        _storage.SaveDocument(
            StorageCategories.Suites,
            JsonSerializer.Serialize(WorkspaceStore.CaptureSuites(suites), JsonOptions)
        );
}
