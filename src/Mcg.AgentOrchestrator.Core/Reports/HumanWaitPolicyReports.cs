namespace Mcg.AgentOrchestrator.Core;

public enum HumanWaitPolicyResolution
{
    Defaulted,
    Dismissed
}

public sealed record HumanWaitPolicyResult(
    HumanInputRequestId RequestId,
    GoalId GoalId,
    TaskId? TaskId,
    HumanWaitKind Kind,
    HumanWaitPolicyResolution Resolution);
