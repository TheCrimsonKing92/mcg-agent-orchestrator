using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class AutoReviewRetryConvergenceBriefBuilder
{
    private sealed record StructuredFindingSource(AgentRole Role, ReviewFinding Finding);

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
            var findings = ReadStructuredReviewFindingStates(goal, triggeringTask);
            return BuildStructuredConvergenceBrief(
                round,
                triggeringTask,
                triggerLabel,
                targetRole,
                outputArtifact,
                findings,
                changedFileScopes);
        }

        if (triggeringTask.RequiredRole == AgentRole.Tester &&
            triggeringTask.LastVerification?.MergedReviewFindings is { Count: > 0 })
        {
            return BuildStructuredConvergenceBrief(
                round,
                triggeringTask,
                triggerLabel,
                targetRole,
                outputArtifact,
                ReadStructuredReviewFindingStates(goal, triggeringTask),
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
        IReadOnlyList<StructuredFindingSource> findings,
        IEnumerable<string> changedFileScopes)
    {
        var open = findings
            .Where(item =>
                item.Finding.State == ReviewFindingState.Open &&
                item.Finding.Severity == FindingSeverity.Blocking)
            .OrderBy(item => item.Finding.Category == FindingCategory.SpecCompliance ? 0 : 1)
            .ThenBy(item => item.Role)
            .ThenBy(item => item.Finding.StableId, StringComparer.Ordinal)
            .ToArray();
        var deferredAdvisories = findings
            .Where(item =>
                item.Finding.State == ReviewFindingState.Open &&
                item.Finding.Severity == FindingSeverity.Advisory)
            .OrderBy(item => item.Role)
            .ThenBy(item => item.Finding.StableId, StringComparer.Ordinal)
            .ToArray();
        var accepted = findings
            .Where(item => item.Finding.State == ReviewFindingState.Resolved)
            .ToArray();
        if (open.Length == 0)
        {
            throw new ReviewFindingConvergenceException(
                ReviewFindingConvergence.NeedsWorkWithoutOpenFindingsViolationCode,
                0,
                0,
                "Reviewer verdict=needs-work contained no structured open findings.");
        }

        var lines = new List<string>
        {
            $"auto-review-retry round {round} convergence brief: {triggeringTask.RequiredRole} task {triggeringTask.Id.Value[..8]} {triggerLabel}; retry upstream {targetRole} task.",
            AcceptedShapePreamble,
            "## RESIDUAL_OPEN_ACTION_ITEMS",
            $"open_count: {open.Length}"
        };

        foreach (var item in open)
        {
            var finding = item.Finding;
            lines.Add($"- stable_id: {finding.StableId}");
            lines.Add($"  source_role: {item.Role}");
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
        foreach (var item in accepted)
        {
            var finding = item.Finding;
            lines.Add($"- stable_id: {finding.StableId}");
            lines.Add($"  source_role: {item.Role}");
            lines.Add($"  location: {finding.Location}");
        }

        lines.Add("## DEFERRED_NON_BLOCKING_ADVISORIES");
        lines.Add("These findings remain visible for Reviewer/follow-up context but are not required repair scope. Do not expand this retry to address them.");
        lines.Add($"advisory_count: {deferredAdvisories.Length}");
        foreach (var item in deferredAdvisories)
        {
            lines.Add($"- stable_id: {item.Finding.StableId}");
            lines.Add($"  source_role: {item.Role}");
            lines.Add($"  location: {item.Finding.Location}");
        }

        lines.Add(BuildFocusedTestReceiptMandate(
            open.Select(item => item.Finding.Description).ToArray(),
            changedFileScopes));
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

    internal static string BuildContractRepairBrief(
        Goal goal,
        TaskSpec reviewerTask,
        ReviewFindingContractViolation violation,
        int attempt,
        int maxAttempts,
        string outputArtifact)
    {
        ArgumentNullException.ThrowIfNull(goal);
        ArgumentNullException.ThrowIfNull(reviewerTask);
        ArgumentNullException.ThrowIfNull(violation);

        var open = ReadStructuredReviewFindingState(
                goal,
                reviewerTask.RequiredRole,
                reviewerTask.LastVerification!.CompletedAt)
            .Where(finding => finding.State == ReviewFindingState.Open)
            .OrderBy(finding => finding.StableId, StringComparer.Ordinal)
            .ToArray();
        var claimedResolutions =
            WorkerResultBlockers.TryFindReviewFindingRound(
                reviewerTask.LastVerification,
                out var rejectedRound,
                out _)
                ? rejectedRound.Findings
                    .Where(finding => finding.State == ReviewFindingState.Resolved)
                    .Select(finding => finding.StableId)
                    .OrderBy(stableId => stableId, StringComparer.Ordinal)
                    .ToArray()
                : [];
        var lines = new List<string>
        {
            $"review-finding contract-repair: attempt {attempt}/{maxAttempts}; avoided_developer_reopen=1; {reviewerTask.RequiredRole} task {reviewerTask.Id.Value[..8]}",
            "produced a substantively valid result whose structured findings round was rejected by the review-finding identity contract. Re-submit the SAME conclusion; do not repeat the work and do not change your verdict.",
            $"violation_code: {violation.Code}",
            $"violation_prior_stable_id: {violation.PriorStableId ?? "none"}",
            $"violation_submitted_stable_id: {violation.SubmittedStableId ?? "none"}",
            $"violation_prior_location: {violation.PriorLocation?.ToString() ?? "none"}",
            $"violation_submitted_location: {violation.SubmittedLocation?.ToString() ?? "none"}",
            $"violation_detail: {violation.Message}",
            "## CANONICAL_OPEN_ACTIVE_RECHECK (authoritative; reuse stable_id and location VERBATIM)",
            $"open_count: {open.Length}"
        };
        foreach (var finding in open)
        {
            lines.Add(
                $"- stable_id: {finding.StableId} | severity={finding.Severity.ToString().ToLowerInvariant()} | {finding.Location}");
        }

        lines.Add("## PREVIOUSLY_CLAIMED_RESOLUTIONS (not applied; re-assert if still true)");
        if (claimedResolutions.Length == 0)
        {
            lines.Add("- none");
        }
        else
        {
            lines.AddRange(claimedResolutions.Select(stableId => $"- stable_id: {stableId}"));
        }

        lines.Add(
            open.Length == 0
                ? "Rules: the canonical ledger is empty; report every finding as newly opened. Open a new stable_id only for a defect at an anchor not listed above. Keep verdict and blockers unchanged unless your conclusion actually changed."
                : "Rules: reuse every carried stable_id and location exactly as printed. Open a new stable_id only for a defect at an anchor not listed above. Keep verdict and blockers unchanged unless your conclusion actually changed.");
        lines.Add($"Full {reviewerTask.RequiredRole} output: {outputArtifact}");
        return string.Join(Environment.NewLine, lines);
    }

    internal static IReadOnlyList<ReviewFinding> ReadStructuredReviewFindingState(
        Goal goal,
        TaskSpec triggeringTask)
        => ReadStructuredReviewFindingState(
            goal,
            triggeringTask.RequiredRole,
            triggeringTask.LastVerification!.CompletedAt);

    private static IReadOnlyList<StructuredFindingSource> ReadStructuredReviewFindingStates(
        Goal goal,
        TaskSpec triggeringTask)
    {
        var completedAt = triggeringTask.LastVerification!.CompletedAt;
        return new[] { AgentRole.Reviewer, AgentRole.Tester }
            .SelectMany(role => ReadStructuredReviewFindingState(goal, role, completedAt)
                .Select(finding => new StructuredFindingSource(role, finding)))
            .ToArray();
    }

    private static IReadOnlyList<ReviewFinding> ReadStructuredReviewFindingState(
        Goal goal,
        AgentRole role,
        DateTimeOffset completedAt)
    {
        var state = goal.Tasks
            .Where(task => task.RequiredRole == role)
            .SelectMany(task => task.VerificationHistory)
            .Where(verification =>
                verification.CompletedAt <= completedAt &&
                verification.MergedReviewFindings is not null)
            .OrderByDescending(verification => verification.CompletedAt)
            .Select(verification => verification.MergedReviewFindings!)
            .FirstOrDefault() ?? [];

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
