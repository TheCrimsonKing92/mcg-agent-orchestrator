namespace Mcg.AgentOrchestrator.Core.Conductor;

public sealed record UnchangedCandidateHoldReason(
    AgentRole Role,
    TaskId PriorVerdictTaskId,
    DateTimeOffset PriorVerdictAt,
    string PriorVerdict,
    CandidateIdentity CandidateIdentity)
{
    public string Render() =>
        $"UNCHANGED_CANDIDATE role={Role} verdict={PriorVerdict} " +
        $"verdictTask={PriorVerdictTaskId.Value} verdictAt={PriorVerdictAt:O} " +
        $"identity={CandidateIdentity.Canonical}. Change the candidate, submit operator retry guidance " +
        "for this role, or use human adjudication to re-open it.";
}
