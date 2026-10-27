using System.Text.Json.Serialization;

namespace PostSharp.Models;

/// <summary>A parsed OpenAPI / Swagger document, ready to become a collection tree.</summary>
public sealed class OpenApiImportDocument
{
    public required string Title { get; init; }

    public required string Version { get; init; }

    public required string Source { get; init; }

    public required IReadOnlyList<OpenApiImportServer> Servers { get; init; }

    public required string BaseUrl { get; init; }

    public int SelectedServerIndex { get; init; }

    public required IReadOnlyList<OpenApiImportGroup> Groups { get; init; }

    public string? Description { get; init; }

    public string? ETag { get; init; }

    public string? LastModifiedHeader { get; init; }

    public string? ContentHash { get; init; }

    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    public int RequestCount => Groups.Sum(group => group.Requests.Count);

    /// <summary>Rebases every request URL from the current server to another parsed server.</summary>
    public OpenApiImportDocument WithServer(int index)
    {
        if (index < 0 || index >= Servers.Count)
        {
            return this;
        }

        var server = Servers[index];
        if (
            string.Equals(server.Url, BaseUrl, StringComparison.OrdinalIgnoreCase)
            || BaseUrl.Length == 0
        )
        {
            return new OpenApiImportDocument
            {
                Title = Title,
                Version = Version,
                Source = Source,
                Servers = Servers,
                BaseUrl = server.Url,
                SelectedServerIndex = index,
                Groups = Groups,
                Description = Description,
                ETag = ETag,
                LastModifiedHeader = LastModifiedHeader,
                ContentHash = ContentHash,
                Warnings = Warnings,
            };
        }

        var groups = Groups
            .Select(group => new OpenApiImportGroup
            {
                Name = group.Name,
                Requests = group
                    .Requests.Select(request => RebaseRequest(request, BaseUrl, server.Url))
                    .ToList(),
            })
            .ToList();

        return new OpenApiImportDocument
        {
            Title = Title,
            Version = Version,
            Source = Source,
            Servers = Servers,
            BaseUrl = server.Url,
            SelectedServerIndex = index,
            Groups = groups,
            Description = Description,
            ETag = ETag,
            LastModifiedHeader = LastModifiedHeader,
            ContentHash = ContentHash,
            Warnings = Warnings,
        };
    }

    private static OpenApiImportRequest RebaseRequest(
        OpenApiImportRequest request,
        string oldBaseUrl,
        string newBaseUrl
    )
    {
        var url = request.Url;
        if (url.StartsWith(oldBaseUrl, StringComparison.OrdinalIgnoreCase))
        {
            url = newBaseUrl + url[oldBaseUrl.Length..];
        }

        return new OpenApiImportRequest
        {
            Name = request.Name,
            Method = request.Method,
            Path = request.Path,
            Url = url,
            BodyMode = request.BodyMode,
            Body = request.Body,
            Query = request.Query,
            Headers = request.Headers,
            FormFields = request.FormFields,
            Doc = WithUrl(request.Doc, url),
        };
    }

    private static OpenApiOperationDoc WithUrl(OpenApiOperationDoc doc, string url)
    {
        var copy = doc.Clone();
        copy.Url = url;
        return copy;
    }
}

/// <summary>One OpenAPI server entry, with variables already replaced by their defaults.</summary>
public sealed record OpenApiImportServer(int Index, string Url, string Description)
{
    public override string ToString() =>
        string.IsNullOrWhiteSpace(Description) ? Url : $"{Url} · {Description}";
}

/// <summary>Requests sharing one OpenAPI tag become a child collection.</summary>
public sealed class OpenApiImportGroup
{
    public required string Name { get; init; }

    public string? Description { get; init; }

    public required IReadOnlyList<OpenApiImportRequest> Requests { get; init; }
}

public sealed class OpenApiImportRequest
{
    public required string Name { get; init; }

    public required string Method { get; init; }

    public required string Path { get; init; }

    public required string Url { get; init; }

    public required string BodyMode { get; init; }

    public required string Body { get; init; }

    public required IReadOnlyList<OpenApiImportField> Query { get; init; }

