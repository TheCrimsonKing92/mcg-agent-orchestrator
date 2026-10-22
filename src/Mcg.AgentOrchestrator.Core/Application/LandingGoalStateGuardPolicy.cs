namespace Mcg.AgentOrchestrator.Core;

public enum LandingGoalStateGuardAction { Proceed, Hold }

/// <summary>Current goal observations at a landing guard; null denotes an unobserved input.</summary>
public sealed record LandingGoalStateGuardFacts(
    GoalStatus? GoalStatus,
    GoalStatus? LifecycleStatus,
    bool? AcceptanceFailureDeferredForRetry,
    bool? AcceptancePassed)
{
    public IReadOnlyList<PolicyDecisionFact> ToRecordedFacts() => Array.AsReadOnly<PolicyDecisionFact>(
    [
        new("goalStatus", GoalStatus?.ToString() ?? ""),
        new("lifecycleStatus", LifecycleStatus?.ToString() ?? ""),
        new("acceptanceFailureDeferredForRetry", Encode(AcceptanceFailureDeferredForRetry)),
        new("acceptancePassed", Encode(AcceptancePassed))
    ]);

    public static LandingGoalStateGuardFacts FromRecordedFacts(IReadOnlyList<PolicyDecisionFact> facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        if (facts.Count != 4)
            throw new InvalidOperationException("Landing goal state guard replay requires exactly four named facts.");
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var fact in facts)
            if (fact is null || fact.Name is null || fact.Value is null || !values.TryAdd(fact.Name, fact.Value))
                throw new InvalidOperationException("Invalid or duplicate landing goal state guard fact.");
        string Read(string name) => values.TryGetValue(name, out var value) ? value
            : throw new InvalidOperationException($"Missing landing goal state guard fact '{name}'.");
        GoalStatus? ReadStatus(string name)
        {
            var value = Read(name);
            if (value == "") return null;
            if (!Enum.TryParse<GoalStatus>(value, out var status) || !Enum.IsDefined(status) || status.ToString() != value)
                throw new InvalidOperationException($"Invalid goal status landing goal state guard fact '{name}'.");
            return status;
        }
        bool? ReadBool(string name) => Read(name) switch
        {
            "true" => true, "false" => false, "" => null,
            _ => throw new InvalidOperationException($"Invalid boolean landing goal state guard fact '{name}'.")
        };
        return new(ReadStatus("goalStatus"), ReadStatus("lifecycleStatus"),
            ReadBool("acceptanceFailureDeferredForRetry"), ReadBool("acceptancePassed"));
    }

    private static string Encode(bool? value) => value switch { true => "true", false => "false", null => "" };
}

public sealed record LandingGoalStateGuardDecision(
    LandingGoalStateGuardAction Action, int DiscriminatingRung, string DiscriminatingEvidence,
    string Reason, LandingGoalStateGuardFacts Facts)
{
    public PolicyDecisionRecord ToRecord() => new(LandingGoalStateGuardPolicy.StageName, Action.ToString(),
        DiscriminatingRung, DiscriminatingEvidence, Reason, Facts.ToRecordedFacts());
}

/// <summary>Attributes landing holds from current goal state without changing lifecycle or gate evidence.</summary>
public static class LandingGoalStateGuardPolicy
{
    public const string StageName = "landing-goal-state-guard";

    public static LandingGoalStateGuardDecision Evaluate(LandingGoalStateGuardFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        if (facts.AcceptancePassed == false && facts.AcceptanceFailureDeferredForRetry == true &&
            facts.LifecycleStatus == GoalStatus.Active)
            return new(LandingGoalStateGuardAction.Hold, 1, "acceptance-retry-already-prepared",
                "Acceptance retry is already prepared; terminal replay admits no new work.", facts);
        if (facts.GoalStatus == GoalStatus.Parked && facts.AcceptancePassed == true)
            return new(LandingGoalStateGuardAction.Hold, 2, "parked-landing-held",
                "Goal is parked; landing remains held.", facts);
        return new(LandingGoalStateGuardAction.Proceed, 0, "goal-state-continue", "Landing goal state permits continuation.", facts);
    }
}
