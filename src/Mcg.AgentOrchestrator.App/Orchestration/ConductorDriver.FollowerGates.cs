using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal abstract record FollowerGateStartOutcome
{
    internal sealed record Started(ConductorGroupedGateAttempt Attempt) : FollowerGateStartOutcome;
    internal sealed record Conflict(IReadOnlyList<string> Paths) : FollowerGateStartOutcome;
    internal sealed record LeaderHasFollower : FollowerGateStartOutcome;
    internal sealed record FollowerBusy : FollowerGateStartOutcome;
    internal sealed record MainDiffers : FollowerGateStartOutcome;
    internal sealed record ReceiptExists : FollowerGateStartOutcome;
}

internal sealed partial class ConductorDriver
{
    private FollowerGateAcceptanceStore? _followerGateAcceptanceStore;

    private FollowerGateAcceptanceStore FollowerGateStore => _followerGateAcceptanceStore ??= new(
        Path.Combine((_cohortWorkspace ?? throw new InvalidOperationException("Follower workspace is unavailable."))
            .OrchestratorDirectory, "follower-gate-acceptance.db"));

    internal FollowerGateStartOutcome StartFollowerGate(
        GateReadyCandidateProjection leader, GateReadyCandidateProjection follower, ConductorAutonomyPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(leader);
        ArgumentNullException.ThrowIfNull(follower);
        if (leader.GoalId == follower.GoalId)
            throw new ArgumentException("A follower gate requires distinct leader and follower goals.", nameof(follower));
        var coordinator = _groupedGateAttempts ?? throw new InvalidOperationException("Grouped gate attempts were not enabled.");
        var root = _executionDirectory ?? throw new InvalidOperationException("Follower execution directory is unavailable.");
        var verifier = _cohortAcceptanceVerifier ?? throw new InvalidOperationException("Follower verifier is unavailable.");
        lock (_cohortGateRegistrationSync)
        {
            SweepCompletedCohortGateRuns();
            if (coordinator.ReadAll().Any(attempt => attempt.Kind == "follower" &&
                attempt.ReconciledAt is null && attempt.Members[0].GoalId == leader.GoalId.Value))
                return new FollowerGateStartOutcome.LeaderHasFollower();
            var heldMembers = new HashSet<string>(StringComparer.Ordinal) { follower.GoalId.Value };
            if (TryGetActiveCohortGateRun(heldMembers, out _)) return new FollowerGateStartOutcome.FollowerBusy();
            if (!string.Equals(leader.MainRevision, follower.MainRevision, StringComparison.Ordinal))
                return new FollowerGateStartOutcome.MainDiffers();
            var result = GoalWorktrees.CreateFollowerWorkspace(root, leader.MainRevision,
                leader.GoalId, leader.CandidateRevision, follower.GoalId, follower.BranchRevision, _cohortCleanupHooks);
            if (result.IsConflict)
                return new FollowerGateStartOutcome.Conflict(result.ConflictPaths.Order(StringComparer.Ordinal).ToArray());
            using var integration = result.Workspace!;
            // The workspace is based on C_A. Only B's scope selects the follower's plan.
            var plan = verifier.ComputeEffectivePlanIdentity(integration.Path, follower.LandingPaths);
            var identity = FollowerGateIdentity.Create(integration.ToReceipt(plan));
            if (FollowerGateStore.TryReadReceipt(identity) is not null) return new FollowerGateStartOutcome.ReceiptExists();
            var attempt = coordinator.Create("follower", [leader, follower], leader.MainRevision,
                integration.TestedTreeRevision, plan, identity, root, policy);
            var run = CreateFollowerGateRun(attempt);
            if (!TryRegisterCohortGateRun(FollowerGateRunKey(attempt), run, out _))
                return new FollowerGateStartOutcome.FollowerBusy();
            try { attempt = coordinator.Launch(attempt); }
            catch (Exception ex) { run.Completion.TrySetException(ex); throw; }
            return new FollowerGateStartOutcome.Started(attempt);
        }
    }

    private static string FollowerGateRunKey(ConductorGroupedGateAttempt attempt) =>
        $"follower:{attempt.Members[0].GoalId}";

    private static CohortGateRun CreateFollowerGateRun(ConductorGroupedGateAttempt attempt) => new(
        attempt.StartedAt, new HashSet<string>(StringComparer.Ordinal) { attempt.Members[1].GoalId },
        $"follower:{attempt.IdentityValue}", new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
        attempt.MetadataPath);

    private static void RequireFollowerMembers(ConductorGroupedGateAttempt attempt)
    {
        if (attempt.Kind != "follower" || attempt.Members.Count != 2 ||
            attempt.Members[0].GoalId == attempt.Members[1].GoalId)
            throw new InvalidDataException("A follower attempt requires ordered, distinct leader and follower members.");
    }

