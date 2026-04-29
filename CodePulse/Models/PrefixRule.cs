namespace CodePulse.Models;

public sealed class PrefixRule
{
    public string Prefix { get; set; } = string.Empty;

    public int SuffixLength { get; set; } = 6;

    public string DisplayText => SuffixLength == 6 ? Prefix : $"{Prefix}:{SuffixLength}";

    public static List<PrefixRule> ParseMany(IEnumerable<string>? values)
    {
        var rules = new List<PrefixRule>();
        if (values is null)
        {
            return rules;
        }

        foreach (var value in values)
        {
            if (TryParse(value, out var rule))
            {
                rules.Add(rule);
            }
        }

        return rules
            .GroupBy(static rule => rule.DisplayText, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.First())
            .ToList();
    }

    public static bool TryParse(string? input, out PrefixRule rule)
    {
        rule = new PrefixRule();
        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        var value = input.Trim().ToUpperInvariant();
        var parts = value.Split(':', 2, StringSplitOptions.TrimEntries);
        var prefix = parts[0];
        if (prefix.Length == 0 || !prefix.All(static ch => char.IsAsciiLetterOrDigit(ch)))
        {
            return false;
        }

        var suffixLength = 6;
        if (parts.Length == 2)
        {
            if (!int.TryParse(parts[1], out suffixLength) || suffixLength <= 0 || suffixLength > 20)
            {
                return false;
            }
        }

        rule = new PrefixRule
        {
            Prefix = prefix,
            SuffixLength = suffixLength
        };

        return true;
    }
}
