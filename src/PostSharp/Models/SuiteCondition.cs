namespace PostSharp.Models;

/// <summary>
/// A structured condition used by suite branches and step conditions. Rules combine with All (AND) or Any (OR).
/// </summary>
public sealed class SuiteCondition
{
    /// <summary>All or Any.</summary>
    public string Mode { get; set; } = "All";

    public List<SuiteConditionRule> Rules { get; } = new();

    public bool IsEmpty => Rules.Count == 0;

    public string Summary => SuiteConditionOptions.Describe(this);
}

/// <summary>One comparison in a <see cref="SuiteCondition"/>.</summary>
public sealed class SuiteConditionRule
{
    /// <summary>StatusCode, BodyJson, Header, ElapsedMs, Result or Variable.</summary>
    public string Source { get; set; } = "StatusCode";

    /// <summary>JSON path, response header name or environment variable name.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>Equals, NotEquals, Contains, NotContains, Exists, NotExists, GreaterThan, LessThan, Matches or In.</summary>
    public string Operator { get; set; } = "Equals";

    public string Expected { get; set; } = string.Empty;
}

/// <summary>Display names and defaults shared by condition editors and summaries.</summary>
public static class SuiteConditionOptions
{
    public const string All = "All";
    public const string Any = "Any";

    public static IReadOnlyList<string> Sources { get; } =
        new[] { "StatusCode", "BodyJson", "Header", "ElapsedMs", "Result", "Variable" };

    public static IReadOnlyList<string> Operators { get; } =
        new[]
        {
            "Equals",
            "NotEquals",
            "Contains",
            "NotContains",
            "Exists",
            "NotExists",
            "GreaterThan",
            "LessThan",
            "Matches",
            "In",
        };

    public static IReadOnlyList<SuiteChoice> SourceChoices { get; } =
        Sources.Select(value => new SuiteChoice(value, SourceLabel(value))).ToList();

    public static IReadOnlyList<SuiteChoice> OperatorChoices { get; } =
        Operators.Select(value => new SuiteChoice(value, OperatorLabel(value))).ToList();

    public static string SourceLabel(string source) =>
        source switch
        {
            "BodyJson" => "响应字段",
            "Header" => "响应头",
            "ElapsedMs" => "耗时毫秒",
            "Result" => "步骤结果",
            "Variable" => "环境变量",
            _ => "状态码",
        };

    public static string OperatorLabel(string value) =>
        value switch
        {
            "NotEquals" => "不等于",
            "Contains" => "包含",
            "NotContains" => "不包含",
            "Exists" => "存在",
            "NotExists" => "不存在",
            "GreaterThan" => "大于",
            "LessThan" => "小于",
            "Matches" => "匹配正则",
            "In" => "属于",
            _ => "等于",
        };

    public static bool NeedsPath(string source) => source is "BodyJson" or "Header" or "Variable";

    public static bool NeedsExpected(string @operator) =>
        @operator is not ("Exists" or "NotExists");

    public static string Describe(SuiteCondition condition)
    {
        if (condition.Rules.Count == 0)
        {
            return "无条件";
        }

        var joiner = condition.Mode == Any ? " 或 " : " 且 ";
        return string.Join(joiner, condition.Rules.Select(rule => DescribeRule(rule)));
    }

    public static string DescribeRule(SuiteConditionRule rule)
    {
        var target = rule.Source switch
        {
            "BodyJson" => string.IsNullOrWhiteSpace(rule.Path) ? "响应字段" : rule.Path,
            "Header" => string.IsNullOrWhiteSpace(rule.Path) ? "响应头" : rule.Path,
            "Variable" => string.IsNullOrWhiteSpace(rule.Path) ? "环境变量" : rule.Path,
            _ => SourceLabel(rule.Source),
        };

        return NeedsExpected(rule.Operator)
            ? $"{target} {OperatorLabel(rule.Operator)} {rule.Expected}"
            : $"{target} {OperatorLabel(rule.Operator)}";
    }
}
