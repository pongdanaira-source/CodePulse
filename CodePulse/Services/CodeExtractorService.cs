using System.Text.RegularExpressions;
using CodePulse.Models;

namespace CodePulse.Services;

public sealed class CodeExtractorService
{
    private const int GenericCodeLength = 10;
    private const int MaxThaiAliasBridgeSeparatorLength = 8;
    private static readonly IReadOnlyDictionary<char, char> GenericAmbiguousCharacterMap = new Dictionary<char, char>
    {
        ['O'] = '9',
        ['Q'] = '9',
        ['G'] = '9'
    };
    private static readonly IReadOnlyList<ThaiCodeAlias> ThaiCodeAliases = new List<ThaiCodeAlias>
    {
        new("ดับเบิลยู", 'W'),
        new("ดับเบิ้ลยู", 'W'),
        new("ดับบลิว", 'W'),
        new("เอ็กซ์", 'X'),
        new("เอ็ก", 'X'),
        new("แอล", 'L'),
        new("เอล", 'L'),
        new("เอ็ม", 'M'),
        new("เอ็น", 'N'),
        new("เอช", 'H'),
        new("เอส", 'S'),
        new("เอฟ", 'F'),
        new("อาร์", 'R'),
        new("คิว", 'Q'),
        new("แซด", 'Z'),
        new("ศูนย์", '0'),
        new("ศูน", '0'),
        new("สูน", '0'),
        new("หนึ่ง", '1'),
        new("นึง", '1'),
        new("สอง", '2'),
        new("สาม", '3'),
        new("สี่", '4'),
        new("สี", '4'),
        new("ห้า", '5'),
        new("หก", '6'),
        new("เจ็ด", '7'),
        new("เจต", '7'),
        new("แปด", '8'),
        new("เก้า", '9'),
        new("เอ", 'A'),
        new("บี", 'B'),
        new("ซี", 'C'),
        new("ดี", 'D'),
        new("อี", 'E'),
        new("จี", 'G'),
        new("ไอ", 'I'),
        new("เจ", 'J'),
        new("เค", 'K'),
        new("โอ", 'O'),
        new("พี", 'P'),
        new("ที", 'T'),
        new("ยู", 'U'),
        new("วี", 'V'),
        new("วาย", 'Y')
    }
    .OrderByDescending(static alias => alias.Text.Length)
    .ToList();

    public CodeCandidate? ExtractBestCandidate(
        ChannelProfile channel,
        string message,
        bool normalizeThaiCodeAliases = true,
        bool includeGenericAmbiguousVariants = true)
    {
        return ExtractCandidates(channel, message, normalizeThaiCodeAliases, includeGenericAmbiguousVariants)
            .OrderByDescending(static candidate => candidate.Score)
            .FirstOrDefault();
    }

