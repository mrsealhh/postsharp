namespace PostSharp.Models;

/// <summary>Arc 的强调色预设。名称直接对应 <c>ArcAccentPreset</c> 的成员，保存在 settings.json。</summary>
public static class Accents
{
    public const string Default = "Blue";

    public static IReadOnlyList<string> All { get; } =
        new[] { "Neutral", "Violet", "Blue", "Green", "Amber", "Orange", "Coral", "Rose" };

    public static string Label(string value) =>
        value switch
        {
            "Violet" => "紫罗兰",
            "Blue" => "蓝",
            "Green" => "绿",
            "Amber" => "琥珀",
            "Orange" => "橙",
            "Coral" => "珊瑚",
            "Rose" => "玫红",
            _ => "中性",
        };
}
