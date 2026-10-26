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
            Url = url,
            BodyMode = request.BodyMode,
            Body = request.Body,
            Query = request.Query,
            Headers = request.Headers,
        };
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

    public required IReadOnlyList<OpenApiImportRequest> Requests { get; init; }
}

public sealed class OpenApiImportRequest
{
    public required string Name { get; init; }

    public required string Method { get; init; }

    public required string Url { get; init; }

    public required string BodyMode { get; init; }

    public required string Body { get; init; }

    public required IReadOnlyList<OpenApiImportField> Query { get; init; }

    public required IReadOnlyList<OpenApiImportField> Headers { get; init; }
}

public sealed record OpenApiImportField(string Name, string Value);
