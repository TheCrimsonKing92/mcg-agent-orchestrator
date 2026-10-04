namespace Mcg.AgentOrchestrator.Core;

public sealed partial class TaskSpec
{
    internal const int VerificationHistoryLimit = 20;
    internal const int PreReviewEvidenceHistoryLimit = 20;

    private readonly CappedVerificationHistory _verificationHistory = [];
    private readonly List<TaskDispatchRecord> _dispatchHistory = [];
    private readonly List<RetryAdmissionReceipt> _retryAdmissionHistory = [];
    private readonly List<PreReviewEvidenceReceipt> _preReviewEvidenceHistory = [];

    public TaskSpec(TaskId id, string description, AgentRole requiredRole, string? verificationPlan = null)
    {
        Id = id;
        Description = RequireText(description, nameof(description));
        RequiredRole = requiredRole;
        VerificationPlan = string.IsNullOrWhiteSpace(verificationPlan) ? null : verificationPlan.Trim();
    }

    public TaskId Id { get; }

    public string Description { get; }

    public AgentRole RequiredRole { get; }

    public string? VerificationPlan { get; private set; }

    public DateTimeOffset? SubscriptionRetryAfter { get; private set; }

    public string? SubscriptionLimitReviewNote { get; private set; }

    public DateTimeOffset? SubscriptionLimitReviewedAt { get; private set; }

    public int SubscriptionLimitReviewedFailureCount { get; private set; }

    public int CriterionRetryCount { get; private set; }
    public int WorkerBuildCheckRecoveryCount { get; private set; }

    public IReadOnlyList<string> CriterionRetryFeedback { get; private set; } = [];

    public AcceptedRetryFeedback? AcceptedRetryFeedback { get; private set; }

    public int EmptyOutputRetryCount { get; private set; }

    public DateTimeOffset? LatestRetryAt { get; private set; }
    public bool LatestRetryInherited { get; private set; }

    public DateTimeOffset? LatestRoleInputRetryAt { get; private set; }

    public DateTimeOffset? LatestProviderBudgetRecoveryAt { get; private set; }

    public RetryRoundKind? PendingRetryRoundKind { get; private set; }

    public ReviewFindingRepairCheckpoint? PendingReviewFindingRepairCheckpoint { get; private set; }

    public RetryCause PendingRetryCause { get; private set; } = RetryCause.Unknown;

    public PreDispatchIntegrationReceipt? PendingPreDispatchIntegrationReceipt { get; private set; }

    public WorkTaskStatus Status { get; private set; } = WorkTaskStatus.Pending;

    public AgentId? AssignedAgentId { get; private set; }

    public int ConductorRoutingRevision { get; private set; }

    public TaskExecutionRecord? LastExecution { get; private set; }

    public TaskVerificationRecord? LastVerification { get; private set; }

    public IReadOnlyList<TaskVerificationRecord> VerificationHistory => _verificationHistory;

    public TaskDispatchRecord? LastDispatch { get; private set; }

    public IReadOnlyList<TaskDispatchRecord> DispatchHistory => _dispatchHistory;

    public IReadOnlyList<RetryAdmissionReceipt> RetryAdmissionHistory => _retryAdmissionHistory;

    public RetryAdmissionRoute? RetryAdmissionHoldRoute { get; private set; }

    public TaskProcessRecord? LastProcess { get; private set; }

    public string? InterruptedDispatchRecoveryId { get; private set; }

    public InterruptedWorkCheckpoint? PendingInterruptedWorkCheckpoint { get; private set; }

    public bool WasCancelledByConductor { get; private set; }

    public PreReviewEvidenceReceipt? PreReviewEvidenceReceipt { get; private set; }

    public IReadOnlyList<PreReviewEvidenceReceipt> PreReviewEvidenceHistory => _preReviewEvidenceHistory;

    // This is durable attempt accounting, not the bounded receipt cache length.
    public int PreReviewEvidenceAttemptCount { get; private set; }

    internal void AssignTo(AgentId agentId)
    {
        AssignedAgentId = agentId;
        Status = WorkTaskStatus.Assigned;
        WasCancelledByConductor = false;
    }

    internal void AdvanceConductorRoutingRevision()
    {
        ConductorRoutingRevision = checked(ConductorRoutingRevision + 1);
    }

    internal void SetStatus(WorkTaskStatus status)
    {
        Status = status;
        WasCancelledByConductor = false;
    }

    internal void SetConductorCancelledStatus()
    {
        Status = WorkTaskStatus.Cancelled;
        WasCancelledByConductor = true;
    }

