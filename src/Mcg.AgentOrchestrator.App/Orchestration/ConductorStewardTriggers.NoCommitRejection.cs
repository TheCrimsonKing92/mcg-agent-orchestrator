using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorStewardTriggerDetector
{
    private static bool IsNoCommitRejection(TaskVerificationRecord verification)
    {
        var stderr = verification.AuthoritativeStandardError ?? verification.StandardError;
        if (DispatchRejectionDiagnosticMarker.TryParse(stderr, out _, out var reason,
                out var postDispatchCommits, out _))
        {
            return postDispatchCommits == 0 &&
                   reason is (DispatchRejectionDiagnosticMarker.NoChangeEvidence or
                       DispatchRejectionDiagnosticMarker.VerificationPatternUnmatched);
        }

        return string.Equals(verification.OrchestratorFailureReason,
            DispatchRejectionDiagnosticMarker.NoChangeEvidence, StringComparison.Ordinal);
    }
}
