using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class VerifiedAcceptanceEscalationDecision
{
    internal static bool IsTransientVerificationFailure(ConductorAdvanceResult result) =>
        result.Outcome is ConductorAdvanceOutcome.Escalated { State: GoalLifecycleState.Verified } escalated
        && EffectiveKind(escalated) == ConductorEscalationKind.AcceptanceVerificationFailed;

    internal static ConductorTickOutcomePayload BuildTickOutcomePayload(ConductorAdvanceOutcome outcome) => outcome switch
    {
        ConductorAdvanceOutcome.Executed executed => new("Executed", executed.FromState.ToString(), null),
        ConductorAdvanceOutcome.Held held => new("Held", held.State.ToString(), null, held.Decision),
        ConductorAdvanceOutcome.Escalated escalated => new("Escalated", escalated.State.ToString(), EffectiveKind(escalated).ToString(), escalated.Decision),
        ConductorAdvanceOutcome.Done done => new("Done", done.State.ToString(), null, done.Decision),
        _ => throw new InvalidOperationException($"Unknown conductor outcome type {outcome.GetType().FullName}.")
    };

    internal static bool HasPersistedVerifiedAcceptanceEscalation(Goal goal)
    {
        foreach (var evt in goal.Timeline.Reverse())
        {
            if (ClearsPersistedVerifiedAcceptanceEscalation(evt))
            {
                return false;
            }

            if (evt.Kind == ProgressKind.GoalPolicyDecision && IsPersistedVerifiedAcceptanceEscalation(evt))
            {
                return true;
            }
        }

        return false;
    }

    internal static bool IsPersistedVerifiedAcceptanceEscalation(ProgressEvent evt)
    {
        if (evt.TickOutcome is { } payload)
        {
            return payload.OutcomeKind == "Escalated"
                && payload.LifecycleState == nameof(GoalLifecycleState.Verified)
                && (payload.EscalationKind == nameof(ConductorEscalationKind.AcceptanceVerificationFailed)
                    || payload.EscalationKind == nameof(ConductorEscalationKind.BackgroundAcceptanceFailed));
        }

        return evt.Message.Contains("escalated at Verified", StringComparison.OrdinalIgnoreCase)
            && IsLegacyVerifiedAcceptanceEscalationMessage(evt.Message);
    }

    internal static bool HasUnresolvedPersistedVerifiedAcceptanceEscalation(Goal goal, ConductorDriver driver)
    {
        if (!HasPersistedVerifiedAcceptanceEscalation(goal))
        {
            return false;
        }

        try
        {
            return GoalLifecycle.ResolveState(goal, driver.GetFacts(goal)) != GoalLifecycleState.CleanedUp;
        }
        catch
        {
            return true;
        }
    }

    internal static bool? TryHasUnresolvedPersistedVerifiedAcceptanceEscalation(Goal goal, ConductorDriver driver)
    {
        try
        {
            return HasUnresolvedPersistedVerifiedAcceptanceEscalation(goal, driver);
        }
        catch
        {
            return null;
        }
    }

    private static ConductorEscalationKind EffectiveKind(ConductorAdvanceOutcome.Escalated escalated)
    {
        if (escalated.Kind is { } kind)
        {
            return kind;
        }

        if (escalated.State != GoalLifecycleState.Verified)
        {
            return ConductorEscalationKind.Unspecified;
        }

        if (escalated.Reason.Contains("Acceptance verification failed", StringComparison.OrdinalIgnoreCase))
        {
            return ConductorEscalationKind.AcceptanceVerificationFailed;
        }

        return escalated.Reason.Contains("background acceptance", StringComparison.OrdinalIgnoreCase)
            ? ConductorEscalationKind.BackgroundAcceptanceFailed
            : ConductorEscalationKind.Unspecified;
    }

    private static bool IsLegacyVerifiedAcceptanceEscalationMessage(string message) =>
        message.Contains("Acceptance verification failed", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("background acceptance", StringComparison.OrdinalIgnoreCase);

    internal static bool ClearsPersistedVerifiedAcceptanceEscalation(ProgressEvent evt) =>
        evt.Kind is ProgressKind.TaskRetried or ProgressKind.GoalCancelled or ProgressKind.GoalSuperseded
        || (evt.Kind is ProgressKind.HumanInputRequested or ProgressKind.GoalPolicyDecision
            && evt.Message.StartsWith("Goal parked:", StringComparison.OrdinalIgnoreCase))
        || (evt.Kind == ProgressKind.GoalPolicyDecision
            && evt.Message.StartsWith(ConductorBatchLoop.SetAsideSelfClearDecisionPrefix, StringComparison.Ordinal))
        || evt.Kind == ProgressKind.HumanInputReceived;
}
