using System.Globalization;
using System.Text.RegularExpressions;
using PostSharp.Models;

namespace PostSharp.Services;

/// <summary>Evaluates structured <see cref="SuiteCondition"/> rules against a completed suite step.</summary>
public static class SuiteConditionEvaluator
{
    public static bool Evaluate(
        SuiteCondition condition,
        HttpResult? response,
        TestStepResult? step,
        EnvironmentModel? environment
    )
    {
        if (condition.Rules.Count == 0)
        {
            return true;
        }

        var matches = condition.Rules.Select(rule =>
            EvaluateRule(rule, response, step, environment)
        );
        return condition.Mode == SuiteConditionOptions.Any
            ? matches.Any(value => value)
            : matches.All(value => value);
    }

    private static bool EvaluateRule(
        SuiteConditionRule rule,
        HttpResult? response,
        TestStepResult? step,
        EnvironmentModel? environment
    )
    {
        var value = ReadValue(rule.Source, rule.Path, response, step, environment, out var exists);
        if (rule.Operator == "Exists")
        {
            return exists;
        }

        if (rule.Operator == "NotExists")
        {
            return !exists;
        }

        if (!exists)
        {
            return false;
        }

        return rule.Operator switch
        {
            "Contains" => value.Contains(rule.Expected, StringComparison.OrdinalIgnoreCase),
            "NotContains" => !value.Contains(rule.Expected, StringComparison.OrdinalIgnoreCase),
            "GreaterThan" => Compare(value, rule.Expected) > 0,
            "LessThan" => Compare(value, rule.Expected) < 0,
            "Matches" => Matches(value, rule.Expected),
            "In" => IsIn(value, rule.Expected),
            "NotEquals" => !string.Equals(value, rule.Expected, StringComparison.OrdinalIgnoreCase),
            _ => string.Equals(value, rule.Expected, StringComparison.OrdinalIgnoreCase),
        };
    }

    private static string ReadValue(
        string source,
        string path,
        HttpResult? response,
        TestStepResult? step,
        EnvironmentModel? environment,
        out bool exists
    )
    {
        exists = false;
        if (source == "Result")
        {
            exists = step is not null;
            if (step is null)
            {
                return string.Empty;
            }

            return step.Skipped ? "跳过"
                : step.Passed ? "成功"
                : "失败";
        }

        if (source == "Variable")
        {
            var variable = environment?.Variables.ActiveRows.FirstOrDefault(row =>
                row.Key.Equals(path, StringComparison.OrdinalIgnoreCase)
            );
            exists = variable is not null;
            return variable?.Value ?? string.Empty;
        }

        if (response is null || !response.HasResponse)
        {
            return string.Empty;
        }

        if (source == "ElapsedMs")
        {
            exists = true;
            return response.ElapsedMs.ToString(CultureInfo.InvariantCulture);
        }

        if (source == "Header")
        {
            var header = response.Headers.FirstOrDefault(candidate =>
                candidate.Key.Equals(path, StringComparison.OrdinalIgnoreCase)
            );
            exists = header is not null;
            return header?.Value ?? string.Empty;
        }

        if (source == "BodyJson")
        {
            exists = JsonValuePath.TryEvaluate(response.Body, path, out var bodyValue);
            return bodyValue;
        }

        exists = true;
        return response.StatusCode.ToString(CultureInfo.InvariantCulture);
    }

    private static int Compare(string actual, string expected)
    {
        if (
            decimal.TryParse(actual, NumberStyles.Any, CultureInfo.InvariantCulture, out var left)
            && decimal.TryParse(
                expected,
                NumberStyles.Any,
                CultureInfo.InvariantCulture,
                out var right
            )
        )
        {
            return left.CompareTo(right);
        }

        return string.Compare(actual, expected, StringComparison.OrdinalIgnoreCase);
    }

    private static bool Matches(string actual, string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return false;
        }

        try
        {
            return Regex.IsMatch(
                actual,
                pattern,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                TimeSpan.FromSeconds(1)
            );
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    private static bool IsIn(string actual, string expected) =>
        expected
            .Split(
                new[] { ',', '，', '|' },
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
            )
            .Any(candidate => string.Equals(candidate, actual, StringComparison.OrdinalIgnoreCase));
}