    private FollowerGateReceipt ReadFollowerBinding(ConductorGroupedGateAttempt attempt)
    {
        RequireFollowerMembers(attempt);
        var leader = attempt.Members[0];
        var follower = attempt.Members[1];
        var leaderTree = GoalWorktrees.ResolveRequiredRef(attempt.ExecutionDirectory, $"{leader.CandidateRevision}^{{tree}}");
        return new(new GoalId(leader.GoalId), leader.CandidateRevision, leaderTree, attempt.MainRevision,
            new GoalId(follower.GoalId), follower.BranchRevision, attempt.CombinedTreeRevision, attempt.ManifestIdentity);
    }

    private void RunFollowerGateBody(ConductorGroupedGateAttempt attempt)
    {
        var binding = ReadFollowerBinding(attempt);
        var verifier = _cohortAcceptanceVerifier ?? throw new InvalidOperationException("Follower verifier is unavailable.");
        var follower = attempt.Members[1];
        try
        {
            var result = GoalWorktrees.CreateFollowerWorkspace(attempt.ExecutionDirectory, binding.BaseMainRevision,
                binding.LeaderGoalId, binding.LeaderCandidateRevision, binding.FollowerGoalId,
                binding.FollowerBranchHead, _cohortCleanupHooks);
            if (result.IsConflict) throw new InvalidOperationException("Follower gate re-materialization conflicted before child execution.");
            using var integration = result.Workspace!;
            var plan = verifier.ComputeEffectivePlanIdentity(integration.Path, follower.LandingPaths);
            var actualBinding = integration.ToReceipt(plan);
            if (integration.TestedTreeRevision != attempt.CombinedTreeRevision || plan != attempt.ManifestIdentity ||
                FollowerGateIdentity.Create(actualBinding) != attempt.IdentityValue)
                throw new InvalidOperationException("Grouped follower gate identity changed before child execution.");
            using var lease = _parallelAcceptanceAttemptCoordinator.AcquireCohortStableSlotLease(
                attempt.IdentityValue, CancellationToken.None);
            var options = CreateFollowerGateExecutionOptions(attempt, binding);
            var owner = AcceptanceExecutionOwners.CreateAttempt(integration.Path, binding.FollowerGoalId,
                lease.Environment.BuildPermitIndex, CancellationToken.None, options);
            var verification = AcceptanceExecutionOwnerLifetime.Run(owner, () => verifier.RunOwnedAsync(
                integration.Path, binding.FollowerGoalId, follower.LandingPaths,
                lease.Environment.BuildPermitIndex, lease, owner).GetAwaiter().GetResult());
            var outcome = ClassifyCohortVerification(verification) switch
            {
                AcceptanceCohortGateOutcome.Passed => FollowerGateRunOutcome.Passed,
                AcceptanceCohortGateOutcome.Failed => FollowerGateRunOutcome.Failed,
                _ => FollowerGateRunOutcome.InfrastructureFailure
            };
            FollowerGateStore.SaveGateReceipt(new FollowerGateRunReceipt(
                $"follower-receipt-{attempt.IdentityValue}", attempt.IdentityValue, actualBinding, outcome,
                _utcNow(), verification.Checks?.Where(check => !check.Passed && !check.Advisory)
                    .Select(check => check.Name).ToArray() ?? [], verification.ExitCode,
                NormalizeCohortTestResultPaths(verification.TestResultPaths), null));
        }
        catch (AcceptanceExecutionIdentityChangedException ex) when (ex.IsChangedIdentity)
        {
            SaveInvalidatedFollowerReceipt(attempt, binding, ReadFollowerStopReason(attempt, binding) ?? FollowerGateInvalidReason.BaseMoved);
        }
    }

    private AcceptanceRunExecutionOptions CreateFollowerGateExecutionOptions(
        ConductorGroupedGateAttempt attempt, FollowerGateReceipt binding)
    {
        var logPath = (_cohortWorkspace ?? throw new InvalidOperationException("Follower workspace is unavailable."))
            .ConductEventsLogPath;
        return new(ProgressSink: progress =>
        {
            try
            {
                TryAppendGateProgressEvent(new ConductEventLogWriter(logPath), binding.FollowerGoalId.Value,
                    FormatGateProgressConductEvent(progress with { GoalId = binding.FollowerGoalId.Value }) +
                    $" leader={binding.LeaderGoalId.Value} follower={binding.FollowerGoalId.Value} scope=follower");
            }
            catch (Exception) { /* Progress is observational. */ }
        }, RemoteLaneEventSink: detail =>
        {
            try { AppendRemoteLaneEvent(new ConductEventLogWriter(logPath), binding.FollowerGoalId.Value, detail); }
            catch (Exception) { /* Progress is observational. */ }
        }, GateRunIdentity: FollowerGateRunKey(attempt),
            PinnedBase: new(binding.BaseMainRevision, binding.LeaderCandidateRevision, binding.LeaderCandidateTree),
            ProjectHomeDirectory: _cohortWorkspace?.ProjectHomeDirectoryOrNull);
    }

