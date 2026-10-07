using System.Security.Cryptography;
using System.Text;

namespace Mcg.AgentOrchestrator.Core;

public static class FollowerGateIdentity
{
    public const string Version = "follower-v1";

    public static string Create(FollowerGateReceipt binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        using var stream = new MemoryStream();
        foreach (var field in new[]
        {
            binding.LeaderGoalId.Value, binding.LeaderCandidateRevision, binding.LeaderCandidateTree,
            binding.BaseMainRevision, binding.FollowerGoalId.Value, binding.FollowerBranchHead,
            binding.FollowerTestedTree, binding.FollowerPlanIdentity
        })
        {
            var bytes = Encoding.UTF8.GetBytes(field);
            stream.Write(BitConverter.GetBytes(bytes.Length));
            stream.Write(bytes);
        }
        return $"{Version}-{Convert.ToHexStringLower(SHA256.HashData(stream.ToArray()))}";
    }
}

public enum FollowerGateRunOutcome
{
    Passed,
    Failed,
    InfrastructureFailure,
    Invalidated
}

public sealed record FollowerGateRunReceipt(
    string ReceiptId,
    string IdentityValue,
    FollowerGateReceipt Binding,
    FollowerGateRunOutcome Outcome,
    DateTimeOffset CompletedAt,
    IReadOnlyList<string> FailedChecks,
    int? GateExitCode,
    IReadOnlyList<string> GateTestResultPaths,
    FollowerGateInvalidReason? InvalidReason);

public enum FollowerLeaderGateStatus
{
    Unknown,
    Pending,
    Landed,
    Failed,
    Cancelled,
    Stale
}

public sealed record FollowerGateDecision(FollowerGateDisposition Disposition, FollowerGateInvalidReason? Reason);

public static partial class FollowerGateBindingRule
{
    public static FollowerGateInvalidReason? StopReason(
        FollowerLeaderGateStatus leader, FollowerLiveBaseState liveBase, bool liveFirstParentIsBase)
    {
        if (!Enum.IsDefined(leader)) throw new ArgumentOutOfRangeException(nameof(leader));
        if (!Enum.IsDefined(liveBase)) throw new ArgumentOutOfRangeException(nameof(liveBase));
        if (leader is FollowerLeaderGateStatus.Failed or FollowerLeaderGateStatus.Cancelled or FollowerLeaderGateStatus.Stale)
            return FollowerGateInvalidReason.LeaderFailed;
        if (leader == FollowerLeaderGateStatus.Unknown || liveBase != FollowerLiveBaseState.Moved)
            return null;
        return liveFirstParentIsBase ? FollowerGateInvalidReason.LeaderTreeDiffers : FollowerGateInvalidReason.BaseMoved;
    }

    public static FollowerGateDecision Decide(FollowerGateRunReceipt run, FollowerGateLandingObservation observation)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(observation);
        if (!Enum.IsDefined(run.Outcome)) throw new ArgumentOutOfRangeException(nameof(run));
        if (run.Outcome == FollowerGateRunOutcome.Invalidated)
            return new(FollowerGateDisposition.Discard, run.InvalidReason);
        if (run.Outcome == FollowerGateRunOutcome.InfrastructureFailure)
            return new(FollowerGateDisposition.Discard, null);
        var verdict = Evaluate(run.Binding, observation);
        return new(Disposition(verdict, run.Outcome == FollowerGateRunOutcome.Passed), verdict.Reason);
    }
}
