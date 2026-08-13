using System.Text.Json;
using System.Text.RegularExpressions;

namespace Mcg.AgentOrchestrator.Core;

/// <summary>
/// Recognizes positive provider-originated subscription and usage-limit diagnostics.
/// Arbitrary worker text is insufficient: evidence must have a provider diagnostic/footer
/// shape or be a structured provider limit event.
/// </summary>
public static partial class ProviderLimitEvidenceParser
{
    public static bool TryGetEvidenceLine(
        string? standardOutput,
        string? standardError,
        out string evidenceLine) =>
        TryGetEvidenceLine(SplitLines(standardOutput).Concat(SplitLines(standardError)), out evidenceLine);

    public static bool TryGetEvidenceLine(IEnumerable<string> lines, out string evidenceLine)
    {
        var inWorkerResultBlock = false;
        foreach (var rawLine in lines)
        {
            var markerLine = rawLine.Trim();
            if (markerLine.Length == 0)
            {
                continue;
            }

            if (IsWorkerResultMarker(markerLine, "WORKER_RESULT"))
            {
                inWorkerResultBlock = true;
                continue;
            }

            if (IsWorkerResultMarker(markerLine, "END_WORKER_RESULT"))
            {
                inWorkerResultBlock = false;
                continue;
            }

            if (!inWorkerResultBlock && IsProviderLimitEvidenceLine(rawLine))
            {
                evidenceLine = rawLine;
                return true;
            }
        }

        evidenceLine = string.Empty;
        return false;
    }

    public static bool IsProviderLimitEvidenceLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line) ||
            char.IsWhiteSpace(line[0]) ||
            LooksLikeSourceLocationEcho(line))
        {
            return false;
        }

        if (!IsProviderDiagnosticLine(line))
        {
            return false;
        }

        return TryParseStructuredLimitEvent(line) || ContainsLimitText(line);
    }

    private static bool IsProviderDiagnosticLine(string line) =>
        line.StartsWith("ERROR:", StringComparison.OrdinalIgnoreCase) ||
        line.Contains(" : ERROR:", StringComparison.Ordinal) ||
        CodexCliDiagnosticPrefix().IsMatch(line);

    private static bool ContainsLimitText(string text) =>
        (text.Contains("usage limit", StringComparison.OrdinalIgnoreCase) &&
         (text.Contains("try again", StringComparison.OrdinalIgnoreCase) ||
          text.Contains("purchase more credits", StringComparison.OrdinalIgnoreCase) ||
          text.Contains("resets in", StringComparison.OrdinalIgnoreCase))) ||
        text.Contains("reached your usage limit", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("hit your usage limit", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("rate limit", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("rate-limit", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("rate-limited", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("ratelimit", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("too many requests", StringComparison.OrdinalIgnoreCase) ||
        Http429Status().IsMatch(text) ||
        text.Contains("retry after", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("try again later due to capacity", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("try again later due to usage", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("quota exceeded", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("rate_limit_error", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("insufficient_quota", StringComparison.OrdinalIgnoreCase);

    private static bool TryParseStructuredLimitEvent(string line)
    {
        var jsonStart = line.IndexOf('{');
        if (jsonStart < 0)
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(line[jsonStart..]);
            var root = document.RootElement;
            return HasLimitToken(root, "event") ||
                HasLimitToken(root, "type") ||
                HasLimitToken(root, "code");
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool HasLimitToken(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        return property.GetString() is { } value && value.ToLowerInvariant() is
            "rate_limit" or "rate_limit_error" or "usage_limit" or "usage_limit_reached" or
            "subscription_limit" or "insufficient_quota";
    }

    private static bool IsWorkerResultMarker(string line, string expected)
    {
        var normalized = new string(line.Where(ch => ch is not ('#' or '*' or '`')).ToArray())
            .Trim()
            .TrimEnd(':')
            .Trim();
        return string.Equals(normalized, expected, StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeSourceLocationEcho(string line)
    {
        var firstColon = line.IndexOf(':');
        if (firstColon <= 0)
        {
            return false;
        }

        var prefix = line[..firstColon];
        return prefix.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ||
            prefix.EndsWith(".fs", StringComparison.OrdinalIgnoreCase) ||
            prefix.EndsWith(".vb", StringComparison.OrdinalIgnoreCase) ||
            prefix.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase) ||
            prefix.EndsWith(".ts", StringComparison.OrdinalIgnoreCase) ||
            prefix.EndsWith(".js", StringComparison.OrdinalIgnoreCase) ||
            prefix.EndsWith(".md", StringComparison.OrdinalIgnoreCase);
    }

    private static string[] SplitLines(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? []
            : text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}T\S+\s+(?:ERROR|WARN)\s+codex_[^:]+:\s", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CodexCliDiagnosticPrefix();

    [GeneratedRegex(@"\b(?:429\s+Too\s+Many\s+Requests|HTTP(?:/\d(?:\.\d)?)?\s+429|(?:http(?:\s+status)?|status(?:\s+code)?|response(?:\s+status)?|error(?:\s+code)?)\s*[:=]?\s*429)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Http429Status();
}