    internal (bool Known, string? Difference) InspectRestoredFollowerGateIdentity(ConductorGroupedGateAttempt attempt)
    {
        if (_cohortKernel is null || _cohortWorkspace is null) return (false, null);
        RequireFollowerMembers(attempt);
        var follower = attempt.Members[1];
        var goal = _cohortKernel.Goals.SingleOrDefault(goal => goal.Id.Value == follower.GoalId);
        var policy = ConductorAutonomyPolicy.ParseJson(attempt.PolicyJson, attempt.MetadataPath);
        if (goal is null || ProjectGateReadyCandidate(goal, policy) is not GateReadyCandidateProjectionResult.Ready ready ||
            ready.Projection.BranchRevision != follower.BranchRevision)
            return (true, "members");
        try
        {
            return (true, ReadFollowerStopReason(attempt, ReadFollowerBinding(attempt)) is null ? null : "main");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return (false, null);
        }
    }

    internal void StopInvalidatedFollowerGates()
    {
        if (_groupedGateAttempts is not { } coordinator || _cohortKernel is null || _executionDirectory is null) return;
        foreach (var attempt in coordinator.ReadAll())
        {
            if (attempt.Kind != "follower" || attempt.ReconciledAt is not null || attempt.Outcome != "Running" ||
                !coordinator.IsAlive(attempt.OwnerProcessId)) continue;
            var binding = ReadFollowerBinding(attempt);
            var reason = ReadFollowerStopReason(attempt, binding);
            if (reason is null || !coordinator.Invalidate(attempt, reason.Value)) continue;
            CompleteOwnedRunForAttempt(attempt);
            SaveInvalidatedFollowerReceipt(attempt, binding, reason.Value);
        }
    }

    private void SaveInvalidatedFollowerReceipt(ConductorGroupedGateAttempt attempt,
        FollowerGateReceipt binding, FollowerGateInvalidReason reason)
    {
        if (FollowerGateStore.TryReadReceipt(attempt.IdentityValue) is not null) return;
        FollowerGateStore.SaveGateReceipt(new($"follower-receipt-{attempt.IdentityValue}", attempt.IdentityValue,
            binding, FollowerGateRunOutcome.Invalidated, _utcNow(), [], null, [], reason));
    }

    private FollowerGateInvalidReason? ReadFollowerStopReason(ConductorGroupedGateAttempt attempt, FollowerGateReceipt binding)
    {
        var root = attempt.ExecutionDirectory;
        var main = ReadFollowerRevision(root, "refs/heads/main");
        var parent = main is null ? null : ReadFollowerRevision(root, $"{main}^1");
        var tree = main is null ? null : ReadFollowerRevision(root, $"{main}^{{tree}}");
        var state = FollowerGateBindingRule.ClassifyLiveBase(binding.BaseMainRevision, binding.LeaderCandidateTree, main, parent, tree);
        return FollowerGateBindingRule.StopReason(ReadFollowerLeaderStatus(attempt), state,
            string.Equals(parent, binding.BaseMainRevision, StringComparison.Ordinal));
    }

    private FollowerLeaderGateStatus ReadFollowerLeaderStatus(ConductorGroupedGateAttempt attempt)
    {
        var leader = attempt.Members[0];
        var goal = _cohortKernel?.Goals.SingleOrDefault(goal => goal.Id.Value == leader.GoalId);
        if (goal is null) return FollowerLeaderGateStatus.Unknown;
        return goal.Status switch
        {
            GoalStatus.Completed => FollowerLeaderGateStatus.Landed,
            GoalStatus.AcceptanceFailed or GoalStatus.Failed => FollowerLeaderGateStatus.Failed,
            GoalStatus.Verified or GoalStatus.Verifying =>
                string.Equals(ReadFollowerRevision(attempt.ExecutionDirectory,
                    $"refs/heads/{GoalWorktrees.BranchName(goal.Id)}"), leader.CandidateRevision, StringComparison.Ordinal)
                    ? FollowerLeaderGateStatus.Pending : FollowerLeaderGateStatus.Stale,
            _ => FollowerLeaderGateStatus.Cancelled
        };
    }

    private static string? ReadFollowerRevision(string root, string revision)
    {
        var result = GitCli.Run(root, "rev-parse", "--verify", revision);
        var value = result.Output.Trim();
        return result.ExitCode == 0 && ConductorGitRevisionReader.IsValid(value) ? value.ToLowerInvariant() : null;
    }
}
