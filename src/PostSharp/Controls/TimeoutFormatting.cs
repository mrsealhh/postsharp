namespace PostSharp.Controls;

/// <summary>Formats the per-request timeout field so zero is visibly "follow the global timeout".</summary>
public static class TimeoutFormatting
{
    public static Func<decimal, string> SuffixFormatter { get; } =
        value => value > 0 ? "秒" : "秒 · 全局";
}
