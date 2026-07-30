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
            AgentRole.Reviewer => [.. BaseWorkerResultFields, "findings", "touched_anchors", "verdict"],
            _ => BaseWorkerResultFields
        };

    private static IReadOnlyList<string> WorkerResultFieldTemplateLinesForRole(AgentRole? role)
    {
        var lines = new List<string>
        {
            "files: <comma-separated changed files or none>",
            "commands: <commands run or none>",
            "tests: <pass|fail|not-run|deferred|inconclusive - token first, then current-round evidence>",
            "commit: <commit sha or none>",
            role == AgentRole.Reviewer
                ? "blockers: <none - token first when verdict is pass; exact-blocker - token first for every open blocking finding; blank is invalid; advisory findings belong only in findings>"
                : "blockers: <none|premise-invalid - fact and evidence (Planner/Researcher only)|exact-blocker - token first; blank is invalid; put deferred-verification notes in tests>"
        };

        if (role == AgentRole.Researcher)
        {
            lines.Add("citations: <repository files, docs, or evidence sources used>");
        }
        else if (role == AgentRole.Reviewer)
        {
            lines.Add("evidence-request: <optional; ProjectAlias: FullyQualifiedName~TestClass or ProjectAlias: TestClass1,TestClass2>");
            lines.Add("findings: <one-line JSON array of {stable_id,state:open|resolved,severity:blocking|advisory,category:spec-compliance|spec-defect|correctness|test-evidence|test-coverage|code-quality|operator-owned,location:{file,region,hunk?},description}; severity is required; category defaults to unspecified; [] when none>");
            lines.Add("touched_anchors: <one-line JSON array of {file,region,hunk?} for prior finding anchors touched by this round's diff; [] when none>");
            lines.Add("criteria_verdicts: <one-line JSON array of {criterion_index,verdict:met|not-met|not-verifiable,evidence}; [] when the goal has no refined acceptance criteria>");
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
