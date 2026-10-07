using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorBatchLoop
{
    private ParallelAcceptanceBatchState BeginParallelAcceptanceBatch(
        IReadOnlyList<Goal> eligible,
        AgentOrchestratorKernel kernel,
        ConductorDriver driver,
        ConductorAutonomyPolicy policy,
        HashSet<string> completedGoals,
        HashSet<string> escalatedGoals,
        int configuredAcceptanceWidth,
        int tick,
        List<string> changedGoalLines)
    {
        var state = new ParallelAcceptanceBatchState
        {
            ConfiguredAcceptanceWidth = configuredAcceptanceWidth
        };
        state.Results = new Dictionary<string, ParallelLandingOutcome>(StringComparer.Ordinal);
        state.DeferredByAdmission = 0;
        state.OrderedEligible = OrderParallelAcceptanceEligibleGoals(eligible
            .Where(goal =>
                IsParallelAcceptanceLifecycleEligible(goal, driver) &&
                !driver.HasRoutableRecordedCohortAttributionFailure(goal) &&
                goal.Status is GoalStatus.Verified or GoalStatus.Verifying &&
                AcceptancePrecheck.HasCompletedPassedVerificationForAllTasks(goal) &&
                !ConductorDriver.HasPendingDeferredNoChangeEvidence(goal) &&
                GetDependencyHoldReason(goal, completedGoals, escalatedGoals, kernel) is null &&
                VerifiedAcceptanceEscalationDecision.TryHasUnresolvedPersistedVerifiedAcceptanceEscalation(goal, driver) == false)
            .ToArray());
        var activeReservations = BuildActiveParallelAcceptanceReservations(
            driver.ParallelAcceptanceAttemptCoordinator,
            kernel.Goals.Where(goal => goal.Status != GoalStatus.Completed).ToArray());
        if (activeReservations.Failure is { } capacityFailure)
        {
            var reason =
                $"acceptance capacity state unavailable; retry on next conduct tick: {SanitizeReason(capacityFailure.Message)}";
            foreach (var goal in state.OrderedEligible)
            {
                state.Results[goal.Id.Value] = new ParallelLandingOutcome(
                    ParallelAcceptanceHeld(goal, policy, reason),
                    null);
            }

            RecordParallelAcceptanceProgress(
                $"ADMISSION tick={tick} result=held reason=acceptance-capacity-state-unavailable detail={SanitizeReason(capacityFailure.Message)}",
                changedGoalLines);
            state.CapacityStateUnavailable = true;
            return state;
        }

        state.LiveAttempts = activeReservations.Attempts.ToList();
        state.ActiveCandidates = activeReservations.Candidates;
        state.ActiveAttemptIds = activeReservations.AttemptIds;
        state.ActiveAttemptSlotIndexes = activeReservations.StableSlotIndexes;
        return state;
    }

    private void ReconcileParallelAcceptanceAttempts(
        ParallelAcceptanceBatchState state,
        AgentOrchestratorKernel kernel,
        ConductorDriver driver,
        ConductorAutonomyPolicy policy,
        int tick,
        List<string> changedGoalLines,
        HashSet<GoalId> changedGoalIds)
    {
        foreach (var goal in state.OrderedEligible)
        {
            try
            {
            var sameGoalAttempts = driver.ParallelAcceptanceAttemptCoordinator.GetUnreconciledAttempts(
                [goal.Id.Value]);
            if (sameGoalAttempts.Count == 0)
            {
                continue;
            }

            var observed = sameGoalAttempts
                .Select(attempt =>
                {
                    var persistedCandidate = ConductorParallelAcceptanceCandidate.Create(
                        goal,
                        attempt.SlotIndex,
                        attempt.ScopePaths ?? [],
                        attempt.BranchHeadSha,
                        attempt.MainHeadSha);
                    return driver.ParallelAcceptanceAttemptCoordinator.ObserveExistingAttempt(
                        attempt,
                        persistedCandidate);
                })
                .OrderByDescending(decision => decision.Attempt.StartedAt)
                .ThenByDescending(decision => decision.Attempt.AttemptId, StringComparer.Ordinal)
                .ToArray();
            var running = observed
                .Where(decision => decision.Kind == ConductorParallelAcceptanceAttemptDecisionKind.Running)
                .ToArray();
            var terminal = observed
                .Where(decision => decision.Kind is ConductorParallelAcceptanceAttemptDecisionKind.Completed or
                    ConductorParallelAcceptanceAttemptDecisionKind.TerminalWithoutRun)
                .ToArray();

            // Terminal siblings release only their own durable claims. A single newest terminal
            // owns the goal-level transition when every same-goal producer is terminal; otherwise
            // every terminal drains and the remaining live producer keeps the goal held.
            var retainedTerminal = running.Length == 0 ? terminal.FirstOrDefault() : null;
            foreach (var terminalSibling in terminal.Where(decision =>
                         retainedTerminal is null ||
                         !string.Equals(
                             decision.Attempt.AttemptId,
                             retainedTerminal.Attempt.AttemptId,
                             StringComparison.Ordinal)))
            {
                driver.ParallelAcceptanceAttemptCoordinator.MarkReconciled(terminalSibling.Attempt);
            }

            if (running.Length > 0)
            {
                state.Results[goal.Id.Value] = ReserveRunningParallelAcceptanceAttempts(
                    kernel,
                    driver,
                    goal,
                    policy,
                    tick,
                    running,
                    changedGoalLines,
                    changedGoalIds);
                continue;
            }

            var verificationGate = kernel.BuildVerificationGate(goal.Id);
            if (!verificationGate.IsSatisfied)
            {
                var blockingReasons = string.Join(
                    ',',
                    verificationGate.Tasks
                        .Where(task => task.GateStatus != VerificationGateStatus.Passed)
                        .Select(task => $"{task.Role}:{task.Reason}"));
                var reason = BoundSingleLine(
                    $"inconsistent {goal.Status} state: authoritative task verification gate unsatisfied ({blockingReasons}); " +
                    "apply verify-manual or retry before acceptance");
                state.Results[goal.Id.Value] = new ParallelLandingOutcome(
                    EscalateParallelAcceptanceSafely(driver, goal, policy, reason),
                    null);
                RecordParallelAcceptanceProgress(
                    $"ADMISSION tick={tick} result=escalated reason=authoritative-verification-gate-unsatisfied goal={goal.Id.Value[..8]} detail={SanitizeReason(reason)}",
                    changedGoalLines);
                continue;
            }

            var engineHealth = _acceptanceEngineCircuit?.Read();
            if (IsAcceptanceEngineCircuitHoldRequired(goal.Status, engineHealth))
            {
                state.Results[goal.Id.Value] = new ParallelLandingOutcome(
                    ParallelAcceptanceHeld(
                        goal,
                        policy,
                        BuildAcceptanceEngineHoldReason(engineHealth!)),
                    null);
                RecordParallelAcceptanceProgress(
                    $"ADMISSION tick={tick} result=held reason=acceptance-engine-circuit goal={goal.Id.Value[..8]} health={engineHealth.Health}",
                    changedGoalLines);
                continue;
            }

            if (retainedTerminal is null)
            {
                continue;
            }

            ReplayParallelAcceptanceLeaseReceipts(driver, retainedTerminal.Attempt, changedGoalLines);
            if (retainedTerminal.Kind == ConductorParallelAcceptanceAttemptDecisionKind.Completed)
            {
                var run = retainedTerminal.Run ?? ConductorParallelAcceptanceRunResult.Fault(
                    ConductorParallelAcceptanceCandidate.Create(
                        goal,
                        retainedTerminal.Attempt.SlotIndex,
                        retainedTerminal.Attempt.ScopePaths ?? [],
                        retainedTerminal.Attempt.BranchHeadSha,
                        retainedTerminal.Attempt.MainHeadSha),
                    new InvalidOperationException("Completed acceptance attempt had no run result."));
                ReconcileParallelAcceptanceTerminalState(kernel, goal, run, retainedTerminal.Attempt);
                var result = CompleteParallelAcceptanceRun(
                    driver,
                    policy,
                    run,
                    retainedTerminal.Attempt,
                    out var evidenceMutationLeaseHeld);
                MarkParallelAcceptanceReconciledUnlessLeaseHeld(
                    driver,
                    run,
                    evidenceMutationLeaseHeld,
                    retainedTerminal.Attempt);
                changedGoalIds.Add(goal.Id);
                state.Results[goal.Id.Value] = new ParallelLandingOutcome(result, retainedTerminal.Attempt.SlotIndex);
                RecordParallelAcceptanceProgress(
                    AcceptanceLifecycleEventFormatter.Format(goal.Id.Value[..8], retainedTerminal.Attempt.SlotIndex, AcceptanceRunDisposition(run), retainedTerminal.Attempt.AttemptId, tick),
                    changedGoalLines);
                continue;
            }

            ReconcileParallelAcceptanceTerminalState(kernel, goal, retainedTerminal.Attempt);
            state.Results[goal.Id.Value] = new ParallelLandingOutcome(
                ParallelAcceptanceTerminal(
                    driver,
                    ConductorParallelAcceptanceCandidate.Create(
                        goal,
                        retainedTerminal.Attempt.SlotIndex,
                        retainedTerminal.Attempt.ScopePaths ?? [],
                        retainedTerminal.Attempt.BranchHeadSha,
                        retainedTerminal.Attempt.MainHeadSha),
                    policy,
                    retainedTerminal.Attempt),
                retainedTerminal.Attempt.SlotIndex);
            driver.ParallelAcceptanceAttemptCoordinator.MarkReconciled(retainedTerminal.Attempt);
            changedGoalIds.Add(goal.Id);
            RecordParallelAcceptanceProgress(
                AcceptanceLifecycleEventFormatter.Format(goal.Id.Value[..8], retainedTerminal.Attempt.SlotIndex, AcceptanceAttemptOutcomeToken(retainedTerminal.Attempt.Outcome), retainedTerminal.Attempt.AttemptId, tick),
                changedGoalLines);
            }
            catch (AcceptanceArtifactWriterLeaseBusyException ex)
            {
                state.Results[goal.Id.Value] = new ParallelLandingOutcome(
                    ParallelAcceptanceHeld(
                        goal,
                        policy,
                        $"acceptance artifact writer busy; retry on next conduct tick. {ex.Message}"),
                    null);
                RecordParallelAcceptanceProgress(
                    $"ADMISSION tick={tick} result=deferred reason=acceptance-artifact-writer-busy goal={goal.Id.Value[..8]} detail={SanitizeReason(ex.Message)}",
                    changedGoalLines);
            }
            catch (Exception ex)
            {
                var reason = BoundSingleLine(
                    $"persisted parallel acceptance reconciliation fault isolated: {ex.GetType().Name}: {ex.Message}");
                state.Results[goal.Id.Value] = new ParallelLandingOutcome(
                    EscalateParallelAcceptanceSafely(driver, goal, policy, reason),
                    null);
                RecordParallelAcceptanceProgress(
                    $"ADMISSION tick={tick} result=escalated reason=persisted-acceptance-reconciliation-fault goal={goal.Id.Value[..8]} detail={SanitizeReason(reason)}",
                    changedGoalLines);
            }
        }
    }

    private void PrepareGroupedAcceptanceAdmission(
        ParallelAcceptanceBatchState state,
        IReadOnlyList<Goal> eligible,
        IReadOnlyList<Goal> scopedGoals,
        IReadOnlySet<GoalId> verifiedGoalIdsAtTickStart,
        IReadOnlySet<GoalId> preWalkIntentChangedGoalIds,
        AgentOrchestratorKernel kernel,
        ConductorDriver driver,
        ConductorAutonomyPolicy policy,
        HashSet<string> completedGoals,
        HashSet<string> escalatedGoals,
        int tick,
        List<string> changedGoalLines,
        bool suppressNewAcceptanceAdmission)
    {
        foreach (var (receipt, partitions) in driver.ConsumeTerminalFailedCohortReceipts())
            RecordParallelAcceptanceProgress(ConductorDriver.FormatTerminalFailedCohortReceipt(tick, receipt, partitions), changedGoalLines);
        var speculativeCandidates = state.OrderedEligible.Select(goal => new ConductorSpeculativeAcceptanceCandidate(
            goal.Id, driver.ProjectGateReadyCandidate(goal, policy))).ToArray();
        state.ActiveCohortCapacity = driver.GetActiveAcceptanceCohortCapacity();
        state.AcceptanceCensus = CaptureLiveAcceptanceCensus(
            state.LiveAttempts, state.ActiveAttemptIds, state.ActiveCohortCapacity, tick,
            changedGoalLines, blockAdmissionOnFailure: true);
        EmitSpeculativeCohortPlanReceipt(scopedGoals, verifiedGoalIdsAtTickStart,
            preWalkIntentChangedGoalIds, eligible, state.OrderedEligible, speculativeCandidates,
            state.LiveAttempts, state.ActiveCohortCapacity, state.AcceptanceCensus, driver, policy,
            completedGoals, escalatedGoals, kernel, tick);
        state.LiveAttemptGoalIds = state.LiveAttempts.Select(attempt => attempt.GoalId).ToHashSet(StringComparer.Ordinal);
        var activeCohortMemberGoalIds = driver.GetActiveCohortGateMemberGoalIds((memberGoalIds, detail) =>
        {
            var memberIds = memberGoalIds.OrderBy(id => id, StringComparer.Ordinal).ToArray();
            EmitProgress(
                $"ACCEPTANCE_COHORT_INFLIGHT tick={tick} goal={memberIds[0][..8]} " +
                $"members={string.Join(',', memberIds.Select(id => id[..8]))} {detail}");
        });
        state.CohortEligible = state.OrderedEligible
            .Where(goal => !state.LiveAttemptGoalIds.Contains(goal.Id.Value) &&
                           !activeCohortMemberGoalIds.Contains(goal.Id.Value))
            .ToArray();
        state.ProductionCandidates = ExcludeGroupedAcceptanceCandidatesWithNonAcceptanceObligations(
            speculativeCandidates, state.CohortEligible, state.LiveAttemptGoalIds, activeCohortMemberGoalIds);
        var trainAdmission = DecideLiveAcceptanceAdmission(state.AcceptanceCensus, state.ConfiguredAcceptanceWidth);
        if (!suppressNewAcceptanceAdmission &&
            driver.AcceptanceCohortsEnabled &&
            state.CohortEligible.Length >= ConductorAcceptanceCohortSelector.CohortSize &&
            !state.CohortEligible.Any(goal => IsAcceptanceEngineCircuitHoldRequired(
                goal.Status, _acceptanceEngineCircuit?.Read())))
        {
            (state.CohortEligible, state.ProductionCandidates) = LandPassedAcceptanceCohortsBeforeTrainSelection(
                driver, policy, state.CohortEligible, state.ProductionCandidates, state.Results, tick, changedGoalLines);
        }
        (state.CohortEligible, state.ProductionCandidates) = LandPassedMergeTrainReceiptsBeforeAdmission(
            driver, policy, state.CohortEligible, state.ProductionCandidates, state.Results, tick, changedGoalLines);
        state.OldestWaiterObservation = suppressNewAcceptanceAdmission
            ? new ParallelAcceptanceOldestWaiterObservation(null, 0)
            : ObserveOldestParallelAcceptanceWaiter(state.OrderedEligible, state.LiveAttemptGoalIds);
        state.TransientRetryPriorityGoal = !suppressNewAcceptanceAdmission &&
            (driver.MergeTrainsEnabled || driver.AcceptanceCohortsEnabled)
            ? SelectTransientRetryPriorityGoal(kernel, driver, state.CohortEligible, policy,
                state.AcceptanceCensus, state.ConfiguredAcceptanceWidth, state.ActiveAttemptSlotIndexes,
                state.ActiveCandidates, state.LiveAttemptGoalIds, state.OldestWaiterObservation, state.Results, tick, changedGoalLines)
            : null;
        var interactionOnlyKeys = driver.ReadInteractionOnlyMemberKeys();
        var interactionOnlyGoalIds = speculativeCandidates
            .Where(candidate => candidate.ProjectionResult is GateReadyCandidateProjectionResult.Ready ready &&
                interactionOnlyKeys.Contains(ConductorAcceptanceCohortAttributedMembers.Key(
                    candidate.GoalId, ready.Projection.CandidateRevision)))
            .Select(candidate => candidate.GoalId.Value).ToHashSet(StringComparer.Ordinal);
        state.CohortEligible = state.CohortEligible
            .Where(goal => !interactionOnlyGoalIds.Contains(goal.Id.Value)).ToArray();
        state.ProductionCandidates = state.ProductionCandidates
            .Where(candidate => !interactionOnlyGoalIds.Contains(candidate.GoalId.Value)).ToArray();
        state.InteractionOnlyPriorityGoal = SelectInteractionOnlyPriorityGoal(state, interactionOnlyGoalIds, kernel, driver, policy,
            tick, changedGoalLines, suppressNewAcceptanceAdmission);
        state.GroupedAdmissionOpen = !suppressNewAcceptanceAdmission &&
            state.TransientRetryPriorityGoal is null && state.InteractionOnlyPriorityGoal is null &&
            trainAdmission.IsAdmitted &&
            driver.MergeTrainsEnabled &&
            state.CohortEligible.Length >= ConductorMergeTrainSelector.MinimumMembers &&
            !state.CohortEligible.Any(goal => IsAcceptanceEngineCircuitHoldRequired(
                goal.Status, _acceptanceEngineCircuit?.Read()));
    }

    private void AdmitMergeTrain(
        ParallelAcceptanceBatchState state,
        ConductorDriver driver,
        ConductorAutonomyPolicy policy,
        int tick,
        List<string> changedGoalLines)
    {
        if (state.GroupedAdmissionOpen &&
            state.CohortEligible.Length >= ConductorMergeTrainSelector.MinimumMembers &&
            ConductorMergeTrainSelector.Select(
                state.ProductionCandidates,
                driver.ReadSuppressedGroupedPairs(),
                TrainIneligibleCriterionEvidenceGoalIds(state.CohortEligible), driver.ReadCohortAttributedMemberKeys(),
                driver.ReadTrainImplicatedMemberKeys()) is { } trainSelection)
        {
            var trainRun = driver.RunMergeTrain(
                trainSelection,
                state.CohortEligible,
                policy,
                onGateAdmitted: () => driver.RecordMergeTrainAdmissionFairness(trainSelection),
                runGateInBackground: true);
            foreach (var member in trainRun.MemberResults)
            {
                state.Results[member.Key] = new ParallelLandingOutcome(member.Value, SlotIndex: 0);
            }
            RecordParallelAcceptanceProgress(
                $"ACCEPTANCE_TRAIN tick={tick} members={string.Join(',', trainSelection.Members.Select(member => member.GoalId.Value[..8]))} " +
                $"ejected={string.Join(',', trainRun.Ejections.Select(ejection => ejection.GoalId.Value[..8]))} {trainRun.Detail}",
                changedGoalLines);
            state.ActiveCohortCapacity = driver.GetActiveAcceptanceCohortCapacity();
            state.AcceptanceCensus = CaptureLiveAcceptanceCensus(
                state.LiveAttempts,
                state.ActiveAttemptIds,
                state.ActiveCohortCapacity,
                tick,
                changedGoalLines,
                blockAdmissionOnFailure: true);
            state.CohortEligible = state.CohortEligible
                .Where(goal => !state.Results.ContainsKey(goal.Id.Value))
                .ToArray();
            state.ProductionCandidates = state.ProductionCandidates
                .Where(candidate => !state.Results.ContainsKey(candidate.GoalId.Value))
                .ToArray();
        }
    }

    private void AdmitPairCohort(
        ParallelAcceptanceBatchState state,
        AgentOrchestratorKernel kernel,
        ConductorDriver driver,
        ConductorAutonomyPolicy policy,
        int tick,
        List<string> changedGoalLines,
        HashSet<GoalId> changedGoalIds,
        bool suppressNewAcceptanceAdmission)
    {
        state.ForcedCohortPriority = null;
        var cohortAdmission = DecideLiveAcceptanceAdmission(state.AcceptanceCensus, state.ConfiguredAcceptanceWidth);
        if (!suppressNewAcceptanceAdmission &&
            state.TransientRetryPriorityGoal is null && state.InteractionOnlyPriorityGoal is null &&
            cohortAdmission.IsAdmitted &&
            driver.AcceptanceCohortsEnabled &&
            state.CohortEligible.Length >= ConductorAcceptanceCohortSelector.CohortSize &&
            !state.CohortEligible.Any(goal => IsAcceptanceEngineCircuitHoldRequired(
                goal.Status,
                _acceptanceEngineCircuit?.Read())))
        {
            state.ForcedCohortPriority = driver.SelectForcedCohortCandidate(state.CohortEligible);
            var cohortDecision = SelectAndReportAcceptanceCohort(state.ProductionCandidates, state.LiveAttempts, state.ForcedCohortPriority, driver);
            if (cohortDecision.Selection is { } cohortSelection)
            {
                var memberIds = string.Join(',', cohortSelection.Members.Select(member => member.GoalId.Value[..8]));
                var markerGoal = cohortSelection.Members[0].GoalId.Value[..8];
                EmitProgress(
                    $"ACCEPTANCE_COHORT_ENTRY tick={tick} goal={markerGoal} members={memberIds}");
                ConductorAcceptanceCohortRunResult cohortRun;
                ConductorAcceptanceCohortGateFault? gateFault;
                var exitOutcome = "exception";
                var exitReason = string.Empty;
                try
                {
                    var cohortOutcome = driver.RunAcceptanceCohortForTick(
                        cohortSelection,
                        state.CohortEligible,
                        policy,
                        onGateAdmitted: () => EmitAcceptanceCohortFairnessTransition(
                            driver.RecordCohortAdmissionFairness(state.CohortEligible, cohortSelection)),
                        runGateInBackground: true);
                    cohortRun = cohortOutcome.Run;
                    gateFault = cohortOutcome.Fault;
                    if (gateFault is { } observedFault)
                    {
                        var observedMemberIds = observedFault.MemberGoalIds
                            .OrderBy(id => id, StringComparer.Ordinal)
                            .ToArray();
                        memberIds = string.Join(',', observedMemberIds.Select(id => id[..8]));
                        markerGoal = observedMemberIds[0][..8];
                    }
                    (exitOutcome, exitReason) = gateFault is { } backgroundFault
                        ? DescribeAcceptanceCohortGateFault(backgroundFault)
                        : DescribeAcceptanceCohortExit(cohortRun);
                }
                catch (Exception cohortGateException)
                {
                    // The cohort gate is the conductor's own machinery. Nothing it throws is allowed to end
                    // the tick: a transient fault is held and retried like the background attempt path does,
                    // and anything else escalates both members on the spot. Either way it leaves as data.
                    gateFault = ConductorDriver.CreateCohortGateFault(
                        cohortSelection,
                        cohortGateException);
                    cohortRun = new ConductorAcceptanceCohortRunResult(
                        Receipt: null,
                        new Dictionary<string, ConductorAdvanceResult>(StringComparer.Ordinal),
                        $"outcome=gate-fault fingerprint={gateFault.PairFingerprint} " +
                        $"fault={gateFault.FaultType} detail={SanitizeReason(gateFault.Message)}");
                    (exitOutcome, exitReason) = DescribeAcceptanceCohortGateFault(gateFault);
                }
                finally
                {
                    EmitProgress(
                        $"ACCEPTANCE_COHORT_EXIT tick={tick} goal={markerGoal} members={memberIds} outcome={exitOutcome}{exitReason}");
                }
                if (gateFault is { } cohortGateFault)
                {
                    cohortRun = ResolveFaultedAcceptanceCohort(
                        driver,
                        policy,
                        state.CohortEligible,
                        cohortRun,
                        cohortGateFault,
                        tick,
                        changedGoalLines);
                }
                foreach (var pair in cohortRun.MemberResults)
                {
                    state.Results[pair.Key] = TrackCohortAttributionFailure(cohortRun, pair, kernel, changedGoalIds);
                }
                if (cohortRun.Detail.Contains("outcome=inflight", StringComparison.Ordinal))
                {
                    EmitProgress(
                        $"ACCEPTANCE_COHORT_INFLIGHT tick={tick} goal={markerGoal} members={memberIds} {cohortRun.Detail}");
                }
                RecordParallelAcceptanceProgress(
                    $"ACCEPTANCE_COHORT tick={tick} members={string.Join(',', cohortSelection.Members.Select(member => member.GoalId.Value[..8]))} {cohortRun.Detail}",
                    changedGoalLines);
                state.ActiveCohortCapacity = driver.GetActiveAcceptanceCohortCapacity();
                state.AcceptanceCensus = CaptureLiveAcceptanceCensus(
                    state.LiveAttempts,
                    state.ActiveAttemptIds,
                    state.ActiveCohortCapacity,
                    tick,
                    changedGoalLines,
                    blockAdmissionOnFailure: true);
            }
            else if (cohortDecision.Exclusions.Count > 0)
            {
                RecordParallelAcceptanceProgress(
                    $"ACCEPTANCE_COHORT tick={tick} outcome=unpaired exclusions={FormatCohortPairExclusions(cohortDecision.Exclusions)} fallback=ordinary",
                    changedGoalLines);
            }
        }
    }

    private void HoldCohortMembers(
        ParallelAcceptanceBatchState state,
        ConductorDriver driver,
        ConductorAutonomyPolicy policy,
        int tick,
        List<string> changedGoalLines)
    {
        foreach (var goal in state.OrderedEligible)
        {
            if (state.Results.ContainsKey(goal.Id.Value) ||
                !driver.TryGetCohortGateHold(goal.Id, out var cohortHoldDetail))
            {
                continue;
            }

            state.Results[goal.Id.Value] = new ParallelLandingOutcome(
                CohortMemberHeld(
                    goal,
                    policy,
                    cohortHoldDetail),
                SlotIndex: null);
            RecordParallelAcceptanceProgress(
                $"ACCEPTANCE_COHORT tick={tick} goal={goal.Id.Value[..8]} result=held {cohortHoldDetail}",
                changedGoalLines);
        }
    }

    private void AdmitSoloAcceptance(
        ParallelAcceptanceBatchState state,
        AgentOrchestratorKernel kernel,
        ConductorDriver driver,
        ConductorAutonomyPolicy policy,
        int tick,
        List<string> changedGoalLines,
        HashSet<GoalId> changedGoalIds)
    {
        var oldestWaiter = state.OldestWaiterObservation.Waiter;
        var oldestServedThisTick = false;
        foreach (var goal in state.OrderedEligible)
        {
            if (state.Results.ContainsKey(goal.Id.Value))
            {
                continue;
            }
            try
            {
            var soloAdmission = EvaluateSoloAcceptanceAdmissibility(
                kernel, driver, goal, policy, state.AcceptanceCensus, state.ConfiguredAcceptanceWidth,
                state.ActiveAttemptSlotIndexes, state.ActiveCandidates, state.LiveAttemptGoalIds,
                state.OldestWaiterObservation, oldestServedThisTick, tick);
            if (soloAdmission.Kind != SoloAcceptanceAdmissionKind.Admissible)
            {
                if (soloAdmission.Kind == SoloAcceptanceAdmissionKind.Capacity) state.DeferredByAdmission++;
                if (ApplySoloAcceptanceHold(soloAdmission, driver, goal, policy, changedGoalLines) is { } held)
                {
                    state.Results[goal.Id.Value] = held;
                }
                continue;
            }
            var candidate = soloAdmission.Candidate!;
            var stalledOldestBypass = soloAdmission.StalledOldestBypass;
            var documentationExclusionAdmission = BuildDocumentationExclusionAdmissionRecord(
                candidate,
                state.ActiveCandidates,
                tick);
            var decision = driver.ParallelAcceptanceAttemptCoordinator.Evaluate(
                candidate,
                policy,
                driver.RunParallelLandingAcceptance);
            if (state.ForcedCohortPriority?.GoalId == goal.Id &&
                decision.Kind is ConductorParallelAcceptanceAttemptDecisionKind.Started or
                    ConductorParallelAcceptanceAttemptDecisionKind.Running or
                    ConductorParallelAcceptanceAttemptDecisionKind.Completed)
            {
                driver.ResetCohortFairness(goal.Id);
            }
            ReplayParallelAcceptanceLeaseReceipts(driver, decision.Attempt, changedGoalLines);

            switch (decision.Kind)
            {
                case ConductorParallelAcceptanceAttemptDecisionKind.Started:
                    if (documentationExclusionAdmission is not null)
                    {
                        RecordParallelAcceptanceProgress(documentationExclusionAdmission, changedGoalLines);
                    }

                    if (decision.Attempt.Outcome != ConductorParallelAcceptanceAttemptOutcome.Running)
                    {
                        var terminalDecision = driver.ParallelAcceptanceAttemptCoordinator.Evaluate(
                            candidate,
                            policy,
                            driver.RunParallelLandingAcceptance);
                        ReplayParallelAcceptanceLeaseReceipts(driver, terminalDecision.Attempt, changedGoalLines);
                        if (terminalDecision.Kind == ConductorParallelAcceptanceAttemptDecisionKind.Completed)
                        {
                            var terminalRun = terminalDecision.Run ?? ConductorParallelAcceptanceRunResult.Fault(
                                candidate,
                                new InvalidOperationException("Completed acceptance attempt had no run result."));
                            ReconcileParallelAcceptanceTerminalState(kernel, goal, terminalRun, terminalDecision.Attempt);
                            var terminalResult = CompleteParallelAcceptanceRun(
                                driver,
                                policy,
                                terminalRun,
                                terminalDecision.Attempt,
                                out var terminalEvidenceMutationLeaseHeld);
                            MarkParallelAcceptanceReconciledUnlessLeaseHeld(
                                driver,
                                terminalRun,
                                terminalEvidenceMutationLeaseHeld,
                                terminalDecision.Attempt);
                            oldestServedThisTick |= goal.Id == oldestWaiter?.Id;
                            RecordParallelAcceptanceFairnessGrant(goal.Id.Value, oldestWaiter?.Id.Value);
                            RecordParallelAcceptanceFairnessAdmission(
                                goal,
                                oldestWaiter,
                                stalledOldestBypass,
                                tick,
                                changedGoalLines);
                            state.Results[candidate.Goal.Id.Value] = new ParallelLandingOutcome(terminalResult, candidate.SlotIndex);
                            RecordParallelAcceptanceProgress(
                                AcceptanceLifecycleEventFormatter.Format(candidate.GoalPrefix, candidate.SlotIndex, AcceptanceRunDisposition(terminalRun), terminalDecision.Attempt.AttemptId, tick),
                                changedGoalLines);
                            break;
                        }

                        ReconcileParallelAcceptanceTerminalState(kernel, goal, terminalDecision.Attempt);
                        state.Results[candidate.Goal.Id.Value] = new ParallelLandingOutcome(
                            ParallelAcceptanceTerminal(driver, candidate, policy, terminalDecision.Attempt),
                            candidate.SlotIndex);
                        driver.ParallelAcceptanceAttemptCoordinator.MarkReconciled(terminalDecision.Attempt);
                        RecordParallelAcceptanceProgress(
                            AcceptanceLifecycleEventFormatter.Format(candidate.GoalPrefix, candidate.SlotIndex, AcceptanceAttemptOutcomeToken(terminalDecision.Attempt.Outcome), terminalDecision.Attempt.AttemptId, tick),
                            changedGoalLines);
                        break;
                    }

                    if (MarkParallelAcceptanceStarted(kernel, candidate.Goal, decision.Attempt, tick))
                    {
                        changedGoalIds.Add(candidate.Goal.Id);
                    }
                    ReserveParallelAcceptanceCandidate(
                        candidate,
                        decision.Attempt,
                        state.LiveAttempts,
                        state.ActiveCandidates,
                        state.ActiveAttemptIds,
                        state.ActiveAttemptSlotIndexes);
                    oldestServedThisTick |= goal.Id == oldestWaiter?.Id;
                    RecordParallelAcceptanceFairnessGrant(goal.Id.Value, oldestWaiter?.Id.Value);
                    RecordParallelAcceptanceFairnessAdmission(
                        goal,
                        oldestWaiter,
                        stalledOldestBypass,
                        tick,
                        changedGoalLines);
                    state.Results[candidate.Goal.Id.Value] = new ParallelLandingOutcome(
                        ParallelAcceptanceHeld(
                            candidate,
                            policy,
                            "acceptance verification running in background"),
                        candidate.SlotIndex);
                    RecordParallelAcceptanceProgress(
                        AcceptanceLifecycleEventFormatter.Format(candidate.GoalPrefix, candidate.SlotIndex, "started", decision.Attempt.AttemptId, tick),
                        changedGoalLines);
                    break;
                case ConductorParallelAcceptanceAttemptDecisionKind.Running:
                    if (MarkParallelAcceptanceStarted(kernel, candidate.Goal, decision.Attempt, tick))
                    {
                        changedGoalIds.Add(candidate.Goal.Id);
                    }
                    ReserveParallelAcceptanceCandidate(
                        candidate,
                        decision.Attempt,
                        state.LiveAttempts,
                        state.ActiveCandidates,
                        state.ActiveAttemptIds,
                        state.ActiveAttemptSlotIndexes);
                    oldestServedThisTick |= goal.Id == oldestWaiter?.Id;
                    RecordParallelAcceptanceFairnessGrant(goal.Id.Value, oldestWaiter?.Id.Value);
                    RecordParallelAcceptanceFairnessAdmission(
                        goal,
                        oldestWaiter,
                        stalledOldestBypass,
                        tick,
                        changedGoalLines);
                    state.Results[candidate.Goal.Id.Value] = new ParallelLandingOutcome(
                        ParallelAcceptanceHeld(
                            candidate,
                            policy,
                            "acceptance verification still running in background", ConductorHoldOwner.BackgroundAttempt),
                        candidate.SlotIndex);
                    RecordParallelAcceptanceProgress(
                        AcceptanceLifecycleEventFormatter.Format(candidate.GoalPrefix, candidate.SlotIndex, "running", decision.Attempt.AttemptId, tick),
                        changedGoalLines);
                    break;
                case ConductorParallelAcceptanceAttemptDecisionKind.Completed:
                    var run = decision.Run ?? ConductorParallelAcceptanceRunResult.Fault(
                        candidate,
                        new InvalidOperationException("Completed acceptance attempt had no run result."));
                    ReconcileParallelAcceptanceTerminalStateUnlessReused(kernel, goal, run, decision.Attempt);
                    var result = CompleteParallelAcceptanceRun(
                        driver,
                        policy,
                        run,
                        decision.Attempt,
                        out var evidenceMutationLeaseHeld);
                    MarkParallelAcceptanceReconciledUnlessLeaseHeld(
                        driver,
                        run,
                        evidenceMutationLeaseHeld,
                        decision.Attempt);
                    oldestServedThisTick |= goal.Id == oldestWaiter?.Id;
                    RecordParallelAcceptanceFairnessGrant(goal.Id.Value, oldestWaiter?.Id.Value);
                    RecordParallelAcceptanceFairnessAdmission(
                        goal,
                        oldestWaiter,
                        stalledOldestBypass,
                        tick,
                        changedGoalLines);
                    state.Results[candidate.Goal.Id.Value] = new ParallelLandingOutcome(result, candidate.SlotIndex);
                    RecordParallelAcceptanceProgress(
                        AcceptanceLifecycleEventFormatter.Format(candidate.GoalPrefix, candidate.SlotIndex, AcceptanceRunDisposition(run), decision.Attempt.AttemptId, tick),
                        changedGoalLines);
                    break;
                case ConductorParallelAcceptanceAttemptDecisionKind.TerminalWithoutRun:
                    ReconcileParallelAcceptanceTerminalState(kernel, goal, decision.Attempt);
                    state.Results[candidate.Goal.Id.Value] = new ParallelLandingOutcome(
                        ParallelAcceptanceTerminal(driver, candidate, policy, decision.Attempt),
                        candidate.SlotIndex);
                    driver.ParallelAcceptanceAttemptCoordinator.MarkReconciled(decision.Attempt);
                    RecordParallelAcceptanceProgress(
                        AcceptanceLifecycleEventFormatter.Format(candidate.GoalPrefix, candidate.SlotIndex, AcceptanceAttemptOutcomeToken(decision.Attempt.Outcome), decision.Attempt.AttemptId, tick),
                        changedGoalLines);
                    break;
            }
            state.AcceptanceCensus = CaptureLiveAcceptanceCensus(
                state.LiveAttempts,
                state.ActiveAttemptIds,
                state.ActiveCohortCapacity,
                tick,
                changedGoalLines,
                blockAdmissionOnFailure: true);
            }
            catch (AcceptanceArtifactWriterLeaseBusyException ex)
            {
                state.Results[goal.Id.Value] = new ParallelLandingOutcome(
                    ParallelAcceptanceHeld(
                        goal,
                        policy,
                        $"acceptance artifact writer busy; retry on next conduct tick. {ex.Message}"),
                    null);
                RecordParallelAcceptanceProgress(
                    $"ADMISSION tick={tick} result=deferred reason=acceptance-artifact-writer-busy goal={goal.Id.Value[..8]} detail={SanitizeReason(ex.Message)}",
                    changedGoalLines);
            }
            catch (Exception ex)
            {
                var reason = BoundSingleLine(
                    $"parallel acceptance fault isolated before goal advance: {ex.GetType().Name}: {ex.Message}");
                state.Results[goal.Id.Value] = new ParallelLandingOutcome(
                    EscalateParallelAcceptanceSafely(driver, goal, policy, reason),
                    null);
                RecordParallelAcceptanceProgress(
                    $"ADMISSION tick={tick} result=escalated reason=parallel-acceptance-fault goal={goal.Id.Value[..8]} detail={SanitizeReason(reason)}",
                    changedGoalLines);
            }
        }
    }

    private void SummarizeAcceptanceDeferrals(
        ParallelAcceptanceBatchState state,
        int tick,
        List<string> changedGoalLines)
    {
        if (state.DeferredByAdmission > 0)
        {
            RecordParallelAcceptanceProgress(
                $"ADMISSION tick={tick} result=deferred reason=parallel-acceptance-slot-cap cap={state.ConfiguredAcceptanceWidth} deferred={state.DeferredByAdmission}",
                changedGoalLines);
        }
    }
}
