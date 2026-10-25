using System.Text.RegularExpressions;
using PostSharp.Models;

namespace PostSharp.Services;

public static partial class VariableResolver
{
    [GeneratedRegex(@"\{\{\s*([A-Za-z0-9_\-\.]+)\s*\}\}", RegexOptions.Compiled)]
    private static partial Regex VariablePattern();

    public static string Resolve(string text, EnvironmentModel? environment)
    {
        if (string.IsNullOrEmpty(text) || environment is null)
        {
            return text;
        }

        var values = environment
            .Variables.Where(row => row.IsEnabled && !string.IsNullOrWhiteSpace(row.Key))
            .GroupBy(row => row.Key.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.Last().Value,
                StringComparer.OrdinalIgnoreCase
            );

        return VariablePattern()
            .Replace(
                text,
                match =>
                    values.TryGetValue(match.Groups[1].Value, out var value) ? value : match.Value
            );
    }
}
