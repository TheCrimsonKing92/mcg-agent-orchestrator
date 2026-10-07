namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed partial class AcceptancePartitionVerdictCache : IRemoteLaneCandidateIdentity
{
    string IRemoteLaneCandidateIdentity.AttemptId => AttemptId;
    string IRemoteLaneCandidateIdentity.GoalId => GoalId;
    string IRemoteLaneCandidateIdentity.VerifyingCommitSha => VerifyingCommitSha;
    string IRemoteLaneCandidateIdentity.CandidateTreeSha => CandidateTreeSha;
    string IRemoteLaneCandidateIdentity.MainSha => MainSha;
    string IRemoteLaneCandidateIdentity.ManifestIdentity => ManifestIdentity;
}
