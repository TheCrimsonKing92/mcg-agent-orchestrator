namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record FaultRecordedAcceptanceFailureDecision(bool IsRegate, string? BranchHeadSha, string Reason);
