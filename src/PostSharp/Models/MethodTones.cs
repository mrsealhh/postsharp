using TKCode.Arc;

namespace PostSharp.Models;

/// <summary>The badge colour of an HTTP method, the same in every list.</summary>
public static class MethodTones
{
    public static BadgeTone Of(string method) =>
        method.ToUpperInvariant() switch
        {
            "GET" => BadgeTone.Info,
            "POST" => BadgeTone.Success,
            "PUT" or "PATCH" => BadgeTone.Warning,
            "DELETE" => BadgeTone.Danger,
            _ => BadgeTone.Neutral,
        };
}
