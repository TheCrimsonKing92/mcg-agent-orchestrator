namespace Mcg.AgentOrchestrator.Core.Conductor;

public sealed record UnchangedCandidateReinstatement(
    AgentRole Role,
    TaskId TaskId,
    TaskId PriorVerdictTaskId,
    DateTimeOffset PriorVerdictAt,
    CandidateIdentity CandidateIdentity,
    TaskVerificationRecord PriorVerification)
{
    public string Render() =>
        $"REINSTATED_UNCHANGED_CANDIDATE role={Role} task={TaskId.Value} " +
        $"verdictTask={PriorVerdictTaskId.Value} verdictAt={PriorVerdictAt:O} " +
        $"identity={CandidateIdentity.Canonical}";
}
