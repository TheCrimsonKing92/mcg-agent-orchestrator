using System.Text;

namespace Mcg.AgentOrchestrator.Core;

internal static class EffectiveAcceptanceCriteriaCorrectionParser
{
    private static readonly string[] Markers =
    [
        "CRITERIA CORRECTION",
        "CONTRACT CORRECTION"
    ];

    public static IReadOnlyList<EffectiveAcceptanceCriteriaCorrection> Parse(
        string message,
        string actor,
        DateTimeOffset recordedAt,
        TaskId? sourceTaskId,
        ProgressKind sourceKind)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return [];
        }

        var corrections = new List<EffectiveAcceptanceCriteriaCorrection>();
        foreach (var rawLine in message.Replace("\r\n", "\n").Split('\n'))
        {
            var line = rawLine.Trim();
            if (!TryReadCorrectionPayload(line, out var payload) ||
                !TryReadNamedValue(payload, "supersedes", out var superseded) ||
                !TryReadNamedValue(payload, "correction", out var correction))
            {
                continue;
            }

            corrections.Add(new EffectiveAcceptanceCriteriaCorrection(
                superseded,
                correction,
                actor,
                recordedAt,
                sourceTaskId,
                sourceKind));
        }

        return corrections;
    }

    public static bool TryFindMatchingCorrection(
        string finding,
        IReadOnlyList<EffectiveAcceptanceCriteriaCorrection> corrections,
        out EffectiveAcceptanceCriteriaCorrection correction)
    {
        foreach (var candidate in corrections.OrderByDescending(item => item.RecordedAt))
        {
            if (MatchesSupersededCriterion(finding, candidate.SupersededCriterion))
            {
                correction = candidate;
                return true;
            }
        }

        correction = null!;
        return false;
    }

    private static bool TryReadCorrectionPayload(string line, out string payload)
    {
        payload = string.Empty;
        foreach (var marker in Markers)
        {
            if (!line.StartsWith(marker, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            payload = line[marker.Length..].TrimStart();
            if (payload.StartsWith(':'))
            {
                payload = payload[1..].TrimStart();
            }

            return payload.Length > 0;
        }

        return false;
    }

    private static bool TryReadNamedValue(string payload, string name, out string value)
    {
        value = string.Empty;
        var key = $"{name}=";
        var keyIndex = payload.IndexOf(key, StringComparison.OrdinalIgnoreCase);
        if (keyIndex < 0)
        {
            return false;
        }

        var index = keyIndex + key.Length;
        if (index >= payload.Length)
        {
            return false;
        }

        if (payload[index] is '"' or '\'')
        {
            var quote = payload[index++];
            var end = payload.IndexOf(quote, index);
            if (end < index)
            {
                return false;
            }

            value = payload[index..end].Trim();
            return value.Length > 0;
        }

        var delimiter = payload.IndexOf(';', index);
        value = (delimiter < 0 ? payload[index..] : payload[index..delimiter]).Trim();
        return value.Length > 0;
    }

    private static bool MatchesSupersededCriterion(string finding, string supersededCriterion)
    {
        var normalizedFinding = NormalizeForMatch(finding);
        var normalizedSuperseded = NormalizeForMatch(supersededCriterion);
        if (normalizedFinding.Length == 0 || normalizedSuperseded.Length == 0)
        {
            return false;
        }

        if ((normalizedSuperseded.Length >= 10 && normalizedFinding.Contains(normalizedSuperseded, StringComparison.Ordinal)) ||
            (normalizedFinding.Length >= 10 && normalizedSuperseded.Contains(normalizedFinding, StringComparison.Ordinal)))
        {
            return true;
        }

        var supersededTokens = SignificantTokens(normalizedSuperseded).ToArray();
        if (supersededTokens.Length < 2)
        {
            return false;
        }

        var findingTokens = SignificantTokens(normalizedFinding).ToHashSet(StringComparer.Ordinal);
        var requiredMatches = Math.Min(3, supersededTokens.Length);
        return supersededTokens.Count(findingTokens.Contains) >= requiredMatches;
    }

    private static IEnumerable<string> SignificantTokens(string value)
    {
        return value
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(token => token.Length >= 4)
            .Where(token => token is not "must" and not "should" and not "with" and not "that" and not "this" and not "from" and not "into");
    }

    private static string NormalizeForMatch(string value)
    {
        var builder = new StringBuilder(value.Length);
        var previousWasSpace = true;
        foreach (var ch in value.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(ch);
                previousWasSpace = false;
                continue;
            }

            if (!previousWasSpace)
            {
                builder.Append(' ');
                previousWasSpace = true;
            }
        }

        return builder.ToString().Trim();
    }
}
