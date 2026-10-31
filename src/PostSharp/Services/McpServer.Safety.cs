using System.Text.Json.Nodes;
using PostSharp.Models;

namespace PostSharp.Services;

/// <summary>
/// Dry-run previews for destructive MCP tools. Nothing is changed here; the caller must repeat the
/// same call without <c>dryRun</c> to apply it.
/// </summary>
public sealed partial class McpServer
{
    private async Task<JsonObject> PreviewDestructiveAsync(string tool, JsonObject arguments)
    {
        var preview = new JsonObject
        {
            ["dryRun"] = true,
            ["tool"] = tool,
            ["message"] =
                "dryRun=true，未执行任何删除、恢复或切换；去掉 dryRun 后再次调用才会生效。",
        };

        switch (tool)
        {
            case "delete_collection":
            {
                var id = RequireGuid(arguments, "id");
                var collections = await _workspace.ListCollectionsAsync().ConfigureAwait(false);
                var root =
                    collections.FirstOrDefault(collection => collection.Id == id)
                    ?? throw new KeyNotFoundException($"没有找到 id 为 {id} 的集合。");
                var ids = CollectCollectionIds(collections, id);
                var requests = collections
                    .Where(collection => ids.Contains(collection.Id))
                    .Sum(collection => collection.Requests.Count);
                preview["target"] = Target(
                    root.Id,
                    root.Name,
                    $"集合 · {requests} 个请求 · {Math.Max(0, ids.Count - 1)} 个子集合"
                );
                preview["affected"] = requests;
                break;
            }

            case "delete_request":
            {
                var request =
                    await _workspace
                        .GetRequestAsync(RequireGuid(arguments, "id"))
                        .ConfigureAwait(false)
                    ?? throw new KeyNotFoundException(
                        $"没有找到 id 为 {RequireGuid(arguments, "id")} 的请求。"
                    );
                preview["target"] = Target(
                    request.Id,
                    request.Name,
                    $"{request.Method} {request.Url} · 集合「{request.CollectionName}」"
                );
                preview["affected"] = 1;
                break;
            }

            case "delete_suite":
            {
                var suite =
                    await _workspace
                        .GetSuiteAsync(RequireGuid(arguments, "id"))
                        .ConfigureAwait(false)
                    ?? throw new KeyNotFoundException(
                        $"没有找到 id 为 {RequireGuid(arguments, "id")} 的套件。"
                    );
                preview["target"] = Target(
                    suite.Id,
                    suite.Name,
                    $"套件 · {suite.Steps.Count} 步 · {suite.Runs.Count} 次运行历史"
                );
                preview["affected"] = suite.Steps.Count;
                break;
            }

            case "delete_audit":
            {
                var id = RequireGuid(arguments, "id");
                var audit =
                    await _workspace.GetAuditAsync(id).ConfigureAwait(false)
                    ?? throw new KeyNotFoundException($"没有找到 id 为 {id} 的审计记录。");
                preview["target"] = Target(
                    audit.Id,
                    audit.RequestName,
                    $"{audit.Method} {audit.Url} · {audit.Timestamp.LocalDateTime:yyyy-MM-dd HH:mm:ss}"
                );
                preview["affected"] = 1;
                break;
            }

            case "delete_history":
            case "purge_history":
            case "restore_history":
            {
                var timestamp = RequireDate(arguments, "timestamp");
                string name;
                string method;
                string url;
                if (tool == "delete_history")
                {
                    var detail = await _workspace.GetHistoryAsync(timestamp).ConfigureAwait(false);
                    name = detail.Name;
                    method = detail.Method;
                    url = detail.Url;
                }
                else
                {
                    var entry = await FindHistoryTrashAsync(timestamp).ConfigureAwait(false);
                    name = entry.Name;
                    method = entry.Method;
                    url = entry.Url;
                }

                preview["target"] = new JsonObject
                {
                    ["id"] = timestamp.ToString("O"),
                    ["name"] = name,
                    ["detail"] = $"{method} {url}",
                };
                preview["affected"] = 1;
                break;
            }

            case "delete_environment":
            {
                var id = RequireGuid(arguments, "id");
                var environments = await _workspace.ListEnvironmentsAsync().ConfigureAwait(false);
                var environment =
                    environments.FirstOrDefault(candidate => candidate.Id == id)
                    ?? throw new KeyNotFoundException($"没有找到 id 为 {id} 的环境。");
                preview["target"] = Target(
                    environment.Id,
                    environment.Name,
                    $"环境 · {environment.Variables.Count} 个变量"
                );
                preview["affected"] = 1;
                break;
            }

            case "delete_variable":
            {
                var environments = await _workspace.ListEnvironmentsAsync().ConfigureAwait(false);
                var environmentId = OptionalGuid(arguments, "environmentId");
                McpEnvironmentInfo? environment;
                if (environmentId is { } requested)
                {
                    environment =
                        environments.FirstOrDefault(candidate => candidate.Id == requested)
                        ?? throw new KeyNotFoundException($"没有找到 id 为 {requested} 的环境。");
                }
                else
                {
                    environment =
                        environments.FirstOrDefault(candidate => candidate.Active)
                        ?? environments.FirstOrDefault();
                }

                if (environment is null)
                {
                    throw new KeyNotFoundException("没有可用的环境。");
                }

                var key = RequireString(arguments, "key");
                var variable = environment.Variables.FirstOrDefault(candidate =>
                    string.Equals(candidate.Key, key, StringComparison.Ordinal)
                );
                if (variable is null)
                {
                    throw new KeyNotFoundException($"环境「{environment.Name}」中没有变量 {key}。");
                }

                preview["target"] = Target(
                    environment.Id,
                    key,
                    $"环境「{environment.Name}」· 变量"
                );
                preview["affected"] = 1;
                break;
            }

            case "delete_storage_profile":
            {
                var id = RequireString(arguments, "id");
                var config = await _workspace.GetStorageConfigAsync().ConfigureAwait(false);
                var profile =
                    config.Profiles.FirstOrDefault(candidate =>
                        string.Equals(candidate.Id, id, StringComparison.Ordinal)
                    ) ?? throw new KeyNotFoundException($"没有找到 id 为 {id} 的存储配置。");
                preview["target"] = Target(
                    Guid.Empty,
                    profile.Name,
                    $"存储配置 · {profile.Provider}"
                );
                preview["affected"] = 1;
                break;
            }

            case "clear_audit":
            case "clear_history":
            {
                var count =
                    tool == "clear_audit"
                        ? (await _workspace.ListAuditAsync(300, null).ConfigureAwait(false)).Count
                        : (await _workspace.ListHistoryAsync(200).ConfigureAwait(false)).Count;
                preview["target"] = new JsonObject
                {
                    ["name"] = tool == "clear_audit" ? "全部审计记录" : "全部发送历史",
                    ["detail"] = "移入回收站；预览最多 300/200 条",
                };
                preview["affected"] = count;
                break;
            }

            case "purge_audit":
            case "restore_audit":
            {
                var id = RequireGuid(arguments, "id");
                var audits = await _workspace.ListAuditTrashAsync(300).ConfigureAwait(false);
                var audit =
                    audits.FirstOrDefault(candidate => candidate.Id == id)
                    ?? throw new KeyNotFoundException($"审计回收站中没有 id 为 {id} 的记录。");
                preview["target"] = Target(
                    audit.Id,
                    audit.Name,
                    $"{audit.Method} {audit.Url} · {audit.Timestamp.LocalDateTime:yyyy-MM-dd HH:mm:ss}"
                );
                preview["affected"] = 1;
                break;
            }

            case "empty_audit_trash":
            case "empty_history_trash":
            case "empty_collection_trash":
            {
                var count =
                    tool == "empty_audit_trash"
                        ? (await _workspace.ListAuditTrashAsync(300).ConfigureAwait(false)).Count
                    : tool == "empty_history_trash"
                        ? (await _workspace.ListHistoryTrashAsync(200).ConfigureAwait(false)).Count
                    : (await _workspace.ListCollectionTrashAsync().ConfigureAwait(false)).Count;
                preview["target"] = new JsonObject
                {
                    ["name"] =
                        tool == "empty_audit_trash" ? "审计回收站"
                        : tool == "empty_history_trash" ? "历史回收站"
                        : "集合回收站",
                    ["detail"] = "永久删除回收站中的全部记录",
                };
                preview["affected"] = count;
                break;
            }

            case "purge_collection_trash":
            case "restore_collection_trash":
            {
                var id = RequireGuid(arguments, "id");
                var items = await _workspace.ListCollectionTrashAsync().ConfigureAwait(false);
                var item =
                    items.FirstOrDefault(candidate => candidate.Id == id)
                    ?? throw new KeyNotFoundException($"集合回收站中没有 id 为 {id} 的记录。");
                preview["target"] = Target(item.Id, item.Name, $"{item.Kind} · {item.Source}");
                preview["affected"] = 1;
                break;
            }

            case "switch_storage":
            {
                var profileId = RequireString(arguments, "targetProfileId");
                var config = await _workspace.GetStorageConfigAsync().ConfigureAwait(false);
                var profile =
                    config.Profiles.FirstOrDefault(candidate =>
                        string.Equals(candidate.Id, profileId, StringComparison.Ordinal)
                    ) ?? throw new KeyNotFoundException($"没有找到 id 为 {profileId} 的存储配置。");
                var categories = ReadStringArray(arguments, "categories");
                preview["target"] = Target(
                    Guid.Empty,
                    profile.Name,
                    $"切换存储目标 · {profile.Provider} · {(categories.Count == 0 ? "全部分类" : string.Join("、", categories))}"
                );
                preview["affected"] =
                    categories.Count == 0 ? StorageCategories.All.Count : categories.Count;
                break;
            }

            default:
                throw new InvalidOperationException($"工具 {tool} 不支持 dry-run。");
        }

        preview["hint"] = "真正执行前会写入审计（适用时）；如需取消，请不要去掉 dryRun。";
        return preview;
    }

    private static JsonObject Target(Guid id, string name, string detail) =>
        new()
        {
            ["id"] = id == Guid.Empty ? (Guid?)null : id,
            ["name"] = name,
            ["detail"] = detail,
        };

    private static HashSet<Guid> CollectCollectionIds(
        IReadOnlyList<McpCollectionInfo> collections,
        Guid rootId
    )
    {
        var ids = new HashSet<Guid> { rootId };
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var collection in collections)
            {
                if (
                    collection.ParentId is { } parentId
                    && ids.Contains(parentId)
                    && ids.Add(collection.Id)
                )
                {
                    changed = true;
                }
            }
        }

        return ids;
    }

    private async Task<McpHistoryInfo> FindHistoryTrashAsync(DateTimeOffset timestamp)
    {
        var entries = await _workspace.ListHistoryTrashAsync(200).ConfigureAwait(false);
        return entries.FirstOrDefault(candidate => candidate.Time == timestamp)
            ?? throw new KeyNotFoundException($"历史回收站中没有时间戳为 {timestamp:O} 的记录。");
    }
}
