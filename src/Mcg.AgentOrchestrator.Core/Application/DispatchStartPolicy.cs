namespace Mcg.AgentOrchestrator.Core;

public enum DispatchStartAction
{
    Proceed,
    Hold,
    Escalate
}

/// <summary>Observed dispatch-start inputs; empty values denote inputs not read on this path.</summary>
public sealed record DispatchStartFacts(string GoalId)
{
    public string UnreconciledTaskIdPrefix { get; init; } = string.Empty;
    public string LeaseRecoveryStatus { get; init; } = string.Empty;
    public string ConcurrentLeaseRefusal { get; init; } = string.Empty;
    public string IntegrationCanDispatch { get; init; } = string.Empty;
    public string IntegrationMessage { get; init; } = string.Empty;
    public string ReadOnlyIntegrationHoldReason { get; init; } = string.Empty;
    public string StartOutcomeCategory { get; init; } = string.Empty;
    public string StartOutcomeReason { get; init; } = string.Empty;
    public string ReadinessVerdict { get; init; } = string.Empty;
    public string ReadinessReason { get; init; } = string.Empty;
    public string CancelledPredecessorBlocker { get; init; } = string.Empty;
    public string AssignedTasksBlockedReason { get; init; } = string.Empty;

    public IReadOnlyList<PolicyDecisionFact> ToRecordedFacts() => Array.AsReadOnly<PolicyDecisionFact>(
    [
        new("goalId", GoalId),
        new("unreconciledTaskIdPrefix", UnreconciledTaskIdPrefix),
        new("leaseRecoveryStatus", LeaseRecoveryStatus),
        new("concurrentLeaseRefusal", ConcurrentLeaseRefusal),
        new("integrationCanDispatch", IntegrationCanDispatch),
        new("integrationMessage", IntegrationMessage),
        new("readOnlyIntegrationHoldReason", ReadOnlyIntegrationHoldReason),
        new("startOutcomeCategory", StartOutcomeCategory),
        new("startOutcomeReason", StartOutcomeReason),
        new("readinessVerdict", ReadinessVerdict),
        new("readinessReason", ReadinessReason),
        new("cancelledPredecessorBlocker", CancelledPredecessorBlocker),
        new("assignedTasksBlockedReason", AssignedTasksBlockedReason)
    ]);

    public static DispatchStartFacts FromRecordedFacts(IReadOnlyList<PolicyDecisionFact> facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        if (facts.Count != 13)
            throw new InvalidOperationException("Dispatch start replay requires exactly thirteen named facts.");

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var fact in facts)
        {
            if (fact.Value is null || !values.TryAdd(fact.Name, fact.Value))
                throw new InvalidOperationException($"Invalid or duplicate dispatch start fact '{fact.Name}'.");
        }

        string Read(string name) => values.TryGetValue(name, out var value)
            ? value
            : throw new InvalidOperationException($"Missing dispatch start fact '{name}'.");
        string ReadChoice(string name, params string[] choices)
        {
            var value = Read(name);
            return choices.Contains(value, StringComparer.Ordinal)
                ? value
                : throw new InvalidOperationException($"Invalid dispatch start fact '{name}': '{value}'.");
        }

        return new DispatchStartFacts(Read("goalId"))
        {
            UnreconciledTaskIdPrefix = Read("unreconciledTaskIdPrefix"),
            LeaseRecoveryStatus = Read("leaseRecoveryStatus"),
            ConcurrentLeaseRefusal = ReadChoice("concurrentLeaseRefusal", "", "refused"),
            IntegrationCanDispatch = ReadChoice("integrationCanDispatch", "", "allowed", "refused"),
            IntegrationMessage = Read("integrationMessage"),
            ReadOnlyIntegrationHoldReason = Read("readOnlyIntegrationHoldReason"),
            StartOutcomeCategory = ReadChoice("startOutcomeCategory", "", "Started", "EmptyBatch", "Deferred", "SpawnFailed", "RecoverableSandboxPrep"),
            StartOutcomeReason = Read("startOutcomeReason"),
            ReadinessVerdict = ReadChoice("readinessVerdict", "", "ready", "deferred", "blocked-with-candidates", "blocked-without-candidates", "other"),
            ReadinessReason = Read("readinessReason"),
            CancelledPredecessorBlocker = Read("cancelledPredecessorBlocker"),
            AssignedTasksBlockedReason = Read("assignedTasksBlockedReason")
        };
    }
}

public sealed record DispatchStartDecision(
    DispatchStartAction Action,
    int DiscriminatingRung,
    string DiscriminatingEvidence,
    string Reason,
    DispatchStartFacts Facts)
{
    public PolicyDecisionRecord ToRecord() => new(
        DispatchStartPolicy.StageName, Action.ToString(), DiscriminatingRung,
        DiscriminatingEvidence, Reason, Facts.ToRecordedFacts());
}

/// <summary>Selects the first dispatch-start refusal from observed facts without owning effects.</summary>
public static class DispatchStartPolicy
{
    public const string StageName = "dispatch-start";

    public static DispatchStartDecision Evaluate(DispatchStartFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        if (facts.UnreconciledTaskIdPrefix.Length > 0)
            return new(DispatchStartAction.Hold, 1, "exited-unapplied-process-record",
                $"Dispatch start refused for task {facts.UnreconciledTaskIdPrefix}: latest process record exited without an applied completion (exited-unapplied-process-record).", facts);
        if (facts.LeaseRecoveryStatus.Length > 0)
            return new(DispatchStartAction.Hold, 2, "evidence-lease-held",
                $"Goal evidence mutation is held for {facts.GoalId}; lease-recovery={facts.LeaseRecoveryStatus}.", facts);
        if (facts.ConcurrentLeaseRefusal == "refused")
            return new(DispatchStartAction.Hold, 3, "evidence-lease-concurrent-mutation",
                $"Goal evidence mutation is blocked by concurrent acceptance or replacement for {facts.GoalId}.", facts);
        if (facts.IntegrationCanDispatch == "refused")
            return new(DispatchStartAction.Escalate, 4, "pre-dispatch-integration-refused", facts.IntegrationMessage, facts);
        if (facts.ReadOnlyIntegrationHoldReason.Length > 0)
            return new(DispatchStartAction.Hold, 5, "read-only-integration-hold", facts.ReadOnlyIntegrationHoldReason, facts);
        if (facts.StartOutcomeCategory is "" or "Started")
            return new(DispatchStartAction.Proceed, 0, "proceed", "Dispatch start may proceed.", facts);
        if (facts.StartOutcomeCategory == "Deferred")
            throw new InvalidOperationException("Deferred dispatch start is owned by its existing hold policy.");
        if (facts.StartOutcomeCategory == "EmptyBatch")
        {
            if (facts.ReadinessVerdict == "deferred")
                return new(DispatchStartAction.Hold, 6, "provider-cooldown",
                    $"All assigned tasks deferred by provider cooldown; {facts.ReadinessReason}. Will retry next tick.", facts);
            if (facts.CancelledPredecessorBlocker.Length > 0)
                return new(DispatchStartAction.Escalate, 7, "cancelled-predecessor",
                    $"STRUCTURAL_TASK_BLOCKER: {facts.CancelledPredecessorBlocker}. Operator recovery is required; retrying cannot complete a cancelled predecessor.", facts);
            if (facts.ReadinessVerdict != "blocked-without-candidates")
                return new(DispatchStartAction.Hold, 8, "assigned-tasks-blocked", facts.AssignedTasksBlockedReason, facts);
        }

        return new(DispatchStartAction.Escalate, 9, "dispatch-start-failed", facts.StartOutcomeReason, facts);
    }
}
