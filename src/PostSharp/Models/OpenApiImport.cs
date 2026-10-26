namespace PostSharp.Models;

/// <summary>A parsed OpenAPI / Swagger document, ready to become a collection tree.</summary>
public sealed class OpenApiImportDocument
{
    public required string Title { get; init; }

    public required string Version { get; init; }

    public required string Source { get; init; }

    public required IReadOnlyList<OpenApiImportGroup> Groups { get; init; }

    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    public int RequestCount => Groups.Sum(group => group.Requests.Count);
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
