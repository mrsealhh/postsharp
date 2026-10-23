using System.Text.Encodings.Web;
using System.Text.Json;
using PostSharp.Models;

namespace PostSharp.Services;

public sealed class WorkspaceStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public string FilePath { get; }

    public WorkspaceStore()
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "PostSharp");
        Directory.CreateDirectory(root);
        FilePath = Path.Combine(root, "workspace.json");
    }

    public WorkspaceDto? Load()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return null;
            }

            return JsonSerializer.Deserialize<WorkspaceDto>(File.ReadAllText(FilePath), JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    public void Save(WorkspaceDto workspace)
    {
        try
        {
            var json = JsonSerializer.Serialize(workspace, JsonOptions);
            File.WriteAllText(FilePath, json);
        }
        catch
        {
            // A failed autosave must never break the request the person is sending.
        }
    }

    public static WorkspaceDto Capture(
        IEnumerable<RequestCollection> collections,
        IEnumerable<EnvironmentModel> environments,
        IEnumerable<HistoryEntry> history,
        IEnumerable<HistoryEntry> recycledHistory,
        IEnumerable<TestSuite> suites,
        Guid? activeEnvironmentId)
    {
        return new WorkspaceDto
        {
            Collections = collections.Select(collection => new CollectionDto
            {
                Id = collection.Id,
                Name = collection.Name,
                IsFavorite = collection.IsFavorite,
                ParentId = collection.Parent?.Id,
                Requests = collection.Requests.Select(ToDto).ToList()
            }).ToList(),
            Environments = environments.Select(environment => new EnvironmentDto
            {
                Id = environment.Id,
                Name = environment.Name,
                Variables = ToRows(environment.Variables)
            }).ToList(),
            History = history.Select(ToHistoryDto).ToList(),
            RecycledHistory = recycledHistory.Select(ToHistoryDto).ToList(),
            Suites = suites.Select(suite => new SuiteDto
            {
                Id = suite.Id,
                Name = suite.Name,
                StopOnError = suite.StopOnError,
                LastSummary = suite.LastSummary,
                Steps = suite.Steps.Select(step => new SuiteStepDto
                {
                    Id = step.Id,
                    RequestId = step.RequestId,
                    RequestName = step.RequestName,
                    Method = step.Method,
                    Url = step.Url,
                    DelayMs = step.DelayMs,
                    ExpectedStatus = step.ExpectedStatus,
                    BodyContains = step.BodyContains,
                    InputMappings = step.InputMappings.Select(mapping => new SuiteMappingDto
                    {
                        SourceStepId = mapping.SourceStepId,
                        SourcePath = mapping.SourcePath,
                        TargetKind = mapping.TargetKind,
                        TargetPath = mapping.TargetPath,
                        Template = mapping.Template
                    }).ToList()
                }).ToList()
            }).ToList(),
            ActiveEnvironmentId = activeEnvironmentId
        };
    }

    public static RequestItem ToModel(RequestDto dto)
    {
        var request = new RequestItem
        {
            Id = dto.Id == Guid.Empty ? Guid.NewGuid() : dto.Id,
            Name = dto.Name,
            Method = string.IsNullOrWhiteSpace(dto.Method) ? "GET" : dto.Method,
            Url = dto.Url,
            BodyMode = dto.BodyMode,
            Body = dto.Body,
            UploadFilePath = dto.UploadFilePath,
            UploadFieldName = string.IsNullOrWhiteSpace(dto.UploadFieldName) ? "file" : dto.UploadFieldName,
            IsFavorite = dto.IsFavorite
        };

        FillRows(request.Params, dto.Params);
        FillRows(request.Headers, dto.Headers);
        FillRows(request.FormFields, dto.FormFields);
        return request;
    }

    public static HistoryEntry ToHistory(HistoryDto dto)
    {
        return new HistoryEntry
        {
            Timestamp = dto.Timestamp,
            Name = dto.Name,
            Method = dto.Method,
            Url = dto.Url,
            BodyMode = dto.BodyMode,
            Body = dto.Body,
            StatusCode = dto.StatusCode,
            DurationMs = dto.DurationMs,
            Params = dto.Params.ToList(),
            Headers = dto.Headers.ToList()
        };
    }

    private static HistoryDto ToHistoryDto(HistoryEntry entry)
    {
        return new HistoryDto
        {
            Timestamp = entry.Timestamp,
            Name = entry.Name,
            Method = entry.Method,
            Url = entry.Url,
            BodyMode = entry.BodyMode,
            Body = entry.Body,
            StatusCode = entry.StatusCode,
            DurationMs = entry.DurationMs,
            Params = entry.Params.ToList(),
            Headers = entry.Headers.ToList()
        };
    }

    private static RequestDto ToDto(RequestItem request)
    {
        return new RequestDto
        {
            Id = request.Id,
            Name = request.Name,
            Method = request.Method,
            Url = request.Url,
            BodyMode = request.BodyMode,
            Body = request.Body,
            UploadFilePath = request.UploadFilePath,
            UploadFieldName = request.UploadFieldName,
            IsFavorite = request.IsFavorite,
            Params = ToRows(request.Params),
            Headers = ToRows(request.Headers),
            FormFields = ToRows(request.FormFields)
        };
    }

    private static List<RowDto> ToRows(EditableRowCollection rows)
    {
        return rows
            .Where(row => !string.IsNullOrWhiteSpace(row.Key) || !string.IsNullOrWhiteSpace(row.Value))
            .Select(row => new RowDto { Key = row.Key, Value = row.Value, IsEnabled = row.IsEnabled })
            .ToList();
    }

    private static void FillRows(EditableRowCollection target, List<RowDto> rows)
    {
        foreach (var row in rows)
        {
            target.AddRow(row.Key, row.Value, row.IsEnabled);
        }

        target.EnsureTrailingRow();
    }
}

