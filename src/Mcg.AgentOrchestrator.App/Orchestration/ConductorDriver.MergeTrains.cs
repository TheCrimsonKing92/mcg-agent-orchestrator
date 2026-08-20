using System.Diagnostics;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorDriver
{
    internal bool MergeTrainsEnabled =>
        !string.Equals(Environment.GetEnvironmentVariable("MCG_MERGE_TRAIN_DISABLED"), "1", StringComparison.Ordinal) &&
        (_runMergeTrainOverride is not null ||
         (_cohortKernel is not null &&
          _cohortWorkspace is not null &&
          _cohortAcceptanceVerifier is not null &&
          _mergeTrainAcceptanceStore is not null));

    internal ConductorMergeTrainRunResult RunMergeTrain(
        ConductorMergeTrainSelection selection,
        IReadOnlyList<Goal> orderedGoals,
        ConductorAutonomyPolicy policy,
        CancellationToken cancellationToken = default,
        Action? onGateAdmitted = null)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(orderedGoals);
        if (_runMergeTrainOverride is not null)
        {
            return _runMergeTrainOverride(selection, orderedGoals, policy);
        }
        if (_cohortKernel is null || _cohortWorkspace is null ||
            _cohortAcceptanceVerifier is null || _mergeTrainAcceptanceStore is null)
        {
            throw new InvalidOperationException("Production merge train dependencies are unavailable.");
        }

        var goalsById = orderedGoals.ToDictionary(goal => goal.Id);
        var originalBindings = selection.BindMembers();
        var attemptId = $"merge-train-attempt-{Guid.NewGuid():N}";
        var allEjections = new List<MergeTrainEjection>();
        IReadOnlyList<MergeTrainMemberBinding> composition = originalBindings;
        var admitted = false;

        for (var attempt = 0; attempt <= 1; attempt++)
        {
            using var workspace = GoalWorktrees.CreateMergeTrainWorkspace(
                _cohortWorkspace.ExecutionDirectory,
                selection.Members[0].MainRevision,
                composition);
            allEjections.AddRange(workspace.Ejections);
            _mergeTrainAcceptanceStore.RecordEjections(attemptId, workspace.Ejections);
            if (workspace.Members.Count < ConductorMergeTrainSelector.MinimumMembers)
            {
                return Fallback("materialization left fewer than two compatible members");
            }

            var members = workspace.Members;
            var changedFiles = members.SelectMany(member => member.LandingPaths)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var manifest = _cohortAcceptanceVerifier.ComputeEffectivePlanIdentity(workspace.Path, changedFiles);
            var identity = MergeTrainIdentity.Create(
                members,
                selection.Members[0].MainRevision,
                workspace.TreeRevision,
                manifest);
            var receipt = _mergeTrainAcceptanceStore.TryReadReceipt(identity.Value);
            if (receipt is null)
            {
                var clock = Stopwatch.StartNew();
                DotnetBuildEnvironmentLease? stableSlotLease = null;
                try
                {
                    stableSlotLease = _parallelAcceptanceAttemptCoordinator.AcquireCohortStableSlotLease(
                        identity.Value,
                        cancellationToken);
                    if (!admitted)
                    {
                        admitted = true;
                        onGateAdmitted?.Invoke();
                    }
                    var verification = _cohortAcceptanceVerifier.RunAsync(
                        workspace.Path,
                        goalId: members[0].GoalId,
                        changedFiles,
                        stableSlotLease.Environment.BuildPermitIndex,
                        stableSlotLease,
                        cancellationToken).GetAwaiter().GetResult();
                    clock.Stop();
                    var cohortOutcome = ClassifyCohortVerification(verification);
                    var outcome = cohortOutcome switch
                    {
                        AcceptanceCohortGateOutcome.Passed => MergeTrainGateOutcome.Passed,
                        AcceptanceCohortGateOutcome.Failed => MergeTrainGateOutcome.Failed,
                        _ => MergeTrainGateOutcome.InfrastructureFailure
                    };
                    receipt = _mergeTrainAcceptanceStore.SaveGateReceipt(new MergeTrainReceipt(
                        $"merge-train-receipt-{identity.Value[(MergeTrainIdentity.Version.Length + 1)..]}",
                        identity,
                        outcome,
                        DateTimeOffset.UtcNow,
                        checked((long)clock.Elapsed.TotalMilliseconds),
                        verification.Checks?.Where(check => !check.Passed && !check.Advisory)
                            .Select(check => check.Name).ToArray() ?? [],
                        verification.ExitCode,
                        NormalizeCohortTestResultPaths(verification.TestResultPaths),
                        ValidForLanding: outcome == MergeTrainGateOutcome.Passed));
                }
                catch (Exception ex) when (ex is AcceptanceInfrastructureDeferredException or
                    DotnetBuildSlotsBusyException or BuildLockBlockedException or OperationCanceledException or
                    IOException or InvalidDataException)
                {
                    return Fallback($"gate infrastructure failure: {ex.GetType().Name}: {BoundCohortDetail(ex.Message)}");
                }
                finally
                {
                    stableSlotLease?.Dispose();
                }
            }

            if (receipt.Outcome == MergeTrainGateOutcome.Passed)
            {
                workspace.AssertGoalBranchesUnchanged();
                var goals = members.Select(member => goalsById[member.GoalId]).ToArray();
                var landing = LandingExecutor.ExecuteMergeTrain(
                    _cohortKernel,
                    goals,
                    _cohortWorkspace,
                    receipt,
                    workspace.CommitRevision,
                    _mergeTrainAcceptanceStore,
                    policy,
                    _cohortEventWriter,
                    LandingMutationBlocker);
                if (!landing.MainAdvanced)
                {
                    return new ConductorMergeTrainRunResult(receipt, Hold(goals, landing.Message), allEjections, landing.Message);
                }
                foreach (var goal in goals)
                {
                    var result = new LandingResult(
                        goal.Id.Value,
                        goal.Id.Value[..8],
                        new LandingDecision.Promote(),
                        $"train/{identity.Value}",
                        true,
                        landing.Message,
                        landing.CommitRevision,
                        changedFiles);
                    SuccessfulLandingSink?.Invoke(new ConductorLandingReceipt(goal.Id.Value, changedFiles, landing.CommitRevision));
                    _afterSuccessfulLanding(goal, result);
                }
                return new ConductorMergeTrainRunResult(
                    receipt,
                    goals.ToDictionary(
                        goal => goal.Id.Value,
                        goal => MakeResult(
                            goal.Id.Value,
                            goal.Id.Value[..8],
                            policy,
                            new ConductorAdvanceOutcome.Executed(
                                GoalLifecycleState.Verified,
                                $"Landed by merge train receipt {receipt.ReceiptId}.")),
                        StringComparer.Ordinal),
                    allEjections,
                    $"outcome=passed attempts={attempt + 1} landings={goals.Length} receipt={receipt.ReceiptId}");
            }

            if (receipt.Outcome != MergeTrainGateOutcome.Failed || members.Count == 2 || attempt == 1)
            {
                return Fallback($"outcome={receipt.Outcome} attempts={attempt + 1} fallback=ordinary");
            }

            // The bounded bisection is deliberately drop-newest, not a full search. The dropped member
            // remains absent from MemberResults so ordinary admission attributes its later solo gate.
            var dropped = members[^1];
            var ejection = new MergeTrainEjection(
                dropped.GoalId,
                MergeTrainEjectionReason.RedNewestMember,
                [],
                $"Dropped after RED receipt {receipt.ReceiptId}.");
            allEjections.Add(ejection);
            _mergeTrainAcceptanceStore.RecordEjections(attemptId, [ejection]);
            var remainingIds = members.Take(members.Count - 1).Select(member => member.GoalId).ToHashSet();
            composition = originalBindings.Where(member => remainingIds.Contains(member.GoalId)).ToArray();
        }

        throw new InvalidOperationException("Merge train bounded bisection exhausted without a disposition.");

        ConductorMergeTrainRunResult Fallback(string detail) => new(
            Receipt: null,
            new Dictionary<string, ConductorAdvanceResult>(StringComparer.Ordinal),
            allEjections,
            detail);

        Dictionary<string, ConductorAdvanceResult> Hold(IReadOnlyList<Goal> goals, string detail) =>
            goals.ToDictionary(
                goal => goal.Id.Value,
                goal => MakeResult(
                    goal.Id.Value,
                    goal.Id.Value[..8],
                    policy,
                    new ConductorAdvanceOutcome.Held(GoalLifecycleState.Verified, detail)),
                StringComparer.Ordinal);
    }
}
