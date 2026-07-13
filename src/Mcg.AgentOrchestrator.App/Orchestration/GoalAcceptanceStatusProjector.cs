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
        if (string.IsNullOrWhiteSpace(executionDirectory) ||
            TryResolveCurrentCandidate(executionDirectory, goal.Id) is not { } candidate)
        {
            return summary;
        }

        var journal = GoalOperationJournal.Read(executionDirectory, goal.Id);
        var current = GoalOperationJournal.NewestAcceptanceOutcomeForCandidate(
            journal,
            candidate.BranchHeadSha,
            candidate.MainHeadSha);
        var historical = GoalOperationJournal.SupersededAcceptanceOutcomes(
                journal,
                candidate.BranchHeadSha,
                candidate.MainHeadSha)
            .Select(entry => ToOutcome(entry, isCurrentCandidate: false))
            .ToList();

        var blockers = summary.Blockers
            .Where(blocker => blocker.Kind != GoalAcceptanceBlockerKind.AcceptanceFailed)
            .ToList();

        if (current is not null)
        {
            var currentOutcome = ToOutcome(current, isCurrentCandidate: true);
            historical.Add(currentOutcome);
            if (IsBlockingOutcome(current.AcceptanceOutcome))
            {
                blockers.Add(new GoalAcceptanceBlocker(
                    GoalAcceptanceBlockerKind.AcceptanceFailed,
                    null,
                    null,
                    currentOutcome.Message,
                    $"Rerun acceptance for goal {goal.Id.Value[..8]} after resolving the current candidate outcome."));
            }
        }
        else if (IsCurrentFailure(goal.LatestAcceptanceFailure, candidate))
        {
            var failure = goal.LatestAcceptanceFailure!;
            blockers.Add(new GoalAcceptanceBlocker(
                GoalAcceptanceBlockerKind.AcceptanceFailed,
                null,
                null,
                $"Latest acceptance failed for current candidate {FormatCandidate(candidate.BranchHeadSha, candidate.MainHeadSha)} at {failure.OccurredAt:u}: {string.Join(", ", failure.FailedChecks)}.",
                $"Rerun acceptance for goal {goal.Id.Value[..8]} after resolving the blocker."));
        }

        var outcomes = historical
            .OrderBy(outcome => outcome.OccurredAt)
            .ToArray();

        return summary with
        {
            IsAccepted = summary.OpenVerificationCount == 0 &&
                summary.PendingHumanInputCount == 0 &&
                blockers.Count == 0,
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
            : IsCurrentFailure(goal.LatestAcceptanceFailure, candidate);
    }

    private static bool IsCurrentFailure(AcceptanceFailureSummary? failure, AcceptanceCandidate candidate) =>
        failure is not null &&
        ShaEquals(failure.BranchHeadSha, candidate.BranchHeadSha) &&
        ShaEquals(failure.MainHeadSha, candidate.MainHeadSha);

    private static bool IsBlockingOutcome(string? outcome) =>
        outcome is not null &&
        !outcome.Equals("passed", StringComparison.OrdinalIgnoreCase);

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

    private static bool ShaEquals(string? left, string? right) =>
        string.Equals(NormalizeSha(left), NormalizeSha(right), StringComparison.OrdinalIgnoreCase);

    private static string? NormalizeSha(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string FormatCandidate(string? branchHeadSha, string? mainHeadSha) =>
        $"branch={FormatShortSha(branchHeadSha)} main={FormatShortSha(mainHeadSha)}";

    private static string FormatShortSha(string? sha) =>
        string.IsNullOrWhiteSpace(sha)
            ? "unknown"
            : sha.Trim()[..Math.Min(12, sha.Trim().Length)];

    private sealed record AcceptanceCandidate(string BranchHeadSha, string MainHeadSha);
}
