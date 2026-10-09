using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

public static class GoalAcceptanceStatusProjector
{
    private const int ListedItemLimit = 5;

    public static GoalAcceptanceSummary Build(
        AgentOrchestratorKernel kernel,
        Goal goal,
        string? executionDirectory, string integrationBranch)
    {
        var summary = kernel.BuildGoalAcceptanceSummary(goal.Id);
        if (summary.Status is not (GoalStatus.Verified or GoalStatus.Completed))
        {
            return summary;
        }

        if (string.IsNullOrWhiteSpace(executionDirectory) ||
            TryResolveCurrentCandidate(executionDirectory, goal.Id) is not { } candidate)
        {
            return summary.IsAccepted
                ? summary with { AcceptanceHoldDescription = null }
                : summary with
                {
                    AcceptanceHoldDescription = BuildHoldDescription(
                        summary,
                        candidateSha: null,
                        hasCurrentPassedOutcome: false,
                        summary.Blockers,
                        GetOrderedPendingWaits(kernel, goal.Id))
                };
        }

        var journal = GoalOperationJournal.Read(executionDirectory, goal.Id);
        var currentOutcomes = GoalOperationJournal.AcceptanceOutcomesForCandidate(
            journal,
            candidate.BranchHeadSha,
            candidate.MainHeadSha);
        if (currentOutcomes.Count == 0 &&
            goal.Status == GoalStatus.Completed &&
            GoalGitFactIndex.Build(executionDirectory, integrationBranch).BuildGoalBranchFacts(goal).BranchAlreadyLanded)
        {
            currentOutcomes = journal.Entries
                .Select((entry, index) => (Entry: entry, Index: index))
                .Where(item =>
                    !string.IsNullOrWhiteSpace(item.Entry.AcceptanceOutcome) &&
                    string.Equals(item.Entry.BranchHeadSha?.Trim(), candidate.BranchHeadSha, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(item => item.Entry.At)
                .ThenByDescending(item => item.Index)
                .Select(item => item.Entry)
                .ToArray();
        }

        var blockers = summary.Blockers
            .Where(blocker => blocker.Kind != GoalAcceptanceBlockerKind.AcceptanceFailed)
            .ToList();

        var outcomes = currentOutcomes
            .Select(entry => ToOutcome(entry, isCurrentCandidate: true))
            .ToList();
        var hasPassingGateReceipt = currentOutcomes.Any(entry =>
            entry.AcceptanceOutcome?.Equals("gate-passed", StringComparison.OrdinalIgnoreCase) == true);
        var hasCurrentPassedOutcome = false;
        if (outcomes.Count > 0)
        {
            var currentOutcome = outcomes[0];
            hasCurrentPassedOutcome =
                currentOutcome.Outcome.Equals("passed", StringComparison.OrdinalIgnoreCase) ||
                currentOutcome.Outcome.Equals("gate-passed", StringComparison.OrdinalIgnoreCase);
            if (!hasCurrentPassedOutcome)
            {
                var aborted = currentOutcome.Outcome.StartsWith("aborted:", StringComparison.OrdinalIgnoreCase);
                blockers.Add(new GoalAcceptanceBlocker(
                    aborted ? GoalAcceptanceBlockerKind.AcceptanceAborted : GoalAcceptanceBlockerKind.AcceptanceFailed,
                    null,
                    null,
                    currentOutcome.Message,
                    aborted
                        ? hasPassingGateReceipt
                            ? $"Quiesce conductor mutations for goal {goal.Id.Value[..8]} before another landing attempt; the passing gate receipt remains recorded."
                            : $"Quiesce conductor mutations for goal {goal.Id.Value[..8]} before another landing attempt; no passing gate receipt was recorded for this candidate."
                        : $"Rerun acceptance for goal {goal.Id.Value[..8]} after resolving the current candidate outcome."));
            }
        }
        else
        {
            outcomes.Add(UnverifiedOutcome());
        }

        var isAccepted = summary.OpenVerificationCount == 0 &&
            summary.PendingHumanInputCount == 0 &&
            blockers.Count == 0 &&
            hasCurrentPassedOutcome;
        return summary with
        {
            IsAccepted = isAccepted,
            Blockers = blockers,
            Outcomes = outcomes,
            AcceptanceHoldDescription = isAccepted
                ? null
                : BuildHoldDescription(
                    summary,
                    candidate.BranchHeadSha,
                    hasCurrentPassedOutcome,
                    blockers,
                    GetOrderedPendingWaits(kernel, goal.Id))
        };
    }

    internal static string? BuildPendingHumanWaitAttentionCommand(
        AgentOrchestratorKernel kernel,
        GoalId goalId,
        string goalPrefix)
    {
        var waits = GetOrderedPendingWaits(kernel, goalId);
        return waits.Count == 0
            ? null
            : $"attention show {goalPrefix}; pending human waits {FormatWaitReferences(waits)}";
    }

    internal static bool HasCurrentBlockingAcceptanceState(
        Goal goal,
        string executionDirectory,
        GoalOperationJournalSummary? journal = null)
    {
        journal ??= GoalOperationJournal.Read(executionDirectory, goal.Id);
        if (TryResolveCurrentCandidate(executionDirectory, goal.Id) is not { } candidate)
        {
            return goal.LatestAcceptanceFailure is not null ||
                journal.LatestByOperation.Any(entry =>
                    entry.Status == GoalOperationStatus.Failed &&
                    entry.Operation.Contains("acceptance", StringComparison.OrdinalIgnoreCase));
        }

        var current = GoalOperationJournal.NewestAcceptanceOutcomeForCandidate(
            journal,
            candidate.BranchHeadSha,
            candidate.MainHeadSha);
        return current is not null
            ? IsBlockingOutcome(current.AcceptanceOutcome)
            : false;
    }

    private static bool IsBlockingOutcome(string? outcome) =>
        outcome is not null &&
        !outcome.Equals("passed", StringComparison.OrdinalIgnoreCase);

    private static GoalAcceptanceOutcome UnverifiedOutcome() =>
        new(
            "unverified (needs a gate run)",
            true,
            DateTimeOffset.MinValue,
            "unverified (needs a gate run)");

    private static GoalAcceptanceOutcome ToOutcome(GoalOperationJournalEntry entry, bool isCurrentCandidate)
    {
        var label = isCurrentCandidate ? "current candidate" : "historical candidate";
        var candidate = FormatCandidate(entry.BranchHeadSha, entry.MainHeadSha);
        var detail = string.IsNullOrWhiteSpace(entry.Detail) ? entry.Operation : entry.Detail.Trim();
        return new GoalAcceptanceOutcome(
            entry.AcceptanceOutcome ?? "unknown",
            isCurrentCandidate,
            entry.At,
            $"Acceptance {entry.AcceptanceOutcome ?? "unknown"} for {label} {candidate} at {entry.At:u}: {detail}");
    }

    private static string BuildHoldDescription(
        GoalAcceptanceSummary summary,
        string? candidateSha,
        bool hasCurrentPassedOutcome,
        IReadOnlyList<GoalAcceptanceBlocker> blockers,
        IReadOnlyList<HumanInputRequest> pendingWaits)
    {
        var terms = new List<string>
        {
            string.IsNullOrWhiteSpace(candidateSha)
                ? "no current candidate"
                : hasCurrentPassedOutcome
                    ? $"acceptance passed at {FormatShortSha8(candidateSha)}"
                    : $"no passed acceptance outcome for candidate {FormatShortSha8(candidateSha)}"
        };

        if (summary.OpenVerificationCount > 0)
        {
            terms.Add($"landing held by {summary.OpenVerificationCount} open verification(s)");
        }

        if (pendingWaits.Count > 0)
        {
            terms.Add($"{pendingWaits.Count} pending human wait(s) ({FormatWaitReferences(pendingWaits)})");
        }

        var distinctBlockers = blockers
            .Where(blocker => blocker.Kind is not (
                GoalAcceptanceBlockerKind.PendingHumanInput or
                GoalAcceptanceBlockerKind.VerificationNotReady or
                GoalAcceptanceBlockerKind.VerificationMissing or
                GoalAcceptanceBlockerKind.VerificationFailed))
            .OrderBy(blocker => blocker.Kind.ToString(), StringComparer.Ordinal)
            .ToList();
        if (distinctBlockers.Count > 0)
        {
            terms.Add($"{distinctBlockers.Count} blocker(s) ({FormatBlockerKinds(distinctBlockers)})");
        }

        return string.Join("; ", terms);
    }

    private static IReadOnlyList<HumanInputRequest> GetOrderedPendingWaits(
        AgentOrchestratorKernel kernel,
        GoalId goalId) =>
        kernel.GetPendingHumanInput(goalId)
            .OrderBy(request => request.RequestedAt)
            .ThenBy(request => request.Id.Value, StringComparer.Ordinal)
            .ToArray();

    private static string FormatWaitReferences(IReadOnlyList<HumanInputRequest> waits)
    {
        var selected = waits.Take(ListedItemLimit).ToArray();
        var groups = selected
            .GroupBy(wait => wait.Kind)
            .OrderBy(group => group.Key.ToString(), StringComparer.Ordinal)
            .Select(group =>
                $"{group.Key} {string.Join(", ", group.Select(wait => FormatShortSha8(wait.Id.Value)))}");
        var suffix = waits.Count > selected.Length
            ? $" and {waits.Count - selected.Length} more"
            : string.Empty;
        return string.Join("; ", groups) + suffix;
    }

    private static string FormatBlockerKinds(IReadOnlyList<GoalAcceptanceBlocker> blockers)
    {
        var selected = blockers.Take(ListedItemLimit).Select(blocker => blocker.Kind.ToString()).ToArray();
        var suffix = blockers.Count > selected.Length
            ? $", +{blockers.Count - selected.Length} more"
            : string.Empty;
        return string.Join(", ", selected) + suffix;
    }

    private static AcceptanceCandidate? TryResolveCurrentCandidate(string executionDirectory, GoalId goalId)
    {
        var worktreePath = GoalWorktrees.TryResolve(executionDirectory, goalId);
        if (string.IsNullOrWhiteSpace(worktreePath))
        {
            return null;
        }

        var branchHead = TryResolveHead(worktreePath);
        var mainHead = TryResolveHead(executionDirectory);
        return string.IsNullOrWhiteSpace(branchHead) || string.IsNullOrWhiteSpace(mainHead)
            ? null
            : new AcceptanceCandidate(branchHead, mainHead);
    }

    private static string? TryResolveHead(string path)
    {
        try
        {
            var result = GitCli.Run(path, "rev-parse", "HEAD");
            return result.Succeeded ? result.Output.Trim() : null;
        }
        catch
        {
            return null;
        }
    }

    private static string FormatCandidate(string? branchHeadSha, string? mainHeadSha) =>
        $"branch={FormatShortSha(branchHeadSha)} main={FormatShortSha(mainHeadSha)}";

    private static string FormatShortSha(string? sha) =>
        string.IsNullOrWhiteSpace(sha)
            ? "unknown"
            : sha.Trim()[..Math.Min(12, sha.Trim().Length)];

    private static string FormatShortSha8(string sha) =>
        sha.Trim()[..Math.Min(8, sha.Trim().Length)].ToLowerInvariant();

    private sealed record AcceptanceCandidate(string BranchHeadSha, string MainHeadSha);
}
