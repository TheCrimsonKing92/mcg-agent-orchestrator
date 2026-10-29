using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal static class OwnerConsolePassedGateHold
{
    internal static bool LatestAttemptPassedFor(string? candidateSha, IEnumerable<GoalOperationJournalEntry>? attempts)
    {
        if (string.IsNullOrWhiteSpace(candidateSha) || attempts is null) return false;
        var latest = attempts.Select((entry, index) => (Entry: entry, Index: index))
            .Where(item => !string.IsNullOrWhiteSpace(item.Entry.AcceptanceOutcome) &&
                string.Equals(item.Entry.BranchHeadSha?.Trim(), candidateSha.Trim(), StringComparison.Ordinal))
            .OrderByDescending(item => item.Entry.At).ThenByDescending(item => item.Index)
            .Select(item => item.Entry).FirstOrDefault();
        return latest?.AcceptanceOutcome is { } outcome &&
            (outcome.Equals("passed", StringComparison.OrdinalIgnoreCase) ||
                outcome.Equals("gate-passed", StringComparison.OrdinalIgnoreCase));
    }

    internal static string? Board(Goal goal, DateTimeOffset now, string? candidateSha,
        IEnumerable<GoalOperationJournalEntry>? attempts)
    {
        if (goal.Status != GoalStatus.Verified || goal.LandingBlocker is not { } blocker ||
            !LatestAttemptPassedFor(candidateSha, attempts)) return null;
        var reason = OwnerHoldReason.FirstLine(blocker.Detail) ?? OwnerConsoleHoldPhrase.For(blocker.Kind);
        return $"gate passed, landing held: {reason}, {OwnerConsoleLandingBlockerText.Age(blocker.Since, now)}";
    }
}
