using System.Security.Cryptography;
using System.Text;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed record ExecutorScreeningCandidate(string GoalId, GoalStatus Status, string Commit, string Tree,
    string MainSha, DateTimeOffset CommittedAt, bool WorktreeClean, bool DevelopersComplete, string WorktreePath);

internal sealed record ExecutorScreeningLaneSelection(IReadOnlyList<AcceptanceTestLane> Selected,
    IReadOnlyList<ExecutorScreeningExcludedLane> Excluded);

internal static class ExecutorScreeningPlanner
{
    internal const string Project = "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj";

    internal static ExecutorScreeningLaneSelection SelectLanes(IReadOnlyList<AcceptanceTestLane> lanes,
        RemoteLaneExecutorConfiguration configuration)
    {
        configuration = configuration.ResolveLanes(lanes.Select(lane => "infrastructure tests: " + lane.Name));
        var selected = new List<AcceptanceTestLane>();
        var excluded = new List<ExecutorScreeningExcludedLane>();
        foreach (var lane in lanes)
        {
            var check = new GoalAcceptanceVerifier.AcceptanceManifestCheck
            {
                Name = "infrastructure tests: " + lane.Name, Type = "dotnet-test", Project = Project,
                Arguments = ["--filter", lane.Filter], ExclusiveResourceKeys = lane.ExclusiveResourceKeys,
                RequiresBuildSystemChange = lane.RequiresBuildSystemChange
            };
            string? reason = null;
            if (!configuration.Lanes.Contains(check.Name, StringComparer.Ordinal)) reason = "not-configured";
            else if (!GoalAcceptanceVerifier.TryGetInfrastructurePartitionId(check, out _, out _)) reason = "not-infrastructure";
            // IsEligible accepts lanes without exclusive keys before checking build-system changes.
            else reason = RemoteLaneResourceEligibility.ExclusionReason(lane.ExclusiveResourceKeys,
                lane.RequiresBuildSystemChange, configuration.MachineLocalResourceKeys);
            if (reason is null) selected.Add(lane);
            else excluded.Add(new(lane.Name, reason));
        }
        return new(selected, excluded);
    }

    internal static ExecutorScreeningCandidate? PickCandidate(IEnumerable<ExecutorScreeningCandidate> candidates,
        IReadOnlyList<ExecutorScreeningReceipt> receipts) => candidates.Where(candidate =>
            candidate.Status is GoalStatus.Active or GoalStatus.Verified && candidate.DevelopersComplete &&
            candidate.WorktreeClean && candidate.Commit != candidate.MainSha &&
            !receipts.Any(receipt => receipt.GoalId == candidate.GoalId && receipt.CandidateCommit == candidate.Commit))
        .OrderBy(candidate => candidate.CommittedAt).ThenBy(candidate => candidate.GoalId, StringComparer.Ordinal).FirstOrDefault();

    internal static RemoteLaneExecutorEntry? PickIdleExecutor(IReadOnlyList<RemoteLaneExecutorEntry> executors,
        Func<string, bool> isOccupied, string? onlyExecutorId = null) => executors.FirstOrDefault(entry =>
            entry.AdmissionEnabled && entry.Transport == "ssh" && (onlyExecutorId is null || entry.Id == onlyExecutorId) && !isOccupied(entry.Id));

    internal static string LaneName(string project, string filter) => "sc-" +
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(project + "\n" + filter)).AsSpan(0, 4)).ToLowerInvariant();
}