    internal TaskSnapshot ToSnapshot()
    {
        return new TaskSnapshot(
            Id.Value,
            Description,
            RequiredRole,
            Status,
            AssignedAgentId?.Value,
            LastExecution is null
                ? null
                : new TaskExecutionSnapshot(
                    LastExecution.AgentId.Value,
                    LastExecution.AgentName,
                    LastExecution.ProviderName,
                    LastExecution.ModelName,
                    LastExecution.Output,
                    LastExecution.StopReason,
                    LastExecution.Usage?.InputTokens,
                    LastExecution.Usage?.OutputTokens,
                    LastExecution.CompletedAt,
                    LastExecution.TaskComplexity,
                    LastExecution.MaxOutputTokens,
                    LastExecution.PromptCharacterCount,
                    LastExecution.Usage?.CachedInputTokens,
                    LastExecution.AuthoritativeOutput),
            LastVerification is null
                ? null
                : new TaskVerificationSnapshot(
                    LastVerification.Command,
                    LastVerification.WorkingDirectory,
                    LastVerification.ExitCode,
                    VerificationTextBounds.BoundText(LastVerification.StandardOutput, LastVerification.StandardOutputPath),
                    VerificationTextBounds.BoundText(LastVerification.StandardError, LastVerification.StandardErrorPath),
                    LastVerification.CompletedAt,
                    LastVerification.ModelFitNote,
                    LastVerification.StandardOutputPath,
                    LastVerification.StandardErrorPath,
                    LastVerification.ProviderFailureKind,
                    LastVerification.ReviewFindingTouchedAnchors,
                    LastVerification.ReviewedCommit,
                    LastVerification.MergedReviewFindings,
                    LastVerification.WorkerResultPresent,
                    LastVerification.ReviewFindingContractViolation,
                    LastVerification.DispatchStartedAt,
                    LastVerification.ChildProcessId,
                    LastVerification.ChildExitCode,
                    LastVerification.FindingEvidenceReceipts,
                    LastVerification.ReviewFindingTouchProofDiagnostic,
                    LastVerification.AuthoritativeStandardOutput,
                    LastVerification.AuthoritativeStandardError,
                    LastVerification.FullStandardOutputUnavailableReason,
                    LastVerification.FullStandardErrorUnavailableReason,
                    LastVerification.PlannerCandidateDivergence,
                    LastVerification.CompletionVerdictVerifiedSuccess,
                    LastVerification.CompletionVerdictRule,
                    LastVerification.AssignedScopeComplete,
                    LastVerification.CandidateIdentity,
                    LastVerification.AcceptanceCriteriaVersionHash,
                    LastVerification.InconclusiveRoundInputs,
                    LastVerification.HumanInputStoreReference),
            _verificationHistory
                .Select(verification => new TaskVerificationSnapshot(
                    verification.Command,
                    verification.WorkingDirectory,
                    verification.ExitCode,
                    VerificationTextBounds.BoundText(verification.StandardOutput, verification.StandardOutputPath),
                    VerificationTextBounds.BoundText(verification.StandardError, verification.StandardErrorPath),
                    verification.CompletedAt,
                    verification.ModelFitNote,
                    verification.StandardOutputPath,
                    verification.StandardErrorPath,
                    verification.ProviderFailureKind,
                    verification.ReviewFindingTouchedAnchors,
                    verification.ReviewedCommit,
                    verification.MergedReviewFindings,
                    verification.WorkerResultPresent,
                    verification.ReviewFindingContractViolation,
                    verification.DispatchStartedAt,
                    verification.ChildProcessId,
                    verification.ChildExitCode,
                    verification.FindingEvidenceReceipts,
                    verification.ReviewFindingTouchProofDiagnostic,
                    verification.AuthoritativeStandardOutput,
                    verification.AuthoritativeStandardError,
                    verification.FullStandardOutputUnavailableReason,
                    verification.FullStandardErrorUnavailableReason,
                    verification.PlannerCandidateDivergence,
                    verification.CompletionVerdictVerifiedSuccess,
                    verification.CompletionVerdictRule,
                    verification.AssignedScopeComplete,
                    verification.CandidateIdentity,
                    verification.AcceptanceCriteriaVersionHash,
                    verification.InconclusiveRoundInputs,
                    verification.HumanInputStoreReference))
                .ToList(),
            LastDispatch is null
                ? null
                : ToDispatchSnapshot(LastDispatch),
            LastProcess is null
                ? null
                : new TaskProcessSnapshot(
                    LastProcess.ProcessId,
                    LastProcess.Command,
                    LastProcess.WorkingDirectory,
                    LastProcess.StandardOutputPath,
                    LastProcess.StandardErrorPath,
                    LastProcess.ExitCodePath,
                    LastProcess.StartedAt,
                    LastProcess.CompletedAt,
                    LastProcess.ExitCode,
                    LastProcess.WasCancelled,
                    LastProcess.TrackedProcessIds,
                    LastProcess.ResourceAccounting is null
                        ? null
                        : new TaskProcessResourceAccountingSnapshot(
                            LastProcess.ResourceAccounting.CpuMilliseconds,
                            LastProcess.ResourceAccounting.PeakMemoryBytes,
                            LastProcess.ResourceAccounting.IoBytes,
                            LastProcess.ResourceAccounting.Reaped,
                            LastProcess.ResourceAccounting.AccountingSource),
                    LastProcess.WasCancelledByConductor,
                    LastProcess.ChildExitRecordPath,
                    LastProcess.ChildProcessId,
                    LastProcess.ChildExitCode,
                    LastProcess.ExitArtifactOrigin,
                    LastProcess.ExitArtifactReason,
                    LastProcess.WasGracefullyDetachedByConductor,
                    LastProcess.NonBlockingProcessIds,
                    LastProcess.ProcessIdentityStartedAt),
            VerificationPlan,
            SubscriptionRetryAfter,
            SubscriptionLimitReviewNote,
            SubscriptionLimitReviewedAt,
            SubscriptionLimitReviewedFailureCount,
            CriterionRetryCount,
            CriterionRetryFeedback,
            EmptyOutputRetryCount,
            LatestRetryAt,
            PendingRetryRoundKind,
            PreReviewEvidenceReceipt,
            InterruptedDispatchRecoveryId,
            WasCancelledByConductor,
            _dispatchHistory.Select(ToDispatchSnapshot).ToArray(),
            PendingRetryCause,
            _retryAdmissionHistory.ToArray(),
            RetryAdmissionHoldRoute,
            PendingReviewFindingRepairCheckpoint,
            AcceptedRetryFeedback,
            _preReviewEvidenceHistory.ToArray(),
            PreReviewEvidenceAttemptCount,
            PendingInterruptedWorkCheckpoint,
            ConductorRoutingRevision,
            PendingPreDispatchIntegrationReceipt,
            LatestProviderBudgetRecoveryAt,
            WorkerBuildCheckRecoveryCount,
            LatestRoleInputRetryAt,
            CriterionRetryFeedbackRoundAt,
            LatestRetryInherited);
    }

