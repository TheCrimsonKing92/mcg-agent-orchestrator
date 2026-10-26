using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal sealed class GoalReplacementRetryableTimeoutException(
    Guid requestId,
    string predecessorGoalId,
    string ownerGoalId,
    long claimVersion)
    : InvalidOperationException(
        $"GOAL_REPLACE_RETRYABLE_TIMEOUT requestId={requestId:D} predecessor={predecessorGoalId} owner={ownerGoalId} claimVersion={claimVersion} reason=competing-replacement-timeout")
{
    public Guid RequestId { get; } = requestId;
    public string PredecessorGoalId { get; } = predecessorGoalId;
    public string OwnerGoalId { get; } = ownerGoalId;
    public long ClaimVersion { get; } = claimVersion;
}

internal static class CliGoalReplacementExecution
{
    internal static Action? AfterGoalReplacementLeaseAcquisitionFailed { get; set; }
    internal static Action? BeforeGoalReplacementFailureClaimObservation { get; set; }

    internal static bool ExecuteGoalReplacementOutsideTransaction(
        IReadOnlyList<string> args,
        ITransactionalOrchestratorStateRepository stateRepository,
        OrchestratorWorkspace workspace,
        ref IReadOnlyList<AgentDefinition> agents,
        IModelProviderRegistry providers,
        ref WorkerProfileCatalog workerProfiles,
        ref Goal? currentGoal,
        IOperatorChannel? channel = null)
    {
        if (stateRepository is not IOrchestratorStateOutboxRepository outboxRepository)
            throw new InvalidOperationException("goal-replace requires a state repository with durable outbox support.");

        var kernel = stateRepository.LoadAsync().GetAwaiter().GetResult();
        currentGoal = CliPersistentStateRunner.ResolveCurrentGoal(kernel, currentGoal?.Id.Value);
        var claimStore = new SourceBacklogClaimStore(workspace.SqliteStatePath);
        if (TryReplayCommittedGoalReplacement(
                args,
                kernel,
                claimStore,
                ref currentGoal,
                out var replayResult))
        {
            return replayResult;
        }
        var predecessorForLease = OrchestratorEntityResolver.ResolveGoal(
            kernel,
            currentGoal,
            args.Count > 1 ? args[1] : null);
        var deferredEventWriter = new DeferredGoalLifecycleEventWriter();
        var deferredCollaborationWriter = new DeferredGoalCreationCollaborationWriter(
            CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory));
        string? committedGoalId = null;

