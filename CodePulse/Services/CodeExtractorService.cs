using System.Text.RegularExpressions;
using CodePulse.Models;

namespace CodePulse.Services;

public sealed class CodeExtractorService
{
    private const int GenericCodeLength = 10;
    private static readonly IReadOnlyDictionary<char, char> GenericAmbiguousCharacterMap = new Dictionary<char, char>
    {
        ['O'] = '9',
        ['Q'] = '9',
        ['G'] = '9'
    };

    public CodeCandidate? ExtractBestCandidate(ChannelProfile channel, string message)
    {
        return ExtractCandidates(channel, message)
            .OrderByDescending(static candidate => candidate.Score)
            .FirstOrDefault();
    }

    public IReadOnlyList<CodeCandidate> ExtractCandidates(ChannelProfile channel, string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return [];
        }

        var text = message.Trim().ToUpperInvariant();
        if (text.Length < GenericCodeLength)
        {
            return [];
        }

        var candidates = new List<(int Index, CodeCandidate Candidate)>();
        var prefixes = GetPrefixes(channel);
        foreach (var tokenMatch in ExtractTokenMatches(text, message, prefixes))
        {
            candidates.Add(tokenMatch);
        }

        foreach (var prefix in prefixes)
        {
            foreach (var boosted in ExtractPrefixMatches(text, prefix, message))
            {
                candidates.Add(boosted);
            }
        }

        var genericCodeRegex = BuildGenericCodeRegex();
        foreach (Match match in genericCodeRegex.Matches(text))
        {
            if (!match.Success)
            {
                continue;
            }

            if (IsRejectedByPrefixLengthRules(match.Value, prefixes))
            {
                continue;
            }

            foreach (var candidate in CreateGenericCandidates(match.Value, message))
            {
                candidates.Add((match.Index, candidate));
            }
        }