    internal static TaskSpec FromSnapshot(TaskSnapshot snapshot)
    {
        var task = new TaskSpec(new TaskId(snapshot.Id), snapshot.Description, snapshot.RequiredRole, snapshot.VerificationPlan);

        if (snapshot.AssignedAgentId is not null)
        {
            task.AssignTo(new AgentId(snapshot.AssignedAgentId));
        }

        task.ConductorRoutingRevision = snapshot.ConductorRoutingRevision;

        task.SetStatus(snapshot.Status);

        if (snapshot.LastExecution is not null)
        {
            task.RecordExecution(new TaskExecutionRecord(
                new AgentId(snapshot.LastExecution.AgentId),
                snapshot.LastExecution.AgentName,
                snapshot.LastExecution.ProviderName,
                snapshot.LastExecution.ModelName,
                snapshot.LastExecution.Output,
                snapshot.LastExecution.StopReason,
                new ModelUsage(snapshot.LastExecution.InputTokens, snapshot.LastExecution.OutputTokens, snapshot.LastExecution.CachedInputTokens),
                snapshot.LastExecution.CompletedAt,
                snapshot.LastExecution.TaskComplexity,
                snapshot.LastExecution.MaxOutputTokens,
                snapshot.LastExecution.PromptCharacterCount,
                snapshot.LastExecution.AuthoritativeOutput,
                OutputIsAuthoritative: snapshot.LastExecution.AuthoritativeOutput is not null));
        }

        if (snapshot.VerificationHistory is { Count: > 0 })
        {
            foreach (var verification in snapshot.VerificationHistory)
            {
                task.RestoreVerificationHistory(new TaskVerificationRecord(
                    verification.Command,
                    verification.WorkingDirectory,
                    verification.ExitCode,
                    verification.StandardOutput,
                    verification.StandardError,
                    verification.CompletedAt,
                    verification.ModelFitNote,
                    verification.StandardOutputPath,
                    verification.StandardErrorPath,
                    ProviderFailureKind: verification.ProviderFailureKind,
                    WorkerResultPresent: verification.WorkerResultPresent,
                    ReviewFindingTouchedAnchors: verification.ReviewFindingTouchedAnchors,
                    ReviewFindingTouchProofDiagnostic: verification.ReviewFindingTouchProofDiagnostic,
                    ReviewedCommit: verification.ReviewedCommit,
                    MergedReviewFindings: verification.MergedReviewFindings,
                    ReviewFindingContractViolation: verification.ReviewFindingContractViolation,
                    DispatchStartedAt: verification.DispatchStartedAt,
                    ChildProcessId: verification.ChildProcessId,
                    ChildExitCode: verification.ChildExitCode,
                    FindingEvidenceReceipts: verification.FindingEvidenceReceipts,
                    FullStandardOutput: verification.AuthoritativeStandardOutput,
                    FullStandardError: verification.AuthoritativeStandardError,
                    FullStandardOutputUnavailableReason: RestoredAuthorityUnavailableReason(
                        verification.AuthoritativeStandardOutput,
                        verification.AuthoritativeStandardOutputUnavailableReason),
                    FullStandardErrorUnavailableReason: RestoredAuthorityUnavailableReason(
                        verification.AuthoritativeStandardError,
                        verification.AuthoritativeStandardErrorUnavailableReason),
                    StandardOutputIsAuthoritative: verification.AuthoritativeStandardOutput is not null,
                    StandardErrorIsAuthoritative: verification.AuthoritativeStandardError is not null,
                    PlannerCandidateDivergence: verification.PlannerCandidateDivergence,
                    CompletionVerdictVerifiedSuccess: verification.CompletionVerdictVerifiedSuccess,
                    CompletionVerdictRule: verification.CompletionVerdictRule,
                    AssignedScopeComplete: verification.AssignedScopeComplete,
                    CandidateIdentity: verification.CandidateIdentity,
                    AcceptanceCriteriaVersionHash: verification.AcceptanceCriteriaVersionHash,
                    InconclusiveRoundInputs: verification.InconclusiveRoundInputs,
                    HumanInputStoreReference: verification.HumanInputStoreReference));
            }
        }

        if (snapshot.LastVerification is not null)
        {
            var latestVerification = new TaskVerificationRecord(
                snapshot.LastVerification.Command,
                snapshot.LastVerification.WorkingDirectory,
                snapshot.LastVerification.ExitCode,
                snapshot.LastVerification.StandardOutput,
                snapshot.LastVerification.StandardError,
                snapshot.LastVerification.CompletedAt,
                snapshot.LastVerification.ModelFitNote,
                snapshot.LastVerification.StandardOutputPath,
                snapshot.LastVerification.StandardErrorPath,
                ProviderFailureKind: snapshot.LastVerification.ProviderFailureKind,
                WorkerResultPresent: snapshot.LastVerification.WorkerResultPresent,
                ReviewFindingTouchedAnchors: snapshot.LastVerification.ReviewFindingTouchedAnchors,
                ReviewFindingTouchProofDiagnostic: snapshot.LastVerification.ReviewFindingTouchProofDiagnostic,
                ReviewedCommit: snapshot.LastVerification.ReviewedCommit,
                MergedReviewFindings: snapshot.LastVerification.MergedReviewFindings,
                ReviewFindingContractViolation: snapshot.LastVerification.ReviewFindingContractViolation,
                DispatchStartedAt: snapshot.LastVerification.DispatchStartedAt,
                ChildProcessId: snapshot.LastVerification.ChildProcessId,
                ChildExitCode: snapshot.LastVerification.ChildExitCode,
                FindingEvidenceReceipts: snapshot.LastVerification.FindingEvidenceReceipts,
                FullStandardOutput: snapshot.LastVerification.AuthoritativeStandardOutput,
                FullStandardError: snapshot.LastVerification.AuthoritativeStandardError,
                FullStandardOutputUnavailableReason: RestoredAuthorityUnavailableReason(
                    snapshot.LastVerification.AuthoritativeStandardOutput,
                    snapshot.LastVerification.AuthoritativeStandardOutputUnavailableReason),
                FullStandardErrorUnavailableReason: RestoredAuthorityUnavailableReason(
                    snapshot.LastVerification.AuthoritativeStandardError,
                    snapshot.LastVerification.AuthoritativeStandardErrorUnavailableReason),
                StandardOutputIsAuthoritative: snapshot.LastVerification.AuthoritativeStandardOutput is not null,
                StandardErrorIsAuthoritative: snapshot.LastVerification.AuthoritativeStandardError is not null,
                PlannerCandidateDivergence: snapshot.LastVerification.PlannerCandidateDivergence,
                CompletionVerdictVerifiedSuccess: snapshot.LastVerification.CompletionVerdictVerifiedSuccess,
                CompletionVerdictRule: snapshot.LastVerification.CompletionVerdictRule,
                AssignedScopeComplete: snapshot.LastVerification.AssignedScopeComplete,
                CandidateIdentity: snapshot.LastVerification.CandidateIdentity,
                AcceptanceCriteriaVersionHash: snapshot.LastVerification.AcceptanceCriteriaVersionHash,
                InconclusiveRoundInputs: snapshot.LastVerification.InconclusiveRoundInputs,
                HumanInputStoreReference: snapshot.LastVerification.HumanInputStoreReference);
            var historyIndex = task._verificationHistory.FindLastIndex(
                verification => verification.HasSameRoundIdentity(latestVerification));
            if (historyIndex < 0)
            {
                task.RestoreVerificationHistory(latestVerification);
                historyIndex = task._verificationHistory.FindLastIndex(
                    verification => verification.HasSameRoundIdentity(latestVerification));
            }
            else
            {
                var mergedVerification = latestVerification.MergeSameRoundEnrichment(
                    task._verificationHistory[historyIndex]);
                task._verificationHistory[historyIndex] = mergedVerification;
            }

            task.LastVerification = task._verificationHistory[historyIndex];
        }

        if (snapshot.DispatchHistory is { Count: > 0 })
        {
            foreach (var dispatch in snapshot.DispatchHistory)
            {
                task._dispatchHistory.Add(FromDispatchSnapshot(dispatch));
            }
        }

        foreach (var receipt in snapshot.RetryAdmissionHistory ?? [])
            task.RecordRetryAdmission(receipt);

        if (snapshot.LastDispatch is not null)
        {
            var currentDispatch = FromDispatchSnapshot(snapshot.LastDispatch);
            var historyIndex = task._dispatchHistory.FindIndex(
                dispatch => dispatch.DispatchedAt == currentDispatch.DispatchedAt);
            if (historyIndex >= 0)
            {
                var historical = task._dispatchHistory[historyIndex];
                // Older snapshots omitted both admission fields from LastDispatch while
                // preserving them in history. Recover only from the same dispatch identity.
                if (currentDispatch.RetryContextFingerprint is null &&
                    currentDispatch.PaidRoute == PaidRouteClassification.Unknown &&
                    historical.RetryContextFingerprint is not null &&
                    currentDispatch.WorkerName == historical.WorkerName &&
                    currentDispatch.Command == historical.Command &&
                    currentDispatch.WorkingDirectory == historical.WorkingDirectory &&
                    currentDispatch.ProviderName == historical.ProviderName &&
                    currentDispatch.ModelName == historical.ModelName)
                {
                    currentDispatch = currentDispatch with
                    {
                        RetryContextFingerprint = historical.RetryContextFingerprint,
                        PaidRoute = historical.PaidRoute
                    };
                }
                task._dispatchHistory[historyIndex] = currentDispatch;
            }
            else
            {
                task._dispatchHistory.Add(currentDispatch);
            }

            task.LastDispatch = currentDispatch;
        }

        if (snapshot.LastProcess is not null)
        {
            task.RecordProcess(new TaskProcessRecord(
                snapshot.LastProcess.ProcessId,
                snapshot.LastProcess.Command,
                snapshot.LastProcess.WorkingDirectory,
                snapshot.LastProcess.StandardOutputPath,
                snapshot.LastProcess.StandardErrorPath,
                snapshot.LastProcess.ExitCodePath,
                snapshot.LastProcess.StartedAt,
                snapshot.LastProcess.CompletedAt,
                snapshot.LastProcess.ExitCode,
                snapshot.LastProcess.WasCancelled,
                snapshot.LastProcess.OwnedProcessIds,
                snapshot.LastProcess.ResourceAccounting is null
                    ? null
                    : new TaskProcessResourceAccounting(
                        snapshot.LastProcess.ResourceAccounting.CpuMilliseconds,
                        snapshot.LastProcess.ResourceAccounting.PeakMemoryBytes,
                        snapshot.LastProcess.ResourceAccounting.IoBytes,
                        snapshot.LastProcess.ResourceAccounting.Reaped,
                        snapshot.LastProcess.ResourceAccounting.AccountingSource),
                snapshot.LastProcess.WasCancelledByConductor,
                snapshot.LastProcess.ChildExitRecordPath,
                snapshot.LastProcess.ChildProcessId,
                snapshot.LastProcess.ChildExitCode,
                snapshot.LastProcess.ExitArtifactOrigin,
                snapshot.LastProcess.ExitArtifactReason,
                snapshot.LastProcess.WasGracefullyDetachedByConductor,
                snapshot.LastProcess.NonBlockingProcessIds,
                snapshot.LastProcess.ProcessIdentityStartedAt));
        }

        task.SetSubscriptionRetryAfter(snapshot.SubscriptionRetryAfter);
        task.RecordSubscriptionLimitReview(
            snapshot.SubscriptionLimitReviewNote,
            snapshot.SubscriptionLimitReviewedAt,
            snapshot.SubscriptionLimitReviewedFailureCount);
        task.RestoreCriterionRetryState(snapshot.CriterionRetryCount, snapshot.CriterionRetryFeedback);
        task.WorkerBuildCheckRecoveryCount = Math.Max(0, snapshot.WorkerBuildCheckRecoveryCount);
        task.AcceptedRetryFeedback = snapshot.AcceptedRetryFeedback;
        task.EmptyOutputRetryCount = Math.Max(0, snapshot.EmptyOutputRetryCount);
        task.LatestRetryAt = snapshot.LatestRetryAt;
        task.LatestRetryInherited = snapshot.LatestRetryInherited;
        task.CriterionRetryFeedbackRoundAt = snapshot.CriterionRetryFeedbackRoundAt;
        task.LatestRoleInputRetryAt = snapshot.LatestRoleInputRetryAt;
        task.PendingRetryRoundKind = snapshot.PendingRetryRoundKind;
        task.PendingReviewFindingRepairCheckpoint = snapshot.PendingReviewFindingRepairCheckpoint;
        task.PendingRetryCause = snapshot.PendingRetryCause;
        task.PendingPreDispatchIntegrationReceipt = snapshot.PendingPreDispatchIntegrationReceipt;
        task.LatestProviderBudgetRecoveryAt = snapshot.LatestProviderBudgetRecoveryAt;
        task.RetryAdmissionHoldRoute = snapshot.RetryAdmissionHoldRoute;
        task._preReviewEvidenceHistory.AddRange(
            snapshot.PreReviewEvidenceHistory ??
            (snapshot.PreReviewEvidenceReceipt is null ? [] : [snapshot.PreReviewEvidenceReceipt]));
        task.PreReviewEvidenceAttemptCount = Math.Max(
            task._preReviewEvidenceHistory.Count(receipt =>
                receipt is not null && !receipt.IsSyntheticReuse),
            snapshot.PreReviewEvidenceAttemptCount);
        task.PreReviewEvidenceReceipt = snapshot.PreReviewEvidenceReceipt ?? task._preReviewEvidenceHistory.LastOrDefault();
        task.InterruptedDispatchRecoveryId = snapshot.InterruptedDispatchRecoveryId;
        task.PendingInterruptedWorkCheckpoint = snapshot.PendingInterruptedWorkCheckpoint;
        task.WasCancelledByConductor = snapshot.WasCancelledByConductor;
        return task;
    }

