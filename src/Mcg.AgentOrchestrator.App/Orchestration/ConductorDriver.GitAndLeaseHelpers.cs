using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.App.SubscriptionPlanning;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorDriver
{
    private static string? TryResolveGitHead(string path)
    {
        var result = GitCli.Run(path, "rev-parse", "HEAD");
        return result.Succeeded ? result.Output.Trim() : null;
    }

    private static (string BranchHead, string MainHead, string Fingerprint) ReadLandingRecheckEvidence(
        string worktreePath, string integrationBranch)
    {
        var revisions = ConductorGitRevisionReader.ReadRequiredPair(worktreePath, integrationBranch);
        return (
            revisions.BranchRevision!,
            revisions.MainRevision!,
            revisions.Fingerprint);
    }

    private string? TryResolveAcceptanceBranchHead(Goal goal)
    {
        if (_executionDirectory is null)
        {
            return null;
        }

        var worktreePath = GoalWorktrees.TryResolve(_executionDirectory, goal.Id);
        return worktreePath is null ? null : TryResolveGitHead(worktreePath);
    }

    private static string FormatAcceptanceCandidate(string? branchHeadSha, string? mainHeadSha) =>
        $"branch={FormatShortSha(branchHeadSha)} main={FormatShortSha(mainHeadSha)}";

    private static bool IsCommitReachableFromMain(string executionDirectory, string commitSha, string integrationBranch) =>
        GitCli.Run(executionDirectory, "merge-base", "--is-ancestor", commitSha, integrationBranch).ExitCode == 0;

    private static string RecoverMainMergeCommitForBranchTip(string executionDirectory, string branchHeadSha, string integrationBranch)
    {
        var ancestry = GitCli.Run(executionDirectory, "log", "--format=%H", "--reverse", "--ancestry-path", $"{branchHeadSha}..{integrationBranch}");
        if (ancestry.ExitCode == 0)
        {
            var mergeCommit = ancestry.Output
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(mergeCommit))
            {
                return mergeCommit;
            }
        }

        var tipLog = GitCli.Run(executionDirectory, "log", "--format=%H", "-n", "1", branchHeadSha);
        return tipLog.ExitCode == 0 && !string.IsNullOrWhiteSpace(tipLog.Output)
            ? tipLog.Output.Trim()
            : branchHeadSha;
    }

    private static string FormatShortSha(string? sha) =>
        string.IsNullOrWhiteSpace(sha)
            ? "unknown"
            : sha.Trim()[..Math.Min(12, sha.Trim().Length)];

    private static string FormatSlotsBusy(DotnetBuildLeaseAcquisition.SlotsBusy slotsBusy)
    {
        var slots = string.Join(
            ", ",
            slotsBusy.BusySlots.Select(slot =>
                $"slot-{slot.SlotIndex} pid {slot.OwnerProcessId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown"}"));
        return $"wanted-by={slotsBusy.WantedBy}; busy slots: {slots}";
    }

    private static string FormatBuildLockBlocked(BuildLockAttribution attribution)
    {
        var holders = attribution.Holders.Count == 0
            ? "unknown"
            : string.Join(", ", attribution.Holders.Select(holder =>
                $"pid {holder.ProcessId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown"} {holder.ProcessName ?? "unknown"}"));
        return $"path={attribution.Path}; holders: {holders}";
    }

    private ReconcileAcceptanceLeaseState? TryGetActiveEvidenceMutationLease(Goal goal)
    {
        _tryRecoverTerminalDeveloperIntegrationLease?.Invoke(goal);
        var lease = _getEvidenceMutationLease(goal);
        return lease is not null && lease.ExpiresAtUtc > _utcNow()
            ? lease
            : null;
    }

    private static string FormatEvidenceMutationLeaseHeld(ReconcileAcceptanceLeaseState lease) =>
        $"acceptance lease held; owner={lease.Owner}; expiresAtUtc={lease.ExpiresAtUtc:O}";

    private ConductorAdvanceResult ReplacementEvidenceMutationHeld(
        Goal goal,
        string goalPrefix,
        ConductorAutonomyPolicy policy)
    {
        var lease = TryGetActiveEvidenceMutationLease(goal);
        var reason = lease is null
            ? "acceptance lease acquisition blocked; owner=unknown; expiresAtUtc=unknown"
            : FormatEvidenceMutationLeaseHeld(lease);
        return MakeResult(
            goal.Id.Value,
            goalPrefix,
            policy,
            new ConductorAdvanceOutcome.Held(
                GoalLifecycleState.Verified,
                reason));
    }

    private sealed class NoopEvidenceMutationLease : IDisposable
    {
        internal static readonly NoopEvidenceMutationLease Instance = new();

        public void Dispose()
        {
        }
    }
}