public sealed class WorkspaceDto
{
    public List<CollectionDto> Collections { get; set; } = new();

    public List<EnvironmentDto> Environments { get; set; } = new();

    public List<HistoryDto> History { get; set; } = new();

    public List<HistoryDto> RecycledHistory { get; set; } = new();

    public List<SuiteDto> Suites { get; set; } = new();

    public Guid? ActiveEnvironmentId { get; set; }
}

public sealed class CollectionDto
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public bool IsFavorite { get; set; }

    public Guid? ParentId { get; set; }

    public List<RequestDto> Requests { get; set; } = new();
}

public sealed class RequestDto
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Method { get; set; } = "GET";

    public string Url { get; set; } = string.Empty;

    public string BodyMode { get; set; } = "None";

    public string Body { get; set; } = string.Empty;

    public string UploadFilePath { get; set; } = string.Empty;

    public string UploadFieldName { get; set; } = "file";

    public bool IsFavorite { get; set; }

    public List<RowDto> Params { get; set; } = new();

    public List<RowDto> Headers { get; set; } = new();

    public List<RowDto> FormFields { get; set; } = new();
}

public sealed class EnvironmentDto
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public List<RowDto> Variables { get; set; } = new();
}

public sealed class HistoryDto
{
    public DateTimeOffset Timestamp { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Method { get; set; } = "GET";

    public string Url { get; set; } = string.Empty;

    public string BodyMode { get; set; } = "None";

    public string Body { get; set; } = string.Empty;

    public int StatusCode { get; set; }

    public long DurationMs { get; set; }

    public List<RowSnapshot> Params { get; set; } = new();

    public List<RowSnapshot> Headers { get; set; } = new();
}

public sealed class RowDto
{
    public string Key { get; set; } = string.Empty;

    public string Value { get; set; } = string.Empty;

    public bool IsEnabled { get; set; } = true;
}

public sealed class SuiteDto
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public bool StopOnError { get; set; } = true;

    public string LastSummary { get; set; } = "尚未运行";

    public List<SuiteStepDto> Steps { get; set; } = new();
}

public sealed class SuiteStepDto
{
    public Guid Id { get; set; }

    public Guid RequestId { get; set; }

    public string RequestName { get; set; } = string.Empty;

    public string Method { get; set; } = "GET";

    public string Url { get; set; } = string.Empty;

    public int DelayMs { get; set; } = 200;

    public int? ExpectedStatus { get; set; }

    public string? BodyContains { get; set; }

    public List<SuiteMappingDto> InputMappings { get; set; } = new();
}

public sealed class SuiteMappingDto
{
    public Guid SourceStepId { get; set; }

    public string SourcePath { get; set; } = string.Empty;

    public string TargetKind { get; set; } = "Body";

    public string TargetPath { get; set; } = string.Empty;

    public string Template { get; set; } = "{value}";
}