    internal void RecordExecution(TaskExecutionRecord execution) => LastExecution = execution;

    internal void ClearLastExecution() => LastExecution = null;

    internal void ClearLastDispatch() => LastDispatch = null;

    internal void ClearLastProcess() => LastProcess = null;

    internal void SetInterruptedDispatchRecovery(string? dispatchId) =>
        InterruptedDispatchRecoveryId = string.IsNullOrWhiteSpace(dispatchId) ? null : dispatchId.Trim();

    internal void SetInterruptedWorkCheckpoint(InterruptedWorkCheckpoint? checkpoint) =>
        PendingInterruptedWorkCheckpoint = checkpoint;

    internal void SetVerificationPlan(string verificationPlan) => VerificationPlan = RequireText(verificationPlan, nameof(verificationPlan));

    internal void RecordVerification(TaskVerificationRecord verification)
    {
        SubscriptionRetryAfter = null;
        PendingRetryRoundKind = null;
        PendingReviewFindingRepairCheckpoint = null;
        if (PendingInterruptedWorkCheckpoint is { } checkpoint &&
            verification.DispatchStartedAt is { } dispatchStartedAt &&
            dispatchStartedAt > checkpoint.RecordedAt)
        {
            PendingInterruptedWorkCheckpoint = null;
        }
        // EmptyOutputRetryCount is the shared bounded transient-dispatch retry budget. It covers
        // missing worker output, sandbox preflight failures, and structured Tester inconclusive
        // results without consuming Developer or Reviewer convergence allowances.
        var dispatchFlakeKind = DispatchFailureClassifier.Classify(this, verification).Kind;
        EmptyOutputRetryCount = dispatchFlakeKind is
            DispatchOutcomeKind.LaunchFailure or
            DispatchOutcomeKind.EmptyOutputFlake or
            DispatchOutcomeKind.PreflightFailure or
            DispatchOutcomeKind.VerificationInconclusive
            ? EmptyOutputRetryCount + 1
            : 0;
        if (verification.ModelFitNote is null)
        {
            var note = ModelFitEvidence.TryExtractNote(verification.StandardOutput, verification.StandardError);
            if (note is not null)
            {
                verification = verification with { ModelFitNote = note };
            }
        }

        _verificationHistory.Add(verification, RequiredRole);
        LastVerification = verification;
    }

