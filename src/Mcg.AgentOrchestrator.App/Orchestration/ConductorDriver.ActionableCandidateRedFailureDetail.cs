using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorDriver
{
    internal static string AppendActionableCandidateRedFailureDetail(
        string message,
        string receiptId,
        IReadOnlyList<string> failingTestIdentities,
        IReadOnlyList<string>? testResultPaths,
        Func<string, AcceptanceTrxReadResult>? readTrx = null)
    {
        return ActionableCandidateRedFailureDetailRenderer.Append(
            message, receiptId, failingTestIdentities, testResultPaths,
            MaxCriterionRetryEvidenceLines, DescribeTrxReadFailure, readTrx);
    }
}
