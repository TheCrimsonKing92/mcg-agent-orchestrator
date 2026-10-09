using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorDriver
{
    private ConductorGroupedGateAttemptCoordinator? _groupedGateAttempts;

    internal void EnableOwnedGroupedGateAttempts(
        ConductorGroupedGateAttemptCoordinator? coordinator = null)
    {
        if (_cohortWorkspace is null)
            throw new InvalidOperationException("Grouped gate attempts require production cohort dependencies.");
        _groupedGateAttempts = coordinator ?? new ConductorGroupedGateAttemptCoordinator(
            Path.Combine(_cohortWorkspace.OrchestratorDirectory, "grouped-gate-attempts"),
            eventSink: line => new ConductEventLogWriter(_cohortWorkspace.ConductEventsLogPath)
                .Append("acceptance-cohort", null, line),
            buildStorageRoot: _cohortCleanupHooks.BuildStorageRoot);
    }

    // The durable record is inspected before a caller reads a receipt. A child may write the primary
    // receipt before its attribution or bisection work is complete, so its exit artifact is the fence.
    private (CohortGateRun? Running, bool DeadWithoutReceipt) RecoverGroupedGateAttempt(
        string kind,
        IReadOnlyList<GateReadyCandidateProjection> members,
        string mainRevision,
        string treeRevision,
        string memberKey,
        bool receiptExists)
    {
        if (_groupedGateAttempts is not { } coordinator) return (null, false);
        var selectedIds = members.Select(member => member.GoalId.Value).ToHashSet(StringComparer.Ordinal);
        foreach (var recorded in coordinator.ReadAll())
        {
            var attempt = recorded;
            if (attempt.Kind == "follower") continue;
            if (!attempt.Members.Any(member => selectedIds.Contains(member.GoalId))) continue;

            var difference = coordinator.IdentityDifference(attempt, kind, members, mainRevision, treeRevision, _integrationBranch);
            if (attempt.ReconciledAt is not null)
            {
                if (difference is null && !receiptExists && !ConductorGroupedGateAttemptCoordinator.IsSlotsBusyDeferral(attempt) &&
                    !string.IsNullOrWhiteSpace(attempt.Detail) &&
                    !attempt.Detail.StartsWith("refused:", StringComparison.Ordinal))
                    return (null, true);
                continue;
            }
            var alive = coordinator.IsAlive(attempt.OwnerProcessId);
            if (!alive)
            {
                attempt = coordinator.TryReconcileDead(attempt, File.Exists(attempt.ResultPath)
                    ? "child-result-published" : "owner-dead");
                alive = coordinator.IsAlive(attempt.OwnerProcessId);
                if (!alive)
                {
                    CompleteOwnedRunForAttempt(attempt);
                    if (difference is null && !receiptExists && !ConductorGroupedGateAttemptCoordinator.IsSlotsBusyDeferral(attempt)) return (null, true);
                    continue;
                }
            }

            // This generation checked the identity at adoption. Keep its live gate fenced
            // until the child publishes a result, even if the selection later moves.
            if (attempt.AdoptedByGenerationId != coordinator.GenerationId &&
                difference is not null && coordinator.ShouldAdopt(attempt))
            {
                if (coordinator.Refuse(attempt, difference))
                {
                    CompleteOwnedRunForAttempt(attempt);
                    continue;
                }
                // Inspection failure is not permission to run a second gate beside a live owner.
            }
            else if (attempt.AdoptedByGenerationId != coordinator.GenerationId &&
                difference is null && coordinator.ShouldAdopt(attempt))
            {
                attempt = coordinator.Adopt(attempt);
            }

            if (File.Exists(attempt.ExitCodePath) || File.Exists(attempt.ResultPath))
            {
                coordinator.Reconcile(attempt, "child-result-published");
                CompleteOwnedRunForAttempt(attempt);
                if (difference is null && !receiptExists && !ConductorGroupedGateAttemptCoordinator.IsSlotsBusyDeferral(attempt)) return (null, true);
                continue;
            }

            var run = new CohortGateRun(attempt.StartedAt,
                attempt.Members.Select(member => member.GoalId).ToHashSet(StringComparer.Ordinal),
                attempt.Kind == "train" ? $"train:{attempt.IdentityValue}" : attempt.IdentityValue,
                new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
                attempt.MetadataPath);
            if (!TryRegisterCohortGateRun(memberKey, run, out var blocking))
                return (blocking, false);
            return (run, false);
        }
        return (null, false);
    }

    private void CompleteOwnedRunForAttempt(ConductorGroupedGateAttempt attempt)
    {
        foreach (var run in _cohortGateRuns.Values)
        {
            if (string.Equals(run.AttemptMetadataPath, attempt.MetadataPath, StringComparison.Ordinal))
                run.Completion.TrySetResult();
        }
    }

    private CohortGateRun StartGroupedGateAttempt(
        string kind,
        IReadOnlyList<GateReadyCandidateProjection> members,
        string mainRevision,
        string treeRevision,
        string manifestIdentity,
        string identityValue,
        string memberKey,
        ConductorAutonomyPolicy policy)
    {
        var coordinator = _groupedGateAttempts
            ?? throw new InvalidOperationException("Grouped gate attempts were not enabled.");
        var executionDirectory = _executionDirectory
            ?? throw new InvalidOperationException("Grouped gate execution directory is unavailable.");
        var attempt = coordinator.Create(kind, members, mainRevision, treeRevision,
            manifestIdentity, identityValue, executionDirectory, policy);
        var run = new CohortGateRun(attempt.StartedAt,
            members.Select(member => member.GoalId.Value).ToHashSet(StringComparer.Ordinal),
            kind == "train" ? $"train:{identityValue}" : identityValue,
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
            attempt.MetadataPath);
        if (!TryRegisterCohortGateRun(memberKey, run, out var blocking)) return blocking!;
        try { coordinator.Launch(attempt); }
        catch (Exception ex) { run.Completion.TrySetException(ex); }
        return run;
    }

    private void ObserveOwnedGroupedGateAttempts()
    {
        if (_groupedGateAttempts is not { } coordinator) return;
        foreach (var run in _cohortGateRuns.Values)
        {
            if (run.AttemptMetadataPath is not { } path || run.Completion.Task.IsCompleted) continue;
            var attempt = ConductorGroupedGateAttemptCoordinator.Read(path);
            if (File.Exists(attempt.ExitCodePath) || File.Exists(attempt.ResultPath) ||
                !coordinator.IsAlive(attempt.OwnerProcessId))
                run.Completion.TrySetResult();
        }
        StopInvalidatedFollowerGates();
    }

    private void RestoreRunningGroupedGateAttempts()
    {
        if (_groupedGateAttempts is not { } coordinator) return;
        lock (_cohortGateRegistrationSync)
        {
            foreach (var attempt in coordinator.ReadAll())
            {
                if (attempt.ReconciledAt is not null || attempt.Outcome != "Running" ||
                    File.Exists(attempt.ResultPath) || File.Exists(attempt.ExitCodePath) ||
                    !coordinator.IsAlive(attempt.OwnerProcessId))
                    continue;
                var current = attempt;
                if (current.AdoptedByGenerationId != coordinator.GenerationId &&
                    coordinator.ShouldAdopt(current))
                {
                    var identity = InspectRestoredGroupedGateIdentity(current);
                    if (identity.Known && identity.Difference is { } difference)
                    {
                        if (coordinator.Refuse(current, difference))
                        {
                            CompleteOwnedRunForAttempt(current);
                            continue;
                        }
                    }
                    else if (identity.Known)
                    {
                        current = coordinator.Adopt(current);
                    }
                }
                if (_cohortGateRuns.Values.Any(run =>
                    string.Equals(run.AttemptMetadataPath, current.MetadataPath, StringComparison.Ordinal)))
                    continue;
                if (current.Kind == "follower")
                {
                    RequireFollowerMembers(current);
                    var followerRun = CreateFollowerGateRun(current);
                    if (TryGetActiveCohortGateRun(followerRun.MemberGoalIds, out var followerOverlap))
                        throw new InvalidOperationException(
                            $"Live grouped gate attempts overlap: {followerOverlap!.AttemptMetadataPath} and {current.MetadataPath}.");
                    var followerKey = FollowerGateRunKey(current);
                    if (!_cohortGateRuns.TryAdd(followerKey, followerRun))
                        throw new InvalidOperationException($"Grouped gate registry key '{followerKey}' is already owned.");
                    continue;
                }
                var memberIds = current.Members.Select(member => member.GoalId)
                    .ToHashSet(StringComparer.Ordinal);
                if (TryGetActiveCohortGateRun(memberIds, out var overlapping))
                    throw new InvalidOperationException(
                        $"Live grouped gate attempts overlap: {overlapping!.AttemptMetadataPath} and {current.MetadataPath}.");
                var key = current.Kind == "train"
                    ? TrainGateRunIdentity(memberIds)
                    : CohortGateRunIdentity(memberIds);
                var fingerprint = current.Kind == "train"
                    ? $"train:{current.IdentityValue}"
                    : ConductorAcceptanceCohortSelector.PairFingerprint(
                        current.Members[0].ToProjection(current.MainRevision),
                        current.Members[1].ToProjection(current.MainRevision));
                var run = new CohortGateRun(current.StartedAt, memberIds, fingerprint,
                    new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
                    current.MetadataPath);
                if (!_cohortGateRuns.TryAdd(key, run))
                    throw new InvalidOperationException($"Grouped gate registry key '{key}' is already owned.");
            }
        }
    }

    private (bool Known, string? Difference) InspectRestoredGroupedGateIdentity(
        ConductorGroupedGateAttempt attempt)
    {
        if (attempt.Kind == "follower") return InspectRestoredFollowerGateIdentity(attempt);
        if (_cohortKernel is null || _cohortWorkspace is null) return (false, null);
        var policy = ConductorAutonomyPolicy.ParseJson(attempt.PolicyJson, attempt.MetadataPath);
        var goalsById = _cohortKernel.Goals.ToDictionary(goal => goal.Id.Value, StringComparer.Ordinal);
        var projections = new List<GateReadyCandidateProjection>(attempt.Members.Count);
        foreach (var member in attempt.Members)
        {
            if (!goalsById.TryGetValue(member.GoalId, out var goal) ||
                ProjectGateReadyCandidate(goal, policy) is not GateReadyCandidateProjectionResult.Ready ready)
                return (true, "members");
            projections.Add(ready.Projection);
        }
        var mainRevision = projections[0].MainRevision;
        var difference = _groupedGateAttempts!.IdentityDifference(
            attempt, attempt.Kind, projections, mainRevision, attempt.CombinedTreeRevision, _integrationBranch);
        if (difference is not null) return (true, difference);
        try
        {
            if (attempt.Kind == "cohort")
            {
                using var integration = GoalWorktrees.CreateAcceptanceCohortWorkspace(
                    _cohortWorkspace.ExecutionDirectory, mainRevision,
                    new ConductorAcceptanceCohortSelection(projections, []).BindMembers(), _integrationBranch,
                    _cohortCleanupHooks);
                return (true, integration.TreeRevision == attempt.CombinedTreeRevision ? null : "tree");
            }
            if (attempt.Kind == "train")
            {
                using var integration = GoalWorktrees.CreateMergeTrainWorkspace(
                    _cohortWorkspace.ExecutionDirectory, mainRevision,
                    new ConductorMergeTrainSelection(projections).BindMembers(), _integrationBranch,
                    _cohortCleanupHooks);
                return (true, integration.TreeRevision == attempt.CombinedTreeRevision ? null : "tree");
            }
            throw new InvalidDataException($"Unknown grouped gate kind '{attempt.Kind}'.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // Unavailable materialization is not evidence that identity changed. Keep a provisional
            // hold and retry inspection next tick without admitting an overlapping gate.
            return (false, null);
        }
    }

    internal ConductorGroupedGateOutcome? RunGroupedGateAttemptBody(ConductorGroupedGateAttempt attempt)
    {
        var workspace = _cohortWorkspace ?? throw new InvalidOperationException("Cohort workspace is unavailable.");
        var verifier = _cohortAcceptanceVerifier ?? throw new InvalidOperationException("Cohort verifier is unavailable.");
        var members = attempt.Members.Select(member => member.ToProjection(attempt.MainRevision)).ToArray();
        if (attempt.Kind == "cohort")
        {
            var selection = new ConductorAcceptanceCohortSelection(members, []);
            var bindings = selection.BindMembers();
            using var integration = GoalWorktrees.CreateAcceptanceCohortWorkspace(
                workspace.ExecutionDirectory, attempt.MainRevision, bindings, _integrationBranch, _cohortCleanupHooks);
            var manifest = verifier.ComputeEffectivePlanIdentity(integration.Path,
                bindings.SelectMany(member => member.LandingPaths).Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
            var identity = AcceptanceCohortIdentity.Create(bindings, attempt.MainRevision,
                integration.TreeRevision, manifest);
            if (identity.Value != attempt.IdentityValue || manifest != attempt.ManifestIdentity ||
                integration.TreeRevision != attempt.CombinedTreeRevision)
                throw new InvalidOperationException("Grouped cohort gate identity changed before child execution.");
            var receipt = ExecuteAcceptanceCohortGate(integration, identity, bindings,
                ConductorAcceptanceCohortSelector.PairFingerprint(members[0], members[1]),
                CancellationToken.None, null);
            return receipt is null ? CohortStableSlotAcquisitionRounds.DeferredGroupedOutcome : ToGroupedGateOutcome(receipt.Outcome, receipt.ReceiptId);
        }
        else if (attempt.Kind == "train")
        {
            var kernel = _cohortKernel ?? throw new InvalidOperationException("Cohort kernel is unavailable.");
            var goals = members.Select(member => kernel.Goals.Single(goal => goal.Id == member.GoalId)).ToArray();
            var result = RunMergeTrain(new ConductorMergeTrainSelection(members), goals,
                ConductorAutonomyPolicy.ParseJson(attempt.PolicyJson, attempt.MetadataPath),
                gateOnly: true, expectedGateIdentity: attempt.IdentityValue);
            return result.RecordedReceipt is { } receipt
                ? ToGroupedGateOutcome(receipt.Outcome, receipt.ReceiptId) : null;
        }
        else if (attempt.Kind == "follower")
        {
            RunFollowerGateBody(attempt);
            return null;
        }
        else
        {
            throw new InvalidDataException($"Unknown grouped gate kind '{attempt.Kind}'.");
        }
    }

    internal static ConductorGroupedGateOutcome? ToGroupedGateOutcome(
        AcceptanceCohortGateOutcome outcome, string receiptId) => outcome switch
    {
        AcceptanceCohortGateOutcome.Passed => new("passed", receiptId),
        AcceptanceCohortGateOutcome.Failed => new("failed", receiptId),
        _ => null
    };

    internal static ConductorGroupedGateOutcome? ToGroupedGateOutcome(
        MergeTrainGateOutcome outcome, string receiptId) => outcome switch
    {
        MergeTrainGateOutcome.Passed => new("passed", receiptId),
        MergeTrainGateOutcome.Failed => new("failed", receiptId),
        _ => null
    };
}