    public IReadOnlyList<CodeCandidate> ExtractCandidates(
        ChannelProfile channel,
        string message,
        bool normalizeThaiCodeAliases = true,
        bool includeGenericAmbiguousVariants = true)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return [];
        }

        var text = message.Trim().ToUpperInvariant();
        var prefixes = GetPrefixes(channel);
        var prefixOnly = channel.PrefixOnly;
        if (prefixOnly && prefixes.Count == 0)
        {
            return [];
        }

        if (normalizeThaiCodeAliases)
        {
            text = NormalizeCodeAdjacentThaiAliases(text, prefixes);
        }

        var minimumCodeLength = prefixOnly
            ? prefixes.Min(static prefix => prefix.Prefix.Length + prefix.SuffixLength)
            : prefixes.Count == 0
                ? GenericCodeLength
                : Math.Min(GenericCodeLength, prefixes.Min(static prefix => prefix.Prefix.Length + prefix.SuffixLength));
        if (text.Length < minimumCodeLength)
        {
            return [];
        }

        var candidates = new List<(int Index, CodeCandidate Candidate)>();
        foreach (var tokenMatch in ExtractTokenMatches(text, message, prefixes, prefixOnly, includeGenericAmbiguousVariants))
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

        if (!prefixOnly)
        {
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

                foreach (var candidate in CreateGenericCandidates(match.Value, message, includeGenericAmbiguousVariants))
                {
                    candidates.Add((match.Index, candidate));
                }
            }
        }

        return candidates
            .OrderBy(static item => item.Index)
            .ThenByDescending(static item => item.Candidate.Score)
            .Select(static item => item.Candidate)
            .DistinctBy(static candidate => candidate.Value, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public IReadOnlyList<RuleRejectedCodeCandidate> ExtractRuleRejectedCandidates(
        ChannelProfile channel,
        string message,
        int maxCandidates = 5)
    {
        if (string.IsNullOrWhiteSpace(message) || maxCandidates <= 0)
        {
            return [];
        }

        var text = message.Trim().ToUpperInvariant();
        var explicitPrefixes = PrefixRule.ParseMany(channel.Prefixes)
            .OrderByDescending(static prefix => prefix.Prefix.Length)
            .ThenByDescending(static prefix => prefix.SuffixLength)
            .ToList();
        var acceptedValues = ExtractCandidates(
                channel,
                message,
                normalizeThaiCodeAliases: false,
                includeGenericAmbiguousVariants: false)
            .Select(static candidate => candidate.Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var rejected = new List<RuleRejectedCodeCandidate>();

        foreach (Match tokenMatch in Regex.Matches(text, @"[A-Z0-9]{8,32}", RegexOptions.CultureInvariant))
        {
            if (!tokenMatch.Success)
            {
                continue;
            }

            var token = tokenMatch.Value;
            if (acceptedValues.Contains(token))
            {
                continue;
            }

            var rejection = TryDescribeRuleRejection(channel, token, explicitPrefixes);
            if (rejection is null)
            {
                continue;
            }

            if (rejected.Any(item => item.Value.Equals(token, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            rejected.Add(new RuleRejectedCodeCandidate(token, rejection));
            if (rejected.Count >= maxCandidates)
            {
                break;
            }
        }

        return rejected;
    }

    private static IEnumerable<(int Index, CodeCandidate Candidate)> ExtractTokenMatches(
        string text,
        string sourceMessage,
        IReadOnlyList<PrefixRule> prefixes,
        bool prefixOnly,
        bool includeGenericAmbiguousVariants)
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

            if (prefixOnly)
            {
                continue;
            }

            if (token.Length == GenericCodeLength)
            {
                if (IsRejectedByPrefixLengthRules(token, prefixes))
                {
                    continue;
                }

                foreach (var candidate in CreateGenericCandidates(token, sourceMessage, includeGenericAmbiguousVariants))
                {
                    yield return (tokenMatch.Index, candidate);
                }
            }
            else if (token.Length > GenericCodeLength)
            {
                foreach (var candidate in ExtractJoinedGenericMatches(token, tokenMatch.Index, sourceMessage, prefixes, includeGenericAmbiguousVariants))
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
        IReadOnlyList<PrefixRule> prefixes,
        bool includeGenericAmbiguousVariants)
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

                    foreach (var candidate in CreateGenericCandidates(value, sourceMessage, includeGenericAmbiguousVariants))
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

    private static string? TryDescribeRuleRejection(
        ChannelProfile channel,
        string token,
        IReadOnlyList<PrefixRule> explicitPrefixes)
    {
        if (channel.PrefixOnly)
        {
            if (explicitPrefixes.Count == 0)
            {
                return "Prefix only is enabled but this channel has no prefix rules.";
            }

            var matchedPrefix = explicitPrefixes.FirstOrDefault(prefix =>
                token.StartsWith(prefix.Prefix, StringComparison.OrdinalIgnoreCase));
            if (matchedPrefix is null)
            {
                return $"Prefix only rejected it. Allowed: {FormatPrefixRules(explicitPrefixes)}";
            }

            var expectedLength = matchedPrefix.Prefix.Length + matchedPrefix.SuffixLength;
            if (token.Length != expectedLength)
            {
                return $"{matchedPrefix.DisplayText} expects {expectedLength} characters, OCR read {token.Length}.";
            }

            return null;
        }

        var prefixes = GetPrefixes(channel);
        foreach (var prefix in prefixes)
        {
            if (!token.StartsWith(prefix.Prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var expectedLength = prefix.Prefix.Length + prefix.SuffixLength;
            if (token.Length != expectedLength)
            {
                return $"{prefix.DisplayText} expects {expectedLength} characters, OCR read {token.Length}.";
            }
        }

        return null;
    }

    private static IEnumerable<CodeCandidate> CreateGenericCandidates(
        string value,
        string sourceMessage,
        bool includeGenericAmbiguousVariants)
    {
        yield return new CodeCandidate
        {
            Value = value,
            Score = 230,
            Reason = "generic-10",
            SourceMessage = sourceMessage
        };

        if (!includeGenericAmbiguousVariants)
        {
            yield break;
        }

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

    private static string NormalizeCodeAdjacentThaiAliases(
        string text,
        IReadOnlyList<PrefixRule> prefixes)
    {
        if (string.IsNullOrWhiteSpace(text) ||
            !text.Any(IsThaiCharacter))
        {
            return text;
        }

        var normalized = text;
        var searchIndex = 0;
        while (searchIndex < normalized.Length)
        {
            var replaced = false;
            foreach (var alias in ThaiCodeAliases)
            {
                if (searchIndex + alias.Text.Length > normalized.Length ||
                    !normalized.AsSpan(searchIndex, alias.Text.Length).SequenceEqual(alias.Text))
                {
                    continue;
                }

                var nextIndex = searchIndex + alias.Text.Length;
                if (!TouchesCodeRun(normalized, searchIndex, nextIndex))
                {
                    if (!TryBuildSeparatedThaiAliasReplacement(
                            normalized,
                            searchIndex,
                            alias,
                            prefixes,
                            out var replacementStart,
                            out var replacementLength,
                            out var replacementText))
                    {
                        continue;
                    }

                    normalized = normalized
                        .Remove(replacementStart, replacementLength)
                        .Insert(replacementStart, replacementText);
                    searchIndex = replacementStart + replacementText.Length;
                    replaced = true;
                    break;
                }

                normalized = normalized
                    .Remove(searchIndex, alias.Text.Length)
                    .Insert(searchIndex, alias.Replacement.ToString());
                searchIndex++;
                replaced = true;
                break;
            }

            if (!replaced)
            {
                searchIndex++;
            }
        }

        return normalized;
    }

    private static bool TryBuildSeparatedThaiAliasReplacement(
        string text,
        int aliasStartIndex,
        ThaiCodeAlias alias,
        IReadOnlyList<PrefixRule> prefixes,
        out int replacementStart,
        out int replacementLength,
        out string replacementText)
    {
        replacementStart = 0;
        replacementLength = 0;
        replacementText = string.Empty;

        var aliasEndIndex = aliasStartIndex + alias.Text.Length;
        var leftRun = GetLeftAsciiRunAcrossBridge(text, aliasStartIndex, out var leftRunStart, out var leftRunEnd);
        var rightRun = GetRightAsciiRunAcrossBridge(text, aliasEndIndex, out var rightRunStart, out var rightRunEnd);

        if (leftRun.Length > 0 && rightRun.Length > 0)
        {
            var combined = leftRun + alias.Replacement + rightRun;
            if (IsAcceptedNormalizedCode(combined, prefixes))
            {
                replacementStart = leftRunStart;
                replacementLength = rightRunEnd - leftRunStart;
                replacementText = combined;
                return true;
            }
        }

        if (leftRun.Length > 0)
        {
            var leftCombined = leftRun + alias.Replacement;
            if (IsAcceptedNormalizedCode(leftCombined, prefixes))
            {
                replacementStart = leftRunStart;
                replacementLength = aliasEndIndex - leftRunStart;
                replacementText = leftCombined;
                return true;
            }
        }

        if (rightRun.Length > 0)
        {
            var rightCombined = alias.Replacement + rightRun;
            if (IsAcceptedNormalizedCode(rightCombined, prefixes))
            {
                replacementStart = aliasStartIndex;
                replacementLength = rightRunEnd - aliasStartIndex;
                replacementText = rightCombined;
                return true;
            }
        }

        return false;
    }

    private static string GetLeftAsciiRunAcrossBridge(
        string text,
        int bridgeEnd,
        out int runStart,
        out int runEnd)
    {
        runStart = bridgeEnd;
        runEnd = bridgeEnd;

        var index = bridgeEnd - 1;
        var bridgeLength = 0;
        while (index >= 0 &&
               bridgeLength < MaxThaiAliasBridgeSeparatorLength &&
               IsCodeBridgeSeparator(text[index]))
        {
            index--;
            bridgeLength++;
        }

        runEnd = index + 1;
        if (runEnd == bridgeEnd || index < 0 || !char.IsAsciiLetterOrDigit(text[index]))
        {
            return string.Empty;
        }

        while (index >= 0 && char.IsAsciiLetterOrDigit(text[index]))
        {
            index--;
        }

        runStart = index + 1;
        return text.Substring(runStart, runEnd - runStart);
    }

    private static string GetRightAsciiRunAcrossBridge(
        string text,
        int bridgeStart,
        out int runStart,
        out int runEnd)
    {
        runStart = bridgeStart;
        runEnd = bridgeStart;

        var index = bridgeStart;
        var bridgeLength = 0;
        while (index < text.Length &&
               bridgeLength < MaxThaiAliasBridgeSeparatorLength &&
               IsCodeBridgeSeparator(text[index]))
        {
            index++;
            bridgeLength++;
        }

        runStart = index;
        if (runStart == bridgeStart || index >= text.Length || !char.IsAsciiLetterOrDigit(text[index]))
        {
            return string.Empty;
        }

        while (index < text.Length && char.IsAsciiLetterOrDigit(text[index]))
        {
            index++;
        }

        runEnd = index;
        return text.Substring(runStart, runEnd - runStart);
    }

    private static bool IsAcceptedNormalizedCode(string value, IReadOnlyList<PrefixRule> prefixes)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            !value.All(static character => char.IsAsciiLetterUpper(character) || char.IsDigit(character)))
        {
            return false;
        }

        foreach (var prefix in prefixes)
        {
            if (!value.StartsWith(prefix.Prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return value.Length == prefix.Prefix.Length + prefix.SuffixLength;
        }

        return value.Length == GenericCodeLength;
    }

    private static bool TouchesCodeRun(string text, int startIndex, int nextIndex)
    {
        return (startIndex > 0 && char.IsAsciiLetterOrDigit(text[startIndex - 1])) ||
               (nextIndex < text.Length && char.IsAsciiLetterOrDigit(text[nextIndex]));
    }

    private static bool IsCodeBridgeSeparator(char value)
    {
        return char.IsWhiteSpace(value) || value is '-' or '_' or '/' or '\\' or '.' or ':' or ',' or ';' or '|';
    }

    private static bool IsThaiCharacter(char value)
    {
        return value is >= '\u0E00' and <= '\u0E7F';
    }

    private static List<PrefixRule> GetPrefixes(ChannelProfile channel)
    {
        var prefixes = channel.PrefixOnly
            ? new List<string>()
            : new List<string> { "KOL", "BRAND" };
        prefixes.AddRange(channel.Prefixes);

        return PrefixRule.ParseMany(prefixes)
            .OrderByDescending(static prefix => prefix.Prefix.Length)
            .ThenByDescending(static prefix => prefix.SuffixLength)
            .ToList();
    }

    private static string FormatPrefixRules(IReadOnlyList<PrefixRule> prefixes)
    {
        return prefixes.Count == 0
            ? "-"
            : string.Join(", ", prefixes.Select(static prefix => prefix.DisplayText));
    }

    public sealed record RuleRejectedCodeCandidate(string Value, string Reason);

    private readonly record struct ThaiCodeAlias(string Text, char Replacement);
}