    internal void RestoreVerificationHistory(TaskVerificationRecord verification)
    {
        var historyIndex = _verificationHistory.FindLastIndex(
            item => item.HasSameRoundIdentity(verification));
        if (historyIndex >= 0)
        {
            // Verification enrichments (finding evidence, completion verdicts, and model-fit notes)
            // mutate the latest round without changing its durable round identity. Old snapshots could
            // contain that same round once per load because LastVerification was deserialized separately.
            // Merge monotonically so a stale plain copy cannot erase a receipt or verdict from
            // an earlier copy. When both copies carry a later non-null enrichment, the incoming
            // history occurrence is the causal authority.
            _verificationHistory[historyIndex] =
                _verificationHistory[historyIndex].MergeSameRoundEnrichment(verification);
            return;
        }

        _verificationHistory.Add(verification, RequiredRole);
    }

    internal void ClearLatestVerification() => LastVerification = null;

    internal void ReinstateVerification(TaskVerificationRecord verification)
    {
        if (!_verificationHistory.Any(item => ReferenceEquals(item, verification)))
            throw new InvalidOperationException("Only this task's retained verification can be reinstated.");
        LastVerification = verification;
        Status = WorkTaskStatus.Completed;
        LatestRetryInherited = false;
    }

    internal void RecordCompletionVerdict(bool verifiedSuccess, string? rule)
    {
        if (LastVerification is null)
        {
            throw new InvalidOperationException("A completion verdict requires an existing verification.");
        }

        var prior = LastVerification;
        var updated = prior with
        {
            CompletionVerdictVerifiedSuccess = verifiedSuccess,
            CompletionVerdictRule = string.IsNullOrWhiteSpace(rule) ? null : rule.Trim()
        };
        var historyIndex = _verificationHistory.FindLastIndex(item =>
            ReferenceEquals(item, prior) || item.HasSameRoundIdentity(prior));
        if (historyIndex >= 0)
        {
            _verificationHistory[historyIndex] = updated;
        }
        LastVerification = updated;
    }

    internal void RecordFindingEvidenceOutcome(
        string stableId,
        FindingEvidenceOutcome outcome,
        FindingEvidenceReceipt? receipt)
    {
        if (LastVerification is null)
        {
            throw new InvalidOperationException("A finding evidence outcome requires an existing verification.");
        }

        var findings = LastVerification.MergedReviewFindings?.ToArray() ?? [];
        var index = Array.FindIndex(findings, finding =>
            string.Equals(finding.StableId, stableId, StringComparison.Ordinal));
        if (index < 0 &&
            WorkerResultBlockers.TryFindReviewFindingRound(LastVerification, out var reportedRound, out _))
        {
            var reportedFinding = reportedRound.Findings.FirstOrDefault(finding =>
                string.Equals(finding.StableId, stableId, StringComparison.Ordinal));
            if (reportedFinding is not null)
            {
                if (LastVerification.MergedReviewFindings is null)
                {
                    findings = [.. findings, reportedFinding];
                    index = findings.Length - 1;
                }
                else
                {
                    var mergedFinding = ReviewFindingConvergence.ResolveMergedFinding(
                        findings, reportedRound, stableId);
                    if (mergedFinding is not null)
                    {
                        index = Array.FindIndex(findings, finding => ReferenceEquals(finding, mergedFinding));
                    }
                    else
                    {
                        // An exact anchor can legitimately be shared by historical resolved and current
                        // open findings. If convergence cannot uniquely identify the canonical merged
                        // entry, retain the worker-reported finding as the outcome ledger entry instead
                        // of discarding the completed evidence run.
                        findings = [.. findings, reportedFinding];
                        index = findings.Length - 1;
                    }
                }
            }
        }
        if (index < 0)
        {
            throw new InvalidOperationException($"Finding '{stableId}' is not present in the latest verification.");
        }

        findings[index] = findings[index] with { EvidenceOutcome = outcome };
        var receipts = (LastVerification.FindingEvidenceReceipts ?? []).ToList();
        if (receipt is not null && receipts.All(existing =>
                !string.Equals(existing.ReceiptId, receipt.ReceiptId, StringComparison.Ordinal)))
        {
            receipts.Add(receipt);
        }

        var prior = LastVerification;
        var updated = prior with
        {
            MergedReviewFindings = findings,
            FindingEvidenceReceipts = receipts
        };
        var historyIndex = _verificationHistory.FindLastIndex(item =>
            ReferenceEquals(item, prior) || item.HasSameRoundIdentity(prior));
        if (historyIndex >= 0)
        {
            _verificationHistory[historyIndex] = updated;
        }
        LastVerification = updated;
    }

