namespace Mcg.AgentOrchestrator.Core;

public static class AgentOutputDirectives
{
    private static readonly string[] NoHumanInputMarkers = ["none", "no", "not needed", "no input needed"];

    public static IReadOnlyList<string> WorkerResultTemplateLines =>
        WorkerResultTemplateLinesForRole(null);

    public static IReadOnlyList<string> WorkerResultTemplateLinesForRole(AgentRole? role)
    {
        var lines = new List<string>
        {
            "WORKER_RESULT:",
            "files: <comma-separated changed files or none>",
            "commands: <commands run or none>",
            "tests: <pass/fail/not-run evidence>",
            "commit: <commit sha or none>",
            "blockers: <none or exact blocker; put deferred-verification notes in tests>"
        };

        if (role == AgentRole.Researcher)
        {
            lines.Add("citations: <repo files, commands, URLs, or none when no external/source evidence was used>");
        }

        if (role == AgentRole.Reviewer)
        {
            lines.Add("verdict: <pass|fail|needs-work>");
        }

        lines.AddRange(
        [
            "model_fit: <provider/model - adequate|overkill|underpowered - task shape - reason>",
            "skills: <selected skills used or none>",
            "confidence: <high|medium|low>",
            "END_WORKER_RESULT"
        ]);
        return lines;
    }

    public static IReadOnlyList<string> WorkerResultRequiredFieldsForRole(AgentRole role)
    {
        var fields = new List<string>(WorkerResultRequiredFields);

        if (role == AgentRole.Researcher)
        {
            fields.Add("citations");
        }

        if (role == AgentRole.Reviewer)
        {
            fields.Add("verdict");
        }

        return fields;
    }

    private static readonly string[] WorkerResultRequiredFields =
        ["files", "commands", "tests", "blockers", "model_fit", "skills", "confidence"];

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
