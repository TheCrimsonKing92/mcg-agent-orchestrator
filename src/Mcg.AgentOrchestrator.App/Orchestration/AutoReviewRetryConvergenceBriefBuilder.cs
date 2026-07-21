using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class AutoReviewRetryConvergenceBriefBuilder
{
    internal const string AcceptedShapePreamble =
        "Existing implementation shape is accepted. Do NOT rewrite or re-architect the accepted work; preserve it and close only the residual blockers below.";

    internal const string GenericRerunMandate =
        "Rerun the focused test classes covering your changed files at your final commit and quote receipts.";

    private static readonly Regex WhitespacePattern = new(@"\s+", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex FocusedTestClassPattern = new(
        @"(?:FullyQualifiedName~|tests[/\\][A-Za-z0-9_.-]+[/\\])?(?<class>[A-Z][A-Za-z0-9_]*(?:Tests|Test))(?:\.cs)?\b",
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

        var accumulatedFindings = CollectAccumulatedVerifyingFindings(goal, targetTask, triggeringTask, currentFinding);
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
            foreach (Match match in FocusedTestClassPattern.Matches(finding))
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
            else if (normalized.StartsWith("src/", StringComparison.OrdinalIgnoreCase))
            {
                classes.Add(name.EndsWith("Tests", StringComparison.Ordinal) ? name : $"{name}Tests");
            }
        }

        return classes.ToArray();
    }

    private static IReadOnlyList<string> CollectAccumulatedVerifyingFindings(
        Goal goal,
        TaskSpec targetTask,
        TaskSpec triggeringTask,
        string currentFinding)
    {
        var findings = new List<string>();
        var relevantTasks = goal.Tasks
            .SkipWhile(task => task.Id != targetTask.Id)
            .Where(task => task.Id == triggeringTask.Id ||
                task.RequiredRole is AgentRole.Reviewer or AgentRole.Tester);

        foreach (var task in relevantTasks)
        {
            foreach (var verification in task.VerificationHistory)
            {
                if (task.RequiredRole == AgentRole.Reviewer &&
                    WorkerResultBlockers.TryFindUnsuppressedNeedsWorkVerdict(
                        verification,
                        goal.EffectiveAcceptanceCriteriaCorrections,
                        out var reviewerBlocker,
                        out _))
                {
                    findings.AddRange(SplitConvergenceFindings(reviewerBlocker));
                }
                else if (task.RequiredRole == AgentRole.Tester &&
                    WorkerResultBlockers.TryFindHardFailureBlocker(verification, out var testerBlocker))
                {
                    findings.AddRange(SplitConvergenceFindings(testerBlocker));
                }
            }
        }

        findings.AddRange(SplitConvergenceFindings(currentFinding));
        return findings;
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
