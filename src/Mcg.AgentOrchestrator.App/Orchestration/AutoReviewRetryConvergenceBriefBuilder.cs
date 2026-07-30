using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class AutoReviewRetryConvergenceBriefBuilder
{
    internal const string AcceptedShapePreamble =
        "Existing implementation shape is accepted. Do NOT rewrite or re-architect the accepted work; preserve it and close only the residual blockers below.";

    internal const string GenericRerunMandate =
        "Rerun the focused test classes covering your changed files at your final commit and quote receipts.";

    internal const string PreserveAcceptedDirective =
        "The following findings are accepted — do not rewrite these sections; close ONLY the residual findings listed below.";

    private static readonly Regex WhitespacePattern = new(@"\s+", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex ExplicitFocusedTestClassPattern = new(
        @"(?:FullyQualifiedName~|tests[/\\][A-Za-z0-9_.-]+[/\\])(?<class>[A-Z][A-Za-z0-9_]*(?:Tests|Test))(?:\.cs)?\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    internal static string BuildConvergenceBrief(
        Goal goal,
        TaskSpec targetTask,
        TaskSpec triggeringTask,
        string currentFinding,
        string triggerLabel,
        AgentRole targetRole,
        int round,
        string outputArtifact,
        IEnumerable<string> changedFileScopes)
    {
        ArgumentNullException.ThrowIfNull(goal);
        ArgumentNullException.ThrowIfNull(targetTask);
        ArgumentNullException.ThrowIfNull(triggeringTask);

        if (triggeringTask.RequiredRole == AgentRole.Reviewer)
        {
            var findings = ReadStructuredReviewFindingState(goal, triggeringTask);
            return BuildStructuredConvergenceBrief(
                round,
                triggeringTask,
                triggerLabel,
                targetRole,
                outputArtifact,
                findings,
                changedFileScopes);
        }

        var accumulatedFindings = CollectAccumulatedTesterFindings(goal, targetTask, triggeringTask, currentFinding);
        return BuildConvergenceBrief(
            round,
            triggeringTask.RequiredRole,
            triggeringTask.Id,
            triggerLabel,
            targetRole,
            outputArtifact,
            accumulatedFindings,
            changedFileScopes);
    }

    private static string BuildStructuredConvergenceBrief(
        int round,
        TaskSpec triggeringTask,
        string triggerLabel,
        AgentRole targetRole,
        string outputArtifact,
        IReadOnlyList<ReviewFinding> findings,
        IEnumerable<string> changedFileScopes)
    {
        var open = findings
            .Where(finding => finding.State == ReviewFindingState.Open)
            .OrderBy(finding => finding.Category == FindingCategory.SpecCompliance ? 0 : 1)
            .ToArray();
        var accepted = findings
            .Where(finding => finding.State == ReviewFindingState.Resolved)
            .ToArray();
        if (open.Length == 0)
        {
            throw new ReviewFindingConvergenceException(
                "ERR_REVIEW_NEEDS_WORK_WITHOUT_OPEN_FINDINGS",
                0,
                0,
                "Reviewer verdict=needs-work contained no structured open findings.");
        }

        var lines = new List<string>
        {
            $"auto-review-retry round {round} convergence brief: Reviewer task {triggeringTask.Id.Value[..8]} {triggerLabel}; retry upstream {targetRole} task.",
            AcceptedShapePreamble,
            "## RESIDUAL_OPEN_ACTION_ITEMS",
            $"open_count: {open.Length}"
        };

        foreach (var finding in open)
        {
            lines.Add($"- stable_id: {finding.StableId}");
            if (finding.Category != FindingCategory.Unspecified)
            {
                lines.Add($"  category: {FindingCategoryJsonConverter.ToWireValue(finding.Category)}");
            }
            lines.Add($"  location: {finding.Location}");
            lines.Add($"  description: {finding.Description}");
        }

        lines.Add("## PRESERVE_ACCEPTED");
        lines.Add(PreserveAcceptedDirective);
        lines.Add($"accepted_count: {accepted.Length}");
        foreach (var finding in accepted)
        {
            lines.Add($"- stable_id: {finding.StableId}");
            lines.Add($"  location: {finding.Location}");
        }

        lines.Add(BuildFocusedTestReceiptMandate(open.Select(finding => finding.Description).ToArray(), changedFileScopes));
        lines.Add($"Full reviewer output: {outputArtifact}");
        return string.Join(Environment.NewLine, lines);
    }

    internal static string BuildConvergenceBrief(
        int round,
        AgentRole triggeringRole,
        TaskId triggeringTaskId,
        string triggerLabel,
        AgentRole targetRole,
        string outputArtifact,
        IEnumerable<string> accumulatedFindings,
        IEnumerable<string> changedFileScopes)
    {
        var residualBlockers = DeduplicateConvergenceFindings(accumulatedFindings);
        var lines = new List<string>
        {
            $"auto-review-retry round {round} convergence brief: {triggeringRole} task {triggeringTaskId.Value[..8]} {triggerLabel}; retry upstream {targetRole} task.",
            AcceptedShapePreamble,
            "Deduplicated residual blockers accumulated across reviewer/tester retry rounds:"
        };

        if (residualBlockers.Count == 0)
        {
            lines.Add("- No concrete reviewer/tester blocker text was extracted; inspect the verifier output artifact before changing code.");
        }
        else
        {
            foreach (var blocker in residualBlockers)
            {
                lines.Add($"- {blocker}");
            }
        }

        lines.Add(BuildFocusedTestReceiptMandate(residualBlockers, changedFileScopes));
        lines.Add($"Full {triggeringRole.ToString().ToLowerInvariant()} output: {outputArtifact}");
        return string.Join(Environment.NewLine, lines);
    }

    internal static IReadOnlyList<string> InferFocusedTestClasses(
        IEnumerable<string> findings,
        IEnumerable<string> changedFileScopes)
    {
        var classes = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var finding in findings)
        {
            foreach (Match match in ExplicitFocusedTestClassPattern.Matches(finding))
            {
                classes.Add(match.Groups["class"].Value);
            }
        }

        foreach (var scope in changedFileScopes)
        {
            var normalized = scope.Replace('\\', '/').Trim();
            if (!normalized.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var name = Path.GetFileNameWithoutExtension(normalized);
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            if (normalized.StartsWith("tests/", StringComparison.OrdinalIgnoreCase) &&
                name.EndsWith("Tests", StringComparison.Ordinal))
            {
                classes.Add(name);
            }
        }

        return classes.ToArray();
    }

    private static IReadOnlyList<string> CollectAccumulatedTesterFindings(
        Goal goal,
        TaskSpec targetTask,
        TaskSpec triggeringTask,
        string currentFinding)
    {
        var findings = new List<string>();
        var relevantTasks = goal.Tasks
            .SkipWhile(task => task.Id != targetTask.Id)
            .Where(task => task.Id == triggeringTask.Id || task.RequiredRole == AgentRole.Tester);

        foreach (var task in relevantTasks)
        {
            foreach (var verification in task.VerificationHistory)
            {
                if (task.RequiredRole == AgentRole.Tester &&
                    WorkerResultBlockers.TryFindHardFailureBlocker(verification, out var testerBlocker))
                {
                    findings.AddRange(SplitConvergenceFindings(testerBlocker));
                }
            }
        }

        findings.AddRange(SplitConvergenceFindings(currentFinding));
        return findings;
    }

    internal static IReadOnlyList<ReviewFinding> ReadStructuredReviewFindingState(
        Goal goal,
        TaskSpec triggeringTask)
    {
        IReadOnlyList<ReviewFinding> state = [];
        var verifications = goal.Tasks
            .Where(task => task.RequiredRole == AgentRole.Reviewer)
            .SelectMany(task => task.VerificationHistory)
            .Where(verification => verification.CompletedAt <= triggeringTask.LastVerification!.CompletedAt)
            .OrderBy(verification => verification.CompletedAt)
            .ToArray();

        foreach (var verification in verifications)
        {
            if (!WorkerResultBlockers.TryFindReviewFindingRound(verification, out var nextRound, out _))
            {
                // Reviewer history legitimately contains records without a structured findings round - manual
                // verifications (verify-manual) carry only the operator note. Current-round validity is enforced
                // when the round is recorded; replaying history here must skip such records, not strand the goal.
                continue;
            }

            try
            {
                state = ReviewFindingConvergence.ApplyRound(state, nextRound);
            }
            catch (ReviewFindingConvergenceException)
            {
                // A round that cannot be folded was already rejected when it was recorded. Skip it rather
                // than abandon the brief: the residual-items list is derived from the rounds that ARE valid,
                // and refusing to build a brief here strands the goal with no retry path at all.
            }
        }

        return state
            .Where(finding => !WorkerResultBlockers.IsSuppressedByCriteriaCorrection(
                finding.Description,
                goal.EffectiveAcceptanceCriteriaCorrections))
            .ToArray();
    }

    private static IReadOnlyList<string> DeduplicateConvergenceFindings(IEnumerable<string> findings)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var finding in findings)
        {
            var normalized = NormalizeConvergenceText(finding);
            if (normalized.Length > 0 && seen.Add(normalized))
            {
                result.Add(normalized);
            }
        }

        return result;
    }

    private static IReadOnlyList<string> SplitConvergenceFindings(string finding)
    {
        var items = finding
            .Replace("\r\n", "\n")
            .Split(['\n', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(item => item.TrimStart('-', '*', ' '))
            .Where(item => item.Length > 0)
            .ToArray();

        return items.Length == 0 ? [finding.Trim()] : items;
    }

    private static string BuildFocusedTestReceiptMandate(
        IReadOnlyList<string> residualBlockers,
        IEnumerable<string> changedFileScopes)
    {
        var testClasses = InferFocusedTestClasses(residualBlockers, changedFileScopes);
        if (testClasses.Count == 0)
        {
            return GenericRerunMandate;
        }

        return $"Rerun these focused test classes at your final commit and quote receipts: {string.Join(", ", testClasses)}.";
    }

    private static string NormalizeConvergenceText(string value) =>
        WhitespacePattern.Replace(value.Trim(), " ");
}
