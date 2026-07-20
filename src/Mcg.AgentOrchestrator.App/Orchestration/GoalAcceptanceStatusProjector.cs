using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class GoalAcceptanceStatusProjector
{
    public static GoalAcceptanceSummary Build(
        AgentOrchestratorKernel kernel,
        Goal goal,
        string? executionDirectory)
    {
        var summary = kernel.BuildGoalAcceptanceSummary(goal.Id);
        if (summary.Status != GoalStatus.Verified)
        {
            return summary;
        }

        if (string.IsNullOrWhiteSpace(executionDirectory) ||
            TryResolveCurrentCandidate(executionDirectory, goal.Id) is not { } candidate)
        {
            return summary;
        }

        var journal = GoalOperationJournal.Read(executionDirectory, goal.Id);
        var currentOutcomes = GoalOperationJournal.AcceptanceOutcomesForCandidate(
            journal,
            candidate.BranchHeadSha,
            candidate.MainHeadSha);

        var blockers = summary.Blockers
            .Where(blocker => blocker.Kind != GoalAcceptanceBlockerKind.AcceptanceFailed)
            .ToList();

        var outcomes = currentOutcomes
            .Select(entry => ToOutcome(entry, isCurrentCandidate: true))
            .ToList();
        var hasCurrentPassedOutcome = false;
        if (outcomes.Count > 0)
        {
            var currentOutcome = outcomes[0];
            hasCurrentPassedOutcome = currentOutcome.Outcome.Equals("passed", StringComparison.OrdinalIgnoreCase);
            if (!hasCurrentPassedOutcome)
            {
                blockers.Add(new GoalAcceptanceBlocker(
                    GoalAcceptanceBlockerKind.AcceptanceFailed,
                    null,
                    null,
                    currentOutcome.Message,
                    $"Rerun acceptance for goal {goal.Id.Value[..8]} after resolving the current candidate outcome."));
            }
        }
        else
        {
            outcomes.Add(UnverifiedOutcome());
        }

        return summary with
        {
            IsAccepted = summary.OpenVerificationCount == 0 &&
                summary.PendingHumanInputCount == 0 &&
                blockers.Count == 0 &&
                hasCurrentPassedOutcome,
            Blockers = blockers,
            Outcomes = outcomes
        };
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

    private sealed record AcceptanceCandidate(string BranchHeadSha, string MainHeadSha);
}
