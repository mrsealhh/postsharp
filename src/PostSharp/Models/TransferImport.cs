namespace PostSharp.Models;

/// <summary>A detached collection tree produced by an import before it enters the workspace.</summary>
public sealed class ImportedCollection
{
    public required string Name { get; init; }

    public List<ImportedCollection> Children { get; } = new();

    public List<ImportedRequest> Requests { get; } = new();
}

/// <summary>A detached request produced by Postman or cURL import.</summary>
public sealed class ImportedRequest
{
    public required string Name { get; init; }

    public required string Method { get; init; }

    public required string Url { get; init; }

    public required string BodyMode { get; init; }

    public required string Body { get; init; }

    public int TimeoutSeconds { get; init; }

    public string UploadFilePath { get; init; } = string.Empty;

    public string UploadFieldName { get; init; } = "file";

    public List<ImportedField> Params { get; } = new();

    public List<ImportedField> Headers { get; } = new();

    public List<ImportedField> FormFields { get; } = new();
}

public sealed record ImportedField(string Key, string Value, bool IsEnabled = true);

public sealed class ImportedEnvironment
{
    public required string Name { get; init; }

    public List<ImportedField> Variables { get; } = new();
}

public sealed class ImportResult
{
    public required string Format { get; init; }

    public required string Source { get; init; }

    public required ImportedCollection Root { get; init; }

    public ImportedEnvironment? Environment { get; init; }

    public required IReadOnlyList<string> Warnings { get; init; }

    public required int RequestCount { get; init; }

    public required int FolderCount { get; init; }
}

public sealed class CurlImportResult
{
    public required ImportedRequest Request { get; init; }

    public required IReadOnlyList<string> Warnings { get; init; }
}

public sealed record ExportDocument(string Content, string FileName);

public sealed class EnvironmentImportResult
{
    public required ImportedEnvironment Environment { get; init; }

    public required string Source { get; init; }

    public required IReadOnlyList<string> Warnings { get; init; }
}