    internal void SetSubscriptionRetryAfter(DateTimeOffset? retryAfter) => SubscriptionRetryAfter = retryAfter;

    internal void ClearSubscriptionRetryAfter() => SubscriptionRetryAfter = null;

    internal bool RecordPreReviewEvidence(PreReviewEvidenceReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        if (PreReviewEvidenceReceipt?.ContentEquals(receipt) == true)
        {
            return false;
        }

        _preReviewEvidenceHistory.Add(receipt);
        if (!receipt.IsSyntheticReuse)
        {
            PreReviewEvidenceAttemptCount++;
        }
        if (_preReviewEvidenceHistory.Count > PreReviewEvidenceHistoryLimit)
        {
            _preReviewEvidenceHistory.RemoveRange(0, _preReviewEvidenceHistory.Count - PreReviewEvidenceHistoryLimit);
        }
        PreReviewEvidenceReceipt = receipt;
        return true;
    }

    internal void RecordRetry(
        DateTimeOffset retriedAt,
        RetryCause retryCause,
        RetryRoundKind? retryRoundKind = null,
        bool inherited = false)
    {
        LatestRetryAt = retriedAt;
        LatestRetryInherited = inherited;
        if (!inherited && retryCause != RetryCause.UnchangedContextRepeat)
            LatestRoleInputRetryAt = retriedAt;
        PendingRetryRoundKind = retryRoundKind;
        var priorVerification = _verificationHistory.LastOrDefault();
        PendingReviewFindingRepairCheckpoint = retryRoundKind == RetryRoundKind.Mechanical &&
                                               priorVerification?.ReviewFindingContractViolation is not null
            ? ReviewFindingRepairCheckpoint.Create(priorVerification)
            : null;
        PendingRetryCause = retryCause;
        if (retryCause == RetryCause.ProviderBudgetRecovery)
        {
            LatestProviderBudgetRecoveryAt = retriedAt;
        }
        PendingPreDispatchIntegrationReceipt = null;
        RetryAdmissionHoldRoute = null;
    }

    internal void RecordPreDispatchIntegrationReceipt(PreDispatchIntegrationReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        PendingPreDispatchIntegrationReceipt = receipt;
    }

    internal bool RecordRetryAdmission(RetryAdmissionReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        if (_retryAdmissionHistory.Any(existing =>
                string.Equals(existing.ReceiptId, receipt.ReceiptId, StringComparison.Ordinal)))
        {
            return false;
        }

        _retryAdmissionHistory.Add(receipt);
        return true;
    }

    internal void SetRetryAdmissionHold(RetryAdmissionRoute? route) => RetryAdmissionHoldRoute = route;

    internal void BindPreparedDispatch(TaskDispatchRecord dispatch)
    {
        ArgumentNullException.ThrowIfNull(dispatch);
        if (LastDispatch is not null && LastDispatch.DispatchedAt == dispatch.DispatchedAt)
        {
            if (LastDispatch != dispatch)
                ReplaceLastDispatch(dispatch);
            SetStatus(WorkTaskStatus.Running);
            return;
        }

        if (_dispatchHistory.Any(existing => existing.DispatchedAt >= dispatch.DispatchedAt))
            throw new InvalidOperationException("Prepared retry dispatch identity is stale or conflicts with durable dispatch history.");

        RecordDispatch(dispatch);
        if (LastDispatch?.DispatchedAt != dispatch.DispatchedAt)
            throw new InvalidOperationException("Prepared retry dispatch identity changed while binding the admission reservation.");
        SetStatus(WorkTaskStatus.Running);
    }

    internal void MarkRetryAdmissionStarted(DateTimeOffset linkedDispatchAt, DateTimeOffset workerStartedAt)
    {
        var index = _retryAdmissionHistory.FindLastIndex(receipt =>
            receipt.LinkedDispatchAt == linkedDispatchAt &&
            receipt.Decision is RetryAdmissionDecision.Allowed or RetryAdmissionDecision.ResumedReservation);
        if (index >= 0)
            _retryAdmissionHistory[index] = _retryAdmissionHistory[index] with { WorkerStartedAt = workerStartedAt };
        RetryAdmissionHoldRoute = null;
    }

    internal bool TryClaimRetryAdmissionStart(
        DateTimeOffset linkedDispatchAt,
        string reservationOwnerId,
        DateTimeOffset workerStartedAt)
    {
        var index = _retryAdmissionHistory.FindLastIndex(receipt =>
            receipt.LinkedDispatchAt == linkedDispatchAt &&
            receipt.Decision is RetryAdmissionDecision.Allowed or RetryAdmissionDecision.ResumedReservation);
        if (index < 0)
            return false;

        var receipt = _retryAdmissionHistory[index];
        if (receipt.WorkerStartedAt is not null ||
            receipt.WorkerStartClaimedAt is not null ||
            !string.Equals(receipt.ReservationOwnerId, reservationOwnerId, StringComparison.Ordinal))
        {
            return false;
        }

        _retryAdmissionHistory[index] = receipt with { WorkerStartClaimedAt = workerStartedAt };
        RetryAdmissionHoldRoute = null;
        return true;
    }

    internal bool TryConfirmRetryAdmissionStart(
        DateTimeOffset linkedDispatchAt,
        string reservationOwnerId,
        DateTimeOffset workerStartedAt)
    {
        var index = _retryAdmissionHistory.FindLastIndex(receipt =>
            receipt.LinkedDispatchAt == linkedDispatchAt &&
            receipt.Decision is RetryAdmissionDecision.Allowed or RetryAdmissionDecision.ResumedReservation);
        if (index < 0)
            return false;

        var receipt = _retryAdmissionHistory[index];
        if (receipt.WorkerStartedAt is not null ||
            receipt.WorkerStartClaimedAt is null ||
            !string.Equals(receipt.ReservationOwnerId, reservationOwnerId, StringComparison.Ordinal))
        {
            return false;
        }

        _retryAdmissionHistory[index] = receipt with { WorkerStartedAt = workerStartedAt };
        RetryAdmissionHoldRoute = null;
        return true;
    }