    public required IReadOnlyList<OpenApiImportField> Headers { get; init; }

    public IReadOnlyList<OpenApiImportField> FormFields { get; init; } =
        Array.Empty<OpenApiImportField>();

    public required OpenApiOperationDoc Doc { get; init; }

    public string Key => $"{Method} {Path}";
}

public sealed record OpenApiImportField(string Name, string Value);

/// <summary>Offline copy of one operation's documentation, stored with the imported request.</summary>
public sealed class OpenApiOperationDoc
{
    public string Method { get; set; } = "GET";

    public string Path { get; set; } = string.Empty;

    public string OperationId { get; set; } = string.Empty;

    public string Summary { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public bool Deprecated { get; set; }

    public string TagsText { get; set; } = string.Empty;

    public string ServersText { get; set; } = string.Empty;

    public string SecurityText { get; set; } = string.Empty;

    public string GeneratedName { get; set; } = string.Empty;

    public string Url { get; set; } = string.Empty;

    public IReadOnlyList<OpenApiParameterDoc> Parameters { get; set; } =
        Array.Empty<OpenApiParameterDoc>();

    public OpenApiBodyDoc? RequestBody { get; set; }

    public IReadOnlyList<OpenApiResponseDoc> Responses { get; set; } =
        Array.Empty<OpenApiResponseDoc>();

    public OpenApiOperationDoc Clone() => (OpenApiOperationDoc)MemberwiseClone();
}

public sealed class OpenApiParameterDoc
{
    public string Name { get; set; } = string.Empty;

    public string Location { get; set; } = string.Empty;

    public string Type { get; set; } = string.Empty;

    public bool Required { get; set; }

    public string Description { get; set; } = string.Empty;

    public string Example { get; set; } = string.Empty;

    public string EnumText { get; set; } = string.Empty;
}

public sealed class OpenApiBodyDoc
{
    public string MediaType { get; set; } = string.Empty;

    public bool Required { get; set; }

    public string Sample { get; set; } = string.Empty;

    /// <summary>Actual value written to the request body, used to detect user edits during sync.</summary>
    public string Body { get; set; } = string.Empty;

    public IReadOnlyList<OpenApiSchemaFieldDoc> Fields { get; set; } =
        Array.Empty<OpenApiSchemaFieldDoc>();
}

public sealed class OpenApiSchemaFieldDoc
{
    public string Name { get; set; } = string.Empty;

    public string Type { get; set; } = string.Empty;

    public bool Required { get; set; }

    public string Description { get; set; } = string.Empty;

    public string Example { get; set; } = string.Empty;

    public string EnumText { get; set; } = string.Empty;
}

public sealed class OpenApiResponseDoc
{
    public string StatusCode { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public string HeadersText { get; set; } = string.Empty;

    public IReadOnlyList<OpenApiResponseSampleDoc> Samples { get; set; } =
        Array.Empty<OpenApiResponseSampleDoc>();

    public IReadOnlyList<OpenApiSchemaFieldDoc> Fields { get; set; } =
        Array.Empty<OpenApiSchemaFieldDoc>();
}

public sealed class OpenApiResponseSampleDoc
{
    public string MediaType { get; set; } = string.Empty;

    public string Sample { get; set; } = string.Empty;
}

/// <summary>Link and freshness metadata for a collection imported from an OpenAPI URL.</summary>
public sealed class OpenApiSource
{
    public string Url { get; set; } = string.Empty;

    public string DocumentTitle { get; set; } = string.Empty;

    public string Version { get; set; } = string.Empty;

    public string ETag { get; set; } = string.Empty;

    public string LastModifiedHeader { get; set; } = string.Empty;

    public string ContentHash { get; set; } = string.Empty;

    public DateTimeOffset ImportedAt { get; set; }

    public DateTimeOffset LastSyncedAt { get; set; }

    public bool AutoSync { get; set; } = true;

    public int LastRequestCount { get; set; }

    public int SelectedServerIndex { get; set; }

    [JsonIgnore]
    public bool CanSync => Url.StartsWith("http", StringComparison.OrdinalIgnoreCase);
}
