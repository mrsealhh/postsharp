namespace PostSharp.Models;

/// <summary>
/// 按客户端签发的 MCP token：只保存 SHA256 哈希，创建时把明文返回一次。
/// 客户端 token 在全局 token 之外生效，可以单独限定发送/修改/管理权限。
/// </summary>
public sealed class McpClientToken
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string TokenHash { get; set; } = string.Empty;

    public bool AllowSend { get; set; } = true;

    public bool AllowWrite { get; set; } = true;

    public bool AllowAdmin { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
