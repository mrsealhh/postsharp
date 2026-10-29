using System.Text.Json;
using PostSharp.Models;

namespace PostSharp.Services;

public sealed record OpenApiSyncResult(
    int Added,
    int Updated,
    int Removed,
    int Unchanged,
    IReadOnlyList<string> RemovedNames
)
{
    public bool Changed => Added > 0 || Updated > 0 || Removed > 0;

    public string Summary =>
        Added + Updated + Removed == 0
            ? $"已是最新，{Unchanged} 个接口没有变化。"
            : $"新增 {Added} 个、更新 {Updated} 个、移除 {Removed} 个接口"
                + (
                    RemovedNames.Count == 0
                        ? "。"
                        : $"（已移入回收站：{string.Join("、", RemovedNames.Take(5))}"
                            + (RemovedNames.Count > 5 ? " 等" : string.Empty)
                            + "）。"
                );
}

/// <summary>
/// Merges a freshly downloaded OpenAPI document into an existing collection. Matching uses METHOD + path
/// template, so renames and server changes do not create duplicates. Values edited by the user are kept:
/// only missing parameters/headers/form fields are added, and generated URLs/names/samples are replaced only
/// while they still equal the previous generated values. Operations deleted from the document go to the
/// collection recycle bin instead of being removed for good.
/// </summary>
public static class OpenApiSyncService
{
    public static OpenApiSyncResult Apply(
        RequestCollection root,
        OpenApiImportDocument document,
        Action<RequestCollection> registerGroup,
        Action<RequestItem> trash
    )
    {
        var incoming = document
            .Groups.SelectMany(group =>
                group.Requests.Select(request => (Group: group, Request: request))
            )
            .ToList();
        var incomingKeys = incoming
            .Select(item => item.Request.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var existing = root
            .Requests.Select(request => (request, GroupKey: string.Empty))
            .Concat(
                root.Children.SelectMany(child =>
                    child.Requests.Select(request => (request, GroupKey: child.Name))
                )
            )
            .Where(item => !string.IsNullOrWhiteSpace(item.request.OpenApiKey))
            .ToList();
        var byKey = existing
            .GroupBy(item => item.request.OpenApiKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.ToList(),
                StringComparer.OrdinalIgnoreCase
            );

        var added = 0;
        var updated = 0;
        var unchanged = 0;
        foreach (var (group, source) in incoming)
        {
            if (byKey.TryGetValue(source.Key, out var matches) && matches.Count > 0)
            {
                var changed = false;
                foreach (var match in matches)
                {
                    changed |= Merge(match.request, source);
                }

                if (changed)
                {
                    updated++;
                }
                else
                {
                    unchanged++;
                }

                continue;
            }

            var target = ResolveGroup(root, group.Name, registerGroup);
            var request = OpenApiRequestFactory.Create(source);
            request.Owner = target;
            target.Requests.Add(request);
            byKey[source.Key] = [(request, group.Name)];
            added++;
        }

        var removedNames = new List<string>();
        foreach (var (request, _) in existing)
        {
            if (incomingKeys.Contains(request.OpenApiKey))
            {
                continue;
            }

            removedNames.Add(request.Name);
            trash(request);
        }

        return new OpenApiSyncResult(added, updated, removedNames.Count, unchanged, removedNames);
    }

    private static RequestCollection ResolveGroup(
        RequestCollection root,
        string groupName,
        Action<RequestCollection> registerGroup
    )
    {
        if (string.Equals(groupName, "未分组", StringComparison.Ordinal))
        {
            return root;
        }

        var existing = root.Children.FirstOrDefault(child =>
            string.Equals(child.Name, groupName, StringComparison.OrdinalIgnoreCase)
        );
        if (existing is not null)
        {
            return existing;
        }

        var group = new RequestCollection
        {
            Name = groupName,
            Parent = root,
            IsExpanded = true,
        };
        root.Children.Add(group);
        registerGroup(group);
        return group;
    }

    private static bool Merge(RequestItem request, OpenApiImportRequest incoming)
    {
        var oldDoc = request.OpenApiDoc;
        var changed = oldDoc is null || Serialize(oldDoc) != Serialize(incoming.Doc);
        if (oldDoc is null || request.Url.Length == 0 || request.Url == oldDoc.Url)
        {
            request.Url = incoming.Url;
        }

        if (oldDoc is null || request.Name.Length == 0 || request.Name == oldDoc.GeneratedName)
        {
            request.Name = incoming.Name;
        }

        var oldSample = oldDoc?.RequestBody?.Sample ?? string.Empty;
        var oldBody = oldDoc?.RequestBody?.Body ?? string.Empty;
        if (
            request.BodyMode is "None"
            || request.Body.Length == 0
            || request.Body == oldSample
            || request.Body == oldBody
        )
        {
            request.BodyMode = incoming.BodyMode;
            request.Body = incoming.Body;
        }

        MergeRows(request.Params, incoming.Query.Select(field => (field.Name, field.Value)));
        MergeRows(request.Headers, incoming.Headers.Select(field => (field.Name, field.Value)));
        MergeRows(
            request.FormFields,
            incoming.FormFields.Select(field => (field.Name, field.Value))
        );
        request.OpenApiKey = incoming.Key;
        request.OpenApiDoc = incoming.Doc;
        return changed;
    }

    private static void MergeRows(
        EditableRowCollection rows,
        IEnumerable<(string Name, string Value)> incoming
    )
    {
        foreach (var (name, value) in incoming)
        {
            if (
                rows.Any(row =>
                    string.Equals(row.Key.Trim(), name, StringComparison.OrdinalIgnoreCase)
                )
            )
            {
                continue;
            }

            rows.AddRow(name, value, true);
        }

        rows.EnsureTrailingRow();
    }

    private static string Serialize(OpenApiOperationDoc doc) => DataJson.Serialize(doc);
}
