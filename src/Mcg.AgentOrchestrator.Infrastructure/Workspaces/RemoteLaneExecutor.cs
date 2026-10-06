namespace Mcg.AgentOrchestrator.Infrastructure;

// A submission is executor work, never a local process or machine shard permit.
internal interface IRemoteLaneExecutor
{
    Task<RemoteLaneSubmission> SubmitAsync(RemoteLaneRequest request, CancellationToken cancellationToken);
}

internal interface IRemoteLaneHandle
{
    DateTimeOffset? NewestHeartbeat { get; }
    RemoteLaneResult? TryGetResult();
    void Abandon();
}

internal sealed record RemoteLaneSubmission(IRemoteLaneHandle? Handle, string? FailureReason = null);
internal sealed record RemoteLaneRequest(
    string ExecutorId, string AttemptId, string GoalId, string Lane, string Project, string Filter,
    string FilterHash, string VerifyingCommitSha, string CandidateTreeSha, string MainSha, string ManifestIdentity);
internal sealed record RemoteLaneResult(
    string ExecutorId, string Lane, string FilterHash, string VerifyingCommitSha, string ObservedTreeSha,
    string MainSha, string ManifestIdentity, int ExitCode, IReadOnlyList<string> TestResultPaths);

internal sealed class UnavailableRemoteLaneExecutor : IRemoteLaneExecutor
{
    internal static UnavailableRemoteLaneExecutor Instance { get; } = new();
    public Task<RemoteLaneSubmission> SubmitAsync(RemoteLaneRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(new RemoteLaneSubmission(null, "transport-unavailable"));
}
