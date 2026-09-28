namespace Mcg.AgentOrchestrator.Core;

public sealed record AcceptanceCohortAttributedMember(
    GoalId GoalId,
    int MemberOrdinal,
    string CandidateRevision,
    IReadOnlyList<string> ReproducedFailingTests);

public sealed record AcceptanceCohortUnrelatedFailure(
    GoalId GoalId,
    int MemberOrdinal,
    IReadOnlyList<string> FailingTests);

public sealed partial record AcceptanceCohortPartitionReceipt
{
    public IReadOnlyList<string> FailingTestIdentities { get; init; } = [];
}

public sealed partial record AcceptanceCohortReceipt
{
    public IReadOnlyList<AcceptanceCohortAttributedMember> AttributedMembers { get; init; } = [];
    public IReadOnlyList<AcceptanceCohortUnrelatedFailure> UnrelatedFailures { get; init; } = [];
}