    internal void RecordSubscriptionLimitReview(string? note, DateTimeOffset? reviewedAt, int failureCount)
    {
        SubscriptionLimitReviewNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        SubscriptionLimitReviewedAt = reviewedAt;
        SubscriptionLimitReviewedFailureCount = Math.Max(0, failureCount);
    }

    internal void RecordCriterionRetryFeedback(IReadOnlyList<string> feedback)
    {
        CriterionRetryFeedback = feedback
            .Select(item => item.Trim())
            .Where(item => item.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        CriterionRetryFeedbackRoundAt = LatestRetryAt;
        AcceptedRetryFeedback = null;
    }

    internal void RecordAcceptedRetryFeedback(string message, DateTimeOffset acceptedAt) =>
        AcceptedRetryFeedback = new AcceptedRetryFeedback(RequireText(message, nameof(message)), acceptedAt);

    internal void IncrementCriterionRetryCount() => CriterionRetryCount++;
    internal void IncrementWorkerBuildCheckRecoveryCount() => WorkerBuildCheckRecoveryCount++;

    internal void ClearCriterionRetryFeedback()
    {
        CriterionRetryFeedback = [];
        CriterionRetryFeedbackRoundAt = null;
        AcceptedRetryFeedback = null;
    }

    private void RestoreCriterionRetryState(int retryCount, IReadOnlyList<string>? feedback)
    {
        CriterionRetryCount = Math.Max(0, retryCount);
        CriterionRetryFeedback = feedback?
            .Select(item => item.Trim())
            .Where(item => item.Length > 0)
            .ToArray() ?? [];
    }

    internal void RecordDispatch(TaskDispatchRecord dispatch)
    {
        if (_dispatchHistory.Count > 0)
        {
            var latestAttemptAt = _dispatchHistory.Max(candidate => candidate.DispatchedAt);
            if (dispatch.DispatchedAt <= latestAttemptAt)
            {
                if (latestAttemptAt == DateTimeOffset.MaxValue)
                {
                    throw new InvalidOperationException("Cannot allocate a unique dispatch attempt timestamp after DateTimeOffset.MaxValue.");
                }

                dispatch = dispatch with { DispatchedAt = latestAttemptAt.AddTicks(1) };
            }
        }

        SubscriptionRetryAfter = null;
        LastDispatch = dispatch;
        _dispatchHistory.Add(dispatch);
        PendingPreDispatchIntegrationReceipt = null;
        LastProcess = null;
    }

    internal void ReplacePreparedDispatch(TaskDispatchRecord dispatch)
    {
        if (Status != WorkTaskStatus.Running || LastDispatch is null || LastProcess is not null)
        {
            throw new InvalidOperationException("Only the current prepared dispatch may be rewritten before its process starts.");
        }

        if (LastDispatch.DispatchedAt != dispatch.DispatchedAt)
        {
            throw new InvalidOperationException("A prepared-dispatch rewrite must preserve the originating attempt timestamp.");
        }

        ReplaceLastDispatch(dispatch);
    }

    private static string? RestoredAuthorityUnavailableReason(string? authoritativeText, string? unavailableReason) =>
        authoritativeText is null && unavailableReason is null
            ? "legacy-snapshot-authoritative-output-unavailable"
            : unavailableReason;

    private void ReplaceLastDispatch(TaskDispatchRecord dispatch)
    {
        LastDispatch = dispatch;
        if (_dispatchHistory.Count > 0)
        {
            _dispatchHistory[^1] = dispatch;
        }
    }

    internal void SetDispatchBaseCommit(string baseCommit)
    {
        if (LastDispatch is not null)
            ReplaceLastDispatch(LastDispatch with { BaseCommit = baseCommit });
    }

    internal void SetDispatchResultCommit(string resultCommit)
    {
        if (LastDispatch is not null)
            ReplaceLastDispatch(LastDispatch with { ResultCommit = resultCommit });
    }

    internal void SetDispatchSandboxLowIntegrity(bool sandboxLowIntegrity)
    {
        if (LastDispatch is not null)
            ReplaceLastDispatch(LastDispatch with { SandboxLowIntegrity = sandboxLowIntegrity });
    }

    internal void SetDispatchSpawnReceipt(
        string command,
        string? providerSessionId,
        string? worktreeHeadSha,
        string? dirtyStateHash)
    {
        if (LastDispatch is not null)
        {
            ReplaceLastDispatch(LastDispatch with
            {
                Command = command,
                ProviderSessionId = NormalizeOptional(providerSessionId),
                WorktreeHeadSha = NormalizeOptional(worktreeHeadSha),
                DirtyStateHash = NormalizeOptional(dirtyStateHash)
            });
        }
    }

    internal void SetDispatchProviderSessionId(string providerSessionId)
    {
        if (LastDispatch is not null && !string.IsNullOrWhiteSpace(providerSessionId))
            ReplaceLastDispatch(LastDispatch with { ProviderSessionId = providerSessionId.Trim() });
    }

    internal void RetireDispatchProviderSession(DateTimeOffset retiredAt)
    {
        if (LastDispatch is not null)
            ReplaceLastDispatch(LastDispatch with { ProviderSessionRetiredAt = retiredAt });
    }

    internal void SetDispatchContextPackageReceipt(DateTimeOffset dispatchedAt, WorkerContextPackageReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        var matches = _dispatchHistory
            .Select((dispatch, index) => (dispatch, index))
            .Where(item => item.dispatch.DispatchedAt == dispatchedAt)
            .Select(item => item.index)
            .ToArray();
        if (matches.Length == 0)
        {
            throw new InvalidOperationException($"Cannot record context usage for unknown dispatch attempt {dispatchedAt:O}.");
        }
        if (matches.Length > 1)
        {
            throw new InvalidOperationException(
                $"Cannot record context usage because dispatch attempt timestamp {dispatchedAt:O} is ambiguous ({matches.Length} records).");
        }

        var index = matches[0];
        var updated = _dispatchHistory[index] with { ContextPackageReceipt = receipt };
        _dispatchHistory[index] = updated;
        if (index == _dispatchHistory.Count - 1)
        {
            LastDispatch = updated;
        }
    }

    internal void SetDispatchProviderUsage(DateTimeOffset dispatchedAt, DispatchProviderUsage usage)
    {
        ArgumentNullException.ThrowIfNull(usage);
        var matches = _dispatchHistory
            .Select((dispatch, index) => (dispatch, index))
            .Where(item => item.dispatch.DispatchedAt == dispatchedAt)
            .Select(item => item.index)
            .ToArray();
        if (matches.Length == 0)
            throw new InvalidOperationException($"Cannot record provider usage for unknown dispatch attempt {dispatchedAt:O}.");
        if (matches.Length > 1)
            throw new InvalidOperationException(
                $"Cannot record provider usage because dispatch attempt timestamp {dispatchedAt:O} is ambiguous ({matches.Length} records).");

        var index = matches[0];
        var updated = _dispatchHistory[index] with { ProviderUsage = usage };
        _dispatchHistory[index] = updated;
        if (index == _dispatchHistory.Count - 1)
            LastDispatch = updated;
    }

    internal void RecordProcess(TaskProcessRecord process)
    {
        LastProcess = process;
    }

    private static TaskDispatchSnapshot ToDispatchSnapshot(TaskDispatchRecord dispatch) => new(
        dispatch.WorkerName,
        dispatch.Command,
        dispatch.WorkingDirectory,
        dispatch.DispatchedAt,
        dispatch.ProviderName,
        dispatch.ModelName,
        dispatch.ReasoningEffort,
        dispatch.TaskComplexity,
        dispatch.PromptCharacterCount,
        dispatch.UsesComplexModel,
        dispatch.BaseCommit,
        dispatch.ResultCommit,
        dispatch.SandboxLowIntegrity,
        dispatch.PromptPath,
        dispatch.WorkerProviderKind,
        dispatch.ReasoningEffortReason,
        dispatch.DispatchLane,
        dispatch.ModelSelectionReason,
        dispatch.ProviderSessionId,
        dispatch.WorktreeHeadSha,
        dispatch.DirtyStateHash,
        dispatch.ProviderSessionRetiredAt,
        dispatch.ReviewFindingTouchedAnchors,
        dispatch.BriefVersion,
        dispatch.BriefSnapshot,
        dispatch.ReviewFindingTouchProofDiagnostic,
        dispatch.ReviewRetryCap,
        dispatch.ContextPackageReceipt,
        dispatch.PlannerSampleCount,
        dispatch.RetryContextFingerprint,
        dispatch.PaidRoute,
        dispatch.ClaudeCredentialSourceDirectory,
        dispatch.ClaudeCredentialSourceIsExplicit,
        dispatch.AssignedAgentId,
        dispatch.ConductorRoutingRevision,
        dispatch.PreDispatchIntegrationReceipt,
        dispatch.GoalId,
        dispatch.CandidateIdentity,
        dispatch.ProviderUsage,
        dispatch.InconclusiveRoundInputs, dispatch.ShadowDecision);

    private static TaskDispatchRecord FromDispatchSnapshot(TaskDispatchSnapshot dispatch) => new(
        dispatch.WorkerName,
        dispatch.Command,
        dispatch.WorkingDirectory,
        dispatch.DispatchedAt,
        dispatch.ProviderName,
        dispatch.ModelName,
        dispatch.ReasoningEffort,
        dispatch.TaskComplexity,
        dispatch.PromptCharacterCount,
        dispatch.UsesComplexModel,
        dispatch.BaseCommit,
        dispatch.ResultCommit,
        dispatch.SandboxLowIntegrity,
        dispatch.PromptPath,
        dispatch.WorkerProviderKind,
        dispatch.ReasoningEffortReason,
        dispatch.DispatchLane,
        dispatch.ModelSelectionReason,
        dispatch.ProviderSessionId,
        dispatch.WorktreeHeadSha,
        dispatch.DirtyStateHash,
        dispatch.ProviderSessionRetiredAt,
        dispatch.ReviewFindingTouchedAnchors,
        dispatch.BriefVersion,
        dispatch.BriefSnapshot,
        dispatch.ReviewFindingTouchProofDiagnostic,
        dispatch.ReviewRetryCap,
        dispatch.ContextPackageReceipt,
        dispatch.PlannerSampleCount,
        dispatch.RetryContextFingerprint,
        dispatch.PaidRoute,
        dispatch.ClaudeCredentialSourceDirectory,
        dispatch.ClaudeCredentialSourceIsExplicit,
        dispatch.AssignedAgentId,
        dispatch.ConductorRoutingRevision,
        dispatch.PreDispatchIntegrationReceipt,
        dispatch.GoalId,
        dispatch.CandidateIdentity,
        dispatch.ProviderUsage,
        dispatch.InconclusiveRoundInputs, dispatch.ShadowDecision);

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string RequireText(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Value cannot be empty.", parameterName);
        }

        return value.Trim();
    }

