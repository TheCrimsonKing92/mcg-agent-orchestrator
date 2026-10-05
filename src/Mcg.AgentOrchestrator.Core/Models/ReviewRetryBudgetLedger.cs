namespace Mcg.AgentOrchestrator.Core;

// A budget is derived from append order, never from timestamps or retry prose claiming a reset.
public sealed record ReviewRetryBudgetState(int Round, int LifetimeRound, string? ResetMarker);

public static class ReviewRetryBudgetLedger
{
    public static ReviewRetryBudgetState Evaluate(Goal goal)
    {
        ArgumentNullException.ThrowIfNull(goal);
        var reviewers = goal.Tasks.Where(task => task.RequiredRole == AgentRole.Reviewer).ToArray();
        var reviewerIds = reviewers.Select(task => task.Id.Value).ToHashSet(StringComparer.Ordinal);
        var operatorReset = -1;
        var latestReset = -1;
        string? marker = null;
        for (var index = 0; index < goal.Timeline.Count; index++)
        {
            if (goal.Timeline[index].OperatorIntentApplied is
                { ActorKind: OperatorActorKind.Human, Outcome: "applied", RetryRoundKind: RetryRoundKind.Standard, TaskId: { } taskId } payload &&
                reviewerIds.Contains(taskId))
            {
                operatorReset = latestReset = index;
                marker = $"review_budget_reset=operator-retry intent={payload.IntentId}";
            }
        }

        // Retained verification histories align with their timeline events from the end.
        // Validate the command suffix; an unpaired/pruned prefix cannot prove a shrink.
        var ledgers = new List<(int Index, IReadOnlyList<ReviewFinding> Findings)>();
        foreach (var reviewer in reviewers)
        {
            var historyIndex = reviewer.VerificationHistory.Count - 1;
            for (var index = goal.Timeline.Count - 1; index >= 0 && historyIndex >= 0; index--)
            {
                var evt = goal.Timeline[index];
                if (evt.TaskId != reviewer.Id || evt.Kind != ProgressKind.TaskVerificationRecorded) continue;
                var verification = reviewer.VerificationHistory[historyIndex];
                if (!evt.Message.EndsWith($": {verification.Command}", StringComparison.Ordinal)) break;
                if (verification.MergedReviewFindings is { } findings) ledgers.Add((index, findings));
                historyIndex--;
            }
        }

        HashSet<string>? previous = null;
        foreach (var ledger in ledgers.OrderBy(item => item.Index))
        {
            var current = ReviewFindings.GetOpenBlockingFindings(ledger.Findings, goal.EffectiveAcceptanceCriteriaCorrections)
                .Where(finding => finding.Category is not (FindingCategory.SpecDefect or FindingCategory.OperatorOwned))
                .Select(finding => finding.StableId).ToHashSet(StringComparer.Ordinal);
            var closed = previous?.Except(current, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray() ?? [];
            if (closed.Length > 0 && ledger.Index > latestReset)
            {
                latestReset = ledger.Index;
                marker = $"review_budget_reset=open-set-shrank closed={string.Join(",", closed)}";
            }
            previous = current;
        }

        var consecutive = 0;
        var lifetime = 0;
        for (var index = 0; index < goal.Timeline.Count; index++)
        {
            var evt = goal.Timeline[index];
            if (evt.Kind != ProgressKind.TaskRetried || !evt.Message.Contains("auto-review-retry", StringComparison.OrdinalIgnoreCase)) continue;
            if (index > operatorReset) lifetime++;
            if (index > latestReset) consecutive++;
        }
        return new ReviewRetryBudgetState(consecutive + 1, lifetime + 1, consecutive == 0 ? marker : null);
    }
}
