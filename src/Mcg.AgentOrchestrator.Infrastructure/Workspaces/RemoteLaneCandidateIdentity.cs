namespace Mcg.AgentOrchestrator.Infrastructure;

internal interface IRemoteLaneCandidateIdentity
{
    string AttemptId { get; }
    string GoalId { get; }
    string VerifyingCommitSha { get; }
    string CandidateTreeSha { get; }
    string MainSha { get; }
    string ManifestIdentity { get; }
}

internal sealed record RemoteLaneCandidateIdentity(
    string AttemptId,
    string GoalId,
    string VerifyingCommitSha,
    string CandidateTreeSha,
    string MainSha,
    string ManifestIdentity) : IRemoteLaneCandidateIdentity;
