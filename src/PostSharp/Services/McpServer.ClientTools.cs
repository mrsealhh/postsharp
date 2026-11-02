using System.Text.Json.Nodes;
using PostSharp.Models;

namespace PostSharp.Services;

/// <summary>
/// 客户端可见性与凭据管理：列出调用过的 MCP 客户端、单个客户端最近活动，
/// 以及按客户端签发/吊销只保存哈希的 token。
/// </summary>
public sealed partial class McpServer
{
    private async Task<object> ListClientsToolAsync()
    {
        var entries = await _workspace.ListAuditAsync(300, "MCP").ConfigureAwait(false);
        var tokens = Policy.ClientTokens ?? Array.Empty<McpClientToken>();
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var token in tokens)
        {
            names.Add(token.Name);
        }

        foreach (var entry in entries)
        {
            names.Add(string.IsNullOrWhiteSpace(entry.Client) ? "未知客户端" : entry.Client);
        }

        if (!string.IsNullOrWhiteSpace(_clientName))
        {
            names.Add(_clientName);
        }

        var groups = names
            .Select(name =>
            {
                var calls = entries
                    .Where(entry =>
                        string.Equals(
                            string.IsNullOrWhiteSpace(entry.Client) ? "未知客户端" : entry.Client,
                            name,
                            StringComparison.OrdinalIgnoreCase
                        )
                    )
                    .ToList();
                var token = tokens.FirstOrDefault(candidate =>
                    candidate.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
                );
                return new McpClientInfo(
                    name,
                    calls.Count,
                    calls.Count(entry => !entry.IsSuccess),
                    calls.Count == 0
                        ? DateTimeOffset.MinValue
                        : calls.Max(entry => entry.Timestamp),
                    calls
                        .GroupBy(entry =>
                            string.IsNullOrWhiteSpace(entry.Tool) ? "未知工具" : entry.Tool
                        )
                        .OrderByDescending(tool => tool.Count())
                        .Take(8)
                        .Select(tool => tool.Key)
                        .ToList(),
                    token is not null,
                    token?.Id,
                    token?.AllowSend ?? false,
                    token?.AllowWrite ?? false,
                    token?.AllowAdmin ?? false,
                    string.Equals(name, _clientName, StringComparison.OrdinalIgnoreCase)
                );
            })
            .OrderByDescending(client => client.LastSeen)
            .ThenBy(client => client.Name, StringComparer.Ordinal)
            .ToList();

        return new McpClientsOverview(
            groups,
            _clientName,
            Policy.RequireToken,
            tokens.Count,
            entries.Count >= 300 ? "只统计最近 300 条 MCP 审计记录。" : string.Empty
        );
    }

    private async Task<object> GetClientActivityToolAsync(JsonObject arguments)
    {
        var client = RequireString(arguments, "client");
        var limit = Math.Clamp(OptionalInt(arguments, "limit") ?? 20, 1, 200);
        var entries = await _workspace.ListAuditAsync(300, "MCP").ConfigureAwait(false);
        var matched = entries
            .Where(entry =>
                string.Equals(
                    string.IsNullOrWhiteSpace(entry.Client) ? "未知客户端" : entry.Client,
                    client,
                    StringComparison.OrdinalIgnoreCase
                )
            )
            .ToList();
        var selected = matched.Take(limit).ToList();
        return new McpClientActivity(
            client,
            matched.Count,
            matched.Count(entry => !entry.IsSuccess),
            selected
                .Select(entry => new McpClientActivityEntry(
                    entry.Id,
                    entry.Timestamp,
                    entry.Tool ?? string.Empty,
                    entry.Name,
                    entry.Method,
                    entry.Url,
                    entry.StatusCode,
                    entry.IsSuccess,
                    entry.ElapsedMs
                ))
                .ToList()
        );
    }

    private object ListClientTokensTool()
    {
        var tokens = Policy.ClientTokens ?? Array.Empty<McpClientToken>();
        return new McpClientTokensOverview(
            Policy.RequireToken,
            tokens
                .Select(token => new McpClientTokenInfo(
                    token.Id,
                    token.Name,
                    token.AllowSend,
                    token.AllowWrite,
                    token.AllowAdmin,
                    token.CreatedAt
                ))
                .ToList()
        );
    }

    private async Task<object> CreateClientTokenToolAsync(JsonObject arguments)
    {
        var name = RequireString(arguments, "name").Trim();
        if (name.Length == 0)
        {
            throw new InvalidOperationException("客户端名称不能为空。");
        }

        return await _workspace
            .CreateClientTokenAsync(
                name,
                OptionalBool(arguments, "allowSend") ?? true,
                OptionalBool(arguments, "allowWrite") ?? true,
                OptionalBool(arguments, "allowAdmin") ?? false
            )
            .ConfigureAwait(false);
    }

    private async Task<object> RevokeClientTokenToolAsync(JsonObject arguments)
    {
        var id = OptionalString(arguments, "id");
        var name = OptionalString(arguments, "name");
        if (string.IsNullOrWhiteSpace(id) && string.IsNullOrWhiteSpace(name))
        {
            throw new InvalidOperationException("需要 id 或 name 指定要吊销的客户端 token。");
        }

        var revoked = await _workspace.RevokeClientTokenAsync(id, name).ConfigureAwait(false);
        if (!revoked)
        {
            throw new InvalidOperationException("没有找到匹配的客户端 token。");
        }

        return new McpClientTokenRevoked(true, id, name);
    }

    private static JsonObject ListClientsSchema() => ObjectSchema(new JsonObject());

    private static JsonObject GetClientActivitySchema() =>
        ObjectSchema(
            new JsonObject
            {
                ["client"] = StringSchema("客户端名称，来自 list_clients"),
                ["limit"] = IntegerSchema("返回最近多少条，默认 20，范围 1-200", 1, 200),
            },
            "client"
        );

    private static JsonObject ListClientTokensSchema() => ObjectSchema(new JsonObject());

    private static JsonObject CreateClientTokenSchema() =>
        ObjectSchema(
            new JsonObject
            {
                ["name"] = StringSchema("客户端名称，例如 claude-code / cursor"),
                ["allowSend"] = BooleanSchema("允许发送请求，默认 true"),
                ["allowWrite"] = BooleanSchema("允许修改工作区，默认 true"),
                ["allowAdmin"] = BooleanSchema("允许修改系统设置，默认 false"),
            },
            "name"
        );

    private static JsonObject RevokeClientTokenSchema() =>
        ObjectSchema(
            new JsonObject
            {
                ["id"] = StringSchema("token id；与 name 二选一"),
                ["name"] = StringSchema("客户端名称；与 id 二选一"),
            }
        );
}
