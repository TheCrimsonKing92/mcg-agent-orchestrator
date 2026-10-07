using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record VerifiedGateStartExecution(
    AcceptanceVerificationSummary? Summary,
    string? DeferralKind,
    string? InfrastructureDeferredReasonCode,
    string? InfrastructureDeferredMessage,
    DotnetBuildLeaseAcquisition.SlotsBusy? SlotsBusy,
    BuildLockAttribution? BuildLockAttribution,
    string? CancellationCause);

internal static class VerifiedGateStartExecutor
{
    internal const string InfrastructureDeferred = "infrastructure-deferred";
    internal const string BuildSlotsBusy = "build-slots-busy";
    internal const string BuildLockBlocked = "build-lock-blocked";
    internal const string AttemptCancelled = "attempt-cancelled";

    internal static VerifiedGateStartExecution Execute(
        Func<AcceptanceVerificationSummary?> runPreflight,
        Func<AcceptanceVerificationSummary> runAcceptance)
    {
        try
        {
            return new(runPreflight() ?? runAcceptance(), null, null, null, null, null, null);
        }
        catch (AcceptanceInfrastructureDeferredException ex)
        {
            return new(null, InfrastructureDeferred, ex.ReasonCode, ex.Message, null, null, null);
        }
        catch (DotnetBuildSlotsBusyException ex)
        {
            return new(null, BuildSlotsBusy, null, null, ex.SlotsBusy, null, null);
        }
        catch (BuildLockBlockedException ex)
        {
            return new(null, BuildLockBlocked, null, null, null, ex.Attribution, null);
        }
        catch (AcceptanceAttemptCancelledException ex)
        {
            return new(null, AttemptCancelled, null, null, null, null, ex.Decision.Cause.ToString());
        }
    }
}
