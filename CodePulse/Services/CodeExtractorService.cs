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
            foreach (var boosted in ExtractPrefixMatches(text, prefix, message, prefixes))
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
            }

            if (token.Length == GenericCodeLength)
            {
                if (IsRejectedByPrefixLengthRules(token, prefixes))
                {
                    continue;
                }

                foreach (var candidate in CreateGenericCandidates(token, sourceMessage))
                {
                    yield return (tokenMatch.Index, candidate);
                }
            }
            else if (token.Length > GenericCodeLength)
            {
                foreach (var candidate in ExtractJoinedGenericMatches(token, tokenMatch.Index, sourceMessage, prefixes))
                {
                    yield return candidate;
                }
            }
        }
    }

    private static IEnumerable<(int Index, CodeCandidate Candidate)> ExtractPrefixMatches(
        string text,
        PrefixRule prefix,
        string sourceMessage,
        IReadOnlyList<PrefixRule> prefixes)
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
                break;
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

            if (IsRejectedByPrefixLengthRules(candidateValue, prefixes))
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

    private static IEnumerable<(int Index, CodeCandidate Candidate)> ExtractJoinedGenericMatches(
        string token,
        int tokenStartIndex,
        string sourceMessage,
        IReadOnlyList<PrefixRule> prefixes)
    {
        var occupied = new bool[token.Length];
        foreach (var span in FindPrefixSpans(token, prefixes))
        {
            for (var index = span.Start; index < span.Start + span.Length && index < occupied.Length; index++)
            {
                occupied[index] = true;
            }
        }

        var segmentStart = 0;
        while (segmentStart < token.Length)
        {
            while (segmentStart < token.Length && occupied[segmentStart])
            {
                segmentStart++;
            }

            if (segmentStart >= token.Length)
            {
                break;
            }

            var segmentEnd = segmentStart;
            while (segmentEnd < token.Length && !occupied[segmentEnd])
            {
                segmentEnd++;
            }

            var segmentLength = segmentEnd - segmentStart;
            var touchesPrefix = (segmentStart > 0 && occupied[segmentStart - 1]) ||
                                (segmentEnd < token.Length && occupied[segmentEnd]);
            if (touchesPrefix && segmentLength % GenericCodeLength == 0)
            {
                for (var offset = 0; offset < segmentLength; offset += GenericCodeLength)
                {
                    var value = token.Substring(segmentStart + offset, GenericCodeLength);
                    if (IsRejectedByPrefixLengthRules(value, prefixes))
                    {
                        continue;
                    }

                    foreach (var candidate in CreateGenericCandidates(value, sourceMessage))
                    {
                        yield return (tokenStartIndex + segmentStart + offset, candidate);
                    }
                }
            }

            segmentStart = segmentEnd;
        }
    }

    private static List<(int Start, int Length)> FindPrefixSpans(string token, IReadOnlyList<PrefixRule> prefixes)
    {
        var spans = new List<(int Start, int Length)>();
        foreach (var prefix in prefixes)
        {
            var expectedLength = prefix.Prefix.Length + prefix.SuffixLength;
            var searchStartIndex = 0;
            while (searchStartIndex < token.Length)
            {
                var matchIndex = token.IndexOf(prefix.Prefix, searchStartIndex, StringComparison.OrdinalIgnoreCase);
                if (matchIndex < 0)
                {
                    break;
                }

                searchStartIndex = matchIndex + 1;
                if (matchIndex + expectedLength > token.Length)
                {
                    continue;
                }

                spans.Add((matchIndex, expectedLength));
            }
        }

        return spans
            .OrderBy(static span => span.Start)
            .ThenByDescending(static span => span.Length)
            .Aggregate(new List<(int Start, int Length)>(), static (selected, span) =>
            {
                var overlaps = selected.Any(existing =>
                    span.Start < existing.Start + existing.Length &&
                    existing.Start < span.Start + span.Length);
                if (!overlaps)
                {
                    selected.Add(span);
                }

                return selected;
            });
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
