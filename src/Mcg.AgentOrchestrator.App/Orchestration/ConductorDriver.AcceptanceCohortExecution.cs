using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorDriver
{
    private sealed class TransferableCohortWorkspace(AcceptanceCohortWorkspace workspace) : IDisposable
    {
        private AcceptanceCohortWorkspace? _workspace = workspace;

        internal AcceptanceCohortWorkspace Transfer() =>
            Interlocked.Exchange(ref _workspace, null)
            ?? throw new InvalidOperationException("Acceptance cohort workspace ownership was already transferred.");

        public void Dispose() => Interlocked.Exchange(ref _workspace, null)?.Dispose();
    }

    internal ConductorAcceptanceCohortRunResult RunAcceptanceCohort(
        ConductorAcceptanceCohortSelection selection,
        IReadOnlyList<Goal> orderedGoals,
        ConductorAutonomyPolicy policy,
        CancellationToken cancellationToken = default,
        Action? onGateAdmitted = null,
        bool runGateInBackground = false)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(orderedGoals);
        if (_runAcceptanceCohortOverride is not null)
        {
            return _runAcceptanceCohortOverride(selection, orderedGoals, policy);
        }
        var pairFingerprint = ConductorAcceptanceCohortSelector.PairFingerprint(
            selection.Members[0],
            selection.Members[1]);
        var memberPairKey = CohortGateMemberPairKey(selection);
        var memberGoalIds = selection.Members
            .Select(member => member.GoalId.Value)
            .ToHashSet(StringComparer.Ordinal);
        if (_groupedGateAttempts is null &&
            TryGetActiveCohortGateRun(memberGoalIds, out var currentRun))
        {
            return CohortInFlight(
                selection,
                orderedGoals,
                policy,
                currentRun);
        }

        // Drained ahead of the production-dependency check so a faulted background completion is always
        // reported as data; describing the fault needs the selection only, not the cohort dependencies.
        // A direct caller that did not come through RunAcceptanceCohortForTick still gets the held result
        // rather than the background thread's exception.
        if (TakeCohortGateFault(selection) is { } backgroundFault)
        {
            return CohortGateFaulted(orderedGoals, policy, backgroundFault);
        }

        if (_cohortKernel is null ||
            _cohortWorkspace is null ||
            _cohortAcceptanceVerifier is null ||
            _cohortAcceptanceStore is null)
        {
            throw new InvalidOperationException("Production acceptance cohort dependencies are unavailable.");
        }

        var goalsById = orderedGoals.ToDictionary(goal => goal.Id);
        var goals = selection.Members.Select(member =>
            goalsById.TryGetValue(member.GoalId, out var goal)
                ? goal
                : throw new InvalidOperationException($"Selected cohort goal {member.GoalId.Value} is not in the current Ready batch.")).ToArray();
        var bindings = selection.BindMembers();
        AcceptanceCohortWorkspace integration;
        try
        {
            integration = GoalWorktrees.CreateAcceptanceCohortWorkspace(
                _cohortWorkspace.ExecutionDirectory,
                selection.Members[0].MainRevision,
                bindings,
                _cohortCleanupHooks);
        }
        catch (AcceptanceCohortMaterializationException ex)
        {
            return MaterializationFallback(ex.Kind, ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return MaterializationFallback(
                AcceptanceCohortMaterializationFailureKind.WorkspaceFailure,
                ex.Message);
        }

        using var integrationScope = new TransferableCohortWorkspace(integration);
        string manifestIdentity;
        try
        {
            manifestIdentity = _cohortAcceptanceVerifier.ComputeEffectivePlanIdentity(
                integration.Path,
                bindings.SelectMany(member => member.LandingPaths).Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return MaterializationFallback(
                AcceptanceCohortMaterializationFailureKind.ManifestUnavailable,
                ex.Message);
        }
        var identity = AcceptanceCohortIdentity.Create(
            bindings,
            selection.Members[0].MainRevision,
            integration.TreeRevision,
            manifestIdentity);
        var receipt = _cohortAcceptanceStore.TryReadReceipt(identity.Value);
        if (runGateInBackground && _groupedGateAttempts is not null)
        {
            var recovered = RecoverGroupedGateAttempt("cohort", selection.Members,
                selection.Members[0].MainRevision, integration.TreeRevision,
                memberPairKey, receipt is not null);
            if (recovered.Running is not null)
                return CohortInFlight(selection, orderedGoals, policy, recovered.Running);
            if (recovered.DeadWithoutReceipt)
                receipt = _cohortAcceptanceStore.SaveGateReceipt(new AcceptanceCohortReceipt(
                    $"cohort-receipt-v2-{identity.Value[(AcceptanceCohortIdentity.Version.Length + 1)..]}",
                    identity, AcceptanceCohortGateOutcome.InfrastructureFailure, _utcNow(), 0,
                    ["infrastructure:grouped-gate-owner-dead"], null, [],
                    InfrastructureReasonCode: AcceptanceCohortInfrastructureReasonCodes.ExitCodeMissing,
                    InfrastructureDetail: "Grouped gate child exited without a receipt."));
        }
        if (receipt?.Invalidation is not null ||
            receipt?.Outcome == AcceptanceCohortGateOutcome.Invalidated)
        {
            _cohortAcceptanceStore.SuppressPair(
                ConductorAcceptanceCohortSelector.PairFingerprint(selection.Members[0], selection.Members[1]),
                identity.Value);
            return CohortOrdinaryFallback(
                receipt,
                "persisted cohort invalidation exhausted shared-receipt reuse; exact pair suppressed and routed to ordinary acceptance");
        }

        if (receipt?.Outcome == AcceptanceCohortGateOutcome.InfrastructureFailure)
        {
            receipt = _cohortAcceptanceStore.InvalidateLanding(
                identity.Value,
                AcceptanceCohortInvalidationReason.InfrastructureRetryExhausted,
                "The exact cohort identity already has an indeterminate infrastructure attempt; bounded cohort reuse is exhausted.");
            _cohortAcceptanceStore.SuppressPair(
                ConductorAcceptanceCohortSelector.PairFingerprint(selection.Members[0], selection.Members[1]),
                identity.Value);
            return CohortOrdinaryFallback(
                receipt,
                "persisted infrastructure attempt exhausted bounded cohort reuse; exact pair suppressed and routed to ordinary acceptance");
        }

        if (receipt is { Outcome: AcceptanceCohortGateOutcome.Passed } &&
            !receipt.HasAuthoritativeLandingEvidence)
        {
            receipt = _cohortAcceptanceStore.InvalidateLanding(
                identity.Value,
                AcceptanceCohortInvalidationReason.EvidenceUnavailable,
                "Cached passing receipt lacks successful exit or immutable content-bound coherent TRX evidence.");
            return CohortReprojection(
                receipt,
                "cached passing receipt lacks successful exit and extant coherent TRX evidence; both goals held for fresh Ready projection and pair selection");
        }

        if (receipt is null)
        {
            if (runGateInBackground)
            {
                if (_groupedGateAttempts is not null)
                {
                    var ownedRun = StartGroupedGateAttempt("cohort", selection.Members,
                        selection.Members[0].MainRevision, integration.TreeRevision,
                        manifestIdentity, identity.Value, memberPairKey, policy);
                    onGateAdmitted?.Invoke();
                    return CohortInFlight(selection, orderedGoals, policy, ownedRun);
                }
                var run = new CohortGateRun(
                    _utcNow(),
                    memberGoalIds,
                    pairFingerprint,
                    new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
                if (!TryRegisterCohortGateRun(memberPairKey, run, out var blockingRun))
                {
                    return CohortInFlight(
                        selection,
                        orderedGoals,
                        policy,
                        blockingRun!);
                }

                var ownedIntegration = integrationScope.Transfer();
                _startCohortGateBackground(() =>
                {
                    try
                    {
                        using (ownedIntegration)
                        {
                            _ = ExecuteAcceptanceCohortGate(
                                ownedIntegration,
                                identity,
                                bindings,
                                pairFingerprint,
                                cancellationToken,
                                onGateAdmitted);
                        }
                        run.Completion.SetResult();
                    }
                    catch (Exception ex)
                    {
                        run.Completion.SetException(ex);
                    }
                });
                return CohortInFlight(
                    selection,
                    orderedGoals,
                    policy,
                    run);
            }

            receipt = ExecuteAcceptanceCohortGate(
                integration,
                identity,
                bindings,
                pairFingerprint,
                cancellationToken,
                onGateAdmitted);
        }

        try
        {
            integration.AssertGoalBranchesUnchanged();
        }
        catch (InvalidOperationException ex)
        {
            receipt = _cohortAcceptanceStore.InvalidateLanding(
                identity.Value,
                AcceptanceCohortInvalidationReason.GoalBranchChanged,
                $"Post-gate goal branch changed: {BoundCohortDetail(ex.Message)}");
            return CohortReprojection(receipt, $"post-gate goal branch changed; fresh Ready projection required; detail={BoundCohortDetail(ex.Message)}");
        }
        if (receipt.Outcome is AcceptanceCohortGateOutcome.InfrastructureFailure or AcceptanceCohortGateOutcome.Invalidated)
        {
            return CohortReprojection(
                receipt,
                $"indeterminate cohort outcome={receipt.Outcome}; attribution=none; fresh Ready projection required");
        }
        if (receipt.Outcome == AcceptanceCohortGateOutcome.Failed &&
            receipt.Attribution != AcceptanceCohortAttributionOutcome.NotApplicable)
        {
            var innocentGoalId = receipt.Attribution switch
            {
                AcceptanceCohortAttributionOutcome.FirstMemberFailed => bindings[1].GoalId,
                AcceptanceCohortAttributionOutcome.SecondMemberFailed => bindings[0].GoalId,
                _ => (GoalId?)null
            };
            _cohortAcceptanceStore.EnsureAttributionSideEffects(
                identity.Value,
                receipt.Attribution,
                ConductorAcceptanceCohortSelector.PairFingerprint(selection.Members[0], selection.Members[1]),
                innocentGoalId);
        }

        if (receipt.Outcome == AcceptanceCohortGateOutcome.Passed)
        {
            for (var index = 0; index < goals.Length; index++)
            {
                if (ProjectGateReadyCandidate(goals[index], policy) is not GateReadyCandidateProjectionResult.Ready live ||
                    !live.Projection.Equals(selection.Members[index]))
                {
                    receipt = _cohortAcceptanceStore.InvalidateLanding(
                        identity.Value,
                        AcceptanceCohortInvalidationReason.BindingChanged,
                        $"Post-gate Ready projection changed for goal {goals[index].Id.Value}.");
                    return CohortReprojection(receipt, "post-gate binding changed; both goals held for fresh Ready projection and pair selection");
                }
            }
            try
            {
                integration.AssertGoalBranchesUnchanged();
            }
            catch (InvalidOperationException ex)
            {
                receipt = _cohortAcceptanceStore.InvalidateLanding(
                    identity.Value,
                    AcceptanceCohortInvalidationReason.GoalBranchChanged,
                    $"Post-gate goal branch changed: {BoundCohortDetail(ex.Message)}");
                return CohortReprojection(receipt, $"post-gate goal branch changed; fresh Ready projection required; detail={BoundCohortDetail(ex.Message)}");
            }
            var landing = LandingExecutor.ExecuteCohort(
                _cohortKernel,
                goals,
                _cohortWorkspace,
                receipt,
                integration.CommitRevision,
                _cohortAcceptanceStore,
                policy,
                _cohortEventWriter,
                LandingMutationBlocker);
            if (!landing.MainAdvanced)
            {
                if (landing.Outcome == AcceptanceCohortLandingOutcome.StateInvalidated)
                {
                    receipt = _cohortAcceptanceStore.InvalidateLanding(
                        identity.Value,
                        AcceptanceCohortInvalidationReason.LandingStateChanged,
                        landing.Message);
                    return CohortReprojection(receipt, $"{landing.Message} fresh Ready projection required");
                }
                return CohortHeld(goals, policy, receipt, landing.Message);
            }

            foreach (var goal in goals)
            {
                var landingResult = new LandingResult(
                    goal.Id.Value,
                    goal.Id.Value[..8],
                    new LandingDecision.Promote(),
                    $"cohort/{identity.Value}",
                    true,
                    landing.Message,
                    landing.CommitRevision,
                    selection.Members.SelectMany(member => member.LandingPaths).ToArray());
                SuccessfulLandingSink?.Invoke(new ConductorLandingReceipt(
                    goal.Id.Value,
                    landingResult.ChangedFiles ?? [],
                    landingResult.MergeCommitSha));
                _afterSuccessfulLanding(goal, landingResult);
            }
            _cohortAcceptanceStore.CompleteLandingEffects(identity.Value, receipt.ReceiptId);
            return new ConductorAcceptanceCohortRunResult(
                receipt,
                goals.ToDictionary(
                    goal => goal.Id.Value,
                    goal => MakeResult(
                        goal.Id.Value,
                        goal.Id.Value[..8],
                        policy,
                        new ConductorAdvanceOutcome.Executed(
                            GoalLifecycleState.Verified,
                            $"Landed by shared cohort receipt {receipt.ReceiptId}.")),
                    StringComparer.Ordinal),
                $"outcome=passed receipt={receipt.ReceiptId} tree={identity.CombinedTreeRevision} gateMs={receipt.GateElapsedMilliseconds} " +
                $"gateExit={receipt.GateExitCode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none"} " +
                $"trxCount={receipt.GateTestResultPaths.Count}");
        }

        var shouldUseOrdinaryFallback = receipt.Outcome == AcceptanceCohortGateOutcome.Failed && receipt.Attribution == AcceptanceCohortAttributionOutcome.NotApplicable;
        return shouldUseOrdinaryFallback
            ? CohortOrdinaryFallback(receipt, "deterministic cohort content failure has no member attribution; members remain eligible for ordinary acceptance")
            : ApplyCohortAttributionFailureVerdicts(CohortHeld(goals, policy, receipt, receipt.Outcome == AcceptanceCohortGateOutcome.Failed
                ? $"deterministic RED; attribution={receipt.Attribution}"
                : $"cohort infrastructure outcome={receipt.Outcome}; no attribution or landing"), goals, receipt, policy);

        ConductorAcceptanceCohortRunResult MaterializationFallback(
            AcceptanceCohortMaterializationFailureKind outcome,
            string detail)
        {
            var failure = _cohortAcceptanceStore.SaveMaterializationFailure(
                bindings,
                selection.Members[0].MainRevision,
                outcome,
                BoundCohortDetail(detail));
            return new ConductorAcceptanceCohortRunResult(
                Receipt: null,
                new Dictionary<string, ConductorAdvanceResult>(StringComparer.Ordinal),
                $"outcome=materialization-failure kind={failure.Outcome} attempt={failure.AttemptId} fallback=ordinary detail={BoundCohortDetail(failure.Detail)}");
        }

        ConductorAcceptanceCohortRunResult CohortReprojection(
            AcceptanceCohortReceipt diagnosticReceipt,
            string detail) => CohortHeld(goals, policy, diagnosticReceipt, detail);

        ConductorAcceptanceCohortRunResult CohortOrdinaryFallback(
            AcceptanceCohortReceipt diagnosticReceipt,
            string detail) => new(
                diagnosticReceipt,
                new Dictionary<string, ConductorAdvanceResult>(StringComparer.Ordinal),
                $"outcome={diagnosticReceipt.Outcome} receipt={diagnosticReceipt.ReceiptId} " +
                $"invalidation={diagnosticReceipt.Invalidation?.Reason.ToString() ?? "legacy"} fallback=ordinary " +
                $"detail={BoundCohortDetail(detail)}");
    }

    private AcceptanceCohortReceipt ExecuteAcceptanceCohortGate(
        AcceptanceCohortWorkspace integration,
        AcceptanceCohortIdentity identity,
        IReadOnlyList<AcceptanceCohortMemberBinding> bindings,
        string pairFingerprint,
        CancellationToken cancellationToken,
        Action? onGateAdmitted)
    {
        var verifier = _cohortAcceptanceVerifier
            ?? throw new InvalidOperationException("Production acceptance cohort verifier is unavailable.");
        var store = _cohortAcceptanceStore
            ?? throw new InvalidOperationException("Production acceptance cohort store is unavailable.");
        var workspace = _cohortWorkspace ?? throw new InvalidOperationException("Production acceptance cohort workspace is unavailable.");
        if (RunAcceptanceCohortSourceSizePreflight(integration.Path, identity, store) is { } sourceSizeReceipt) return sourceSizeReceipt;
        var gateProgressEventWriter = new ConductEventLogWriter(
            Path.Combine(workspace.ExecutionDirectory, ".orchestrator", "logs", ConductEventLogWriter.CurrentFileName));
        var executionOptions = new AcceptanceRunExecutionOptions(
            ProgressSink: progress => AppendCohortGateProgressEvents(gateProgressEventWriter, identity, bindings, progress),
            OwnerProtectedCohortMembers: bindings.Select(binding =>
                new AcceptanceOwnerProtectedCohortMember(binding.GoalId, binding.CandidateRevision)).ToArray(),
            GateRunIdentity: CohortGateRunIdentity(bindings.Select(binding => binding.GoalId.Value)));

        var gateClock = Stopwatch.StartNew();
        AcceptanceCohortGateClassification? classification = null;
        IReadOnlyList<string> failedChecks = [];
        IReadOnlyList<string> cohortFailingTests = [];
        IReadOnlyList<AcceptanceCheckResult> cohortFailedChecks = [];
        int? gateExitCode = null;
        IReadOnlyList<string> gateTestResultPaths = [];
        DotnetBuildEnvironmentLease? stableSlotLease = null;
        AcceptanceCohortReceipt? receipt = null;
        var gateExecutionComplete = false;
        try
        {
            stableSlotLease = _parallelAcceptanceAttemptCoordinator.AcquireCohortStableSlotLease(
                identity.Value,
                cancellationToken);
            onGateAdmitted?.Invoke();
            var verification = AcceptanceExecutionRunner.RunAttempt(
                verifier,
                integration.Path,
                goalId: null,
                bindings.SelectMany(member => member.LandingPaths).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                stableSlotLease.Environment.BuildPermitIndex,
                stableSlotLease,
                cancellationToken,
                executionOptions);
            gateExitCode = verification.ExitCode;
            var classifiedVerification = ClassifyCohortVerificationResultWithPaths(verification);
            classification = classifiedVerification.Classification;
            gateTestResultPaths = classifiedVerification.NormalizedTestResultPaths;
            failedChecks = verification.Checks?
                .Where(check => !check.Passed && !check.Advisory)
                .Select(check => check.Name)
                .ToArray() ?? [];
            cohortFailingTests = CohortFailingTestIdentities(verification);
            cohortFailedChecks = verification.Checks?.Where(check => !check.Passed && !check.Advisory).ToArray() ?? [];
            gateExecutionComplete = true;
            gateClock.Stop();
            receipt = store.SaveGateReceipt(new AcceptanceCohortReceipt(
                $"cohort-receipt-v2-{identity.Value[(AcceptanceCohortIdentity.Version.Length + 1)..]}",
                identity,
                classification.Outcome,
                _utcNow(),
                checked((long)gateClock.Elapsed.TotalMilliseconds),
                failedChecks,
                GateExitCode: gateExitCode,
                GateTestResultPaths: gateTestResultPaths,
                ValidForLanding: classification.Outcome == AcceptanceCohortGateOutcome.Passed,
                InfrastructureReasonCode: classification.InfrastructureReasonCode,
                InfrastructureDetail: classification.InfrastructureDetail));
        }
        catch (Exception ex) when (!gateExecutionComplete && ex is (
            AcceptanceInfrastructureDeferredException or DotnetBuildSlotsBusyException or
            BuildLockBlockedException or OperationCanceledException or IOException or
            InvalidDataException))
        {
            classification = ClassifyCohortInfrastructureException(ex);
            if (ex is AcceptanceInfrastructureDeferredException deferred)
            {
                gateExitCode = deferred.ExitCode;
            }
            failedChecks = [$"infrastructure:{ex.GetType().Name}:{BoundCohortDetail(ex.Message)}"];
        }
        finally
        {
            stableSlotLease?.Dispose();
        }

        if (receipt is null)
        {
            gateClock.Stop();
            if (classification is null)
            {
                throw new InvalidOperationException(
                    "Acceptance cohort gate ended without a structured classification.");
            }
            receipt = store.SaveGateReceipt(new AcceptanceCohortReceipt(
                $"cohort-receipt-v2-{identity.Value[(AcceptanceCohortIdentity.Version.Length + 1)..]}",
                identity,
                classification.Outcome,
                _utcNow(),
                checked((long)gateClock.Elapsed.TotalMilliseconds),
                failedChecks,
                GateExitCode: gateExitCode,
                GateTestResultPaths: gateTestResultPaths,
                ValidForLanding: false,
                InfrastructureReasonCode: classification.InfrastructureReasonCode,
                InfrastructureDetail: classification.InfrastructureDetail));
        }

        if (receipt.Outcome == AcceptanceCohortGateOutcome.Failed &&
            receipt.Attribution == AcceptanceCohortAttributionOutcome.NotApplicable)
        {
            receipt = AttributeFailedCohort(store, identity, bindings, pairFingerprint,
                cohortFailingTests, cohortFailedChecks, gateProgressEventWriter, cancellationToken);
        }

        return receipt;
    }

    private AcceptanceCohortPartitionReceipt RunCohortPartition(
        AcceptanceCohortMemberBinding member,
        int memberOrdinal,
        AcceptanceCohortIdentity identity,
        ConductEventLogWriter gateProgressEventWriter,
        CancellationToken cancellationToken)
    {
        var workspace = _cohortWorkspace
            ?? throw new InvalidOperationException("Production acceptance cohort workspace is unavailable.");
        var verifier = _cohortAcceptanceVerifier
            ?? throw new InvalidOperationException("Production acceptance cohort verifier is unavailable.");
        var clock = Stopwatch.StartNew();
        string? treeRevision = null;
        var partitionManifest = identity.ManifestIdentity;
        IReadOnlyList<string> testResultPaths = [];
        IReadOnlyList<string> failingTestIdentities = [];
        IReadOnlyList<string> failedChecks = [];
        AcceptanceCohortGateOutcome outcome;
        try
        {
            using var partition = GoalWorktrees.CreateAcceptancePartitionWorkspace(
                workspace.ExecutionDirectory,
                identity.ObservedMainRevision,
                member,
                _cohortCleanupHooks);
            treeRevision = partition.TreeRevision;
            partitionManifest = verifier.ComputeEffectivePlanIdentity(
                partition.Path,
                member.LandingPaths);
            var result = RunCohortPartitionAttempt(
                verifier, partition.Path, member, identity, gateProgressEventWriter, cancellationToken);
            testResultPaths = NormalizeCohortTestResultPaths(result.TestResultPaths);
            failingTestIdentities = CohortFailingTestIdentities(result);
            failedChecks = result.Checks?
                .Where(check => !check.Advisory && !check.Passed)
                .Select(check => check.Name).ToArray() ?? [];
            partition.AssertGoalBranchesUnchanged();
            outcome = ClassifyCohortVerification(result);
        }
        catch (Exception ex) when (ex is AcceptanceInfrastructureDeferredException or
            DotnetBuildSlotsBusyException or BuildLockBlockedException or
            OperationCanceledException or IOException or InvalidDataException or InvalidOperationException)
        {
            outcome = AcceptanceCohortGateOutcome.InfrastructureFailure;
        }
        clock.Stop();
        var receiptId = CreateCohortPartitionReceiptId(
            member,
            identity.ObservedMainRevision,
            treeRevision,
            partitionManifest);
        return new AcceptanceCohortPartitionReceipt(
            receiptId,
            member.GoalId,
            memberOrdinal,
            member.CandidateRevision,
            identity.ObservedMainRevision,
            treeRevision,
            partitionManifest,
            outcome,
            checked((long)clock.Elapsed.TotalMilliseconds),
            testResultPaths) { FailingTestIdentities = failingTestIdentities, FailedChecks = failedChecks };
    }

    private static string CohortGateMemberPairKey(ConductorAcceptanceCohortSelection selection) =>
        CohortGateRunIdentity(selection.Members.Select(member => member.GoalId.Value));

    private ConductorAcceptanceCohortRunResult CohortInFlight(
        ConductorAcceptanceCohortSelection selection,
        IReadOnlyList<Goal> orderedGoals,
        ConductorAutonomyPolicy policy,
        CohortGateRun run)
    {
        var selectedIds = selection.Members.Select(member => member.GoalId).ToHashSet();
        var goals = orderedGoals.Where(goal => selectedIds.Contains(goal.Id)).ToArray();
        var detail = FormatCohortGateInFlightDetail(run, _utcNow());
        return new ConductorAcceptanceCohortRunResult(
            Receipt: null,
            goals.ToDictionary(
                goal => goal.Id.Value,
                goal => MakeResult(
                    goal.Id.Value,
                    goal.Id.Value[..8],
                    policy,
                    new ConductorAdvanceOutcome.Held(
                        GoalLifecycleState.Verified,
                        $"Acceptance cohort gate is running in the background: {detail}")),
                StringComparer.Ordinal),
            detail);
    }

    private static string CreateCohortPartitionReceiptId(
        AcceptanceCohortMemberBinding member,
        string mainRevision,
        string? treeRevision,
        string manifestIdentity)
    {
        var payload = string.Join('\n',
            "cohort-partition-v1",
            member.GoalId.Value,
            member.CandidateRevision,
            mainRevision,
            treeRevision ?? "unavailable",
            manifestIdentity);
        return $"cohort-partition-v1-{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(payload)))}";
    }

    private static ConductorAcceptanceCohortRunResult CohortHeld(
        IReadOnlyList<Goal> goals,
        ConductorAutonomyPolicy policy,
        AcceptanceCohortReceipt receipt,
        string detail) => new(
            receipt,
            goals.ToDictionary(
                goal => goal.Id.Value,
                goal => MakeResult(
                    goal.Id.Value,
                    goal.Id.Value[..8],
                    policy,
                    new ConductorAdvanceOutcome.Held(
                        GoalLifecycleState.Verified,
                        $"Acceptance cohort {receipt.Identity.Value}: {detail}")),
                StringComparer.Ordinal),
            $"outcome={receipt.Outcome} receipt={receipt.ReceiptId} attribution={receipt.Attribution} detail={BoundCohortDetail(detail)}");

    private static string BoundCohortDetail(string value)
    {
        var singleLine = value.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');
        return singleLine.Length <= 256 ? singleLine : singleLine[..256];
    }
}