        return candidates
            .OrderBy(static item => item.Index)
            .ThenByDescending(static item => item.Candidate.Score)
            .Select(static item => item.Candidate)
            .DistinctBy(static candidate => candidate.Value, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IEnumerable<(int Index, CodeCandidate Candidate)> ExtractTokenMatches(
        string text,
        string sourceMessage,
        IReadOnlyList<PrefixRule> prefixes)
    {
        foreach (Match tokenMatch in Regex.Matches(text, @"[A-Z0-9]+", RegexOptions.CultureInvariant))
        {
            if (!tokenMatch.Success)
            {
                continue;
            }

            var token = tokenMatch.Value;
            if (token.Length < GenericCodeLength)
            {
                continue;
            }

            var matchedPrefix = prefixes.FirstOrDefault(prefix =>
                token.StartsWith(prefix.Prefix, StringComparison.OrdinalIgnoreCase));

            if (matchedPrefix is not null)
            {
                var expectedLength = matchedPrefix.Prefix.Length + matchedPrefix.SuffixLength;
                if (token.Length == expectedLength)
                {
                    yield return (tokenMatch.Index, new CodeCandidate
                    {
                        Value = token,
                        Score = 420 + (matchedPrefix.Prefix.Length * 2) + matchedPrefix.SuffixLength,
                        Reason = $"prefix-token-fast({matchedPrefix.Prefix})+{matchedPrefix.SuffixLength}",
                        SourceMessage = sourceMessage
                    });
                }

                continue;
            }

            if (token.Length == GenericCodeLength)
            {
                foreach (var candidate in CreateGenericCandidates(token, sourceMessage))
                {
                    yield return (tokenMatch.Index, candidate);
                }
            }
        }
    }

    private static IEnumerable<(int Index, CodeCandidate Candidate)> ExtractPrefixMatches(string text, PrefixRule prefix, string sourceMessage)
    {
        if (string.IsNullOrWhiteSpace(prefix.Prefix))
        {
            yield break;
        }

        var expectedLength = prefix.Prefix.Length + prefix.SuffixLength;
        var exactMatches = new List<(int Index, CodeCandidate Candidate)>();
        var embeddedMatches = new List<(int Index, CodeCandidate Candidate)>();
        var searchStartIndex = 0;
        while (searchStartIndex < text.Length)
        {
            var matchIndex = text.IndexOf(prefix.Prefix, searchStartIndex, StringComparison.OrdinalIgnoreCase);
            if (matchIndex < 0)
            {
                yield break;
            }

            searchStartIndex = matchIndex + 1;
            if (matchIndex + expectedLength > text.Length)
            {
                continue;
            }

            var candidateValue = text.Substring(matchIndex, expectedLength);
            if (!candidateValue.All(static character => char.IsAsciiLetterUpper(character) || char.IsDigit(character)))
            {
                continue;
            }

            var isLeftBounded = matchIndex == 0 || !char.IsAsciiLetterOrDigit(text[matchIndex - 1]);
            var rightIndex = matchIndex + expectedLength;
            var isRightBounded = rightIndex >= text.Length || !char.IsAsciiLetterOrDigit(text[rightIndex]);
            var isExactToken = isLeftBounded && isRightBounded;

            var candidate = new CodeCandidate
            {
                Value = candidateValue,
                Score = (isExactToken ? 320 : 180) + (prefix.Prefix.Length * 2) + prefix.SuffixLength,
                Reason = isExactToken
                    ? $"prefix-token({prefix.Prefix})+{prefix.SuffixLength}"
                    : $"prefix-embedded({prefix.Prefix})+{prefix.SuffixLength}",
                SourceMessage = sourceMessage
            };

            if (isExactToken)
            {
                exactMatches.Add((matchIndex, candidate));
            }
            else
            {
                embeddedMatches.Add((matchIndex, candidate));
            }
        }

        var selectedMatches = exactMatches.Count > 0 ? exactMatches : embeddedMatches;
        foreach (var match in selectedMatches)
        {
            yield return match;
        }
    }

    private static Regex BuildGenericCodeRegex()
    {
        return new Regex(
            $@"(?<![A-Z0-9])[A-Z0-9]{{{GenericCodeLength}}}(?![A-Z0-9])",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);
    }

    private static bool IsRejectedByPrefixLengthRules(string candidateValue, IReadOnlyList<PrefixRule> prefixes)
    {
        foreach (var prefix in prefixes)
        {
            if (!candidateValue.StartsWith(prefix.Prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var expectedLength = prefix.Prefix.Length + prefix.SuffixLength;
            return candidateValue.Length != expectedLength;
        }

        return false;
    }

    private static IEnumerable<CodeCandidate> CreateGenericCandidates(string value, string sourceMessage)
    {
        yield return new CodeCandidate
        {
            Value = value,
            Score = 230,
            Reason = "generic-10",
            SourceMessage = sourceMessage
        };

        foreach (var normalizedValue in ExpandGenericAmbiguousVariants(value))
        {
            yield return new CodeCandidate
            {
                Value = normalizedValue,
                Score = 230,
                Reason = "generic-10-normalized",
                SourceMessage = sourceMessage
            };
        }
    }

    private static IEnumerable<string> ExpandGenericAmbiguousVariants(string value)
    {
        var emitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < value.Length; index++)
        {
            if (!GenericAmbiguousCharacterMap.TryGetValue(value[index], out var replacement))
            {
                continue;
            }

            var buffer = value.ToCharArray();
            buffer[index] = replacement;
            var normalized = new string(buffer);
            if (!normalized.Equals(value, StringComparison.OrdinalIgnoreCase) && emitted.Add(normalized))
            {
                yield return normalized;
            }
        }
    }

    private static List<PrefixRule> GetPrefixes(ChannelProfile channel)
    {
        var prefixes = new List<string> { "KOL", "BRAND" };
        prefixes.AddRange(channel.Prefixes);

        return PrefixRule.ParseMany(prefixes)
            .OrderByDescending(static prefix => prefix.Prefix.Length)
            .ThenByDescending(static prefix => prefix.SuffixLength)
            .ToList();
    }
}