        void FinalizeGoalReplacement(Goal preparedGoal, GoalReplacementCommand command)
        {
            GoalRefinementWorkCoordinator.RecordPending(kernel, preparedGoal.Id);
            var preparedSnapshot = kernel.ExportGoalSnapshot(preparedGoal.Id);
            var fingerprintInputs = BuildGoalReplacementFingerprint(command, preparedSnapshot);
            var fingerprint = fingerprintInputs.Fingerprint;
            GoalCreationSideEffectDelivery.BeforeStateCommit?.Invoke(preparedGoal.Id);
            GoalReplacementTransferLeaseAcquisition replacementLeaseAcquisition;
            try
            {
                replacementLeaseAcquisition = AcquireGoalReplacementTransferLease(
                    workspace.SqliteStatePath,
                    command.PredecessorGoalId.Value,
                    () => ResolveObservedClaim(
                        claimStore,
                        stateRepository,
                        preparedSnapshot.SourceBacklogItemId!));
            }
            catch (LegacySourceBacklogOwnerAmbiguousException ambiguous)
            {
                throw RecordLegacyOwnerAmbiguousFailure(
                    claimStore,
                    command,
                    preparedSnapshot,
                    fingerprint,
                    CreateUnavailableGoalReplacementFacts(predecessorForLease),
                    ambiguous);
            }
            if (replacementLeaseAcquisition.Kind == GoalReplacementTransferLeaseAcquisitionKind.ProtectedEvidenceMutationOwner)
            {
                var failureCode = replacementLeaseAcquisition.FailureCode ?? "evidence-mutation-active";
                var observedClaim = replacementLeaseAcquisition.ObservedClaim;
                if (observedClaim is null)
                {
                    TryRecordGoalReplacementFailure(
                        claimStore,
                        command,
                        preparedSnapshot,
                        fingerprint,
                        GoalReplacementOutcome.PersistenceFailed,
                        observedOwner: null,
                        observedVersion: null,
                        "claim-observation-unavailable",
                        CreateUnavailableGoalReplacementFacts(predecessorForLease));
                    throw new InvalidOperationException(
                        $"GOAL_REPLACE_PERSISTENCE_FAILED requestId={command.RequestId:D} predecessor={command.PredecessorGoalId.Value} owner=unknown claimVersion=unknown reason=claim-observation-unavailable");
                }
                if (!string.Equals(observedClaim.OwnerGoalId, command.ExpectedOwnerGoalId, StringComparison.Ordinal) ||
                    observedClaim.Version != command.ExpectedClaimVersion)
                {
                    TryRecordGoalReplacementFailure(
                        claimStore,
                        command,
                        preparedSnapshot,
                        fingerprint,
                        GoalReplacementOutcome.CurrentOwnerConflict,
                        observedClaim.OwnerGoalId,
                        observedClaim.Version,
                        "current-owner-conflict",
                        CreateUnavailableGoalReplacementFacts(predecessorForLease));
                    throw new SourceBacklogClaimConflictException(
                        observedClaim.BacklogItemId,
                        command.PredecessorGoalId.Value,
                        observedClaim.OwnerGoalId,
                        observedClaim.Version);
                }
                TryRecordGoalReplacementFailure(
                    claimStore,
                    command,
                    preparedSnapshot,
                    fingerprint,
                    GoalReplacementOutcome.ProtectedOwner,
                    observedClaim.OwnerGoalId,
                    observedClaim.Version,
                    failureCode,
                    CreateUnavailableGoalReplacementFacts(predecessorForLease));
                throw new InvalidOperationException(
                    $"GOAL_REPLACE_PROTECTED_OWNER predecessor={command.PredecessorGoalId.Value} reason={failureCode}");
            }
            if (replacementLeaseAcquisition.Kind == GoalReplacementTransferLeaseAcquisitionKind.CompetingReplacementTimedOut)
            {
                ThrowGoalReplacementTransferLeaseTimeout(
                    command,
                    replacementLeaseAcquisition,
                    (outcome, observedOwner, observedVersion, failureCode) =>
                        TryRecordGoalReplacementFailure(
                            claimStore,
                            command,
                            preparedSnapshot,
                            fingerprint,
                            outcome,
                            observedOwner,
                            observedVersion,
                            failureCode,
                            CreateUnavailableGoalReplacementFacts(predecessorForLease)));
            }
            using var replacementLeaseScope = replacementLeaseAcquisition.Lease
                ?? throw new InvalidOperationException(
                    "GOAL_REPLACE_STATE_INVALID reason=acquired-replacement-lease-missing");
            SourceBacklogClaimSnapshot? observedClaimUnderLease;
            try
            {
                observedClaimUnderLease = ResolveObservedClaim(
                    claimStore,
                    stateRepository,
                    preparedSnapshot.SourceBacklogItemId!);
            }
            catch (LegacySourceBacklogOwnerAmbiguousException ambiguous)
            {
                throw RecordLegacyOwnerAmbiguousFailure(
                    claimStore,
                    command,
                    preparedSnapshot,
                    fingerprint,
                    CreateUnavailableGoalReplacementFacts(predecessorForLease),
                    ambiguous);
            }
            if (observedClaimUnderLease is null)
            {
                TryRecordGoalReplacementFailure(
                    claimStore,
                    command,
                    preparedSnapshot,
                    fingerprint,
                    GoalReplacementOutcome.PersistenceFailed,
                    observedOwner: null,
                    observedVersion: null,
                    "claim-observation-unavailable",
                    CreateUnavailableGoalReplacementFacts(predecessorForLease));
                throw new InvalidOperationException(
                    $"GOAL_REPLACE_PERSISTENCE_FAILED requestId={command.RequestId:D} predecessor={command.PredecessorGoalId.Value} owner=unknown claimVersion=unknown reason=claim-observation-unavailable");
            }
            if (!string.Equals(observedClaimUnderLease.OwnerGoalId, command.ExpectedOwnerGoalId, StringComparison.Ordinal) ||
                observedClaimUnderLease.Version != command.ExpectedClaimVersion)
            {
                TryRecordGoalReplacementFailure(
                    claimStore,
                    command,
                    preparedSnapshot,
                    fingerprint,
                    GoalReplacementOutcome.CurrentOwnerConflict,
                    observedClaimUnderLease.OwnerGoalId,
                    observedClaimUnderLease.Version,
                    "current-owner-conflict",
                    CreateUnavailableGoalReplacementFacts(predecessorForLease));
                throw new SourceBacklogClaimConflictException(
                    observedClaimUnderLease.BacklogItemId,
                    command.PredecessorGoalId.Value,
                    observedClaimUnderLease.OwnerGoalId,
                    observedClaimUnderLease.Version);
            }
            var eligibilityKernel = stateRepository.LoadAsync().GetAwaiter().GetResult();
            var eligibilityPredecessor = eligibilityKernel.Goals.SingleOrDefault(goal => goal.Id == command.PredecessorGoalId)
                ?? throw new InvalidOperationException(
                    $"GOAL_REPLACE_VALIDATION_REJECTED predecessor={command.PredecessorGoalId.Value} reason=predecessor-missing");
            var authorizedFacts = GoalReplacementEvidence.Capture(workspace, eligibilityPredecessor);
            var deliveryMessage = GoalCreationSideEffectDelivery.CreateMessage(
                preparedGoal.Id,
                deferredCollaborationWriter.SnapshotEffects(),
                deferredEventWriter.SnapshotEffects());
            var refinementMessage = GoalRefinementWorkCoordinator.CreateMessage(preparedGoal.Id);

            GoalReplacementReceipt receipt;
            try
            {
                // The committed replacement lease serializes every supported mutation of the
                // predecessor's eligibility evidence. Complete the Git/journal validation before
                // opening the state write transaction so a competing replacement can continue
                // polling the lease instead of escaping with SQLITE_BUSY while Git is running.
                var transferAuthority = GoalReplacementEvidence.CreateTransferAuthority(
                    workspace,
                    eligibilityPredecessor,
                    observedClaimUnderLease.BacklogItemId,
                    observedClaimUnderLease.Version,
                    authorizedFacts);
                var validatedFacts = transferAuthority.RevalidateForTransfer();

                using var backlogLease = new BacklogStore(workspace.BacklogStorePath)
                    .TryAcquireOpenItemLease(preparedSnapshot.SourceBacklogItemId!, out var observedBacklogStatus);
                if (backlogLease is null)
                {
                    throw new GoalReplacementValidationException(
                        $"GOAL_REPLACE_VALIDATION_REJECTED backlogItem={preparedSnapshot.SourceBacklogItemId} status={observedBacklogStatus?.ToString() ?? "missing"} reason=source-backlog-not-open",
                        "source-backlog-not-open");
                }

                receipt = outboxRepository.TransactWithOutboxAsync(
                        (currentKernel, _) =>
                        {
                            var existingAudit = claimStore.FindAuditInCurrentTransaction(command.RequestId);
                            if (existingAudit is not null)
                            {
                                if (!string.Equals(existingAudit.Fingerprint, fingerprint, StringComparison.Ordinal))
                                    throw new GoalReplacementIdempotencyConflictException(command.RequestId);
                                if (existingAudit.Outcome != GoalReplacementOutcome.Succeeded ||
                                    existingAudit.SuccessorGoalId is null)
                                {
                                    return Task.FromResult((
                                        ShouldSave: false,
                                        Result: new GoalReplacementReceipt(
                                            existingAudit.Outcome,
                                            command.RequestId,
                                            existingAudit.BacklogItemId,
                                            existingAudit.PredecessorGoalId,
                                            existingAudit.SuccessorGoalId,
                                            existingAudit.ObservedOwnerGoalId,
                                            existingAudit.ObservedClaimVersion,
                                            existingAudit.FailureCode ?? "replayed-failure"),
                                        OutboxMessages: (IReadOnlyList<OrchestratorStateOutboxMessage>)[]));
                                }

                                var replayClaim = claimStore.ResolveOrMaterializeClaim(currentKernel, existingAudit.BacklogItemId);
                                return Task.FromResult((
                                    ShouldSave: false,
                                    Result: new GoalReplacementReceipt(
                                        GoalReplacementOutcome.Replayed,
                                        command.RequestId,
                                        existingAudit.BacklogItemId,
                                        existingAudit.PredecessorGoalId,
                                        existingAudit.SuccessorGoalId,
                                        replayClaim.OwnerGoalId,
                                        replayClaim.Version,
                                        "idempotent-replay"),
                                    OutboxMessages: (IReadOnlyList<OrchestratorStateOutboxMessage>)[]));
                            }

                            var predecessor = currentKernel.Goals.FirstOrDefault(goal => goal.Id == command.PredecessorGoalId)
                                ?? throw new GoalReplacementValidationException(
                                    $"GOAL_REPLACE_VALIDATION_REJECTED predecessor={command.PredecessorGoalId.Value} reason=predecessor-missing",
                                    "predecessor-missing");
                            var backlogItemId = predecessor.SourceBacklogItemId
                                ?? throw new GoalReplacementValidationException(
                                    $"GOAL_REPLACE_VALIDATION_REJECTED predecessor={predecessor.Id.Value} reason=predecessor-has-no-source-backlog",
                                    "predecessor-has-no-source-backlog");
                            var claim = claimStore.ResolveOrMaterializeClaim(currentKernel, backlogItemId);
                            if (!string.Equals(claim.OwnerGoalId, predecessor.Id.Value, StringComparison.Ordinal))
                            {
                                throw new SourceBacklogClaimConflictException(
                                    backlogItemId,
                                    predecessor.Id.Value,
                                    claim.OwnerGoalId,
                                    claim.Version);
                            }
                            GoalCreationPreconditionValidator.Validate(
                                currentKernel,
                                preparedSnapshot,
                                workspace,
                                predecessor.Id.Value);

                            if (!GoalReplacementEvidence.MatchesStateFacts(predecessor, validatedFacts))
                            {
                                throw new GoalReplacementValidationException(
                                    $"GOAL_REPLACE_VALIDATION_REJECTED predecessor={predecessor.Id.Value} reason=eligibility-evidence-changed",
                                    "eligibility-evidence-changed");
                            }
                            var eligibility = SourceBacklogClaimEligibility.Evaluate(command.Disposition, validatedFacts);
                            if (eligibility != GoalReplacementOutcome.Succeeded)
                            {
                                var rejectedAudit = new GoalReplacementAuditSnapshot(
                                    command.RequestId,
                                    fingerprint,
                                    eligibility,
                                    backlogItemId,
                                    predecessor.Id.Value,
                                    null,
                                    command.Disposition,
                                    command.Reason,
                                    predecessor.Status,
                                    null,
                                    claim.Coverage,
                                    "operator",
                                    "cli",
                                    "local-process",
                                    DateTimeOffset.UtcNow,
                                    predecessor.Id.Value,
                                    claim.Version,
                                    claim.OwnerGoalId,
                                    claim.Version,
                                    validatedFacts,
                                    eligibility == GoalReplacementOutcome.ProtectedOwner
                                        ? validatedFacts.IsGitEvidenceAvailable
                                            ? "protected-owner"
                                            : "git-evidence-unavailable"
                                        : "ineligible-disposition",
                                    fingerprintInputs.ObjectiveHash,
                                    fingerprintInputs.OrderedRoles,
                                    fingerprintInputs.AssignedAgents);
                                claimStore.CommitRejectedAttempt(rejectedAudit);
                                return Task.FromResult((
                                    ShouldSave: false,
                                    Result: new GoalReplacementReceipt(
                                        eligibility,
                                        command.RequestId,
                                        backlogItemId,
                                        predecessor.Id.Value,
                                        null,
                                        claim.OwnerGoalId,
                                        claim.Version,
                                        rejectedAudit.FailureCode!),
                                    OutboxMessages: (IReadOnlyList<OrchestratorStateOutboxMessage>)[]));
                            }

                            var now = DateTimeOffset.UtcNow;
                            var audit = new GoalReplacementAuditSnapshot(
                                command.RequestId,
                                fingerprint,
                                GoalReplacementOutcome.Succeeded,
                                backlogItemId,
                                predecessor.Id.Value,
                                preparedSnapshot.Id,
                                command.Disposition,
                                command.Reason,
                                predecessor.Status,
                                preparedSnapshot.Status,
                                claim.Coverage,
                                "operator",
                                "cli",
                                "local-process",
                                now,
                                predecessor.Id.Value,
                                claim.Version,
                                predecessor.Id.Value,
                                claim.Version,
                                validatedFacts,
                                FailureCode: null,
                                ObjectiveHash: fingerprintInputs.ObjectiveHash,
                                OrderedRoles: fingerprintInputs.OrderedRoles,
                                AssignedAgents: fingerprintInputs.AssignedAgents);

                            var transferred = claimStore.CommitReplacement(
                                audit,
                                transferAuthority,
                                validatedFacts,
                                replacementLeaseAcquisition.LeaseOwner
                                    ?? throw new InvalidOperationException(
                                        "GOAL_REPLACE_STATE_INVALID reason=acquired-replacement-lease-owner-missing"));
                            currentKernel.ReplaceGoalWithSnapshot(preparedSnapshot);
                            currentKernel.RecordGoalPolicyDecision(
                                predecessor.Id,
                                $"Source backlog claim replaced by goal {preparedSnapshot.Id}; request={command.RequestId:D} disposition={command.Disposition}.");
                            currentKernel.RecordGoalPolicyDecision(
                                new GoalId(preparedSnapshot.Id),
                                $"Source backlog claim inherited from goal {predecessor.Id.Value}; request={command.RequestId:D} disposition={command.Disposition}.");
                            return Task.FromResult((
                                ShouldSave: true,
                                Result: new GoalReplacementReceipt(
                                    GoalReplacementOutcome.Succeeded,
                                    command.RequestId,
                                    backlogItemId,
                                    predecessor.Id.Value,
                                    preparedSnapshot.Id,
                                    transferred.OwnerGoalId,
                                    transferred.Version,
                                    "replaced"),
                                OutboxMessages: (IReadOnlyList<OrchestratorStateOutboxMessage>)[deliveryMessage, refinementMessage]));
                        })
                    .GetAwaiter()
                    .GetResult();
            }
            catch (SourceBacklogClaimConflictException conflict)
            {
                TryRecordGoalReplacementFailure(
                    claimStore,
                    command,
                    preparedSnapshot,
                    fingerprint,
                    GoalReplacementOutcome.CurrentOwnerConflict,
                    conflict.ObservedOwnerGoalId,
                    conflict.ObservedVersion,
                    "current-owner-conflict",
                    authorizedFacts);
                throw;
            }
            catch (GoalReplacementIdempotencyConflictException)
            {
                throw;
            }
            catch (GoalReplacementTransferAuthorityException authorityFailure)
            {
                TryRecordGoalReplacementFailure(
                    claimStore,
                    command,
                    preparedSnapshot,
                    fingerprint,
                    GoalReplacementOutcome.ValidationRejected,
                    observedClaimUnderLease.OwnerGoalId,
                    observedClaimUnderLease.Version,
                    authorityFailure.ReasonCode,
                    authorizedFacts,
                    authorityFailure.ObservedFacts);
                throw new InvalidOperationException(
                    $"GOAL_REPLACE_VALIDATION_REJECTED requestId={command.RequestId:D} predecessor={command.PredecessorGoalId.Value} reason={authorityFailure.ReasonCode}",
                    authorityFailure);
            }
            catch (GoalReplacementValidationException validation)
            {
                TryRecordGoalReplacementFailure(
                    claimStore,
                    command,
                    preparedSnapshot,
                    fingerprint,
                    GoalReplacementOutcome.ValidationRejected,
                    observedClaimUnderLease.OwnerGoalId,
                    observedClaimUnderLease.Version,
                    validation.ReasonCode,
                    authorizedFacts);
                throw;
            }
            catch (InvalidOperationException precondition)
                when (GoalCreationPreconditionValidator.GetReason(precondition) is not null)
            {
                var reasonCode = GoalCreationPreconditionValidator.GetReason(precondition)!;
                TryRecordGoalReplacementFailure(
                    claimStore,
                    command,
                    preparedSnapshot,
                    fingerprint,
                    GoalReplacementOutcome.ValidationRejected,
                    observedClaimUnderLease.OwnerGoalId,
                    observedClaimUnderLease.Version,
                    reasonCode,
                    authorizedFacts);
                throw new InvalidOperationException(
                    $"GOAL_REPLACE_VALIDATION_REJECTED requestId={command.RequestId:D} predecessor={command.PredecessorGoalId.Value} reason={reasonCode} detail={precondition.Message}",
                    precondition);
            }
            catch (LegacySourceBacklogOwnerAmbiguousException ambiguous)
            {
                throw RecordLegacyOwnerAmbiguousFailure(
                    claimStore,
                    command,
                    preparedSnapshot,
                    fingerprint,
                    authorizedFacts,
                    ambiguous);
            }
            catch (Exception ex)
            {
                var observedClaim = ResolveObservedClaimAfterReplacementFailure(
                    claimStore,
                    stateRepository,
                    preparedSnapshot.SourceBacklogItemId!);
                var failureCode = observedClaim is null
                    ? "transaction-failed-claim-observation-unavailable"
                    : "transaction-failed";
                TryRecordGoalReplacementFailure(
                    claimStore,
                    command,
                    preparedSnapshot,
                    fingerprint,
                    GoalReplacementOutcome.PersistenceFailed,
                    observedClaim?.OwnerGoalId,
                    observedClaim?.Version,
                    failureCode,
                    authorizedFacts);
                throw new InvalidOperationException(
                    $"GOAL_REPLACE_PERSISTENCE_FAILED requestId={command.RequestId:D} predecessor={command.PredecessorGoalId.Value} owner={observedClaim?.OwnerGoalId ?? "unknown"} claimVersion={observedClaim?.Version.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown"} reason={failureCode} detail={ex.Message}",
                    ex);
            }

