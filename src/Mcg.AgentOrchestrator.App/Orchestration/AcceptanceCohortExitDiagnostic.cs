using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class AcceptanceCohortExitDiagnostic
{
    internal static (string Outcome, string Reason) Describe(
        ConductorAcceptanceCohortRunResult cohortRun)
    {
        var outcome = cohortRun.Receipt?.Outcome.ToString() ??
            (cohortRun.Detail.Contains("outcome=inflight", StringComparison.Ordinal)
                ? "inflight"
                : "no-receipt");
        if (cohortRun.Receipt?.Outcome != AcceptanceCohortGateOutcome.InfrastructureFailure)
        {
            return (outcome, string.Empty);
        }

        var reasonCode = AcceptanceCohortInfrastructureReasonCodes.IsSingleToken(
            cohortRun.Receipt.InfrastructureReasonCode)
            ? cohortRun.Receipt.InfrastructureReasonCode
            : AcceptanceCohortInfrastructureReasonCodes.LegacyUnknown;
        return (outcome, $" reason={reasonCode}");
    }
}
