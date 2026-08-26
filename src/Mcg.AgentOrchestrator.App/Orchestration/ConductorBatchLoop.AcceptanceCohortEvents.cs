using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorBatchLoop
{
    private static (string Outcome, string Reason) DescribeAcceptanceCohortExit(
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