    private sealed class CappedVerificationHistory : List<TaskVerificationRecord>
    {
        public void Add(TaskVerificationRecord item, AgentRole role)
        {
            base.Add(item);
            while (Count > VerificationHistoryLimit)
            {
                // Recovery must see the Tester outcome immediately preceding the latest round,
                // including interruptions that reset consecutive inconclusive history.
                var removableIndex = FindIndex(
                    0,
                    role == AgentRole.Tester ? Count - 2 : Count - 1,
                    candidate => !MustPreserveStructuredOutcome(role, candidate));
                if (removableIndex < 0)
                {
                    break;
                }

                RemoveAt(removableIndex);
            }
        }

        private static bool MustPreserveStructuredOutcome(AgentRole role, TaskVerificationRecord verification)
        {
            if (verification.FindingEvidenceReceipts is { Count: > 0 })
            {
                return true;
            }

            if (role is AgentRole.Planner or AgentRole.Researcher &&
                verification.Succeeded &&
                !string.IsNullOrWhiteSpace(verification.StandardOutputPath))
            {
                return true;
            }

            if (!verification.WorkerResultPresent)
            {
                return false;
            }

            if (role == AgentRole.Tester &&
                WorkerResultBlockers.TryGetTestsStatus(verification, out var testsStatus) &&
                testsStatus == WorkerResultBlockers.TestsStatus.Inconclusive)
            {
                return true;
            }

            return role is AgentRole.Planner or AgentRole.Researcher &&
                WorkerResultBlockers.TryFindPremiseInvalidEvidence(verification, out _);
        }
    }
}
