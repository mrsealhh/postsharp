namespace PostSharp.Models;

/// <summary>One cookie captured by the current desktop session, shown in the cookie manager.</summary>
public sealed record SessionCookie(
    string Name,
    string Value,
    string Domain,
    string Path,
    string ExpiresText,
    bool Secure,
    bool HttpOnly
)
{
    public string Scope => $"{Domain}{Path}";

    public string Flags =>
        string.Join(
            " · ",
            new[] { Secure ? "Secure" : null, HttpOnly ? "HttpOnly" : null }.Where(flag =>
                flag is not null
            )
        );
}
