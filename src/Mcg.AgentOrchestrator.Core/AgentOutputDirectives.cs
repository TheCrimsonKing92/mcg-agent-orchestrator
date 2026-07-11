namespace Mcg.AgentOrchestrator.Core;

public static class AgentOutputDirectives
{
    private static readonly string[] NoHumanInputMarkers = ["none", "no", "not needed", "no input needed"];
    private static readonly string[] BaseWorkerResultFields =
        ["files", "commands", "tests", "blockers", "model_fit", "skills", "confidence"];

    public static IReadOnlyList<string> WorkerResultTemplateLines =>
        WorkerResultTemplateLinesForRole(null);

    public static IReadOnlyList<string> WorkerResultTemplateLinesForRole(AgentRole? role) =>
    [
        "WORKER_RESULT:",
        .. WorkerResultFieldTemplateLinesForRole(role),
        "END_WORKER_RESULT"
    ];

    public static IReadOnlyList<string> WorkerResultFieldNames => BaseWorkerResultFields;

    public static IReadOnlyList<string> RequiredWorkerResultFieldNamesForRole(AgentRole role) =>
        role switch
        {
            AgentRole.Researcher => [.. BaseWorkerResultFields, "citations"],
            AgentRole.Reviewer => [.. BaseWorkerResultFields, "verdict"],
            _ => BaseWorkerResultFields
        };

    private static IReadOnlyList<string> WorkerResultFieldTemplateLinesForRole(AgentRole? role)
    {
        var lines = new List<string>
        {
            "files: <comma-separated changed files or none>",
            "commands: <commands run or none>",
            "tests: <pass|fail|not-run|deferred - evidence>",
            "commit: <commit sha or none>",
            "blockers: <none|exact-blocker; put deferred-verification notes in tests>"
        };

        if (role == AgentRole.Researcher)
        {
            lines.Add("citations: <repository files, docs, or evidence sources used>");
        }
        else if (role == AgentRole.Reviewer)
        {
            lines.Add("verdict: <pass|needs-work|fail>");
        }

        lines.AddRange(
        [
            "model_fit: <provider/model - adequate|overkill|underpowered - task shape - reason>",
            "skills: <selected skills used or none>",
            "confidence: <high|medium|low>"
        ]);

        return lines;
    }

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