            committedGoalId = receipt.SuccessorGoalId;
            if (receipt.Outcome == GoalReplacementOutcome.Succeeded)
            {
                _ = CliPersistentStateRunner.DeliverGoalCreationSideEffects(outboxRepository, workspace, kernel, preparedGoal.Id);
                deferredCollaborationWriter.CompleteDelivery();
                deferredEventWriter.CompleteDeliveryTo(
                    new GoalLifecycleEventWriter(workspace.GoalLifecycleEventsDirectory, kernel: kernel, integrationBranch: workspace.IntegrationBranch));
                _ = GoalRefinementWorkCoordinator.TryLaunch(workspace, preparedGoal.Id);
            }
            var outcomeCode = RenderGoalReplacementOutcomeCode(receipt.Outcome);
            var receiptOwner = receipt.OwnerGoalId ?? "unknown";
            var receiptVersion = receipt.ClaimVersion?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown";
            Console.WriteLine(
                $"GOAL_REPLACE_{outcomeCode} requestId={receipt.RequestId:D} predecessor={receipt.PredecessorGoalId} successor={receipt.SuccessorGoalId} backlogItem={receipt.BacklogItemId} owner={receiptOwner} claimVersion={receiptVersion}");
            if (receipt.Outcome is not (GoalReplacementOutcome.Succeeded or GoalReplacementOutcome.Replayed))
            {
                throw new InvalidOperationException(
                    $"GOAL_REPLACE_{outcomeCode} predecessor={receipt.PredecessorGoalId} backlogItem={receipt.BacklogItemId} owner={receiptOwner} claimVersion={receiptVersion} reason={receipt.ReasonCode}");
            }
        }

        bool shouldSave;
        try
        {
            shouldSave = CliCommandDispatcher.ExecuteCommand(
                args,
                kernel,
                workspace,
                ref agents,
                providers,
                ref workerProfiles,
                ref currentGoal,
                channel,
                () => stateRepository.LoadAsync().GetAwaiter().GetResult(),
                eventWriter: deferredEventWriter,
                refinementCollaborationItemRaise: deferredCollaborationWriter.RaiseAsync,
                finalizeGoalReplacement: FinalizeGoalReplacement);
        }
        catch (Exception ex)
        {
            if (ex is not GoalReplacementRetryableTimeoutException &&
                (!Guid.TryParse(TryGetGoalReplacementFlag(args, "--request-id"), out var failedRequestId) ||
                 claimStore.FindAudit(failedRequestId) is null))
            {
                TryRecordGoalReplacementPreflightFailure(claimStore, args, kernel, workspace, ex);
            }
            throw;
        }

        if (committedGoalId is null)
            throw new InvalidOperationException("GOAL_REPLACE_COMMIT_MISSING reason=finalizer-not-invoked");

        var committedKernel = stateRepository.LoadAsync().GetAwaiter().GetResult();
        currentGoal = committedKernel.Goals.Single(goal => goal.Id.Value == committedGoalId);
        GoalTextView.PrintGoal(currentGoal);
        return shouldSave;
    }

    private sealed class GoalReplacementValidationException(string message, string reasonCode)
        : InvalidOperationException(message)
    {
        public string ReasonCode { get; } = reasonCode;
    }

    internal enum GoalReplacementTransferLeaseAcquisitionKind
    {
        Acquired,
        ProtectedEvidenceMutationOwner,
        CompetingReplacementTimedOut
    }

    internal sealed record GoalReplacementTransferLeaseAcquisition(
        GoalReplacementTransferLeaseAcquisitionKind Kind,
        IDisposable? Lease = null,
        string? LeaseOwner = null,
        SourceBacklogClaimSnapshot? ObservedClaim = null,
        string? FailureCode = null);

    internal static void ThrowGoalReplacementTransferLeaseTimeout(
        GoalReplacementCommand command,
        GoalReplacementTransferLeaseAcquisition acquisition,
        Action<GoalReplacementOutcome, string?, long?, string> recordTerminalFailure)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(acquisition);
        ArgumentNullException.ThrowIfNull(recordTerminalFailure);
        if (acquisition.Kind != GoalReplacementTransferLeaseAcquisitionKind.CompetingReplacementTimedOut)
        {
            throw new InvalidOperationException(
                $"GOAL_REPLACE_STATE_INVALID reason=non-timeout-lease-resolution kind={acquisition.Kind}");
        }

        var observedClaim = acquisition.ObservedClaim;
        if (observedClaim is null)
        {
            const string failureCode = "claim-observation-unavailable";
            recordTerminalFailure(GoalReplacementOutcome.PersistenceFailed, null, null, failureCode);
            throw new InvalidOperationException(
                $"GOAL_REPLACE_PERSISTENCE_FAILED requestId={command.RequestId:D} predecessor={command.PredecessorGoalId.Value} owner=unknown claimVersion=unknown reason={failureCode}");
        }

        if (!string.Equals(observedClaim.OwnerGoalId, command.ExpectedOwnerGoalId, StringComparison.Ordinal) ||
            observedClaim.Version != command.ExpectedClaimVersion)
        {
            recordTerminalFailure(
                GoalReplacementOutcome.CurrentOwnerConflict,
                observedClaim.OwnerGoalId,
                observedClaim.Version,
                "current-owner-conflict");
            throw new SourceBacklogClaimConflictException(
                observedClaim.BacklogItemId,
                command.PredecessorGoalId.Value,
                observedClaim.OwnerGoalId,
                observedClaim.Version);
        }

        // The claim CAS precondition still holds. The competing lease can disappear after this
        // bounded wait, so do not create a permanent idempotency audit for a retryable timeout.
        throw new GoalReplacementRetryableTimeoutException(
            command.RequestId,
            command.PredecessorGoalId.Value,
            observedClaim.OwnerGoalId,
            observedClaim.Version);
    }

    private static string RenderGoalReplacementOutcomeCode(GoalReplacementOutcome outcome) => outcome switch
    {
        GoalReplacementOutcome.Succeeded => "SUCCEEDED",
        GoalReplacementOutcome.Replayed => "REPLAYED",
        GoalReplacementOutcome.ValidationRejected => "VALIDATION_REJECTED",
        GoalReplacementOutcome.IneligibleDisposition => "INELIGIBLE_DISPOSITION",
        GoalReplacementOutcome.ProtectedOwner => "PROTECTED_OWNER",
        GoalReplacementOutcome.CurrentOwnerConflict => "CURRENT_OWNER_CONFLICT",
        GoalReplacementOutcome.LegacyOwnerAmbiguous => "LEGACY_OWNER_AMBIGUOUS",
        GoalReplacementOutcome.PersistenceFailed => "PERSISTENCE_FAILED",
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "Unknown goal replacement outcome.")
    };

    internal static string HashGoalReplacementObjective(string objective)
    {
        var normalizedObjective = string.Join(
            '\n',
            objective.Replace("\r\n", "\n", StringComparison.Ordinal)
                .Split('\n')
                .Select(line => line.TrimEnd()))
            .Trim();
        var objectiveHash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(normalizedObjective)))
            .ToLowerInvariant();
        return objectiveHash;
    }

    private static (string Fingerprint, string ObjectiveHash, string OrderedRoles, string AssignedAgents) BuildGoalReplacementFingerprint(
        GoalReplacementCommand command,
        GoalSnapshot preparedSnapshot)
    {
        var objectiveHash = HashGoalReplacementObjective(command.Objective);
        var orderedRoles = string.Join(',', preparedSnapshot.Tasks.Select(task => task.RequiredRole.ToString()));
        var assignedAgents = string.Join(
            ',',
            preparedSnapshot.Tasks.Select(task => $"{task.RequiredRole}={task.AssignedAgentId ?? "unassigned"}"));
        var fingerprint = ComputeGoalReplacementFingerprint(
            command.PredecessorGoalId.Value,
            preparedSnapshot.SourceBacklogItemId!,
            command.Disposition,
            command.Reason,
            objectiveHash,
            preparedSnapshot.SourceBacklogCoverage ?? SourceBacklogCoverage.Full,
            orderedRoles,
            command.RequestedPipeline,
            command.RequestedAgentOverrides,
            assignedAgents);
        return (fingerprint, objectiveHash, orderedRoles, assignedAgents);
    }

    private static string ComputeGoalReplacementFingerprint(
        string predecessorGoalId,
        string backlogItemId,
        GoalReplacementDisposition disposition,
        string reason,
        string objectiveHash,
        SourceBacklogCoverage coverage,
        string orderedRoles,
        string requestedPipeline,
        string requestedAgentOverrides,
        string assignedAgents) =>
        ComputeGoalReplacementFingerprint(
            predecessorGoalId,
            backlogItemId,
            disposition.ToString(),
            reason,
            objectiveHash,
            coverage,
            orderedRoles,
            requestedPipeline,
            requestedAgentOverrides,
            assignedAgents);

    internal static string ComputeGoalReplacementFingerprint(
        string predecessorGoalId,
        string backlogItemId,
        string dispositionMaterial,
        string reason,
        string objectiveHash,
        SourceBacklogCoverage coverage,
        string orderedRoles,
        string requestedPipeline,
        string requestedAgentOverrides,
        string assignedAgents)
    {
        var material = string.Join(
            "|",
            predecessorGoalId,
            backlogItemId,
            dispositionMaterial,
            reason.Replace("\r\n", "\n", StringComparison.Ordinal).Trim(),
            objectiveHash,
            coverage,
            orderedRoles,
            requestedPipeline,
            requestedAgentOverrides,
            assignedAgents);
        return Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(material)))
            .ToLowerInvariant();
    }

    private static string ComputeLegacyGoalReplacementFingerprint(
        string predecessorGoalId,
        string backlogItemId,
        GoalReplacementDisposition disposition,
        string reason,
        string objectiveHash,
        SourceBacklogCoverage coverage,
        string orderedRoles)
    {
        var material = string.Join(
            "|",
            predecessorGoalId,
            backlogItemId,
            disposition,
            reason.Replace("\r\n", "\n", StringComparison.Ordinal).Trim(),
            objectiveHash,
            coverage,
            orderedRoles);
        return Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(material)))
            .ToLowerInvariant();
    }

    internal static GoalReplacementTransferLeaseAcquisition AcquireGoalReplacementTransferLease(
        string stateDbPath,
        string predecessorGoalId,
        Func<SourceBacklogClaimSnapshot?> resolveCurrentClaim,
        TimeProvider? timeProvider = null,
        Action<TimeSpan>? wait = null)
    {
        ArgumentNullException.ThrowIfNull(resolveCurrentClaim);
        timeProvider ??= GoalReplacementTransferLeaseClock.Current;
        wait ??= GoalReplacementTransferLeaseClock.CurrentWait;
        var store = new ReconcileSweepRemediationStore(stateDbPath);
        var owner = $"goal-replace:{Environment.ProcessId}:{Guid.NewGuid():N}";
        var deadline = timeProvider.GetUtcNow().AddSeconds(15);
        do
        {
            var lease = store.TryAcquireAcceptanceLease(predecessorGoalId, owner, TimeSpan.FromMinutes(30));
            if (lease is not null)
                return new GoalReplacementTransferLeaseAcquisition(
                    GoalReplacementTransferLeaseAcquisitionKind.Acquired,
                    Lease: lease,
                    LeaseOwner: owner);

            AfterGoalReplacementLeaseAcquisitionFailed?.Invoke();
            var activeOwner = store.TryGetAcceptanceLeaseOwner(predecessorGoalId);
            if (activeOwner is null)
            {
                wait(TimeSpan.FromMilliseconds(10));
                continue;
            }
            if (!activeOwner.StartsWith("goal-replace:", StringComparison.Ordinal))
            {
                var observedClaim = resolveCurrentClaim();
                var confirmedOwner = store.TryGetAcceptanceLeaseOwner(predecessorGoalId);
                if (!string.Equals(activeOwner, confirmedOwner, StringComparison.Ordinal))
                {
                    wait(TimeSpan.FromMilliseconds(10));
                    continue;
                }
                return new GoalReplacementTransferLeaseAcquisition(
                    GoalReplacementTransferLeaseAcquisitionKind.ProtectedEvidenceMutationOwner,
                    ObservedClaim: observedClaim,
                    FailureCode: activeOwner.StartsWith("goal-evidence:", StringComparison.Ordinal)
                        ? "evidence-mutation-active"
                        : "landing-operation-active");
            }
            wait(TimeSpan.FromMilliseconds(10));
        }
        while (timeProvider.GetUtcNow() < deadline);

        return new GoalReplacementTransferLeaseAcquisition(
            GoalReplacementTransferLeaseAcquisitionKind.CompetingReplacementTimedOut,
            ObservedClaim: resolveCurrentClaim());
    }

    private static bool TryReplayCommittedGoalReplacement(
        IReadOnlyList<string> args,
        AgentOrchestratorKernel kernel,
        SourceBacklogClaimStore claimStore,
        ref Goal? currentGoal,
        out bool result)
    {
        result = false;
        if (!args.Any(part => part.Equals("--confirm-goal-replace", StringComparison.OrdinalIgnoreCase)))
            return false;
        if (!Guid.TryParse(TryGetGoalReplacementFlag(args, "--request-id"), out var requestId) ||
            claimStore.FindAudit(requestId) is not { } audit)
        {
            return false;
        }

        var predecessorSelector = args.Count > 1 ? args[1].Trim() : string.Empty;
        if (predecessorSelector.Length < 8 ||
            !audit.PredecessorGoalId.StartsWith(predecessorSelector, StringComparison.OrdinalIgnoreCase))
        {
            throw new GoalReplacementIdempotencyConflictException(requestId);
        }

        var briefPath = TryGetGoalReplacementFlag(args, "--brief-file");
        var reasonPath = TryGetGoalReplacementFlag(args, "--reason-file");
        var hasBriefFile = !string.IsNullOrWhiteSpace(briefPath) && File.Exists(briefPath);
        var hasReasonFile = !string.IsNullOrWhiteSpace(reasonPath) && File.Exists(reasonPath);
        var hasDisposition = TryParseGoalReplacementDisposition(
            TryGetGoalReplacementFlag(args, "--disposition"),
            out var disposition);
        if (!hasBriefFile || !hasReasonFile || !hasDisposition)
        {
            var preflightFingerprint = ComputeUnpreparedGoalReplacementFingerprint(
                args,
                audit.PredecessorGoalId,
                audit.BacklogItemId,
                audit.Coverage,
                hasBriefFile ? File.ReadAllText(briefPath!) : string.Empty,
                hasReasonFile ? File.ReadAllText(reasonPath!).Trim() : string.Empty,
                hasDisposition,
                disposition);
            if (!string.Equals(preflightFingerprint, audit.Fingerprint, StringComparison.Ordinal))
                throw new GoalReplacementIdempotencyConflictException(requestId);
            ThrowReplayedGoalReplacementFailure(audit);
        }

        var objectiveHash = HashGoalReplacementObjective(File.ReadAllText(briefPath));
        if (!string.IsNullOrWhiteSpace(audit.ObjectiveHash) &&
            !string.Equals(objectiveHash, audit.ObjectiveHash, StringComparison.Ordinal))
        {
            throw new GoalReplacementIdempotencyConflictException(requestId);
        }
        var replayOrderedRoles = audit.OrderedRoles;
        if (string.IsNullOrWhiteSpace(replayOrderedRoles))
        {
            replayOrderedRoles = audit.SuccessorGoalId is { } legacySuccessorGoalId
                ? string.Join(',', kernel.Goals
                    .SingleOrDefault(goal => goal.Id.Value == legacySuccessorGoalId)?.Tasks
                    .Select(task => task.RequiredRole) ?? [])
                : ResolveLegacyGoalReplacementReplayRoles(args);
        }
        if (string.IsNullOrWhiteSpace(replayOrderedRoles))
            throw new GoalReplacementIdempotencyConflictException(requestId);
        var fingerprint = string.IsNullOrWhiteSpace(audit.AssignedAgents)
            ? ComputeLegacyGoalReplacementFingerprint(
                audit.PredecessorGoalId,
                audit.BacklogItemId,
                disposition,
                File.ReadAllText(reasonPath).Trim(),
                objectiveHash,
                audit.Coverage,
                replayOrderedRoles)
            : ComputeGoalReplacementFingerprint(
                audit.PredecessorGoalId,
                audit.BacklogItemId,
                disposition,
                File.ReadAllText(reasonPath).Trim(),
                objectiveHash,
                audit.Coverage,
                replayOrderedRoles,
                NormalizeGoalReplacementPipelineRequest(args),
                BuildGoalReplacementRequestedAgentOverrides(args),
                audit.AssignedAgents);
        if (!string.Equals(fingerprint, audit.Fingerprint, StringComparison.Ordinal))
            throw new GoalReplacementIdempotencyConflictException(requestId);

        if (audit.Outcome != GoalReplacementOutcome.Succeeded || audit.SuccessorGoalId is null)
            ThrowReplayedGoalReplacementFailure(audit);
        var successorGoalId = audit.SuccessorGoalId;

        var successor = kernel.Goals.SingleOrDefault(goal => goal.Id.Value == successorGoalId)
            ?? throw new InvalidOperationException(
                $"GOAL_REPLACE_REPLAY_UNAVAILABLE requestId={requestId:D} successor={successorGoalId} reason=successor-missing");
        var claim = claimStore.ResolveClaim(kernel, audit.BacklogItemId)
            ?? throw new InvalidOperationException(
                $"GOAL_REPLACE_REPLAY_UNAVAILABLE requestId={requestId:D} backlogItem={audit.BacklogItemId} reason=source-claim-missing");
        currentGoal = successor;
        Console.WriteLine(
            $"GOAL_REPLACE_REPLAYED requestId={requestId:D} predecessor={audit.PredecessorGoalId} successor={successorGoalId} backlogItem={audit.BacklogItemId} owner={claim.OwnerGoalId} claimVersion={claim.Version}");
        GoalTextView.PrintGoal(successor);
        result = true;
        return true;
    }

    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void ThrowReplayedGoalReplacementFailure(GoalReplacementAuditSnapshot audit)
    {
        var failureOutcomeCode = RenderGoalReplacementOutcomeCode(audit.Outcome);
        var failureOwner = audit.ObservedOwnerGoalId ?? "unknown";
        var failureVersion = audit.ObservedClaimVersion?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown";
        Console.WriteLine(
            $"GOAL_REPLACE_{failureOutcomeCode} requestId={audit.RequestId:D} predecessor={audit.PredecessorGoalId} successor={audit.SuccessorGoalId} backlogItem={audit.BacklogItemId} owner={failureOwner} claimVersion={failureVersion}");
        throw new InvalidOperationException(
            $"GOAL_REPLACE_{failureOutcomeCode} predecessor={audit.PredecessorGoalId} backlogItem={audit.BacklogItemId} owner={failureOwner} claimVersion={failureVersion} reason={audit.FailureCode ?? "replayed-failure"}");
    }

    private static string ComputeUnpreparedGoalReplacementFingerprint(
        IReadOnlyList<string> args,
        string predecessorGoalId,
        string backlogItemId,
        SourceBacklogCoverage coverage,
        string objective,
        string reason,
        bool hasValidDisposition,
        GoalReplacementDisposition disposition)
    {
        var objectiveHash = HashGoalReplacementObjective(objective);
        var pipeline = NormalizeGoalReplacementPipelineRequest(args);
        var orderedRoles = ResolveGoalReplacementPreflightRoles(pipeline);
        var requestedDisposition = TryGetGoalReplacementFlag(args, "--disposition")?.Trim();
        return ComputeGoalReplacementFingerprint(
            predecessorGoalId,
            backlogItemId,
            hasValidDisposition
                ? disposition.ToString()
                : $"invalid:{requestedDisposition?.ToLowerInvariant() ?? "missing"}",
            reason,
            objectiveHash,
            coverage,
            orderedRoles,
            pipeline,
            BuildGoalReplacementRequestedAgentOverrides(args),
            "unprepared");
    }

    private static string ResolveLegacyGoalReplacementReplayRoles(IReadOnlyList<string> args)
    {
        var pipeline = TryGetGoalReplacementFlag(args, "--pipeline");
        if (!string.Equals(pipeline, "five-role", StringComparison.OrdinalIgnoreCase))
            return string.Empty;

        return string.Join(',', new[]
        {
            AgentRole.Researcher,
            AgentRole.Planner,
            AgentRole.Developer,
            AgentRole.Tester,
            AgentRole.Reviewer
        });
    }

    private static void TryRecordGoalReplacementFailure(
        SourceBacklogClaimStore claimStore,
        GoalReplacementCommand command,
        GoalSnapshot preparedSnapshot,
        string fingerprint,
        GoalReplacementOutcome outcome,
        string? observedOwner,
        long? observedVersion,
        string failureCode,
        GoalReplacementEligibilityFacts eligibilityFacts,
        GoalReplacementEligibilityFacts? observedFacts = null)
    {
        try
        {
            var fingerprintInputs = BuildGoalReplacementFingerprint(command, preparedSnapshot);
            claimStore.RecordFailedAttempt(new GoalReplacementAuditSnapshot(
                command.RequestId,
                fingerprint,
                outcome,
                preparedSnapshot.SourceBacklogItemId!,
                command.PredecessorGoalId.Value,
                null,
                command.Disposition,
                command.Reason,
                eligibilityFacts.Status,
                null,
                preparedSnapshot.SourceBacklogCoverage ?? SourceBacklogCoverage.Full,
                "operator",
                "cli",
                "local-process",
                DateTimeOffset.UtcNow,
                command.ExpectedOwnerGoalId,
                command.ExpectedClaimVersion,
                observedOwner,
                observedVersion,
                observedFacts ?? eligibilityFacts,
                failureCode,
                fingerprintInputs.ObjectiveHash,
                fingerprintInputs.OrderedRoles,
                fingerprintInputs.AssignedAgents));
        }
        catch (GoalReplacementIdempotencyConflictException)
        {
            throw;
        }
        catch (Exception auditFailure)
        {
            throw new InvalidOperationException(
                $"GOAL_REPLACE_AUDIT_UNAVAILABLE requestId={command.RequestId:D} originalOutcome={outcome} reason=durable-receipt-unavailable",
                auditFailure);
        }
    }

    internal static SourceBacklogClaimSnapshot? ResolveObservedClaimAfterReplacementFailure(
        SourceBacklogClaimStore claimStore,
        IOrchestratorStateRepository stateRepository,
        string backlogItemId)
    {
        try
        {
            BeforeGoalReplacementFailureClaimObservation?.Invoke();
            var currentKernel = stateRepository.LoadAsync().GetAwaiter().GetResult();
            return claimStore.ResolveClaim(currentKernel, backlogItemId);
        }
        catch (LegacySourceBacklogOwnerAmbiguousException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private static SourceBacklogClaimSnapshot? ResolveObservedClaim(
        SourceBacklogClaimStore claimStore,
        IOrchestratorStateRepository stateRepository,
        string backlogItemId)
    {
        try
        {
            var currentKernel = stateRepository.LoadAsync().GetAwaiter().GetResult();
            return claimStore.ResolveClaim(currentKernel, backlogItemId);
        }
        catch (LegacySourceBacklogOwnerAmbiguousException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private static InvalidOperationException RecordLegacyOwnerAmbiguousFailure(
        SourceBacklogClaimStore claimStore,
        GoalReplacementCommand command,
        GoalSnapshot preparedSnapshot,
        string fingerprint,
        GoalReplacementEligibilityFacts eligibilityFacts,
        LegacySourceBacklogOwnerAmbiguousException ambiguous)
    {
        TryRecordGoalReplacementFailure(
            claimStore,
            command,
            preparedSnapshot,
            fingerprint,
            GoalReplacementOutcome.LegacyOwnerAmbiguous,
            string.Join(',', ambiguous.LinkedGoalIds),
            observedVersion: null,
            "legacy-owner-ambiguous",
            eligibilityFacts);
        return new InvalidOperationException(
            $"GOAL_REPLACE_LEGACY_OWNER_AMBIGUOUS requestId={command.RequestId:D} backlogItem={ambiguous.BacklogItemId} linkedGoals={string.Join(',', ambiguous.LinkedGoalIds)} reason=legacy-owner-ambiguous",
            ambiguous);
    }

    private static GoalReplacementEligibilityFacts CreateUnavailableGoalReplacementFacts(Goal predecessor) =>
        new(
            predecessor.Status,
            HasWorkspace: false,
            HasBranch: false,
            HasDispatch: predecessor.Tasks.Any(task => task.LastDispatch is not null),
            HasRepositoryDelta: false,
            IsRunning: predecessor.Tasks.Any(task => task.Status == WorkTaskStatus.Running),
            IsLanded: false,
            IsMerged: false,
            IsRecorded: false,
            IsGitEvidenceAvailable: false);

    private static void TryRecordGoalReplacementPreflightFailure(
        SourceBacklogClaimStore claimStore,
        IReadOnlyList<string> args,
        AgentOrchestratorKernel kernel,
        OrchestratorWorkspace workspace,
        Exception exception)
    {
        try
        {
            if (!Guid.TryParse(TryGetGoalReplacementFlag(args, "--request-id"), out var requestId) ||
                args.Count < 2)
            {
                return;
            }

            var predecessorMatches = kernel.Goals
                .Where(goal => goal.Id.Value.Equals(args[1], StringComparison.OrdinalIgnoreCase) ||
                               goal.Id.Value.StartsWith(args[1], StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (predecessorMatches.Length != 1)
            {
                return;
            }

            var predecessor = predecessorMatches[0];
            var hasSourceBacklogItem = predecessor.SourceBacklogItemId is not null;
            var backlogItemId = predecessor.SourceBacklogItemId
                ?? GoalReplacementUnlinkedBacklogItemId(predecessor.Id);
            var requestedDisposition = TryGetGoalReplacementFlag(args, "--disposition")?.Trim();
            var hasValidDisposition = TryParseGoalReplacementDisposition(requestedDisposition, out var disposition);

            var reasonPath = TryGetGoalReplacementFlag(args, "--reason-file");
            var briefPath = TryGetGoalReplacementFlag(args, "--brief-file");
            var hasReasonFile = !string.IsNullOrWhiteSpace(reasonPath) && File.Exists(reasonPath);
            var hasBriefFile = !string.IsNullOrWhiteSpace(briefPath) && File.Exists(briefPath);
            var reason = hasReasonFile ? File.ReadAllText(reasonPath!).Trim() : string.Empty;
            var objective = hasBriefFile ? File.ReadAllText(briefPath!) : string.Empty;
            SourceBacklogClaimSnapshot? claim = null;
            try
            {
                if (hasSourceBacklogItem)
                    claim = claimStore.ResolveClaim(kernel, backlogItemId);
            }
            catch (LegacySourceBacklogOwnerAmbiguousException)
            {
            }

            GoalReplacementEligibilityFacts facts;
            try
            {
                facts = GoalReplacementEvidence.Capture(workspace, predecessor);
            }
            catch
            {
                facts = CreateUnavailableGoalReplacementFacts(predecessor);
            }

            var coverage = claim?.Coverage ?? predecessor.SourceBacklogCoverage ?? SourceBacklogCoverage.Full;
            var expectedOwner = claim?.OwnerGoalId ?? predecessor.Id.Value;
            var expectedVersion = claim?.Version ?? 0;
            var outcome = exception switch
            {
                SourceBacklogClaimConflictException => GoalReplacementOutcome.CurrentOwnerConflict,
                LegacySourceBacklogOwnerAmbiguousException => GoalReplacementOutcome.LegacyOwnerAmbiguous,
                _ => GoalReplacementOutcome.ValidationRejected
            };
            var observedOwner = exception is SourceBacklogClaimConflictException conflict
                ? conflict.ObservedOwnerGoalId
                : claim?.OwnerGoalId;
            var observedVersion = exception is SourceBacklogClaimConflictException ownerConflict
                ? ownerConflict.ObservedVersion
                : claim?.Version;
            var failureCode = ResolveGoalReplacementPreflightFailureCode(
                exception,
                hasValidDisposition,
                hasBriefFile,
                hasReasonFile,
                reason);
            var pipeline = NormalizeGoalReplacementPipelineRequest(args);
            var objectiveHash = HashGoalReplacementObjective(objective);
            var orderedRoles = ResolveGoalReplacementPreflightRoles(pipeline);
            const string assignedAgents = "unprepared";
            var requestedAgentOverrides = BuildGoalReplacementRequestedAgentOverrides(args);
            var fingerprint = ComputeGoalReplacementFingerprint(
                predecessor.Id.Value,
                backlogItemId,
                hasValidDisposition
                    ? disposition.ToString()
                    : $"invalid:{requestedDisposition?.ToLowerInvariant() ?? "missing"}",
                reason,
                objectiveHash,
                coverage,
                orderedRoles,
                pipeline,
                requestedAgentOverrides,
                assignedAgents);

            if (claimStore.FindAudit(requestId) is { } existingAudit)
            {
                if (!string.Equals(existingAudit.Fingerprint, fingerprint, StringComparison.Ordinal))
                    throw new GoalReplacementIdempotencyConflictException(requestId);
                return;
            }

            claimStore.RecordFailedAttempt(new GoalReplacementAuditSnapshot(
                requestId,
                fingerprint,
                outcome,
                backlogItemId,
                predecessor.Id.Value,
                null,
                disposition,
                reason,
                predecessor.Status,
                null,
                coverage,
                "operator",
                "cli",
                "local-process",
                DateTimeOffset.UtcNow,
                expectedOwner,
                expectedVersion,
                observedOwner,
                observedVersion,
                facts,
                failureCode,
                objectiveHash,
                orderedRoles,
                assignedAgents));
        }
        catch (GoalReplacementIdempotencyConflictException)
        {
            throw;
        }
        catch (Exception auditFailure)
        {
            throw new InvalidOperationException(
                $"GOAL_REPLACE_AUDIT_UNAVAILABLE requestId={TryGetGoalReplacementFlag(args, "--request-id") ?? "unknown"} reason=durable-receipt-unavailable",
                auditFailure);
        }
    }

    private static string? TryGetGoalReplacementFlag(IReadOnlyList<string> args, string flag)
    {
        for (var index = 0; index < args.Count - 1; index++)
        {
            if (args[index].Equals(flag, StringComparison.OrdinalIgnoreCase))
                return args[index + 1];
        }
        return null;
    }

    private static string GoalReplacementUnlinkedBacklogItemId(GoalId predecessorGoalId) =>
        $"unlinked:{predecessorGoalId.Value}";

    private static string NormalizeGoalReplacementPipelineRequest(IReadOnlyList<string> args)
    {
        var pipeline = TryGetGoalReplacementFlag(args, "--pipeline")?.Trim();
        return pipeline?.ToLowerInvariant() switch
        {
            null or "" or "auto" => "auto",
            "five-role" => "five-role",
            "scout" => "scout",
            _ => pipeline.ToLowerInvariant()
        };
    }

    private static string BuildGoalReplacementRequestedAgentOverrides(IReadOnlyList<string> args)
    {
        (string Flag, AgentRole Role)[] flags =
        [
            ("--ideation", AgentRole.Ideation),
            ("--researcher", AgentRole.Researcher),
            ("--planner", AgentRole.Planner),
            ("--developer", AgentRole.Developer),
            ("--tester", AgentRole.Tester),
            ("--reviewer", AgentRole.Reviewer)
        ];
        return string.Join(
            ',',
            flags.OrderBy(entry => entry.Role)
                .Select(entry => (entry.Role, AgentId: TryGetGoalReplacementFlag(args, entry.Flag)))
                .Where(entry => !string.IsNullOrWhiteSpace(entry.AgentId))
                .Select(entry => $"{entry.Role}={entry.AgentId}"));
    }

    private static bool TryParseGoalReplacementDisposition(
        string? value,
        out GoalReplacementDisposition disposition)
    {
        disposition = value?.Trim().ToLowerInvariant() switch
        {
            "zero-work-correction" => GoalReplacementDisposition.ZeroWorkCorrection,
            "abandon-failed-attempt" => GoalReplacementDisposition.AbandonFailedAttempt,
            "supersede-unlanded-attempt" => GoalReplacementDisposition.SupersedeUnlandedAttempt,
            _ => GoalReplacementDisposition.Unspecified
        };
        return value?.Trim().ToLowerInvariant() is
            "zero-work-correction" or "abandon-failed-attempt" or "supersede-unlanded-attempt";
    }

    private static string ExtractGoalReplacementFailureCode(string message)
    {
        const string marker = "reason=";
        var start = message.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
            return "validation-rejected";
        start += marker.Length;
        var end = message.IndexOfAny([' ', '\r', '\n'], start);
        return end < 0 ? message[start..] : message[start..end];
    }

    private static string ResolveGoalReplacementPreflightFailureCode(
        Exception exception,
        bool hasValidDisposition,
        bool hasBriefFile,
        bool hasReasonFile,
        string reason)
    {
        if (!hasValidDisposition)
            return "invalid-disposition";
        if (!hasBriefFile)
            return "brief-file-missing";
        if (!hasReasonFile)
            return "reason-file-missing";
        if (string.IsNullOrWhiteSpace(reason))
            return "reason-required";
        return ExtractGoalReplacementFailureCode(exception.Message);
    }

    private static string ResolveGoalReplacementPreflightRoles(string pipeline) => pipeline switch
    {
        "five-role" => string.Join(',', new[]
        {
            AgentRole.Researcher,
            AgentRole.Planner,
            AgentRole.Developer,
            AgentRole.Tester,
            AgentRole.Reviewer
        }),
        "scout" => string.Join(',', new[]
        {
            AgentRole.Planner,
            AgentRole.Developer,
            AgentRole.Tester,
            AgentRole.Reviewer
        }),
        _ => $"unprepared:{pipeline}"
    };
}
