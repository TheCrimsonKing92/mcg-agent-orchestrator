using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorDriver
{
    private ICollaborationItemStore? _ownerReviewDecisions;
    private Action<Goal, string>? _writeOwnerReviewEscalation;
    private Func<Goal, string, string?>? _resolveOwnerReviewFingerprint;
    private AgentOrchestratorKernel? _ownerReviewKernel;

    internal void OverrideOwnerReviewHoldForTests(
        ICollaborationItemStore decisions, Action<Goal, string> writeGoalEscalation,
        Func<Goal, string, string?> resolveFingerprint, AgentOrchestratorKernel? kernel = null)
    {
        _ownerReviewDecisions = decisions;
        _writeOwnerReviewEscalation = writeGoalEscalation;
        _resolveOwnerReviewFingerprint = resolveFingerprint;
        _ownerReviewKernel = kernel;
    }

    private static bool IsOwnerReviewCheckName(string name) =>
        name.Trim().Equals("owner-protected configuration", StringComparison.OrdinalIgnoreCase) ||
        name.Trim().Equals("acceptance manifest trusted dimensions", StringComparison.OrdinalIgnoreCase);

    private static bool IsOwnerReviewOnlyFailure(AcceptanceVerificationSummary acceptance)
    {
        var failed = acceptance.UnmetCriteria.Where(check => !check.Passed).ToArray();
        return !acceptance.Passed && acceptance.RequiredUnmetCriteria.Count > 0 &&
            failed.All(check => IsOwnerReviewCheckName(check.Name) &&
                string.Equals(check.ResultSummary?.Trim(), "operator review required", StringComparison.OrdinalIgnoreCase)) &&
            (acceptance.FailedChecks is null || acceptance.FailedChecks.All(name =>
                failed.Any(check => string.Equals(check.Name.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase))));
    }

    private ConductorAdvanceResult? TryHoldForOwnerReview(
        Goal goal, string goalPrefix, ConductorAutonomyPolicy policy, AcceptanceVerificationSummary acceptance)
    {
        if (!IsOwnerReviewOnlyFailure(acceptance)) return null;
        var sha = acceptance.BranchHeadSha ?? _resolveAcceptanceHeads(goal).BranchHeadSha;
        if (sha is null || !AcceptancePolicyChangeDecision.IsFullSha(sha)) return null;
        sha = sha.ToLowerInvariant();
        var fingerprint = ResolveOwnerReviewFingerprint(goal, sha);
        if (IsOwnerReviewApproved(goal, sha, fingerprint)) return null;

        var directory = _executionDirectory ??
            throw new InvalidOperationException("Owner review hold requires a durable execution directory.");
        var reason = FormatOwnerReviewEscalation(goalPrefix, sha, fingerprint, acceptance);
        // Background solo gates are reconciled to AcceptanceFailed before this handler. Restore
        // their non-terminal landing state without reopening or invalidating any worker lane.
        if (goal.Status is GoalStatus.AcceptanceFailed or GoalStatus.Verifying)
        {
            var kernel = _ownerReviewKernel ?? _cohortKernel ?? _conductorTickKernel ??
                throw new InvalidOperationException("Owner review hold requires the goal's kernel to restore Verified.");
            if (goal.Status == GoalStatus.Verifying)
                kernel.ReconcileGoalAcceptanceVerified(goal.Id, reason);
            else
                kernel.RestoreVerifiedForClassifiedAcceptanceFailure(goal.Id, reason);
        }
        var checks = acceptance.RequiredUnmetCriteria.Select(check => check.Name).ToArray();
        _recordAcceptanceFailure(goal, checks, sha, acceptance.MainHeadSha,
            acceptance.CheckAttributions, acceptance.BaselineAttestation);
        var failure = goal.LatestAcceptanceFailure ??
            throw new InvalidOperationException("Owner review hold requires a retained acceptance failure.");
        var entries = GoalOperationJournal.Read(directory, goal.Id).Entries;
        var alreadyRaised = entries.Any(entry =>
            string.Equals(entry.BranchHeadSha, sha, StringComparison.OrdinalIgnoreCase) &&
            GoalOperationJournal.ReadOwnerReviewHold(entry) is { } previous && previous.Fingerprint == fingerprint);
        var receipt = new OwnerReviewHoldReceipt(fingerprint, failure.OccurredAt, reason);
        GoalOperationJournal.OwnerReviewHoldEntered(directory, goal, sha, acceptance.MainHeadSha, receipt, checks);
        if (!alreadyRaised) WriteOwnerReviewEscalation(goal, reason);
        return OwnerReviewHeld(goal, goalPrefix, policy, sha, receipt);
    }

    private bool HasActiveOwnerReviewHold(Goal goal, out string sha, out OwnerReviewHoldReceipt? receipt)
    {
        sha = "";
        receipt = null;
        if (_executionDirectory is null || goal.LatestAcceptanceFailure is not { } failure ||
            failure.BranchHeadSha is not { } candidate || failure.FailedChecks.Count == 0 ||
            !failure.FailedChecks.All(IsOwnerReviewCheckName)) return false;
        var entry = GoalOperationJournal.Read(_executionDirectory, goal.Id).Entries.LastOrDefault(entry =>
            entry.Operation == GoalOperationJournal.OwnerReviewHoldOperation &&
            string.Equals(entry.BranchHeadSha, candidate, StringComparison.OrdinalIgnoreCase));
        if (entry is null || GoalOperationJournal.ReadOwnerReviewHold(entry) is not { } held ||
            held.FailureOccurredAt != failure.OccurredAt) return false;
        var current = _resolveAcceptanceHeads(goal).BranchHeadSha;
        if (!string.IsNullOrWhiteSpace(current) &&
            !string.Equals(current, candidate, StringComparison.OrdinalIgnoreCase)) return false;
        sha = candidate;
        receipt = held;
        return !IsOwnerReviewApproved(goal, sha, held.Fingerprint);
    }

    private ConductorAdvanceResult OwnerReviewHeld(
        Goal goal, string prefix, ConductorAutonomyPolicy policy, string sha, OwnerReviewHoldReceipt receipt,
        VerifiedAdmissionDecision? decision = null) =>
        MakeResult(goal.Id.Value, prefix, policy, new ConductorAdvanceOutcome.Held(
            GoalLifecycleState.Verified, receipt.Reason,
            StableIdentity: $"owner-review-hold:{sha}:{receipt.Fingerprint ?? "none"}")
            { Decision = decision?.ToRecord() });

    private bool IsOwnerReviewApproved(Goal goal, string sha, string? fingerprint)
    {
        var decisions = _ownerReviewDecisions ??= _cohortWorkspace is { } workspace
            ? CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory)
            : throw new InvalidOperationException("Owner review approval store is unavailable.");
        return AcceptancePolicyChangeDecision.IsApprovedAsync(decisions, goal.Id.Value, sha).GetAwaiter().GetResult() ||
            (fingerprint is not null && AcceptancePolicyChangeDecision
                .IsApprovedForFingerprintAsync(decisions, goal.Id.Value, fingerprint).GetAwaiter().GetResult());
    }

    private string? ResolveOwnerReviewFingerprint(Goal goal, string sha)
    {
        try
        {
            if (_resolveOwnerReviewFingerprint is not null) return _resolveOwnerReviewFingerprint(goal, sha);
            var worktree = _executionDirectory is null ? null : GoalWorktrees.TryResolve(_executionDirectory, goal.Id);
            return worktree is null ? null :
                GoalAcceptanceVerifier.ComputeOwnerProtectedChangeFingerprintForCandidate(worktree, sha, _integrationBranch);
        }
        catch (Exception)
        {
            // The fingerprint is optional; SHA-bound owner approval still works.
            return null;
        }
    }

    private void WriteOwnerReviewEscalation(Goal goal, string text)
    {
        if (_writeOwnerReviewEscalation is not null) { _writeOwnerReviewEscalation(goal, text); return; }
        var workspace = _cohortWorkspace ?? throw new InvalidOperationException("Owner review escalation workspace is unavailable.");
        var lifecycle = _cohortEventWriter ?? throw new InvalidOperationException("Owner review goal event writer is unavailable.");
        new ConductEventLogWriter(workspace.ConductEventsLogPath).Append("goal-escalation", goal.Id.Value, text);
        lifecycle.AppendGoalEscalated(goal.Id, GoalLifecycleState.Verified, goal.Status, text, "owner-review-hold");
    }

    private static string FormatOwnerReviewEscalation(
        string prefix, string sha, string? fingerprint, AcceptanceVerificationSummary acceptance) =>
        $"owner-review-hold goal={prefix} candidate={sha}" + Environment.NewLine +
        "checks=" + string.Join(", ", acceptance.RequiredUnmetCriteria.Select(check => check.Name)) + Environment.NewLine +
        "changed-protected-fields=" + string.Join(Environment.NewLine, acceptance.RequiredUnmetCriteria.Select(check => OwnerReviewFields(check.OutputTail))) + Environment.NewLine +
        (fingerprint is null ? "" : $"fingerprint={fingerprint}{Environment.NewLine}") +
        $"approve: .\\mcg-orchestrator.cmd approve-policy-change {prefix} {sha} --text-file <reason-file>" + Environment.NewLine +
        $"decline: cancel-goal {prefix} or abandon-goal {prefix}";

    private static string OwnerReviewFields(string? output)
    {
        var fields = (output ?? "").Split(';', StringSplitOptions.TrimEntries)
            .Where(part => part.Contains("changed field", StringComparison.OrdinalIgnoreCase)).ToArray();
        return fields.Length > 0 ? string.Join("; ", fields) : output ?? "(check supplied no detail)";
    }
}
