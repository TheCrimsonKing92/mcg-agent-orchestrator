namespace Mcg.AgentOrchestrator.Core;

public static class AgentOutputDirectives
{
    private static readonly string[] NoHumanInputMarkers = ["none", "no", "not needed", "no input needed"];

    public static string? TryParseHumanInputRequest(string output)
    {
        foreach (var line in output.Split(["\r\n", "\n"], StringSplitOptions.None))
        {
            var trimmed = line.Trim();
            var question = TryReadDirective(trimmed, "HUMAN_INPUT:")
                ?? TryReadDirective(trimmed, "HUMAN INPUT:");
            if (!string.IsNullOrWhiteSpace(question))
            {
                return question;
            }
        }

        return null;
    }

    private static string? TryReadDirective(string value, string prefix)
    {
        if (!value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var directiveValue = value[prefix.Length..].Trim();
        return IsNoHumanInputMarker(directiveValue) ? null : directiveValue;
    }

    private static bool IsNoHumanInputMarker(string value)
    {
        var normalized = value.Trim().TrimEnd('.', '!', ';', ':').Trim();
        return NoHumanInputMarkers.Contains(normalized, StringComparer.OrdinalIgnoreCase);
    }
}
