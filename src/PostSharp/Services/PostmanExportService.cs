using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using PostSharp.Models;

namespace PostSharp.Services;

/// <summary>Exports workspace collections as a Postman Collection v2.1 document.</summary>
public static class PostmanExportService
{
    private const string Schema =
        "https://schema.getpostman.com/json/collection/v2.1.0/collection.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string ExportCollection(RequestCollection collection)
    {
        return Serialize(BuildCollection(collection));
    }

    public static string ExportWorkspace(IReadOnlyList<RequestCollection> collections)
    {
        var root = new JsonObject
        {
            ["info"] = new JsonObject { ["name"] = "PostSharp 工作区", ["schema"] = Schema },
        };
        var items = new JsonArray();
        foreach (var collection in collections)
        {
            items.Add((JsonNode)ToFolder(collection));
        }

        root["item"] = items;
        return Serialize(root);
    }

    public static string SuggestFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(
            name.Select(character => invalid.Contains(character) ? '-' : character).ToArray()
        )
            .Trim()
            .TrimEnd('.');
        if (string.IsNullOrEmpty(safe))
        {
            safe = "postsharp-collection";
        }

        return $"{safe}.postman_collection.json";
    }

    private static JsonObject BuildCollection(RequestCollection collection)
    {
        var root = new JsonObject
        {
            ["info"] = new JsonObject
            {
                ["name"] = string.IsNullOrWhiteSpace(collection.Name)
                    ? "PostSharp 集合"
                    : collection.Name,
                ["schema"] = Schema,
            },
        };
        var items = new JsonArray();
        foreach (var child in collection.Children)
        {
            items.Add((JsonNode)ToFolder(child));
        }

        foreach (var request in collection.Requests)
        {
            items.Add((JsonNode)ToRequest(request));
        }

        root["item"] = items;
        return root;
    }

    private static JsonObject ToFolder(RequestCollection collection)
    {
        var folder = new JsonObject
        {
            ["name"] = string.IsNullOrWhiteSpace(collection.Name) ? "未命名集合" : collection.Name,
        };
        var items = new JsonArray();
        foreach (var child in collection.Children)
        {
            items.Add((JsonNode)ToFolder(child));
        }

        foreach (var request in collection.Requests)
        {
            items.Add((JsonNode)ToRequest(request));
        }

        folder["item"] = items;
        return folder;
    }

    private static JsonObject ToRequest(RequestItem request)
    {
        var requestNode = new JsonObject
        {
            ["method"] = string.IsNullOrWhiteSpace(request.Method)
                ? "GET"
                : request.Method.Trim().ToUpperInvariant(),
            ["header"] = ToHeaders(request),
            ["url"] = ToUrl(request),
        };

        var body = ToBody(request);
        if (body is not null)
        {
            requestNode["body"] = body;
        }

        return new JsonObject
        {
            ["name"] = string.IsNullOrWhiteSpace(request.Name) ? "未命名请求" : request.Name,
            ["request"] = requestNode,
        };
    }

    private static JsonArray ToHeaders(RequestItem request)
    {
        var headers = new JsonArray();
        foreach (var row in request.Headers.Where(row => !string.IsNullOrWhiteSpace(row.Key)))
        {
            var header = new JsonObject
            {
                ["key"] = row.Key,
                ["value"] = row.Value,
                ["type"] = "text",
            };
            if (!row.IsEnabled)
            {
                header["disabled"] = true;
            }

            headers.Add((JsonNode)header);
        }

        return headers;
    }

    private static JsonObject ToUrl(RequestItem request)
    {
        return new JsonObject { ["raw"] = CombineUrl(request) };
    }

    private static string CombineUrl(RequestItem request)
    {
        var url = request.Url.Trim();
        var parameters = request
            .Params.ActiveRows.Where(row => !string.IsNullOrWhiteSpace(row.Key))
            .Select(row => $"{row.Key}={row.Value}")
            .ToList();
        if (parameters.Count == 0)
        {
            return url;
        }

        var fragmentIndex = url.IndexOf('#');
        var fragment = fragmentIndex >= 0 ? url[fragmentIndex..] : string.Empty;
        var withoutFragment = fragmentIndex >= 0 ? url[..fragmentIndex] : url;
        var separator = withoutFragment.Contains('?') ? "&" : "?";
        return withoutFragment + separator + string.Join("&", parameters) + fragment;
    }

    private static JsonObject? ToBody(RequestItem request)
    {
        var mode = request.BodyMode ?? "None";
        if (string.Equals(mode, "GraphQL", StringComparison.OrdinalIgnoreCase))
        {
            return new JsonObject
            {
                ["mode"] = "graphql",
                ["graphql"] = new JsonObject
                {
                    ["query"] = request.GraphQlQuery,
                    ["variables"] = request.GraphQlVariables,
                    ["operationName"] = request.GraphQlOperationName,
                },
            };
        }

        if (string.Equals(mode, "Json", StringComparison.OrdinalIgnoreCase))
        {
            return new JsonObject
            {
                ["mode"] = "raw",
                ["raw"] = request.Body,
                ["options"] = new JsonObject { ["raw"] = new JsonObject { ["language"] = "json" } },
            };
        }

        if (string.Equals(mode, "Text", StringComparison.OrdinalIgnoreCase))
        {
            return new JsonObject
            {
                ["mode"] = "raw",
                ["raw"] = request.Body,
                ["options"] = new JsonObject { ["raw"] = new JsonObject { ["language"] = "text" } },
            };
        }

        if (!string.Equals(mode, "Multipart", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var formData = new JsonArray();
        foreach (
            var row in request.FormFields.Where(row =>
                !string.IsNullOrWhiteSpace(row.Key) || !string.IsNullOrWhiteSpace(row.Value)
            )
        )
        {
            var field = new JsonObject
            {
                ["key"] = row.Key,
                ["value"] = row.Value,
                ["type"] = "text",
            };
            if (!row.IsEnabled)
            {
                field["disabled"] = true;
            }

            formData.Add((JsonNode)field);
        }

        if (request.UploadFiles.Count > 0)
        {
            foreach (var file in request.UploadFiles)
            {
                formData.Add(
                    (JsonNode)
                        new JsonObject
                        {
                            ["key"] = string.IsNullOrWhiteSpace(file.FieldName)
                                ? "file"
                                : file.FieldName,
                            ["type"] = "file",
                            ["src"] = file.FilePath,
                        }
                );
            }
        }
        else if (!string.IsNullOrWhiteSpace(request.UploadFilePath))
        {
            formData.Add(
                (JsonNode)
                    new JsonObject
                    {
                        ["key"] = string.IsNullOrWhiteSpace(request.UploadFieldName)
                            ? "file"
                            : request.UploadFieldName,
                        ["type"] = "file",
                        ["src"] = request.UploadFilePath,
                    }
            );
        }

        return new JsonObject { ["mode"] = "formdata", ["formdata"] = formData };
    }

    private static string Serialize(JsonObject root) => root.ToJsonString(JsonOptions);
}
