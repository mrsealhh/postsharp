using PostSharp.Models;

namespace PostSharp.Services;

/// <summary>Builds editable workspace requests from parsed OpenAPI operations.</summary>
public static class OpenApiRequestFactory
{
    public static RequestItem Create(OpenApiImportRequest imported)
    {
        var request = new RequestItem
        {
            Name = imported.Name,
            Method = imported.Method,
            Url = imported.Url,
            BodyMode = imported.BodyMode,
            Body = imported.Body,
            OpenApiKey = imported.Key,
            OpenApiDoc = imported.Doc,
        };

        foreach (var field in imported.Query)
        {
            request.Params.AddRow(field.Name, field.Value, true);
        }

        foreach (var header in imported.Headers)
        {
            request.Headers.AddRow(header.Name, header.Value, true);
        }

        foreach (var field in imported.FormFields)
        {
            request.FormFields.AddRow(field.Name, field.Value, true);
        }

        request.Params.EnsureTrailingRow();
        request.Headers.EnsureTrailingRow();
        request.FormFields.EnsureTrailingRow();
        return request;
    }
}
