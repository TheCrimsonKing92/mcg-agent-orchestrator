using System.Diagnostics;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorDriver
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, ConductorAcceptanceCohortGateFault>
        _trainGateFaults = new(StringComparer.Ordinal);

    internal void SetCohortGateBackgroundStartForTests(Action<Action> start)
    {
        ArgumentNullException.ThrowIfNull(start);
        _startCohortGateBackground = start;
    }

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
        Action? onGateAdmitted = null,
        bool runGateInBackground = false,
        bool gateOnly = false,
        bool landFromReceiptOnly = false,
        string? expectedGateIdentity = null)
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
        foreach (var member in selection.Members)
        {
            if (!goalsById.ContainsKey(member.GoalId))
            {
                throw new InvalidOperationException(
                    $"Selected merge train goal {member.GoalId.Value} is not in the current Ready batch.");
            }
        }
        var originalBindings = selection.BindMembers();
        var attemptId = $"merge-train-attempt-{Guid.NewGuid():N}";
        var allEjections = new List<MergeTrainEjection>();
        MergeTrainReceipt? lastRecordedReceipt = null;
        var selectedGoalIds = selection.Members.Select(member => member.GoalId.Value)
            .ToHashSet(StringComparer.Ordinal);
        var trainKey = TrainGateRunIdentity(selectedGoalIds);
        if (runGateInBackground)
        {
            if (_groupedGateAttempts is null &&
                TryGetActiveCohortGateRun(selectedGoalIds, out var activeRun))
            {
                return InFlight(activeRun!);
            }
            SweepCompletedCohortGateRuns();
            if (_trainGateFaults.TryRemove(trainKey, out var fault))
            {
                return new ConductorMergeTrainRunResult(
                    null,
                    new Dictionary<string, ConductorAdvanceResult>(StringComparer.Ordinal),
                    [],
                    $"gate infrastructure failure: {fault.FaultType}: {BoundCohortDetail(fault.Message)}");
            }
        }
        IReadOnlyList<MergeTrainMemberBinding> composition = originalBindings;
        var admitted = false;
        MergeTrainMemberBinding? redDropped = null;
        MergeTrainReceipt? redReceipt = null;
        IReadOnlyList<string> redSubjects = [];

        for (var attempt = 0; attempt <= 1; attempt++)
        {
            MergeTrainWorkspace workspace;
            try
            {
                workspace = GoalWorktrees.CreateMergeTrainWorkspace(
                    _cohortWorkspace.ExecutionDirectory,
                    selection.Members[0].MainRevision,
                    composition,
                    _cohortCleanupHooks);
            }
            catch (InvalidOperationException ex)
            {
                var reason = ex.Message.Contains("stale", StringComparison.OrdinalIgnoreCase)
                    ? MergeTrainEjectionReason.StaleBinding
                    : MergeTrainEjectionReason.MaterializationFailure;
                var ejections = composition.Select(member => new MergeTrainEjection(
                    member.GoalId,
                    reason,
                    [],
                    BoundCohortDetail(ex.Message))).ToArray();
                allEjections.AddRange(ejections);
                _mergeTrainAcceptanceStore.RecordEjections(attemptId, ejections);
                return Fallback($"materialization fallback: {reason}: {BoundCohortDetail(ex.Message)}");
            }

            using var workspaceScope = workspace;
            allEjections.AddRange(workspace.Ejections);
            _mergeTrainAcceptanceStore.RecordEjections(attemptId, workspace.Ejections);
            if (workspace.Members.Count < ConductorMergeTrainSelector.MinimumCompositionMembers)
            {
                return Fallback("materialization left fewer than two compatible members");
            }

            var members = workspace.Members;
            var changedFiles = members.SelectMany(member => member.LandingPaths)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            string manifest;
            try
            {
                manifest = _cohortAcceptanceVerifier.ComputeEffectivePlanIdentity(workspace.Path, changedFiles);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                return Fallback($"manifest fallback: {ex.GetType().Name}: {BoundCohortDetail(ex.Message)}");
            }
            var identity = MergeTrainIdentity.Create(
                members,
                selection.Members[0].MainRevision,
                workspace.TreeRevision,
                manifest);
            if (gateOnly && attempt == 0 && expectedGateIdentity is not null &&
                identity.Value != expectedGateIdentity)
                throw new InvalidOperationException("Grouped train gate identity changed before child execution.");
            var receipt = _mergeTrainAcceptanceStore.TryReadReceipt(identity.Value);
            if (runGateInBackground && _groupedGateAttempts is not null)
            {
                var recovered = RecoverGroupedGateAttempt("train", selection.Members,
                    selection.Members[0].MainRevision, workspace.TreeRevision,
                    trainKey, receipt is not null);
                if (recovered.Running is not null) return InFlight(recovered.Running);
                if (recovered.DeadWithoutReceipt)
                    receipt = _mergeTrainAcceptanceStore.SaveGateReceipt(new MergeTrainReceipt(
                        $"merge-train-receipt-{identity.Value[(MergeTrainIdentity.Version.Length + 1)..]}",
                        identity, MergeTrainGateOutcome.InfrastructureFailure, _utcNow(), 0,
                        ["infrastructure:grouped-gate-owner-dead"], null, []));
            }
            if (receipt is null && landFromReceiptOnly)
            {
                return Fallback("outcome=replay-miss receipt=none");
            }
            receipt ??= RunMergeTrainSourceSizePreflight(workspace.Path, identity, _mergeTrainAcceptanceStore);
            if (receipt is null)
            {
                if (runGateInBackground)
                {
                    if (_groupedGateAttempts is not null)
                    {
                        var ownedRun = StartGroupedGateAttempt("train", selection.Members,
                            selection.Members[0].MainRevision, workspace.TreeRevision,
                            manifest, identity.Value, trainKey, policy);
                        onGateAdmitted?.Invoke();
                        return InFlight(ownedRun);
                    }
                    var run = new CohortGateRun(
                        _utcNow(), selectedGoalIds, $"train:{identity.Value}",
                        new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
                    if (!TryRegisterCohortGateRun(trainKey, run, out var blockingRun))
                    {
                        return InFlight(blockingRun!);
                    }

                    _startCohortGateBackground(() =>
                    {
                        try
                        {
                            // The gate-only replay owns its workspace and writes receipts, including the
                            // bounded RED bisection. A later tick replays those receipts and lands.
                            _ = RunMergeTrain(selection, orderedGoals, policy, cancellationToken,
                                onGateAdmitted, gateOnly: true);
                            run.Completion.SetResult();
                        }
                        catch (Exception ex)
                        {
                            run.Completion.SetException(ex);
                        }
                    });
                    return InFlight(run);
                }
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
                    var executionOwner = AcceptanceExecutionOwners.CreateAttempt(
                        workspace.Path,
                        members[0].GoalId,
                        stableSlotLease.Environment.BuildPermitIndex,
                        cancellationToken,
                        CreateMergeTrainGateExecutionOptions(identity, members, trainKey));
                    var verification = AcceptanceExecutionOwnerLifetime.Run(
                        executionOwner,
                        () => _cohortAcceptanceVerifier.RunOwnedAsync(
                            workspace.Path,
                            goalId: members[0].GoalId,
                            changedFiles,
                            stableSlotLease.Environment.BuildPermitIndex,
                            stableSlotLease,
                            executionOwner).GetAwaiter().GetResult());
                    clock.Stop();
                    var resultPaths = NormalizeCohortTestResultPaths(verification.TestResultPaths);
                    var outcome = MergeTrainTimeoutAttribution.ResolveGateOutcome(
                        ClassifyCohortVerificationResult(verification), resultPaths, workspace.Path, members);
                    receipt = _mergeTrainAcceptanceStore.SaveGateReceipt(new MergeTrainReceipt(
                        $"merge-train-receipt-{identity.Value[(MergeTrainIdentity.Version.Length + 1)..]}",
                        identity,
                        outcome,
                        DateTimeOffset.UtcNow,
                        checked((long)clock.Elapsed.TotalMilliseconds),
                        verification.Checks?.Where(check => !check.Passed && !check.Advisory)
                            .Select(check => check.Name).ToArray() ?? [],
                        verification.ExitCode,
                        resultPaths,
                        ValidForLanding: outcome == MergeTrainGateOutcome.Passed));
                }
                catch (Exception ex) when (ex is AcceptanceInfrastructureDeferredException or
                    DotnetBuildSlotsBusyException or BuildLockBlockedException or OperationCanceledException or
                    IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or
                    NotSupportedException)
                {
                    if (gateOnly)
                    {
                        throw;
                    }
                    return Fallback($"gate infrastructure failure: {ex.GetType().Name}: {BoundCohortDetail(ex.Message)}");
                }
                finally
                {
                    stableSlotLease?.Dispose();
                }
            }

            lastRecordedReceipt = receipt;
            if (receipt.Outcome == MergeTrainGateOutcome.Passed)
            {
                if (redDropped is not null && redReceipt is not null)
                    RecordTrainImplicatedMember(redDropped, redReceipt, redSubjects);
                if (gateOnly)
                {
                    return new ConductorMergeTrainRunResult(receipt,
                        new Dictionary<string, ConductorAdvanceResult>(StringComparer.Ordinal),
                        allEjections, $"outcome=gate-passed receipt={receipt.ReceiptId}");
                }
                var goals = members.Select(member => goalsById[member.GoalId]).ToArray();
                var originalSelection = selection.Members.ToDictionary(member => member.GoalId);
                for (var index = 0; index < goals.Length; index++)
                {
                    if (ProjectGateReadyCandidate(goals[index], policy) is not GateReadyCandidateProjectionResult.Ready live ||
                        !live.Projection.Equals(originalSelection[members[index].GoalId]))
                    {
                        return new ConductorMergeTrainRunResult(
                            receipt,
                            Hold(goals, "post-gate binding changed; train held for fresh Ready projection"),
                            allEjections,
                            "post-gate binding changed; train held for fresh Ready projection");
                    }
                }
                try
                {
                    workspace.AssertGoalBranchesUnchanged();
                }
                catch (InvalidOperationException ex)
                {
                    var detail = $"post-gate goal branch changed; fresh Ready projection required; detail={BoundCohortDetail(ex.Message)}";
                    return new ConductorMergeTrainRunResult(receipt, Hold(goals, detail), allEjections, detail);
                }
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
                _mergeTrainAcceptanceStore.CompleteLandingEffects(identity.Value, receipt.ReceiptId);
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

            var attributed = MergeTrainRedAttribution.TryAttribute(receipt, workspace.Path, members, out var subjects);
            var messageSubjectFailure = MergeTrainRedAttribution.ReadFatalFailures(receipt.GateTestResultPaths)
                .Any(MergeTrainRedAttribution.IsMessageSubjectGuard);
            // Positive subject ownership already proves this member's RED; do not wait for
            // the remainder to pass before excluding the unchanged revision.
            if (messageSubjectFailure && attributed is not null)
                RecordTrainImplicatedMember(attributed, receipt, subjects);
            if (receipt.Outcome != MergeTrainGateOutcome.Failed || members.Count == 2 || attempt == 1)
            {
                if (!messageSubjectFailure && attributed is not null)
                    RecordTrainImplicatedMember(attributed, receipt, subjects);
                RecordTrainRedPair(selection, members, receipt, attributed);
                var detail = $"outcome={receipt.Outcome} attempts={attempt + 1} fallback=ordinary";
                return Fallback(detail);
            }

            // Keep the bounded bisection; absent source attribution, retain drop-newest.
            // The dropped member remains absent from MemberResults for its later solo gate.
            var dropped = attributed ?? members[^1];
            redDropped = messageSubjectFailure ? null : dropped;
            redReceipt = receipt;
            redSubjects = subjects;
            var ejection = new MergeTrainEjection(
                dropped.GoalId,
                attributed is null ? MergeTrainEjectionReason.RedNewestMember : MergeTrainEjectionReason.RedAttributedMember,
                [],
                $"Dropped after RED receipt {receipt.ReceiptId}.");
            allEjections.Add(ejection);
            _mergeTrainAcceptanceStore.RecordEjections(attemptId, [ejection]);
            var remainingIds = members.Where(member => member.GoalId != dropped.GoalId)
                .Select(member => member.GoalId).ToHashSet();
            composition = originalBindings.Where(member => remainingIds.Contains(member.GoalId)).ToArray();
        }

        throw new InvalidOperationException("Merge train bounded bisection exhausted without a disposition.");

        ConductorMergeTrainRunResult Fallback(string detail) => new(
            Receipt: null,
            new Dictionary<string, ConductorAdvanceResult>(StringComparer.Ordinal),
            allEjections,
            detail) { RecordedReceipt = lastRecordedReceipt };

        Dictionary<string, ConductorAdvanceResult> Hold(IReadOnlyList<Goal> goals, string detail) =>
            goals.ToDictionary(
                goal => goal.Id.Value,
                goal => MakeResult(
                    goal.Id.Value,
                    goal.Id.Value[..8],
                    policy,
                    new ConductorAdvanceOutcome.Held(GoalLifecycleState.Verified, detail)),
                StringComparer.Ordinal);

        ConductorMergeTrainRunResult InFlight(CohortGateRun run)
        {
            var detail = FormatCohortGateInFlightDetail(run, _utcNow());
            return new ConductorMergeTrainRunResult(null,
                Hold(selection.Members.Select(member => goalsById[member.GoalId]).ToArray(), detail),
                allEjections, detail) { RecordedReceipt = lastRecordedReceipt };
        }
    }

    internal IReadOnlyList<(ConductorMergeTrainSelection Selection, MergeTrainReceipt Receipt)>
        FindLandablePassedMergeTrainSelections(
            IReadOnlyList<ConductorSpeculativeAcceptanceCandidate> candidates,
            IReadOnlySet<string> ineligibleGoalIds,
            Action<MergeTrainReceipt, string> onStale,
            Action<MergeTrainReceipt, PassedMergeTrainReceiptHolds.Observation>? onBlocked = null,
            IReadOnlyList<Goal>? observationGoals = null,
            IReadOnlySet<GoalId>? receiptObservationGoalIds = null,
            Func<Goal, (string? BranchHeadSha, string? MainHeadSha)>? resolveReceiptHeads = null)
    {
        if (_runMergeTrainOverride is not null || _mergeTrainAcceptanceStore is null ||
            _cohortWorkspace is null || _cohortAcceptanceVerifier is null)
        {
            return [];
        }

        return PassedMergeTrainReceiptSelector.Find(
            candidates, ineligibleGoalIds, onStale, onBlocked, observationGoals,
            receiptObservationGoalIds, resolveReceiptHeads ?? _resolveAcceptanceHeads,
            (_cohortKernel ?? _conductorTickKernel)?.Goals,
            _mergeTrainAcceptanceStore.ReadPassedReceiptsForGoal, ReadSuppressedCohortPairs,
            HasCurrentPassedMergeTrainIdentity, _integrationBranch);
    }

    private bool HasCurrentPassedMergeTrainIdentity(
        ConductorMergeTrainSelection selection,
        MergeTrainReceipt receipt)
    {
        if (_cohortWorkspace is null || _cohortAcceptanceVerifier is null)
        {
            return false;
        }
        try
        {
            using var workspace = GoalWorktrees.CreateMergeTrainWorkspace(
                _cohortWorkspace.ExecutionDirectory,
                selection.Members[0].MainRevision,
                selection.BindMembers(),
                _cohortCleanupHooks);
            if (workspace.Members.Count != receipt.Identity.Members.Count)
            {
                return false;
            }
            var changedFiles = workspace.Members.SelectMany(member => member.LandingPaths)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var manifest = _cohortAcceptanceVerifier.ComputeEffectivePlanIdentity(workspace.Path, changedFiles);
            return string.Equals(MergeTrainIdentity.Create(workspace.Members,
                    selection.Members[0].MainRevision, workspace.TreeRevision, manifest).Value,
                receipt.Identity.Value, StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return false;
        }
    }

    private void RecoverMergeTrainLandingEffects(
        AgentOrchestratorKernel kernel,
        OrchestratorWorkspace workspace,
        IGoalLifecycleEventWriter eventWriter,
        MergeTrainAcceptanceStore store)
    {
        var goalsById = kernel.Goals.ToDictionary(goal => goal.Id);
        foreach (var recovery in store.RecoverPreparedLandings(workspace.ExecutionDirectory))
        {
            var goals = recovery.Receipt.Identity.Members
                .Select(member => goalsById.TryGetValue(member.GoalId, out var goal) ? goal : null)
                .ToArray();
            if (goals.Any(goal => goal is null))
            {
                continue;
            }

            var resolvedGoals = goals.Cast<Goal>().ToArray();
            var evidenceMutationLeases = new Stack<IDisposable>();
            try
            {
                foreach (var goal in resolvedGoals.OrderBy(goal => goal.Id.Value, StringComparer.Ordinal))
                {
                    var lease = _tryAcquireEvidenceMutationLease(goal, "conductor:merge-train-recovery");
                    if (lease is null)
                    {
                        break;
                    }
                    evidenceMutationLeases.Push(lease);
                }
                if (evidenceMutationLeases.Count != resolvedGoals.Length)
                {
                    continue;
                }

                var changedFiles = recovery.Receipt.Identity.Members
                    .SelectMany(member => member.LandingPaths)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Order(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                var evidenceDiagnostic = AcceptanceCriterionEvidence.RebindRecordAndDescribeOutstanding(
                    resolvedGoals,
                    goalId => recovery.Receipt.Identity.Members.Single(member => member.GoalId == goalId).CandidateRevision,
                    kernel,
                    $"merge-train-receipt:{recovery.Receipt.ReceiptId}",
                    workspace.ExecutionDirectory);
                if (evidenceDiagnostic is not null)
                {
                    Console.WriteLine($"MERGE_TRAIN_RECOVERY_HELD train={recovery.Receipt.Identity.Value} detail={evidenceDiagnostic}");
                    continue;
                }
                foreach (var goal in resolvedGoals)
                {
                    GoalOperationJournal.Completed(
                        workspace.ExecutionDirectory,
                        goal,
                        "conductor:land",
                        $"Recovered merge train receipt {recovery.Receipt.ReceiptId} after main advanced.");
                    eventWriter.AppendGoalLanded(
                        goal.Id,
                        $"train/{recovery.Receipt.Identity.Value}",
                        GoalWorktrees.BranchName(goal.Id), LandingAdmissionReceipt.Recovered(
                            recovery.Receipt.Identity.Members.Single(member => member.GoalId == goal.Id).CandidateRevision));
                    StateEffectProposalApplier.ApplyLandedProposals(
                        kernel,
                        goal,
                        workspace,
                        changedFiles,
                        Console.WriteLine);
                    var landingResult = new LandingResult(
                        goal.Id.Value,
                        goal.Id.Value[..8],
                        new LandingDecision.Promote(),
                        $"train/{recovery.Receipt.Identity.Value}",
                        MainAdvanced: true,
                        "Recovered exact tested merge train landing after main advanced.",
                        recovery.CommitRevision,
                        changedFiles);
                    _pendingRecoveredMergeTrainLandingReceipts.Add((
                        new ConductorLandingReceipt(goal.Id.Value, changedFiles, recovery.CommitRevision),
                        recovery.Receipt.Identity.Value,
                        recovery.Receipt.ReceiptId));
                    _afterSuccessfulLanding(goal, landingResult);
                }
            }
            finally
            {
                while (evidenceMutationLeases.TryPop(out var lease))
                {
                    lease.Dispose();
                }
            }
        }
    }
}
