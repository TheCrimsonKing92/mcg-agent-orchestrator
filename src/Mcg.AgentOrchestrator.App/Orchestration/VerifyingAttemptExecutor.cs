namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record VerifyingAttemptExecution(
    ConductorParallelAcceptanceAttemptDecision? Decision,
    string? ArtifactWriterBusyMessage,
    string? NoTickWaitOutcome);

internal static class VerifyingAttemptExecutor
{
    internal const string DeadlineElapsed = "deadline-elapsed";
    internal const string ReconciliationOwnershipChanged = "reconciliation-ownership-changed";
    internal const string OwnershipChanged = "ownership-changed";

    internal static VerifyingAttemptExecution Execute(
        bool isConductorTick,
        Func<AcceptanceStableSlotExhaustionPolicy, ConductorParallelAcceptanceAttemptDecision> evaluate,
        Func<ConductorParallelAcceptanceAttempt, ConductorParallelAcceptanceAttemptDecision> observe,
        Action<ConductorParallelAcceptanceAttempt> onStarted,
        Func<DateTimeOffset> utcNow,
        Action<TimeSpan> pollDelay,
        TimeSpan pollInterval,
        TimeSpan pollTimeout)
    {
        ConductorParallelAcceptanceAttemptDecision decision;
        try
        {
            decision = evaluate(isConductorTick
                ? AcceptanceStableSlotExhaustionPolicy.Fail
                : AcceptanceStableSlotExhaustionPolicy.DegradeToSerial);
            if (isConductorTick &&
                decision.Kind == ConductorParallelAcceptanceAttemptDecisionKind.Started &&
                decision.Attempt.Outcome != ConductorParallelAcceptanceAttemptOutcome.Running)
            {
                decision = evaluate(AcceptanceStableSlotExhaustionPolicy.Fail);
            }
        }
        catch (AcceptanceArtifactWriterLeaseBusyException ex)
        {
            return new(null, ex.Message, null);
        }

        if (!isConductorTick &&
            decision.Kind is ConductorParallelAcceptanceAttemptDecisionKind.Started or
                ConductorParallelAcceptanceAttemptDecisionKind.Running)
        {
            if (decision.Kind == ConductorParallelAcceptanceAttemptDecisionKind.Started)
            {
                onStarted(decision.Attempt);
            }
            var deadline = utcNow().Add(pollTimeout);
            while (decision.Kind is ConductorParallelAcceptanceAttemptDecisionKind.Started or
                   ConductorParallelAcceptanceAttemptDecisionKind.Running)
            {
                if (utcNow() >= deadline)
                {
                    return new(decision, null, DeadlineElapsed);
                }

                pollDelay(pollInterval);
                try
                {
                    decision = observe(decision.Attempt);
                }
                catch (InvalidDataException ex) when (
                    ex.Message.Contains(" is unreadable.", StringComparison.Ordinal))
                {
                    continue;
                }
                catch (InvalidDataException ex) when (
                    ex.Message.Contains("after reconciliation", StringComparison.Ordinal))
                {
                    return new(decision, null, ReconciliationOwnershipChanged);
                }
                catch (InvalidDataException)
                {
                    return new(decision, null, OwnershipChanged);
                }
            }
        }

        return new(decision, null, null);
    }
}
