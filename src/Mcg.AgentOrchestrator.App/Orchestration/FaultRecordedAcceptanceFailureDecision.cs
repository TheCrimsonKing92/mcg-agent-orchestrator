using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record FaultRecordedAcceptanceFailureDecision(
    bool IsRegate, string? BranchHeadSha, string Reason, WorkspaceProbeFaultKind FaultKind, bool AlreadyRegated)
{
    internal PolicyDecisionRecord ToRecord(string reason) => new(
        "fault-recorded-acceptance-failure", IsRegate ? "Hold" : "Escalate", IsRegate ? 1 : 2,
        IsRegate ? "workspace-probe-timeout-first-regate" : "fault-recorded-regate-unavailable", reason,
        [new("branchHeadSha", BranchHeadSha ?? string.Empty), new("workspaceProbeFaultKind", FaultKind.ToString()),
            new("alreadyRegated", AlreadyRegated.ToString()), new("recordedFault", Reason), new("regateCap", "1")]);
}
