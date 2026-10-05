namespace Mcg.AgentOrchestrator.Core;

public enum RoundGoalOutcome { Landed, Lost, Pending }
public enum RoundValueClass { Productive, ExpectedOverhead, Wasted }

public sealed record RoundValueRecord(
    WorkerRoundRecord Round, RoundGoalOutcome Outcome, RoundValueClass ValueClass, string? WasteCause);

/// <summary>Values dispatch rounds using terminal goal outcome and positive rework evidence.</summary>
public static class RoundValueClassifier
{
    private sealed record FamilyRule(RoundValueClass Value, string? Cause = null,
        RoundValueClass? ReviewerValue = null);

    private static readonly IReadOnlyDictionary<ReworkCauseFamily, FamilyRule> FamilyTable =
        new Dictionary<ReworkCauseFamily, FamilyRule>
        {
            [ReworkCauseFamily.FirstPass] = new(RoundValueClass.Productive,
                ReviewerValue: RoundValueClass.ExpectedOverhead),
            [ReworkCauseFamily.ReviewFinding] = new(RoundValueClass.Productive),
            [ReworkCauseFamily.CandidateRed] = new(RoundValueClass.Productive),
            [ReworkCauseFamily.GateRed] = new(RoundValueClass.Productive),
            [ReworkCauseFamily.Clarification] = new(RoundValueClass.Productive),
            [ReworkCauseFamily.EvidenceRerun] = new(RoundValueClass.ExpectedOverhead),
            [ReworkCauseFamily.DownstreamRerun] = new(RoundValueClass.ExpectedOverhead),
            [ReworkCauseFamily.Routing] = new(RoundValueClass.ExpectedOverhead),
            [ReworkCauseFamily.FlakeOrApparatus] = new(RoundValueClass.Wasted, "flake-or-apparatus"),
            [ReworkCauseFamily.Environment] = new(RoundValueClass.Wasted, "provider-or-environment"),
            [ReworkCauseFamily.ReviewContractRepair] = new(RoundValueClass.Wasted, "review-contract-repair"),
            [ReworkCauseFamily.OperatorRetry] = new(RoundValueClass.Wasted, "manual-route"),
            [ReworkCauseFamily.StewardRoute] = new(RoundValueClass.Wasted, "manual-route"),
            [ReworkCauseFamily.Unclassified] = new(RoundValueClass.Wasted, "unclassified")
        };

    public static IReadOnlyList<RoundValueRecord> Classify(IEnumerable<Goal> goals) => Classify(goals, []);

    public static IReadOnlyList<RoundValueRecord> Classify(IEnumerable<Goal> goals,
        IReadOnlyCollection<AppliedRetryIntent> intents)
    {
        ArgumentNullException.ThrowIfNull(goals);
        ArgumentNullException.ThrowIfNull(intents);
        var result = new List<RoundValueRecord>();
        foreach (var goal in goals.OrderBy(g => g.Id.Value, StringComparer.Ordinal))
        {
            var outcome = goal.Status switch
            {
                GoalStatus.Completed => RoundGoalOutcome.Landed,
                GoalStatus.Cancelled or GoalStatus.Superseded or GoalStatus.Failed => RoundGoalOutcome.Lost,
                _ => RoundGoalOutcome.Pending
            };
            var rounds = WorkerRoundLedger.FromGoal(goal, intents);
            foreach (var task in goal.Tasks)
            {
                var dispatches = task.DispatchHistory.OrderBy(d => d.DispatchedAt)
                    .DistinctBy(d => d.DispatchedAt).ToArray();
                foreach (var round in rounds.Where(r => r.TaskId == task.Id.Value))
                {
                    var baseCommit = dispatches[round.RoundIndex - 1].BaseCommit;
                    var unchangedReview = round.Role == AgentRole.Reviewer && round.RoundIndex > 1 &&
                        !string.IsNullOrEmpty(baseCommit) && string.Equals(baseCommit,
                            dispatches[round.RoundIndex - 2].BaseCommit, StringComparison.Ordinal);
                    string? cause = outcome == RoundGoalOutcome.Lost ? "abandoned-goal"
                        : round.StopCause == WorkerRoundStopCause.Unknown ? "orphaned-dispatch"
                        : unchangedReview ? "unchanged-commit-review" : null;
                    if (cause is not null)
                        result.Add(new(round, outcome, RoundValueClass.Wasted, cause));
                    else
                    {
                        var rule = FamilyTable[round.ReworkCause];
                        var value = round.Role == AgentRole.Reviewer ? rule.ReviewerValue ?? rule.Value : rule.Value;
                        result.Add(new(round, outcome, value, rule.Cause));
                    }
                }
            }
        }
        return result;
    }
}
