namespace PostSharp.Models;

/// <summary>一个 MCP 客户端在审计里的聚合视图。</summary>
public sealed record McpClientInfo(
    string Name,
    int Calls,
    int Failures,
    DateTimeOffset LastSeen,
    IReadOnlyList<string> Tools,
    bool HasToken,
    string? TokenId,
    bool AllowSend,
    bool AllowWrite,
    bool AllowAdmin,
    bool Current
);

public sealed record McpClientActivity(
    string Client,
    int Calls,
    int Failures,
    IReadOnlyList<McpClientActivityEntry> Entries
);

public sealed record McpClientActivityEntry(
    Guid Id,
    DateTimeOffset Timestamp,
    string Tool,
    string Name,
    string Method,
    string Url,
    int StatusCode,
    bool IsSuccess,
    long ElapsedMs
);

public sealed record McpClientTokenInfo(
    string Id,
    string Name,
    bool AllowSend,
    bool AllowWrite,
    bool AllowAdmin,
    DateTimeOffset CreatedAt
);

public sealed record McpClientTokenSecret(
    string Id,
    string Name,
    string Token,
    bool AllowSend,
    bool AllowWrite,
    bool AllowAdmin
);

public sealed record McpClientsOverview(
    IReadOnlyList<McpClientInfo> Clients,
    string? Current,
    bool TokenRequired,
    int ClientTokens,
    string Note
);

public sealed record McpClientTokensOverview(
    bool TokenRequired,
    IReadOnlyList<McpClientTokenInfo> Tokens
);

public sealed record McpClientTokenRevoked(bool Revoked, string? Id, string? Name);
