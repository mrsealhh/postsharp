namespace PostSharp.Services;

/// <summary>已经解析出来、要附加到某一次请求上的认证材料。</summary>
/// <param name="Key">请求头名或查询参数名，例如 Authorization、X-Api-Key、access_token。</param>
/// <param name="Value">请求头或查询参数的值，例如 Bearer xxx。</param>
/// <param name="InQuery">true 表示写进查询参数，false 表示写进请求头。</param>
public sealed record AuthInjection(string Key, string Value, bool InQuery);
