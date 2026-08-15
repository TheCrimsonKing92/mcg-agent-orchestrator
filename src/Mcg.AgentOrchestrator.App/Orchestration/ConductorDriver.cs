using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.App.Dashboard.Api;
using Mcg.AgentOrchestrator.App.SubscriptionPlanning;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record PreReviewEvidenceContext(
    string? CandidateSha,
    IReadOnlyList<string> SelectedFocusedTests,
    string? FocusedRequest,
    string MappingReason,
    bool NoApplicableTests,
    bool MappingNeedsInput);

internal sealed record WorkerAdmissionSnapshot(
    int ConfiguredWorkerCap,
    int AdmissionCapacity,
    int ReservedGateSlots,
    int EffectiveWorkerCap);

internal enum DeveloperBranchIntegrationStatus
{
    Current,
    Integrated,
    Conflict,
    Failed
}

internal sealed record DeveloperBranchIntegrationResult(
    DeveloperBranchIntegrationStatus Status,
    string Message,
    IReadOnlyList<string> ConflictPaths)
{
    internal bool CanDispatch =>
        Status is DeveloperBranchIntegrationStatus.Current or DeveloperBranchIntegrationStatus.Integrated;
}

internal sealed class ConductorDriver
{
    private sealed class EvidenceMutationLeaseUnavailableException(string message)
        : InvalidOperationException(message);

    private const int MaxCriterionRetryEvidenceLines = 30;
    private static readonly TimeSpan DefaultBuildServerShutdownTimeout = TimeSpan.FromSeconds(5);
    private const string CleanBaselineRedCorrelationKeyPrefix = "clean-baseline-red:";

    // Finding evidence retries deliver a Conductor-owned receipt or typed refusal to the role that
    // requested it. Reviewer round accounting treats that mechanical delivery as part of the same
    // round; requests from other roles use the same role-neutral marker without being relabelled.
    private const string FindingEvidenceRetryMessagePrefix = "finding evidence-on-demand:";
    private const int MaxReviewFindingContractRepairsPerRound = 2;
    private const string ReviewContractRepairRetryMessagePrefix = "review-finding contract-repair:";
    private static readonly string[] MechanicalReviewerRetryMessagePrefixes =
        [FindingEvidenceRetryMessagePrefix, ReviewContractRepairRetryMessagePrefix];
    private static readonly Regex AcceptanceRetryEvidencePattern = new(
        @"error CS\d+|error MSB\d+|\[FAIL\]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex EvidenceBareClassNamePattern = new(
        @"^[A-Za-z_][A-Za-z0-9_.+`]*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex EvidenceFilterTokenPattern = new(
        @"^(?:FullyQualifiedName(?:!~|~)[A-Za-z_][A-Za-z0-9_.]*|Category\s*!=\s*[A-Za-z_][A-Za-z0-9_.-]*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private const int MaxFindingEvidenceFilterLength = 1024;
    private readonly Func<Goal, GoalLifecycleFacts> _getFacts;
    private readonly Func<int> _getRunningPaidWorkerCount;
    private readonly Func<Goal, string> _createWorkspace;
    private readonly Func<Goal, DeveloperBranchIntegrationResult> _integrateMainBeforeDeveloperDispatch;
    private readonly Func<Goal, ConductorAutonomyPolicy, DispatchStartOutcome> _dispatchAndStart;
    private readonly Func<Goal, ConductorAutonomyPolicy, DispatchStartOutcome> _startRecordedDispatches;
    private readonly Func<TimeSpan, string> _buildServerShutdown;
    private readonly TimeSpan _buildServerShutdownTimeout;
    private readonly Func<Goal, int?, DotnetBuildEnvironmentLease?, CancellationToken, AcceptanceVerificationSummary> _runAcceptanceVerification;
    private readonly Action<Goal, AcceptanceVerificationSummary> _runAdvisorySemanticAcceptance;
    private readonly Func<Goal, string, DotnetBuildEnvironmentLease?, CancellationToken, FocusedEvidenceRunResult> _runFocusedEvidence;
    private readonly Func<Goal, string, DotnetBuildEnvironmentLease?, CancellationToken, FocusedEvidenceRunResult> _runDualArmFocusedEvidence;
    private readonly bool _focusedEvidenceRunnerConfigured;
    private readonly Func<Goal, PreReviewEvidenceContext> _getPreReviewEvidenceContext;
    private readonly Func<Goal, AcceptanceGateEngineSettings> _getFindingEvidenceEngineSettings;
    private readonly Action<GoalId, TaskId, PreReviewEvidenceReceipt> _recordPreReviewEvidence;
    private readonly Action<GoalId, TaskId, string, int> _recordPreReviewMappingEscalationSuppressed;
    private readonly Func<GoalId, TaskId, string, RetryRoundKind?, TaskSpec> _retryTask;
    private readonly Action<GoalId, TaskId, string> _recordTaskNote;
    private readonly Action<GoalId, TaskId, string> _recordFindingEvidenceRequest;
    private readonly Action<GoalId, TaskId, string> _recordFindingEvidenceRun;
    private readonly Action<GoalId, TaskId, string, FindingEvidenceOutcome, FindingEvidenceReceipt?> _recordFindingEvidenceOutcome;
    private readonly Func<GoalId, TaskId, IReadOnlyList<string>, int> _recordCriterionRetryFeedback;
    private readonly Action<GoalId, TaskId> _clearCriterionRetryFeedback;
    private readonly Action<
        Goal,
        IReadOnlyList<string>,
        string?,
        string?,
        IReadOnlyList<AcceptanceCheckAttribution>?,
        string?> _recordAcceptanceFailure;
    private readonly Action<Goal> _clearAcceptanceFailure;
    private readonly Func<Goal, GoalWorktreeRebaseResult> _rebaseOntoMain;
    private readonly Func<Goal, LandingEscalationRecheckResult> _recheckPreLandingRebaseConflict;
    private readonly Func<Goal, ConductorAutonomyPolicy, LandingResult> _land;
    private readonly Action<Goal, LandingResult> _afterSuccessfulLanding;
    private readonly Action<Goal> _record;
    private readonly Func<Goal, GoalWorktreeRemoveResult> _cleanup;
    private readonly Action<Goal> _completeGoal;
    private readonly Func<Goal, GoalLifecycleState, string, LandingEscalationWriteResult> _writeEscalation;
    private readonly Func<Goal, ChangeRiskTier?> _classifyChangeRisk;
    private readonly Action<TimeSpan> _emptyOutputBackoffDelay;
    private readonly Func<Goal, DispatchReadinessVerdict> _evaluateReadiness;
    private readonly Func<Goal, string, bool> _normalizeLifecycleState;
    private readonly Func<WorkerSandboxPrepRecoverableAction, bool> _recoverSandboxPrep;
    private readonly Action<Goal, string> _recordMissingBranchRetirement;
    private readonly Func<Goal, IReadOnlyList<string>> _getLandingFileScopes;
    private readonly Func<Goal, bool> _isVerificationGateSatisfied;
    private readonly GateReadyCandidateProjector? _gateReadyCandidateProjector;
    private readonly AgentOrchestratorKernel? _cohortKernel;
    private readonly OrchestratorWorkspace? _cohortWorkspace;
    private readonly IGoalAcceptanceVerifier? _cohortAcceptanceVerifier;
    private readonly IGoalLifecycleEventWriter? _cohortEventWriter;
    private readonly CohortAcceptanceStore? _cohortAcceptanceStore;
    private readonly Func<
        ConductorAcceptanceCohortSelection,
        IReadOnlyList<Goal>,
        ConductorAutonomyPolicy,
        ConductorAcceptanceCohortRunResult>? _runAcceptanceCohortOverride;
    private readonly Func<Goal, int> _getAcceptanceSlotCount;
    private readonly Func<int> _getWorkerAdmissionCapacity;
    private readonly Func<bool> _hasGateReadyGoal;
    private readonly Func<Goal, string?> _tryBuildAwaitingClarificationEscalationReason;
    private readonly Func<Goal, string, IDisposable?> _tryAcquireEvidenceMutationLease;
    private readonly string? _executionDirectory;
    private readonly ConductorParallelAcceptanceAttemptCoordinator _parallelAcceptanceAttemptCoordinator;
    private readonly ConductorParallelAcceptanceAttemptCoordinator _focusedEvidenceAttemptCoordinator;
    private readonly bool _parallelAcceptanceEnabled;
    private readonly List<(ConductorLandingReceipt Receipt, string CohortId, string ReceiptId)>
        _pendingRecoveredLandingReceipts = [];
    private Action<ConductorLandingReceipt>? _successfulLandingSink;
    private bool _buildServerShutdownRanThisTick;

    internal Action<string>? PhaseTimingSink { get; set; }
    internal Action<ConductorLandingReceipt>? SuccessfulLandingSink
    {
        get => _successfulLandingSink;
        set
        {
            _successfulLandingSink = value;
            if (value is null || _pendingRecoveredLandingReceipts.Count == 0)
            {
                return;
            }
            foreach (var pending in _pendingRecoveredLandingReceipts)
            {
                value(pending.Receipt);
            }
            foreach (var pending in _pendingRecoveredLandingReceipts
                         .DistinctBy(item => item.CohortId))
            {
                _cohortAcceptanceStore?.CompleteLandingEffects(pending.CohortId, pending.ReceiptId);
            }
            _pendingRecoveredLandingReceipts.Clear();
        }
    }
    internal Action<GoalId>? DispatchRecordWriteSucceededSink { get; set; }
    internal Func<string?>? LandingMutationBlocker { get; set; }

    internal WorkerAdmissionSnapshot GetWorkerAdmissionSnapshot(ConductorAutonomyPolicy policy)
    {
        var admissionCapacity = Math.Max(0, _getWorkerAdmissionCapacity());
        var reservedGateSlots = _hasGateReadyGoal() ? 1 : 0;
        var effectiveWorkerCap = Math.Min(
            policy.MaxConcurrentPaidWorkers,
            Math.Max(0, admissionCapacity - reservedGateSlots));
        return new WorkerAdmissionSnapshot(
            policy.MaxConcurrentPaidWorkers,
            admissionCapacity,
            reservedGateSlots,
            effectiveWorkerCap);
    }

    public ConductorDriver(
        AgentOrchestratorKernel kernel,
        OrchestratorWorkspace workspace,
        IGoalAcceptanceVerifier acceptanceVerifier,
        IReadOnlyList<AgentDefinition> agents,
        WorkerProfileCatalog profiles,
        IOperatorChannel? channel = null,
        IModelProviderRegistry? providers = null,
        Action<AgentOrchestratorKernel, IReadOnlyCollection<GoalId>>? persistCriticalDispatchStart = null,
        Func<GoalId, TaskId, InterruptedDispatchStateRead>? readCurrentInterruptedDispatchState = null)
    {
        var dir = workspace.ExecutionDirectory;
        _executionDirectory = dir;
        _getAcceptanceSlotCount = _ => ConductorBatchLoop.DefaultParallelAcceptanceCapacity;
        _getWorkerAdmissionCapacity = () => ConductorBatchLoop.WorkerAdmissionCapacity;
        _parallelAcceptanceEnabled = true;
        _parallelAcceptanceAttemptCoordinator = new ConductorParallelAcceptanceAttemptCoordinator(
            Path.Combine(workspace.OrchestratorDirectory, "acceptance-gate-attempts"),
            dir,
            tryRunPreSlot: RunParallelLandingAcceptancePreSlot);
        _focusedEvidenceAttemptCoordinator = new ConductorParallelAcceptanceAttemptCoordinator(
            Path.Combine(workspace.OrchestratorDirectory, "pre-review-evidence-attempts"),
            dir,
            conductEventLogWriter: new ConductEventLogWriter(workspace.ConductEventsLogPath));
        var eventWriter = new GoalLifecycleEventWriter(workspace.GoalLifecycleEventsDirectory);
        _cohortKernel = kernel;
        _cohortWorkspace = workspace;
        _cohortAcceptanceVerifier = acceptanceVerifier;
        _cohortEventWriter = eventWriter;
        _cohortAcceptanceStore = new CohortAcceptanceStore(
            Path.Combine(workspace.OrchestratorDirectory, "cohort-acceptance.db"));
        kernel.SetEventWriter(eventWriter);
        _tryBuildAwaitingClarificationEscalationReason = goal =>
            GoalRefinementGate.TryBuildAwaitingClarificationEscalationReason(workspace, goal, eventWriter, out var reason)
                ? reason
                : null;
        var factGoalIds = kernel.Goals.Select(goal => goal.Id).ToArray();
        var journalSnapshot = GoalOperationJournal.ReadAll(dir, factGoalIds)
            .ToDictionary(pair => pair.Key, pair => pair.Value);
        var worktreeSnapshot = GoalWorktrees.ResolveAll(dir, factGoalIds)
            .ToDictionary(pair => pair.Key, pair => pair.Value);
        void RefreshJournal(GoalId goalId) => journalSnapshot[goalId] = GoalOperationJournal.Read(dir, goalId);
        IDisposable? AcquireEvidenceMutationLease(Goal goal, string operation)
        {
            var owner = $"goal-evidence:{operation}:{Environment.ProcessId}:{Guid.NewGuid():N}";
            return new ReconcileSweepRemediationStore(workspace.SqliteStatePath)
                .TryAcquireAcceptanceLease(goal.Id.Value, owner, TimeSpan.FromMinutes(30));
        }
        _tryAcquireEvidenceMutationLease = AcquireEvidenceMutationLease;
        void RecordMissingBranchRetirement(Goal goal, string detail)
        {
            GoalOperationJournal.RecordTerminalDisposition(
                dir,
                goal,
                new GoalTerminalDisposition(GoalTerminalDispositionKind.Retired, detail));
            kernel.CompleteGoal(goal.Id, detail);
            RefreshJournal(goal.Id);
        }

        _getFacts = goal =>
        {
            if (goal.Status is GoalStatus.Verifying or GoalStatus.Verified or GoalStatus.Completed)
            {
                RefreshJournal(goal.Id);
            }

            var workspaceExists = worktreeSnapshot.ContainsKey(goal.Id);
            var journal = journalSnapshot.TryGetValue(goal.Id, out var summary)
                ? summary
                : new GoalOperationJournalSummary(GoalOperationJournal.PathFor(dir, goal.Id), [], [], []);
            var isMerged = GoalOperationJournal.HasCompletedLandingEvidence(journal);
            var isRecorded = GoalOperationJournal.HasCompletedRecordEvidence(journal);
            var isCleanedUp = GoalOperationJournal.HasCompletedCleanupEvidence(journal);
            var hasOpenClarification = GoalRefinementGate.HasOpenClarification(workspace, goal);
            return new GoalLifecycleFacts(workspaceExists, IsBlocked: false, isMerged, isRecorded, isCleanedUp, hasOpenClarification);
        };

        _getRunningPaidWorkerCount = () =>
            kernel.Goals.Sum(g => g.Tasks.Count(t => t.LastProcess is { IsRunning: true }));
        _hasGateReadyGoal = () =>
            kernel.Goals.Any(g =>
            {
                var state = GoalLifecycle.ResolveState(g, _getFacts(g));
                return state is GoalLifecycleState.Verifying or GoalLifecycleState.Verified;
            });

        _createWorkspace = goal =>
        {
            using var evidenceMutationLease = AcquireEvidenceMutationLease(goal, "conductor:workspace-create")
                ?? throw new EvidenceMutationLeaseUnavailableException(
                    $"GOAL_OPERATION_BLOCKED goal={goal.Id.Value} operation=conductor:workspace-create reason=concurrent-acceptance-or-replacement");
            GoalOperationJournal.Begin(dir, goal, "conductor:workspace-create", GoalWorktrees.BranchName(goal.Id));
            var path = GoalWorktrees.Ensure(dir, goal.Id);
            GoalOperationJournal.Completed(dir, goal, "conductor:workspace-create", path);
            worktreeSnapshot[goal.Id] = path;
            RefreshJournal(goal.Id);
            return path;
        };

        _dispatchAndStart = (goal, policy) =>
        {
            using var evidenceMutationLease = AcquireEvidenceMutationLease(goal, "conductor:dispatch");
            if (evidenceMutationLease is null)
            {
                return DispatchStartOutcome.Deferred(
                    $"Goal evidence mutation is blocked by concurrent acceptance or replacement for {goal.Id.Value}.");
            }
            GoalOperationJournal.Begin(dir, goal, "conductor:dispatch", "Starting subscription dispatch.");
            var goalSnapshotBeforeDispatch = kernel.ExportGoalSnapshot(goal.Id);
            var criticalCheckpointPersisted = false;
            SubscriptionStartResult result;
            try
            {
                result = GoalManagementCommandService.StartSubscriptionReadyTasks(
                    kernel,
                    workspace,
                    goal,
                    agents,
                    profiles,
                    providers ?? new InMemoryModelProviderRegistry([]),
                    approveHighRiskOwnership: policy.AllowsAutonomousHighRiskOwnership,
                    checkpointBeforeWorkerStart: persistCriticalDispatchStart is null
                        ? null
                        : (checkpointKernel, goalId, taskId, checkpointPhase) =>
                        {
                            ConductorBatchLoop.PersistCriticalDispatchStartOrThrow(
                                persistCriticalDispatchStart,
                                checkpointKernel,
                                goalId,
                                taskId,
                                checkpointPhase);
                            criticalCheckpointPersisted = true;
                            DispatchRecordWriteSucceededSink?.Invoke(goalId);
                        },
                    readCurrentInterruptedDispatchState: readCurrentInterruptedDispatchState,
                    reviewAutoRetryStopRound: policy.ReviewAutoRetryStopRound);
            }
            catch (DispatchRecordWriteException ex)
            {
                if (!ex.ProcessMayHaveStarted && !criticalCheckpointPersisted)
                    kernel.ReplaceGoalWithSnapshot(goalSnapshotBeforeDispatch);
                throw;
            }
            catch (Exception ex)
            {
                var exceptionReason = $"Subscription dispatch start failed: {ex.Message}";
                GoalOperationJournal.Failed(dir, goal, "conductor:dispatch", exceptionReason);
                return DispatchStartOutcome.SpawnFailed(exceptionReason);
            }
            var outcome = ClassifySubscriptionStartForConductor(result);
            if (outcome.Category == DispatchStartOutcomeCategory.RecoverableSandboxPrep)
            {
                GoalOperationJournal.Failed(dir, goal, "conductor:dispatch",
                    $"Recoverable Low-IL sandbox prep action required: {outcome.Reason}");
                return outcome;
            }
            if (outcome.Category == DispatchStartOutcomeCategory.Started)
            {
                GoalOperationJournal.Completed(dir, goal, "conductor:dispatch",
                    $"Dispatched {result.Dispatches.Count} tasks, started {result.Processes.Tasks.Count} processes.");
                return outcome;
            }
            GoalOperationJournal.Failed(dir, goal, "conductor:dispatch", outcome.Reason!);
            return outcome;
        };

        _startRecordedDispatches = (goal, policy) =>
        {
            using var evidenceMutationLease = AcquireEvidenceMutationLease(goal, "conductor:dispatch-start");
            if (evidenceMutationLease is null)
            {
                return DispatchStartOutcome.Deferred(
                    $"Goal evidence mutation is blocked by concurrent acceptance or replacement for {goal.Id.Value}.");
            }
            GoalOperationJournal.Begin(dir, goal, "conductor:dispatch-start", "Starting recorded dispatch.");
            ProcessBatchExecutionResult result;
            try
            {
                result = GoalManagementCommandService.StartDispatches(
                    kernel,
                    workspace,
                    goal,
                    checkpointBeforeWorkerStart: persistCriticalDispatchStart is null
                        ? null
                        : (checkpointKernel, goalId, taskId, checkpointPhase) =>
                        {
                            ConductorBatchLoop.PersistCriticalDispatchStartOrThrow(
                                persistCriticalDispatchStart,
                                checkpointKernel,
                                goalId,
                                taskId,
                                checkpointPhase);
                            DispatchRecordWriteSucceededSink?.Invoke(goalId);
                        },
                    readCurrentInterruptedDispatchState: readCurrentInterruptedDispatchState,
                    reviewAutoRetryStopRound: policy.ReviewAutoRetryStopRound);
            }
            catch (Exception ex) when (ex is not DispatchRecordWriteException)
            {
                var exceptionReason = $"Recorded dispatch start failed: {ex.Message}";
                GoalOperationJournal.Failed(dir, goal, "conductor:dispatch-start", exceptionReason);
                return DispatchStartOutcome.SpawnFailed(exceptionReason);
            }

            var outcome = ClassifyRecordedDispatchStartForConductor(result);
            if (outcome.Category == DispatchStartOutcomeCategory.RecoverableSandboxPrep)
            {
                GoalOperationJournal.Failed(dir, goal, "conductor:dispatch-start",
                    $"Recoverable Low-IL sandbox prep action required: {outcome.Reason}");
                return outcome;
            }
            if (outcome.Category == DispatchStartOutcomeCategory.Started)
            {
                GoalOperationJournal.Completed(dir, goal, "conductor:dispatch-start",
                    $"Started {result.Tasks.Count} recorded dispatch process(es).");
                return outcome;
            }

            GoalOperationJournal.Failed(dir, goal, "conductor:dispatch-start", outcome.Reason!);
            return outcome;
        };

        _buildServerShutdownTimeout = DefaultBuildServerShutdownTimeout;
        _buildServerShutdown = timeout => RunBuildServerShutdown(dir, timeout);

        _runAcceptanceVerification = (goal, stableSlotIndex, stableSlotLease, cancellationToken) =>
        {
            var worktreePath = GoalWorktrees.TryResolve(dir, goal.Id);
            if (worktreePath is null) return AcceptanceVerificationSummary.Failed;
            var slotSuffix = stableSlotIndex.HasValue ? $" on stable slot {stableSlotIndex.Value}" : string.Empty;
            var acceptanceAttemptStartedAt = DateTimeOffset.UtcNow;
            GoalOperationJournal.Begin(dir, goal, "conductor:acceptance", $"Running acceptance verification{slotSuffix}.");
            var changedFiles = GoalAcceptanceEvidenceBundleBuilder.GetChangedFiles(worktreePath);
            var branchHeadSha = TryResolveGitHead(worktreePath);
            var mainHeadSha = TryResolveGitHead(dir);
            IReadOnlyDictionary<GoalId, GoalOperationJournalSummary> baselineJournals =
                new Dictionary<GoalId, GoalOperationJournalSummary>();
            var baselineReceipt = CleanTestBaseline.Unattested(mainHeadSha);
            try
            {
                baselineJournals = GoalOperationJournal.ReadAll(dir);
                baselineReceipt = CleanTestBaseline.Resolve(
                    baselineJournals,
                    goal.Id,
                    mainHeadSha ?? string.Empty,
                    mergeBaseSha: null);
                GoalOperationJournal.Begin(
                    dir,
                    goal,
                    "conductor:clean-baseline",
                    "Resolving clean-test baseline from acceptance journals.",
                    mainHeadSha);
                GoalOperationJournal.Completed(
                    dir,
                    goal,
                    "conductor:clean-baseline",
                    CleanTestBaseline.FormatJournalDetail(baselineReceipt),
                    mainHeadSha);
            }
            catch
            {
                baselineReceipt = CleanTestBaseline.Unattested(mainHeadSha);
                try
                {
                    GoalOperationJournal.Failed(
                        dir,
                        goal,
                        "conductor:clean-baseline",
                        CleanTestBaseline.FormatJournalDetail(baselineReceipt),
                        mainHeadSha);
                }
                catch
                {
                    // Baseline observability is advisory; journal failures must not affect the gate.
                }
            }

            try
            {
                ReconcileCleanBaselineAttention(
                    CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory),
                    goal,
                    mainHeadSha,
                    baselineReceipt);
            }
            catch
            {
                // Operator attention is advisory; collaboration failures must not affect the gate.
            }

            AcceptanceVerificationResult verification;
            try
            {
                var gateProgressEventWriter = new ConductEventLogWriter(
                    Path.Combine(dir, ".orchestrator", "logs", ConductEventLogWriter.CurrentFileName));
                using var progressSink = GoalAcceptanceVerifier.PushGateProgressSink(progress =>
                    AppendGateProgressEvent(gateProgressEventWriter, goal.Id, progress));
                using var cancellationProbe = GoalAcceptanceVerifier.PushGateCancellationProbe(
                    () => IsAcceptanceAttemptCancelled(workspace, goal.Id));
                verification = acceptanceVerifier.RunAsync(
                    worktreePath,
                    goal.Id,
                    changedFiles,
                    stableSlotIndex,
                    stableSlotLease,
                    cancellationToken).GetAwaiter().GetResult();
            }
            catch (AcceptanceInfrastructureDeferredException ex)
            {
                GoalOperationJournal.AcceptanceBlocked(
                    dir,
                    goal,
                    "conductor:acceptance",
                    $"INFRASTRUCTURE_DEFERRED:{ex.ReasonCode}",
                    branchHeadSha,
                    mainHeadSha,
                    $"Acceptance blocked:INFRASTRUCTURE_DEFERRED:{ex.ReasonCode} for candidate {FormatAcceptanceCandidate(branchHeadSha, mainHeadSha)}: {ex.Message}",
                    acceptanceAttemptStartedAt);
                throw;
            }
            catch (DotnetBuildSlotsBusyException ex)
            {
                GoalOperationJournal.AcceptanceBlocked(
                    dir,
                    goal,
                    "conductor:acceptance",
                    "slot-unavailable",
                    branchHeadSha,
                    mainHeadSha,
                    $"Acceptance blocked:slot-unavailable for candidate {FormatAcceptanceCandidate(branchHeadSha, mainHeadSha)}: {FormatSlotsBusy(ex.SlotsBusy)}",
                    acceptanceAttemptStartedAt);
                throw;
            }
            catch (BuildLockBlockedException ex)
            {
                GoalOperationJournal.AcceptanceBlocked(
                    dir,
                    goal,
                    "conductor:acceptance",
                    "BUILD_LOCK_BLOCKED",
                    branchHeadSha,
                    mainHeadSha,
                    $"Acceptance blocked:BUILD_LOCK_BLOCKED for candidate {FormatAcceptanceCandidate(branchHeadSha, mainHeadSha)}: {FormatBuildLockBlocked(ex.Attribution)}",
                    acceptanceAttemptStartedAt);
                throw;
            }
            var unmetCriteria = verification.Checks?
                .Where(check => !check.Passed)
                .ToArray() ?? [];
            var failedChecks = verification.Checks?
                .Where(check => !check.Advisory && !check.Passed)
                .Select(check => check.Name)
                .ToArray() ?? [];
            var testResultPaths = verification.TestResultPaths ?? verification.Checks?
                .SelectMany(check => check.TestResultPaths ?? [])
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            IReadOnlyList<AcceptanceCheckAttribution>? checkAttributions = null;
            if (!verification.Passed)
            {
                try
                {
                    checkAttributions = CleanTestBaseline.Attribute(
                        baselineReceipt,
                        failedChecks,
                        baselineJournals,
                        goal.Id,
                        mainHeadSha ?? string.Empty);
                }
                catch
                {
                    baselineReceipt = CleanTestBaseline.Unattested(mainHeadSha);
                    checkAttributions = failedChecks
                        .Select(check => new AcceptanceCheckAttribution(
                            check,
                            AcceptanceFailureOrigin.Unattributed,
                            $"no baseline evidence at main {FormatShortSha(mainHeadSha)}"))
                        .ToArray();
                }
            }
            if (verification.Passed)
                GoalOperationJournal.AcceptancePassed(dir, goal, "conductor:acceptance", branchHeadSha, mainHeadSha,
                    unmetCriteria.Length == 0
                        ? $"Acceptance passed for candidate {FormatAcceptanceCandidate(branchHeadSha, mainHeadSha)} (exit {verification.ExitCode})."
                        : $"Acceptance passed for candidate {FormatAcceptanceCandidate(branchHeadSha, mainHeadSha)} (exit {verification.ExitCode}) with {unmetCriteria.Length} unmet advisory criterion/criteria.",
                    acceptanceAttemptStartedAt,
                    GoalOperationJournal.TryExtractBaseBuildCacheReceipt(verification));
            else if (failedChecks.Any(IsBlockingTimeoutCheck))
                GoalOperationJournal.AcceptanceBlocked(dir, goal, "conductor:acceptance", "timeout", branchHeadSha, mainHeadSha,
                    $"Acceptance blocked:timeout for candidate {FormatAcceptanceCandidate(branchHeadSha, mainHeadSha)} (exit {verification.ExitCode}).{FormatFailureTail(verification.OutputTail)}",
                    acceptanceAttemptStartedAt,
                    GoalOperationJournal.TryExtractBaseBuildCacheReceipt(verification),
                    failedChecks);
            else
                GoalOperationJournal.AcceptanceFailed(dir, goal, "conductor:acceptance", branchHeadSha, mainHeadSha,
                    $"Acceptance failed for candidate {FormatAcceptanceCandidate(branchHeadSha, mainHeadSha)} (exit {verification.ExitCode}).{FormatFailureTail(verification.OutputTail)}",
                    acceptanceAttemptStartedAt,
                    GoalOperationJournal.TryExtractBaseBuildCacheReceipt(verification),
                    failedChecks);
            return new AcceptanceVerificationSummary(
                verification.Passed,
                unmetCriteria,
                verification.Passed ? null : verification.OutputTail,
                failedChecks,
                branchHeadSha,
                mainHeadSha,
                testResultPaths,
                checkAttributions,
                verification.Passed ? null : CleanTestBaseline.FormatFailureAttestation(baselineReceipt));
        };

        FocusedEvidenceRunResult RunFocusedEvidence(
            Goal goal,
            string request,
            DotnetBuildEnvironmentLease? stableSlotLease,
            bool runBaselineArm,
            CancellationToken cancellationToken)
        {
            var worktreePath = GoalWorktrees.TryResolve(dir, goal.Id);
            if (worktreePath is null)
            {
                return new FocusedEvidenceRunResult(
                    request,
                    Accepted: false,
                    Passed: false,
                    Summary: "goal worktree not found for focused evidence request",
                    Checks: []);
            }

            GoalOperationJournal.Begin(dir, goal, "conductor:finding-evidence", $"Running focused finding evidence: {request}");
            var result = acceptanceVerifier.RunFocusedEvidenceAsync(
                    worktreePath,
                    goal.Id,
                    request,
                    stableSlotLease: stableSlotLease,
                    runBaselineArm: runBaselineArm,
                    cancellationToken: cancellationToken)
                .GetAwaiter()
                .GetResult();
            if (result.Passed)
            {
                GoalOperationJournal.Completed(dir, goal, "conductor:finding-evidence", result.Summary);
            }
            else
            {
                GoalOperationJournal.Failed(dir, goal, "conductor:finding-evidence", result.Summary);
            }

            return result;
        }

        _runFocusedEvidence = (goal, request, stableSlotLease, cancellationToken) =>
            RunFocusedEvidence(goal, request, stableSlotLease, runBaselineArm: false, cancellationToken);
        _runDualArmFocusedEvidence = (goal, request, stableSlotLease, cancellationToken) =>
            RunFocusedEvidence(goal, request, stableSlotLease, runBaselineArm: true, cancellationToken);
        _focusedEvidenceRunnerConfigured = true;

        _retryTask = (goalId, taskId, message, retryRoundKind) =>
            kernel.RetryTask(goalId, taskId, message, retryRoundKind: retryRoundKind);
        _recordTaskNote = (goalId, taskId, message) =>
        {
            kernel.RecordTaskNote(goalId, taskId, message);
        };
        _recordFindingEvidenceRequest = (goalId, taskId, message) =>
            kernel.RecordFindingEvidenceRequest(goalId, taskId, message);
        _recordFindingEvidenceRun = (goalId, taskId, message) =>
            kernel.RecordFindingEvidenceRun(goalId, taskId, message);
        _recordFindingEvidenceOutcome = (goalId, taskId, stableId, outcome, receipt) =>
        {
            kernel.RecordFindingEvidenceOutcome(goalId, taskId, stableId, outcome, receipt);
        };
        _recordPreReviewEvidence = (goalId, taskId, receipt) =>
            kernel.RecordPreReviewEvidence(goalId, taskId, receipt);
        _recordPreReviewMappingEscalationSuppressed = (goalId, taskId, candidateSha, suppressedCount) =>
            kernel.RecordPreReviewMappingEscalationSuppressed(goalId, taskId, candidateSha, suppressedCount);
        _recordCriterionRetryFeedback = kernel.RecordCriterionRetryFeedback;
        _clearCriterionRetryFeedback = kernel.ClearCriterionRetryFeedback;
        _recordAcceptanceFailure = (
            goal,
            failedChecks,
            branchHeadSha,
            mainHeadSha,
            checkAttributions,
            baselineAttestation) =>
            kernel.RecordAcceptanceFailure(
                goal.Id,
                failedChecks,
                branchHeadSha,
                mainHeadSha,
                checkAttributions,
                baselineAttestation);
        _clearAcceptanceFailure = goal => kernel.ClearAcceptanceFailure(goal.Id);
        _normalizeLifecycleState = (goal, reason) => kernel.NormalizeGoalLifecycleState(goal.Id, reason);
        _recordMissingBranchRetirement = RecordMissingBranchRetirement;

        _runAdvisorySemanticAcceptance = (goal, _) =>
        {
            var worktreePath = GoalWorktrees.TryResolve(dir, goal.Id);
            if (worktreePath is null)
            {
                return;
            }

            GoalOperationJournal.Begin(dir, goal, "conductor:semantic-acceptance", "Running advisory semantic acceptance.");
            GoalLandingPostActions.RunAdvisorySemanticAcceptance(
                goal,
                workspace,
                providers ?? new InMemoryModelProviderRegistry([]),
                profiles,
                worktreePath,
                null,
                Console.WriteLine);
            GoalOperationJournal.Completed(dir, goal, "conductor:semantic-acceptance", "Advisory semantic acceptance invoked.");
        };

        _integrateMainBeforeDeveloperDispatch = goal =>
        {
            GoalOperationJournal.Begin(
                dir,
                goal,
                "conductor:developer-branch-integration",
                "Checking goal branch against current main before Developer dispatch.");
            var result = IntegrateMainBeforeDeveloperDispatch(dir, goal);
            if (result.CanDispatch)
            {
                GoalOperationJournal.Completed(
                    dir,
                    goal,
                    "conductor:developer-branch-integration",
                    result.Message);
            }
            else
            {
                GoalOperationJournal.Failed(
                    dir,
                    goal,
                    "conductor:developer-branch-integration",
                    result.Message);
            }
            RefreshJournal(goal.Id);
            return result;
        };
        _rebaseOntoMain = goal => GoalWorktrees.TryRebaseOntoMain(dir, goal.Id);
        _recheckPreLandingRebaseConflict = goal =>
        {
            var worktreePath = GoalWorktrees.TryResolve(dir, goal.Id);
            if (worktreePath is null)
            {
                return new LandingEscalationRecheckResult(
                    ConditionResolved: false,
                    Status: "MissingWorktree",
                    Observation: $"Goal branch {goal.Id.Value[..8]} has no registered worktree.",
                    EvidenceFingerprint: "worktree=missing");
            }

            var evidence = ReadLandingRecheckEvidence(worktreePath);
            return ClassifyPreLandingRebaseConflict(
                () => new WorkerGitContext().ReadReviewerMergeTreeStatus(
                    worktreePath,
                    evidence.MainHead,
                    evidence.BranchHead),
                evidence.Fingerprint);
        };

        _land = (goal, policy) =>
        {
            GoalOperationJournal.Begin(dir, goal, "conductor:land", "Landing goal via integration branch.");
            var result = LandingExecutor.Execute(
                kernel,
                goal,
                workspace,
                channel,
                policy,
                eventWriter,
                LandingMutationBlocker);
            if (result.MainAdvanced)
                GoalOperationJournal.Completed(dir, goal, "conductor:land", result.Message);
            else
                GoalOperationJournal.Failed(dir, goal, "conductor:land", result.Message);
            RefreshJournal(goal.Id);
            return result;
        };

        _afterSuccessfulLanding = (goal, result) =>
        {
            if (!result.MainAdvanced)
            {
                return;
            }

            RemoteGitMirror.EnqueueAfterLanding(dir, goal);
            RemoteGitMirror.TryStartBackgroundProcessing(kernel, dir, goal.Id);
            RefreshJournal(goal.Id);

            if (goal.SourceBacklogItemId is null)
            {
                return;
            }

            GoalOperationJournal.Begin(dir, goal, "conductor:backlog-close", "Closing linked source backlog item.");
            var closed = GoalLandingPostActions.AutoCloseSourceBacklogItem(
                goal,
                workspace.BacklogStorePath,
                Console.WriteLine,
                kernel,
                dir,
                stateDbPath: workspace.SqliteStatePath);
            GoalOperationJournal.Completed(dir, goal, "conductor:backlog-close",
                closed ? "Closed linked source backlog item." : "No linked source backlog item closed.");
            RefreshJournal(goal.Id);
        };

        _record = goal =>
        {
            GoalOperationJournal.Begin(dir, goal, "conductor:record", "Recording to SQLite dogfood log.");
            GoalLandingPostActions.RecordDogfoodEntry(goal, dir, workspace.DogfoodLogStorePath, Console.WriteLine);
            GoalOperationJournal.Completed(dir, goal, "conductor:record", workspace.DogfoodLogStorePath);
            RefreshJournal(goal.Id);
        };

        _cleanup = goal =>
        {
            GoalOperationJournal.Begin(dir, goal, "conductor:cleanup", "Deferred goal cleanup scheduled for terminal sweep.");
            var cleanupBackoff = GoalWorktrees.RecordGoalCleanupNeeded(dir, goal.Id, "remove:conductor-deferred");
            var path = GoalWorktrees.WorktreePath(dir, goal.Id);
            var message = cleanupBackoff is null
                ? "Workspace cleanup deferred to terminal sweep."
                : $"Workspace cleanup deferred to terminal sweep: {GoalWorktrees.FormatCleanupBackoff(cleanupBackoff)}";
            GoalOperationJournal.Failed(dir, goal, "conductor:cleanup", message);
            RefreshJournal(goal.Id);
            return new GoalWorktreeRemoveResult(
                message,
                path,
                [],
                $"conduct {goal.Id.Value[..8].ToLowerInvariant()} --loop",
                CleanupBackoff: cleanupBackoff);
        };
        _completeGoal = goal => kernel.CompleteGoal(goal.Id, "Conductor completed goal after durable landing, recording, and cleanup evidence.");

        _writeEscalation = (goal, state, reason) =>
        {
            var source = $"conductor:{state}";
            var result = OperatorInbox.RecordLandingEscalation(workspace, goal, reason, source, channel);
            eventWriter.AppendGoalEscalated(goal.Id, state, goal.Status, reason, source);
            return result;
        };

        _classifyChangeRisk = goal =>
        {
            try
            {
                var branch = GoalWorktrees.BranchName(goal.Id);
                var result = GitCli.Run(dir, "diff", "--name-only", $"main...{branch}");
                if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.Output)) return null;
                var files = result.Output
                    .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var summary = RepositoryChangeClassifier.Classify(files);
                var riskClass = LandingDecisionEngine.ClassifyRisk(summary);
                return (ChangeRiskTier)(int)riskClass;
            }
            catch
            {
                return null;
            }
        };
        _emptyOutputBackoffDelay = Thread.Sleep;
        _recoverSandboxPrep = action => action.Execute();
        _evaluateReadiness = goal =>
        {
            var plan = SubscriptionPlanBuilder.Build(goal, agents, profiles);
            return DispatchReadinessEvaluator.EvaluateDispatchReadiness(goal, plan, DateTimeOffset.UtcNow);
        };
        _getLandingFileScopes = goal =>
        {
            var worktreePath = GoalWorktrees.TryResolve(dir, goal.Id);
            var changedFiles = worktreePath is null
                ? Array.Empty<string>()
                : GoalAcceptanceEvidenceBundleBuilder.GetChangedFiles(worktreePath);
            return changedFiles.Length == 0
                ? InferRecordedFileScopes(goal)
                : changedFiles;
        };
        _isVerificationGateSatisfied = goal => kernel.BuildVerificationGate(goal.Id).IsSatisfied;
        _gateReadyCandidateProjector = GateReadyCandidateProjector.CreateForRepository(dir);
        _getPreReviewEvidenceContext = goal =>
            BuildPreReviewEvidenceContext(
                TryResolveAcceptanceBranchHead(goal),
                _getLandingFileScopes(goal));
        _getFindingEvidenceEngineSettings = goal => AcceptanceGateEngineSettings.Load(
            GoalWorktrees.TryResolve(dir, goal.Id) ?? dir);
        RecoverCohortLandingEffects(kernel, workspace, eventWriter, _cohortAcceptanceStore);
    }

    internal static LandingEscalationRecheckResult ClassifyPreLandingRebaseConflict(
        Func<ReviewerMergeTreeStatus> readMergeTree,
        string evidenceFingerprint)
    {
        ReviewerMergeTreeStatus mergeTree;
        try
        {
            mergeTree = readMergeTree();
        }
        catch (ReviewerMergeTreeStatusException ex) when (!ex.GitProcessStarted)
        {
            return new LandingEscalationRecheckResult(
                ConditionResolved: false,
                Status: "GitMergeTreeCouldNotStart",
                Observation: ex.Message,
                EvidenceFingerprint: evidenceFingerprint,
                TerminalUnsatisfiable: true);
        }

        return mergeTree.IsClean
            ? new LandingEscalationRecheckResult(
                ConditionResolved: true,
                Status: "MergeTreeClean",
                Observation: "Read-only merge-tree check found no conflict with main.",
                EvidenceFingerprint: evidenceFingerprint)
            : new LandingEscalationRecheckResult(
                ConditionResolved: false,
                Status: "MergeTreeConflict",
                Observation: $"Read-only merge-tree check still conflicts with main: {string.Join(", ", mergeTree.ConflictPaths)}",
                EvidenceFingerprint: evidenceFingerprint);
    }

    internal static DeveloperBranchIntegrationResult IntegrateMainBeforeDeveloperDispatch(
        string executionDirectory,
        Goal goal)
    {
        var branch = GoalWorktrees.BranchName(goal.Id);
        var worktreePath = GoalWorktrees.TryResolve(executionDirectory, goal.Id);
        if (worktreePath is null)
        {
            return DeveloperIntegrationFailure($"Goal branch {branch} has no registered worktree.");
        }

        if (GitCli.IsWorktreeDirty(worktreePath))
        {
            return DeveloperIntegrationFailure(
                $"Goal branch {branch} has uncommitted changes; conductor integration cannot start from a dirty worktree.");
        }

        var currentBranch = GitCli.Run(worktreePath, "branch", "--show-current");
        if (currentBranch.ExitCode != 0 ||
            !string.Equals(currentBranch.Output.Trim(), branch, StringComparison.Ordinal))
        {
            return DeveloperIntegrationFailure(
                $"Registered worktree for {branch} is not attached to the expected branch.");
        }

        var mainHead = GitCli.Run(worktreePath, "rev-parse", "--verify", "main^{commit}");
        var branchHead = GitCli.Run(worktreePath, "rev-parse", "--verify", "HEAD^{commit}");
        if (mainHead.ExitCode != 0 || branchHead.ExitCode != 0 ||
            string.IsNullOrWhiteSpace(mainHead.Output) || string.IsNullOrWhiteSpace(branchHead.Output))
        {
            return DeveloperIntegrationFailure(
                $"Could not resolve main and {branch} before Developer dispatch.");
        }

        var mainRevision = mainHead.Output.Trim();
        var branchRevision = branchHead.Output.Trim();
        if (GitCli.Run(worktreePath, "merge-base", "--is-ancestor", mainRevision, branchRevision).ExitCode == 0)
        {
            return new DeveloperBranchIntegrationResult(
                DeveloperBranchIntegrationStatus.Current,
                $"Goal branch {branch} is already current with main at {mainRevision[..12]}.",
                []);
        }

        ReviewerMergeTreeStatus mergeTree;
        try
        {
            mergeTree = new WorkerGitContext().ReadReviewerMergeTreeStatus(
                worktreePath,
                mainRevision,
                branchRevision);
        }
        catch (ReviewerMergeTreeStatusException ex)
        {
            return DeveloperIntegrationFailure(
                $"Conductor could not inspect divergence for {branch} before Developer dispatch: {ex.Message}");
        }

        if (!mergeTree.IsClean)
        {
            return new DeveloperBranchIntegrationResult(
                DeveloperBranchIntegrationStatus.Conflict,
                BuildDeveloperIntegrationConflictMessage(branch, mergeTree.ConflictPaths),
                mergeTree.ConflictPaths);
        }

        var merge = GitCli.Run(
            worktreePath,
            "merge",
            "--no-ff",
            mainRevision,
            "-m",
            $"Integrate main into {branch} before Developer dispatch");
        if (merge.ExitCode != 0)
        {
            var conflictPaths = ReadUnmergedPaths(worktreePath);
            _ = GitCli.Run(worktreePath, "merge", "--abort");
            if (conflictPaths.Length > 0)
            {
                return new DeveloperBranchIntegrationResult(
                    DeveloperBranchIntegrationStatus.Conflict,
                    BuildDeveloperIntegrationConflictMessage(branch, conflictPaths),
                    conflictPaths);
            }

            var diagnostic = string.Join(
                " | ",
                new[] { merge.Error, merge.Output }
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Select(value => value.Trim().ReplaceLineEndings(" | ")));
            return DeveloperIntegrationFailure(
                $"Conductor could not integrate main into {branch} before Developer dispatch: " +
                (diagnostic.Length == 0 ? $"git merge exited {merge.ExitCode}." : diagnostic));
        }

        var integratedHead = GitCli.Run(worktreePath, "rev-parse", "--verify", "HEAD^{commit}");
        var integrationIsCurrent = integratedHead.ExitCode == 0 &&
            GitCli.Run(
                worktreePath,
                "merge-base",
                "--is-ancestor",
                mainRevision,
                integratedHead.Output.Trim()).ExitCode == 0;
        if (!integrationIsCurrent || GitCli.IsWorktreeDirty(worktreePath))
        {
            return DeveloperIntegrationFailure(
                $"Conductor integrated main into {branch}, but the resulting branch failed the clean/current invariant; Developer dispatch is blocked.");
        }

        return new DeveloperBranchIntegrationResult(
            DeveloperBranchIntegrationStatus.Integrated,
            $"Conductor integrated main {mainRevision[..12]} into {branch} before Developer dispatch at {integratedHead.Output.Trim()[..12]}.",
            []);
    }

    private static DeveloperBranchIntegrationResult DeveloperIntegrationFailure(string message) =>
        new(DeveloperBranchIntegrationStatus.Failed, message, []);

    private static string BuildDeveloperIntegrationConflictMessage(
        string branch,
        IReadOnlyList<string> conflictPaths) =>
        $"Developer dispatch blocked: main conflicts with {branch} in {string.Join(", ", conflictPaths)}. " +
        "Conflict resolution requires semantic ownership and must be performed by the conductor or operator; " +
        "do not instruct a worker to rebase or resolve the branch integration.";

    private static string[] ReadUnmergedPaths(string worktreePath)
    {
        var result = GitCli.Run(worktreePath, "diff", "--name-only", "--diff-filter=U");
        return result.ExitCode == 0
            ? result.Output
                .ReplaceLineEndings("\n")
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray()
            : [];
    }

    internal ConductorDriver(
        Func<Goal, GoalLifecycleFacts> getFacts,
        Func<int> getRunningPaidWorkerCount,
        Func<Goal, string> createWorkspace,
        Func<Goal, DispatchStartOutcome> dispatchAndStart,
        Func<Goal, DispatchStartOutcome>? startRecordedDispatches,
        Action? buildServerShutdown,
        Func<Goal, AcceptanceVerificationSummary> runAcceptanceVerification,
        Action<Goal, AcceptanceVerificationSummary>? runAdvisorySemanticAcceptance,
        Func<GoalId, TaskId, string, TaskSpec>? retryTask,
        Action<GoalId, TaskId, string>? recordTaskNote,
        Func<GoalId, TaskId, IReadOnlyList<string>, int>? recordCriterionRetryFeedback,
        Action<GoalId, TaskId>? clearCriterionRetryFeedback,
        Func<Goal, GoalWorktreeRebaseResult> rebaseOntoMain,
        Func<Goal, ConductorAutonomyPolicy, LandingResult> land,
        Action<Goal, LandingResult>? afterSuccessfulLanding,
        Action<Goal> record,
        Func<Goal, GoalWorktreeRemoveResult> cleanup,
        Action<Goal, GoalLifecycleState, string> writeEscalation,
        Func<Goal, ChangeRiskTier?> classifyChangeRisk,
        Action<TimeSpan>? emptyOutputBackoffDelay = null,
        Func<Goal, DispatchReadinessVerdict>? evaluateReadiness = null,
        Action<Goal, IReadOnlyList<string>, string?, string?>? recordAcceptanceFailure = null,
        Action<
            Goal,
            IReadOnlyList<string>,
            string?,
            string?,
            IReadOnlyList<AcceptanceCheckAttribution>?,
            string?>? recordAcceptanceFailureWithAttribution = null,
        Action<Goal>? clearAcceptanceFailure = null,
        Action<Goal>? completeGoal = null,
        Func<Goal, string, bool>? normalizeLifecycleState = null,
        Func<WorkerSandboxPrepRecoverableAction, bool>? recoverSandboxPrep = null,
        Action<Goal, string>? recordMissingBranchRetirement = null,
        Func<Goal, IReadOnlyList<string>>? getLandingFileScopes = null,
        Func<Goal, int?, AcceptanceVerificationSummary>? runAcceptanceVerificationWithSlot = null,
        Func<Goal, int?, DotnetBuildEnvironmentLease?, CancellationToken, AcceptanceVerificationSummary>? runAcceptanceVerificationWithLease = null,
        Func<bool>? hasGateReadyGoal = null,
        ConductorParallelAcceptanceAttemptCoordinator? parallelAcceptanceAttemptCoordinator = null,
        Func<Goal, string, FocusedEvidenceRunResult>? runFocusedEvidence = null,
        Action<GoalId, TaskId, string>? recordReviewerEvidenceRequestReceived = null,
        Action<GoalId, TaskId, string>? recordReviewerEvidenceRunRecorded = null,
        Func<GoalId, TaskId, string, RetryRoundKind?, TaskSpec>? retryTaskWithRoundKind = null,
        Func<Goal, string?>? tryBuildAwaitingClarificationEscalationReason = null,
        Func<Goal, int>? getAcceptanceSlotCount = null,
        Func<int>? getWorkerAdmissionCapacity = null,
        TimeSpan? buildServerShutdownTimeout = null,
        Func<Goal, GoalLifecycleState, string, LandingEscalationWriteResult>? writeEscalationWithResult = null,
        Func<Goal, PreReviewEvidenceContext>? getPreReviewEvidenceContext = null,
        Action<GoalId, TaskId, PreReviewEvidenceReceipt>? recordPreReviewEvidence = null,
        Action<GoalId, TaskId, string, int>? recordPreReviewMappingEscalationSuppressed = null,
        ConductorParallelAcceptanceAttemptCoordinator? focusedEvidenceAttemptCoordinator = null,
        Func<Goal, LandingEscalationRecheckResult>? recheckPreLandingRebaseConflict = null,
        Action<GoalId, TaskId, string, FindingEvidenceOutcome, FindingEvidenceReceipt?>? recordFindingEvidenceOutcome = null,
        Action<GoalId, TaskId, string>? recordFindingEvidenceRequest = null,
        Action<GoalId, TaskId, string>? recordFindingEvidenceRun = null,
        Func<Goal, AcceptanceGateEngineSettings>? getFindingEvidenceEngineSettings = null,
        Func<Goal, bool>? isVerificationGateSatisfied = null,
        GateReadyCandidateProjector? gateReadyCandidateProjector = null,
        Func<
            ConductorAcceptanceCohortSelection,
            IReadOnlyList<Goal>,
            ConductorAutonomyPolicy,
            ConductorAcceptanceCohortRunResult>? runAcceptanceCohort = null,
        Func<Goal, string, IDisposable?>? tryAcquireEvidenceMutationLease = null,
        Func<Goal, DeveloperBranchIntegrationResult>? integrateMainBeforeDeveloperDispatch = null)
    {
        _getFacts = getFacts;
        _getRunningPaidWorkerCount = getRunningPaidWorkerCount;
        _createWorkspace = createWorkspace;
        _integrateMainBeforeDeveloperDispatch = integrateMainBeforeDeveloperDispatch ?? (_ =>
            new DeveloperBranchIntegrationResult(
                DeveloperBranchIntegrationStatus.Current,
                "Goal branch is current with main.",
                []));
        _dispatchAndStart = (goal, _) => dispatchAndStart(goal);
        _startRecordedDispatches = startRecordedDispatches is null
            ? _dispatchAndStart
            : (goal, _) => startRecordedDispatches(goal);
        _buildServerShutdownTimeout = buildServerShutdownTimeout ?? DefaultBuildServerShutdownTimeout;
        _buildServerShutdown = timeout => RunBoundedBuildServerShutdown(
            buildServerShutdown ?? (() => { }),
            timeout);
        _runAcceptanceVerification = runAcceptanceVerificationWithLease
            ?? (runAcceptanceVerificationWithSlot is not null
                ? ((goal, slot, _, _) => runAcceptanceVerificationWithSlot(goal, slot))
                : ((goal, _, _, _) => runAcceptanceVerification(goal)));
        _runAdvisorySemanticAcceptance = runAdvisorySemanticAcceptance ?? ((_, _) => { });
        _runFocusedEvidence = runFocusedEvidence is null
            ? ((_, request, _, _) => new FocusedEvidenceRunResult(
                request,
                Accepted: false,
                Passed: false,
                Summary: "focused evidence runner was not configured",
                Checks: []))
            : ((goal, request, _, _) => runFocusedEvidence(goal, request));
        _runDualArmFocusedEvidence = _runFocusedEvidence;
        _focusedEvidenceRunnerConfigured = runFocusedEvidence is not null;
        _getPreReviewEvidenceContext = getPreReviewEvidenceContext ??
            (_ => new PreReviewEvidenceContext(
                CandidateSha: "test-constructor-candidate",
                SelectedFocusedTests: [],
                FocusedRequest: null,
                MappingReason: "test constructor supplied no changed-file mapping",
                NoApplicableTests: true,
                MappingNeedsInput: false));
        _getFindingEvidenceEngineSettings = getFindingEvidenceEngineSettings ??
            (_ => new AcceptanceGateEngineSettings());
        _recordPreReviewEvidence = recordPreReviewEvidence ?? ((_, _, _) => { });
        _recordPreReviewMappingEscalationSuppressed = recordPreReviewMappingEscalationSuppressed ?? ((_, _, _, _) => { });
        _retryTask = retryTaskWithRoundKind
            ?? (retryTask is not null
                ? ((goalId, taskId, message, _) => retryTask(goalId, taskId, message))
                : ((_, _, _, _) => throw new InvalidOperationException("Retry delegate was not configured.")));
        _recordTaskNote = recordTaskNote ?? ((_, _, _) => { });
        _recordFindingEvidenceRequest = recordFindingEvidenceRequest ?? recordReviewerEvidenceRequestReceived ?? ((_, _, _) => { });
        _recordFindingEvidenceRun = recordFindingEvidenceRun ?? recordReviewerEvidenceRunRecorded ?? ((_, _, _) => { });
        _recordFindingEvidenceOutcome = recordFindingEvidenceOutcome ?? ((_, _, _, _, _) => { });
        _recordCriterionRetryFeedback = recordCriterionRetryFeedback ?? ((_, _, _) => throw new InvalidOperationException("Criterion retry feedback delegate was not configured."));
        _clearCriterionRetryFeedback = clearCriterionRetryFeedback ?? ((_, _) => { });
        _recordAcceptanceFailure = recordAcceptanceFailureWithAttribution
            ?? (recordAcceptanceFailure is null
                ? ((_, _, _, _, _, _) => { })
                : ((goal, checks, branch, main, _, _) =>
                    recordAcceptanceFailure(goal, checks, branch, main)));
        _clearAcceptanceFailure = clearAcceptanceFailure ?? (_ => { });
        _rebaseOntoMain = rebaseOntoMain;
        _recheckPreLandingRebaseConflict = recheckPreLandingRebaseConflict ?? (_ =>
            new LandingEscalationRecheckResult(
                ConditionResolved: false,
                Status: "NotConfigured",
                Observation: "Landing escalation conflict recheck was not configured.",
                EvidenceFingerprint: "recheck=not-configured"));
        _land = land;
        _afterSuccessfulLanding = afterSuccessfulLanding ?? ((_, _) => { });
        _record = record;
        _cleanup = cleanup;
        _completeGoal = completeGoal ?? (_ => { });
        _writeEscalation = writeEscalationWithResult ?? ((goal, state, reason) =>
        {
            writeEscalation(goal, state, reason);
            return LandingEscalationWriteResult.CompletedWithoutExternalSinks;
        });
        _classifyChangeRisk = classifyChangeRisk;
        _emptyOutputBackoffDelay = emptyOutputBackoffDelay ?? Thread.Sleep;
        _evaluateReadiness = evaluateReadiness ?? (goal =>
            GoalManagementCommandService.HasAssignedDispatchCandidates(goal)
                ? new DispatchReadinessReady()
                : new DispatchReadinessBlocked("No assigned dispatch candidates"));
        _normalizeLifecycleState = normalizeLifecycleState ?? ((_, _) => false);
        _recoverSandboxPrep = recoverSandboxPrep ?? (action => action.Execute());
        _recordMissingBranchRetirement = recordMissingBranchRetirement ?? ((_, _) => { });
        _getLandingFileScopes = getLandingFileScopes ?? InferRecordedFileScopes;
        _isVerificationGateSatisfied = isVerificationGateSatisfied ?? (_ => false);
        _gateReadyCandidateProjector = gateReadyCandidateProjector;
        _runAcceptanceCohortOverride = runAcceptanceCohort;
        _getAcceptanceSlotCount = getAcceptanceSlotCount ?? (_ => ConductorBatchLoop.DefaultParallelAcceptanceCapacity);
        _getWorkerAdmissionCapacity = getWorkerAdmissionCapacity ?? (() => ConductorBatchLoop.WorkerAdmissionCapacity);
        _hasGateReadyGoal = hasGateReadyGoal ?? (() => false);
        _tryBuildAwaitingClarificationEscalationReason =
            tryBuildAwaitingClarificationEscalationReason ?? (_ => null);
        _tryAcquireEvidenceMutationLease =
            tryAcquireEvidenceMutationLease ?? ((_, _) => NoopEvidenceMutationLease.Instance);
        _executionDirectory = null;
        _parallelAcceptanceEnabled =
            runAcceptanceVerificationWithSlot is not null ||
            runAcceptanceVerificationWithLease is not null;
        _parallelAcceptanceAttemptCoordinator = parallelAcceptanceAttemptCoordinator
            ?? new ConductorParallelAcceptanceAttemptCoordinator(
                Path.Combine(Path.GetTempPath(), "mcg-conductor-acceptance-attempts", Guid.NewGuid().ToString("N")),
                runInline: true);
        _focusedEvidenceAttemptCoordinator = focusedEvidenceAttemptCoordinator
            ?? new ConductorParallelAcceptanceAttemptCoordinator(
                Path.Combine(Path.GetTempPath(), "mcg-conductor-focused-evidence-attempts", Guid.NewGuid().ToString("N")),
                runInline: true,
                acquireStableSlotLease: (_, _) => null);
    }

    internal ConductorParallelAcceptanceAttemptCoordinator ParallelAcceptanceAttemptCoordinator =>
        _parallelAcceptanceAttemptCoordinator;

    internal string? ExecutionDirectory => _executionDirectory;

    internal bool ParallelAcceptanceEnabled => _parallelAcceptanceEnabled;

    internal int GetAcceptanceSlotCount(Goal goal) => _getAcceptanceSlotCount(goal);

    internal void BeginTick() => _buildServerShutdownRanThisTick = false;

    internal static DispatchStartOutcome ClassifySubscriptionStartForConductor(SubscriptionStartResult result)
    {
        if (result.Processes.RecoveryActions?.FirstOrDefault() is { } recoveryAction)
        {
            return DispatchStartOutcome.RecoverableSandboxPrep(recoveryAction);
        }

        if (result.Processes.Tasks.Count > 0)
        {
            return DispatchStartOutcome.Started();
        }

        if (result.Processes.StartFailures?.FirstOrDefault() is { } startFailure)
        {
            return DispatchStartOutcome.SpawnFailed(startFailure.Reason);
        }

        if (result.Processes.RequeueSkippedCount > 0)
        {
            return DispatchStartOutcome.EmptyBatch(
                $"Skipped {result.Processes.RequeueSkippedCount} automatic interrupted-dispatch requeue(s) after terminal-state preflight");
        }

        var reason = result.Dispatches.Count == 0
            ? DescribeEmptyBatch(result.ParallelPlan, result.BlockedDiagnostics)
            : $"Dispatched {result.Dispatches.Count} task(s) but no processes started (spawn failed)";
        return result.Dispatches.Count == 0
            ? DispatchStartOutcome.EmptyBatch(reason)
            : DispatchStartOutcome.SpawnFailed(reason);
    }

    internal static DispatchStartOutcome ClassifyRecordedDispatchStartForConductor(ProcessBatchExecutionResult result)
    {
        if (result.RecoveryActions?.FirstOrDefault() is { } recoveryAction)
        {
            return DispatchStartOutcome.RecoverableSandboxPrep(recoveryAction);
        }

        if (result.Tasks.Count > 0)
        {
            return DispatchStartOutcome.Started();
        }

        if (result.StartFailures?.FirstOrDefault() is { } startFailure)
        {
            return DispatchStartOutcome.SpawnFailed(startFailure.Reason);
        }

        if (result.RequeueSkippedCount > 0)
        {
            return DispatchStartOutcome.EmptyBatch(
                $"Skipped {result.RequeueSkippedCount} automatic interrupted-dispatch requeue(s) after terminal-state preflight");
        }

        return DispatchStartOutcome.EmptyBatch(FormatNoRecordedDispatchStartedReason(result.Plan));
    }

    public ConductorAdvanceResult AdvanceOnce(Goal goal, ConductorAutonomyPolicy policy)
    {
        var goalId = goal.Id.Value;
        var goalPrefix = goalId[..8];

        _normalizeLifecycleState(
            goal,
            $"Conductor auto-repaired terminal goal with non-terminal task(s) before lifecycle resolution for goal {goalPrefix}.");

        var facts = GetFacts(goal);
        var state = GoalLifecycle.ResolveState(goal, facts);

        if (state == GoalLifecycleState.CleanedUp)
            return MakeResult(goalId, goalPrefix, policy, new ConductorAdvanceOutcome.Done(state));

        // Empty stdout from a subscription worker means the CLI never produced a worker verdict. Treat
        // it as provider/startup flake, retry on a dedicated budget, and only escalate after all bounded
        // auto-recover cycles are spent. Any non-empty stdout resets the task counter in TaskSpec and is
        // handled as a genuine worker result.
        if (state == GoalLifecycleState.Failed)
        {
            var runningSibling = goal.Tasks.FirstOrDefault(task => task.LastProcess is { IsRunning: true });
            if (runningSibling is not null)
            {
                return MakeResult(
                    goalId,
                    goalPrefix,
                    policy,
                    new ConductorAdvanceOutcome.Held(
                        state,
                        $"Failure handling deferred while task {runningSibling.Id.Value[..8]} still has a live worker process."));
            }

            var inconclusiveTester = goal.Tasks.FirstOrDefault(t =>
                t.RequiredRole == AgentRole.Tester &&
                t.Status == WorkTaskStatus.Failed &&
                t.LastVerification is { } latest &&
                DispatchFailureClassifier.Classify(t, latest).Kind == DispatchOutcomeKind.VerificationInconclusive);
            if (inconclusiveTester is not null)
            {
                var outcome = DispatchFailureClassifier.Classify(inconclusiveTester, inconclusiveTester.LastVerification!);
                var maxAttempts = policy.MaxEmptyOutputDispatchRetries * policy.MaxEmptyOutputAutoRecoverCycles;
                if (inconclusiveTester.EmptyOutputRetryCount > maxAttempts)
                {
                    return Escalate(
                        goal,
                        goalPrefix,
                        policy,
                        state,
                        $"Tester task {inconclusiveTester.Id.Value[..8]} exhausted verification-inconclusive recovery " +
                        $"({inconclusiveTester.EmptyOutputRetryCount}/{maxAttempts}); operator action required. " +
                        $"Latest current-round receipt: {outcome.EvidenceSummary}");
                }

                var delay = ComputeEmptyOutputBackoff(policy, inconclusiveTester.EmptyOutputRetryCount);
                if (delay > TimeSpan.Zero)
                {
                    _emptyOutputBackoffDelay(delay);
                }

                var note =
                    $"Auto-retry verification-inconclusive Tester task {inconclusiveTester.Id.Value[..8]} " +
                    $"on the shared transient budget ({inconclusiveTester.EmptyOutputRetryCount}/{maxAttempts}) " +
                    $"without reopening upstream Developer work. Latest current-round receipt: {outcome.EvidenceSummary}";
                _retryTask(goal.Id, inconclusiveTester.Id, note, null);
                return ExecuteDispatchAndStart(goal, goalPrefix, policy, GoalLifecycleState.WorkspaceReady);
            }

            var staleRecoveryTask = goal.Tasks.FirstOrDefault(t =>
                t.Status == WorkTaskStatus.Failed &&
                TryGetDispatchRecoveryAction(t.LastVerification, out var action) &&
                action is DispatchRecoveryAction.RetryStale or DispatchRecoveryAction.BudgetExhausted or DispatchRecoveryAction.MarkStale);
            if (staleRecoveryTask is not null)
            {
                var action = GetDispatchRecoveryAction(staleRecoveryTask.LastVerification!);
                if (IsRetryableStaleRecovery(staleRecoveryTask.LastVerification!))
                {
                    var note = $"Auto-retry stale dispatch recovery for task {staleRecoveryTask.Id.Value[..8]}; " +
                        ExtractDispatchRecoveryDiagnostic(staleRecoveryTask.LastVerification!);
                _retryTask(goal.Id, staleRecoveryTask.Id, note, null);
                    return ExecuteDispatchAndStart(goal, goalPrefix, policy, GoalLifecycleState.WorkspaceReady);
                }

                return Escalate(goal, goalPrefix, policy, state,
                    $"Task {staleRecoveryTask.Id.Value[..8]} blocked by stale dispatch recovery; " +
                    ExtractDispatchRecoveryDiagnostic(staleRecoveryTask.LastVerification!));
            }

            var preflightFlakedTask = goal.Tasks.FirstOrDefault(t =>
                t.Status == WorkTaskStatus.Failed &&
                t.LastVerification is { } latest &&
                DispatchFailureClassifier.Classify(t, latest).Kind == DispatchOutcomeKind.PreflightFailure);
            if (preflightFlakedTask is not null)
            {
                var outcome = DispatchFailureClassifier.Classify(preflightFlakedTask, preflightFlakedTask.LastVerification!);
                // A sandbox launch-preflight failure means the worker never launched -- usually an INTERMITTENT
                // sandbox-prep hiccup that a fresh dispatch clears (most launches in the same window succeed).
                // Auto-retry on the shared transient-dispatch-flake budget and only escalate once it is spent,
                // instead of escalating the whole goal to the operator on a single flake.
                var preflightMaxAttempts = policy.MaxEmptyOutputDispatchRetries * policy.MaxEmptyOutputAutoRecoverCycles;
                if (preflightFlakedTask.EmptyOutputRetryCount > preflightMaxAttempts)
                {
                    return Escalate(goal, goalPrefix, policy, state,
                        $"Task {preflightFlakedTask.Id.Value[..8]} exhausted sandbox-preflight dispatch recovery " +
                        $"({preflightFlakedTask.EmptyOutputRetryCount}/{preflightMaxAttempts}); operator action required: {outcome.EvidenceSummary}");
                }

                var preflightDelay = ComputeEmptyOutputBackoff(policy, preflightFlakedTask.EmptyOutputRetryCount);
                if (preflightDelay > TimeSpan.Zero)
                {
                    _emptyOutputBackoffDelay(preflightDelay);
                }

                var preflightNote = $"Auto-retry sandbox-preflight dispatch flake " +
                    $"{preflightFlakedTask.EmptyOutputRetryCount}/{preflightMaxAttempts} for task " +
                    $"{preflightFlakedTask.Id.Value[..8]}; worker never launched (preflight failure): {outcome.EvidenceSummary}";
                _retryTask(goal.Id, preflightFlakedTask.Id, preflightNote, null);
                return ExecuteDispatchAndStart(goal, goalPrefix, policy, GoalLifecycleState.WorkspaceReady);
            }

            var realFailureTask = goal.Tasks.FirstOrDefault(t =>
            {
                if (t.Status != WorkTaskStatus.Failed || t.LastVerification is not { } latest)
                {
                    return false;
                }

                var outcome = DispatchFailureClassifier.Classify(t, latest);
                var classification = TaskOutcomeClassifier.Classify(
                    WorkTaskStatus.Failed,
                    TaskOutcomeClassifier.TryExtractRule(outcome.ClassifierReceipt));
                return outcome.RecoveryRecommendation == RecoveryRecommendation.AutoRetry &&
                    classification.Class == TaskOutcomeClass.RealFailure;
            });
            if (realFailureTask is not null)
            {
                var verification = realFailureTask.LastVerification!;
                var outcome = DispatchFailureClassifier.Classify(realFailureTask, verification);
                if (goal.AutomaticAcceptanceRetryCount >= policy.MaxCriterionRetries)
                {
                    return Escalate(
                        goal,
                        goalPrefix,
                        policy,
                        state,
                        $"Task {realFailureTask.Id.Value[..8]} exhausted bounded real-failure retries " +
                        $"({goal.AutomaticAcceptanceRetryCount}/{policy.MaxCriterionRetries}); " +
                        $"failed command: {verification.Command}; failure evidence: {outcome.EvidenceSummary}");
                }

                var retryFeedback = new[]
                {
                    $"Failed command: {verification.Command}",
                    $"Failure evidence: {outcome.EvidenceSummary}"
                };
                var retryCount = _recordCriterionRetryFeedback(
                    goal.Id,
                    realFailureTask.Id,
                    retryFeedback);
                var retryNote =
                    $"Auto-retry real worker/command failure for task {realFailureTask.Id.Value[..8]} " +
                    $"(attempt {retryCount}/{policy.MaxCriterionRetries}); " +
                    string.Join("; ", retryFeedback);
                _retryTask(goal.Id, realFailureTask.Id, retryNote, null);
                return ExecuteDispatchAndStart(goal, goalPrefix, policy, GoalLifecycleState.WorkspaceReady);
            }

            var flakedTask = goal.Tasks.FirstOrDefault(t =>
                t.Status == WorkTaskStatus.Failed &&
                t.LastVerification is { } latest && DispatchFailureClassifier.Classify(t, latest).Kind is
                    DispatchOutcomeKind.LaunchFailure or DispatchOutcomeKind.EmptyOutputFlake &&
                t.EmptyOutputRetryCount > 0);
            if (flakedTask is not null)
            {
                var maxAttempts = policy.MaxEmptyOutputDispatchRetries * policy.MaxEmptyOutputAutoRecoverCycles;
                if (flakedTask.EmptyOutputRetryCount > maxAttempts)
                {
                    return Escalate(goal, goalPrefix, policy, state,
                        $"Task {flakedTask.Id.Value[..8]} exhausted empty-output dispatch recovery " +
                        $"({flakedTask.EmptyOutputRetryCount}/{maxAttempts}); operator action required");
                }

                var delay = ComputeEmptyOutputBackoff(policy, flakedTask.EmptyOutputRetryCount);
                if (delay > TimeSpan.Zero)
                {
                    _emptyOutputBackoffDelay(delay);
                }

                var attemptInCycle = ((flakedTask.EmptyOutputRetryCount - 1) % policy.MaxEmptyOutputDispatchRetries) + 1;
                var cycle = ((flakedTask.EmptyOutputRetryCount - 1) / policy.MaxEmptyOutputDispatchRetries) + 1;
                var launchFailure = DispatchFailureClassifier.Classify(flakedTask, flakedTask.LastVerification!).Kind == DispatchOutcomeKind.LaunchFailure;
                var sandboxLaunchFailure = flakedTask.LastVerification!.ProviderFailureKind == ProviderFailureKind.Sandbox1312;
                var failureLabel = sandboxLaunchFailure
                    ? "sandbox command-launch failure"
                    : launchFailure ? "silent launch failure" : "empty-output dispatch flake";
                var failureEvidence = sandboxLaunchFailure
                    ? $"sandbox logon session failed with root exit {flakedTask.LastVerification.ExitCode}"
                    : launchFailure
                    ? $"task produced zero bytes on both streams with root exit {flakedTask.LastVerification.ExitCode}"
                    : $"task produced zero-byte stdout with exit {flakedTask.LastVerification!.ExitCode}";
                var note = attemptInCycle == policy.MaxEmptyOutputDispatchRetries
                    ? $"Auto-recover+re-admit {failureLabel} cycle {cycle}/{policy.MaxEmptyOutputAutoRecoverCycles}; {failureEvidence}"
                    : $"Auto-retry {failureLabel} {attemptInCycle}/{policy.MaxEmptyOutputDispatchRetries} " +
                        $"in recovery cycle {cycle}/{policy.MaxEmptyOutputAutoRecoverCycles}; {failureEvidence}";
                _retryTask(goal.Id, flakedTask.Id, note, null);
                // Immediately dispatch in the same tick after recovery, bypassing the next-tick
                // WorkspaceReady path. If ownership blocks dispatch under Conservative policy,
                // ExecuteDispatchAndStart returns Held (not Escalate) so the goal stays eligible.
                return ExecuteDispatchAndStart(goal, goalPrefix, policy, GoalLifecycleState.WorkspaceReady);
            }

            if (TryBuildReviewContractRepairRetry(goal, out var autoRetry) ||
                TryBuildVerifyingFindingAutoRetry(goal, policy, out autoRetry))
            {
                if (autoRetry.ShouldHold)
                {
                    return MakeResult(
                        goal.Id.Value,
                        goalPrefix,
                        policy,
                        new ConductorAdvanceOutcome.Held(state, autoRetry.Message));
                }

                if (autoRetry.ShouldEscalate)
                {
                    return Escalate(goal, goalPrefix, policy, state, autoRetry.Message);
                }

                if (autoRetry.WarningMessage is not null)
                {
                    _recordTaskNote(goal.Id, autoRetry.TargetTask!.Id, autoRetry.WarningMessage);
                }

                _retryTask(goal.Id, autoRetry.TargetTask!.Id, autoRetry.Message, autoRetry.RoundKind);
                return ExecuteDispatchAndStart(goal, goalPrefix, policy, GoalLifecycleState.WorkspaceReady);
            }
        }

        if (state == GoalLifecycleState.AwaitingClarification)
        {
            return Escalate(goal, goalPrefix, policy, state,
                _tryBuildAwaitingClarificationEscalationReason(goal) ??
                $"Goal is in {state} state; operator action required");
        }

        // Error states always escalate regardless of policy
        if (state is GoalLifecycleState.Failed
                  or GoalLifecycleState.Blocked
                  or GoalLifecycleState.AwaitingHumanInput)
        {
            return Escalate(goal, goalPrefix, policy, state,
                BuildTerminalEscalationReason(goal, state));
        }

        // TransitionMap[Merged] is the base for ExecuteLanding's risk gate (at Verified state),
        // not a gate on the post-landing record step. Skip the pre-check for Merged state.
        if (state != GoalLifecycleState.Merged)
        {
            var decision = policy.GetTransitionDecision(state);
            if (decision == ConductorTransitionDecision.Escalate)
            {
                return Escalate(goal, goalPrefix, policy, state,
                    $"Policy '{policy.Name}' requires manual review at {state}");
            }
        }

        return state switch
        {
            GoalLifecycleState.Created => ExecuteCreateWorkspace(goal, goalPrefix, policy),
            GoalLifecycleState.WorkspaceReady => ExecuteDispatchAndStart(goal, goalPrefix, policy, GoalLifecycleState.WorkspaceReady),
            GoalLifecycleState.Dispatched => ExecuteDispatchAndStart(goal, goalPrefix, policy, GoalLifecycleState.Dispatched),
            GoalLifecycleState.Running => MakeResult(goalId, goalPrefix, policy,
                new ConductorAdvanceOutcome.Held(state, "Worker process running; auto-reconcile will handle completion")),
            GoalLifecycleState.AwaitingVerification => MakeResult(goalId, goalPrefix, policy,
                new ConductorAdvanceOutcome.Held(state, "All tasks done; awaiting task verification gates — auto-reconcile will advance goal to Verified")),
            GoalLifecycleState.Verifying => MakeResult(goalId, goalPrefix, policy,
                new ConductorAdvanceOutcome.Held(state, "Acceptance gate running in background; reconciliation will handle terminal artifact")),
            GoalLifecycleState.Verified => ExecuteLanding(goal, goalPrefix, policy),
            GoalLifecycleState.Merged => ExecuteRecord(goal, goalPrefix, policy),
            GoalLifecycleState.Recorded => ExecuteCleanup(goal, goalPrefix, policy),
            _ => Escalate(goal, goalPrefix, policy, state, $"Unhandled lifecycle state {state}")
        };
    }

    internal GoalLifecycleFacts GetFacts(Goal goal) => _getFacts(goal);

    private bool TryBuildReviewContractRepairRetry(
        Goal goal,
        out VerifyingFindingAutoRetryDecision decision)
    {
        decision = VerifyingFindingAutoRetryDecision.None;
        var reviewerTask = goal.Tasks.FirstOrDefault(task =>
            (task.RequiredRole is AgentRole.Reviewer or AgentRole.Tester) &&
            task.Status == WorkTaskStatus.Failed &&
            task.LastVerification?.ReviewFindingContractViolation is not null);
        if (reviewerTask?.LastVerification?.ReviewFindingContractViolation is not { } violation)
        {
            return false;
        }

        if ((violation.Code is ReviewFindingConvergence.IdentityMovedViolationCode or
                ReviewFindingConvergence.RecycledAnchorIdentityViolationCode) &&
            reviewerTask.LastVerification.MergedReviewFindings is not null)
        {
            // The kernel retained the prior form of the invalid transition and accepted the rest of
            // the substantive round. Let normal Reviewer/Tester convergence route its real blockers.
            return false;
        }

        IReadOnlyList<ReviewFinding> canonicalLedger;
        try
        {
            canonicalLedger = AutoReviewRetryConvergenceBriefBuilder
                .ReadStructuredReviewFindingState(goal, reviewerTask);
        }
        catch (Exception ex) when (
            ex is ReviewFindingConvergenceException or InvalidOperationException or ArgumentException)
        {
            decision = VerifyingFindingAutoRetryDecision.Escalate(
                $"{reviewerTask.RequiredRole} contract-repair could not reconstruct the canonical finding ledger for task {reviewerTask.Id.Value[..8]}; " +
                $"violation={violation.Code}; diagnostic={TrimForConductorMessage(ex.Message)}; operator action required.");
            return true;
        }

        if (violation.Code is ReviewFindingConvergence.UnprovenResolutionAtCapViolationCode or
                ReviewFindingConvergence.MissingReviewRetryCapReceiptViolationCode)
        {
            var receipt = reviewerTask.LastDispatch?.ReviewRetryCap;
            decision = VerifyingFindingAutoRetryDecision.Escalate(BuildReviewCapDecisionMessage(
                goal,
                reviewerTask,
                receipt,
                violation.Message,
                FormatVerifyingRoleOutputArtifact(reviewerTask),
                canonicalLedger));
            return true;
        }

        if (violation.Code is ReviewFindingConvergence.IdentityMovedViolationCode or
                ReviewFindingConvergence.UntouchedReopenViolationCode &&
            reviewerTask.LastVerification?.ReviewFindingTouchProofDiagnostic is { Length: > 0 } touchProofDiagnostic)
        {
            decision = VerifyingFindingAutoRetryDecision.Escalate(
                $"{reviewerTask.RequiredRole} review-finding contract cannot classify touch-dependent violation {violation.Code} " +
                $"for task {reviewerTask.Id.Value[..8]} because system-derived round-diff proof is unavailable; " +
                $"suppression=missing-system-derived-round-diff-proof; operator adjudication is required and the mechanical repair budget was not consumed. " +
                $"Diagnostic: {TrimForConductorMessage(touchProofDiagnostic)}");
            return true;
        }

        var priorRepairs = CountReviewerContractRepairsInCurrentRound(goal, reviewerTask);
        if (priorRepairs >= MaxReviewFindingContractRepairsPerRound)
        {
            decision = VerifyingFindingAutoRetryDecision.Escalate(
                $"{reviewerTask.RequiredRole} exhausted the contract-repair limit ({MaxReviewFindingContractRepairsPerRound}) in the same review round for task {reviewerTask.Id.Value[..8]}; " +
                $"violation_code={violation.Code}; prior_stable_id={violation.PriorStableId ?? "none"}; " +
                $"submitted_stable_id={violation.SubmittedStableId ?? "none"}; " +
                $"prior_location={violation.PriorLocation?.ToString() ?? "none"}; " +
                $"submitted_location={violation.SubmittedLocation?.ToString() ?? "none"}; " +
                $"canonical_open_count={canonicalLedger.Count(finding => finding.State == ReviewFindingState.Open)}. " +
                "Operator remedy: retry <goal> <task#> \"<reason>\" --mechanical, then progress <task#> completed and verify-manual <task#> passed.");
            return true;
        }

        var attempt = priorRepairs + 1;
        var brief = AutoReviewRetryConvergenceBriefBuilder.BuildContractRepairBrief(
            goal,
            reviewerTask,
            violation,
            attempt,
            MaxReviewFindingContractRepairsPerRound,
            FormatVerifyingRoleOutputArtifact(reviewerTask));
        decision = VerifyingFindingAutoRetryDecision.Retry(
            reviewerTask,
            brief,
            null,
            RetryRoundKind.Mechanical);
        return true;
    }

    private bool TryBuildVerifyingFindingAutoRetry(
        Goal goal,
        ConductorAutonomyPolicy policy,
        out VerifyingFindingAutoRetryDecision decision)
    {
        decision = VerifyingFindingAutoRetryDecision.None;
        foreach (var requestingTask in goal.Tasks.Where(task => task.LastVerification is not null))
        {
            if (TryBuildFindingEvidenceRequest(goal, requestingTask, policy, out decision))
            {
                return true;
            }
        }

        var trigger = goal.Tasks
            .Select(task => BuildVerifyingFindingTrigger(goal, task))
            .FirstOrDefault(candidate => candidate is not null);
        if (trigger is null)
        {
            return false;
        }

        var triggeringTask = trigger.TriggeringTask;
        var outputArtifact = FormatVerifyingRoleOutputArtifact(triggeringTask);
        if (triggeringTask.RequiredRole == AgentRole.Reviewer)
        {
            RecordSuppressedAutoReviewRetryFindings(goal, triggeringTask, trigger.SuppressedFindings);
        }

        ReviewRetryRoute? reviewerRoute = null;
        if (triggeringTask.RequiredRole == AgentRole.Reviewer)
        {
            if (goal.RefinedSpec is { AcceptanceCriteria.Count: > 0 } &&
                !WorkerResultBlockers.TryFindCriteriaVerdicts(
                    triggeringTask.LastVerification,
                    out _,
                    out var criteriaDiagnostic))
            {
                _recordTaskNote(
                    goal.Id,
                    triggeringTask.Id,
                    $"CRITERIA_ATTESTATION missing: {TrimForConductorMessage(criteriaDiagnostic)}");
            }

            reviewerRoute = ResolveReviewerRetryRoute(goal, triggeringTask, trigger.Finding);
            if (reviewerRoute.EscalateToOperator)
            {
                decision = VerifyingFindingAutoRetryDecision.Escalate(
                    $"Reviewer needs-work blocker requires operator-owned evidence; auto-review-retry skipped for task {triggeringTask.Id.Value[..8]}. " +
                    $"Route: {reviewerRoute.Reason}. Findings: {TrimForConductorMessage(trigger.Finding)}. Full reviewer output: {outputArtifact}");
                return true;
            }
        }

        var targetRole = triggeringTask.RequiredRole == AgentRole.Tester
            ? AgentRole.Developer
            : reviewerRoute?.TargetRole ?? AgentRole.Developer;
        var targetTask = trigger.TargetTask ?? goal.Tasks
            .TakeWhile(t => t.Id != triggeringTask.Id)
            .LastOrDefault(t => t.RequiredRole == targetRole);
        if (targetTask is null && triggeringTask.RequiredRole == AgentRole.Reviewer && targetRole != AgentRole.Developer)
        {
            targetRole = AgentRole.Developer;
            targetTask = goal.Tasks
                .TakeWhile(t => t.Id != triggeringTask.Id)
                .LastOrDefault(t => t.RequiredRole == AgentRole.Developer);
        }

        if (targetTask is null)
        {
            decision = VerifyingFindingAutoRetryDecision.Escalate(
                $"{triggeringTask.RequiredRole} blocker could not be routed to an upstream {targetRole} task; operator action required. " +
                $"Findings: {TrimForConductorMessage(trigger.Finding)}. Full {triggeringTask.RequiredRole.ToString().ToLowerInvariant()} output: {outputArtifact}");
            return true;
        }

        var round = ReviewRetryCapReceipt.Create(goal, policy.ReviewAutoRetryStopRound).Round;
        if (round >= policy.ReviewAutoRetryStopRound)
        {
            decision = VerifyingFindingAutoRetryDecision.Escalate(
                triggeringTask.RequiredRole == AgentRole.Reviewer
                    ? BuildReviewCapDecisionMessage(
                        goal,
                        triggeringTask,
                        new ReviewRetryCapReceipt(round, policy.ReviewAutoRetryStopRound),
                        trigger.Finding,
                        outputArtifact,
                        triggeringTask.LastVerification?.MergedReviewFindings ?? [])
                    : $"auto-review-retry stopped at review round {round}/{policy.ReviewAutoRetryStopRound} for task {targetTask.Id.Value[..8]}; " +
                        $"operator decision required (split, supersede, or continue). Findings: {TrimForConductorMessage(trigger.Finding)}. " +
                        $"Full {triggeringTask.RequiredRole.ToString().ToLowerInvariant()} output: {outputArtifact}");
            return true;
        }

        var triggerLabel = triggeringTask.RequiredRole == AgentRole.Reviewer
            ? "verdict=needs-work"
            : "WORKER_RESULT blocker";
        string message;
        try
        {
            message = AutoReviewRetryConvergenceBriefBuilder.BuildConvergenceBrief(
                goal,
                targetTask,
                triggeringTask,
                trigger.Finding,
                triggerLabel,
                targetRole,
                round,
                outputArtifact,
                _getLandingFileScopes(goal));
        }
        catch (ReviewFindingConvergenceException ex)
        {
            decision = VerifyingFindingAutoRetryDecision.Escalate(
                $"review finding convergence violation code={ex.Code} previous_open={ex.PreviousOpenCount} next_open={ex.NextOpenCount}; " +
                $"{ex.Message} Loop stopped before another retry brief was issued. Full reviewer output: {outputArtifact}");
            return true;
        }

        var warning = round >= policy.ReviewAutoRetryWarningRound
            ? $"auto-review-retry escalation-warning round {round}/{policy.ReviewAutoRetryStopRound - 1}: " +
                $"continuing automatic retry for task {targetTask.Id.Value[..8]}; operator review will be required at round {policy.ReviewAutoRetryStopRound}."
            : null;
        decision = VerifyingFindingAutoRetryDecision.Retry(
            targetTask,
            message,
            warning,
            null);
        return true;
    }

    private VerifyingFindingTrigger? BuildVerifyingFindingTrigger(Goal goal, TaskSpec task)
    {
        if (task.Status != WorkTaskStatus.Failed)
        {
            return null;
        }

        string blocker;
        if (task.RequiredRole == AgentRole.Reviewer)
        {
            var hasNeedsWork = WorkerResultBlockers.TryFindUnsuppressedNeedsWorkVerdict(
                task.LastVerification,
                goal.EffectiveAcceptanceCriteriaCorrections,
                out blocker,
                out var suppressedFindings);
            var hasBlockedAtCap = WorkerResultBlockers.TryFindUnsuppressedBlockedAtCapVerdict(
                task.LastVerification,
                goal.EffectiveAcceptanceCriteriaCorrections,
                out var capBlocker,
                out var capSuppressedFindings);
            if (hasBlockedAtCap && task.LastDispatch?.ReviewRetryCap is not { IsAtCap: true })
            {
                return null;
            }

            if (hasNeedsWork || hasBlockedAtCap)
            {
                return new VerifyingFindingTrigger(
                    task,
                    hasBlockedAtCap ? capBlocker : blocker,
                    hasBlockedAtCap ? capSuppressedFindings : suppressedFindings,
                    null);
            }
        }

        if (task.RequiredRole != AgentRole.Tester ||
            !WorkerResultBlockers.TryGetTestsStatus(task.LastVerification, out var testsStatus) ||
            testsStatus != WorkerResultBlockers.TestsStatus.Fail ||
            !WorkerResultBlockers.TryFindHardFailureBlocker(task.LastVerification, out blocker))
        {
            return null;
        }

        var upstreamDeveloper = goal.Tasks
            .TakeWhile(t => t.Id != task.Id)
            .LastOrDefault(t => t.RequiredRole == AgentRole.Developer && HasCommittedOutput(t));
        if (upstreamDeveloper is null)
        {
            return null;
        }

        return new VerifyingFindingTrigger(task, blocker, [], upstreamDeveloper);
    }

    private static string BuildReviewCapDecisionMessage(
        Goal goal,
        TaskSpec reviewerTask,
        ReviewRetryCapReceipt? receipt,
        string trigger,
        string outputArtifact,
        IReadOnlyList<ReviewFinding> ledger)
    {
        var open = ReviewFindings.GetOpenBlockingFindings(
            ledger,
            goal.EffectiveAcceptanceCriteriaCorrections);
        var stableIds = open.Count == 0
            ? "unavailable"
            : string.Join(",", open.Select(finding => finding.StableId));
        var findings = open.Count == 0
            ? TrimForConductorMessage(trigger)
            : string.Join("; ", open.Select(finding =>
                $"stable_id={finding.StableId} description={TrimForConductorMessage(finding.Description)}"));
        var candidateSha = reviewerTask.LastVerification?.ReviewedCommit ??
            reviewerTask.LastDispatch?.BaseCommit ??
            "missing";
        var capBoundary = receipt is null
            ? "because the system-owned review-cap receipt is missing"
            : $"at review round {receipt.Round}/{receipt.StopRound}";
        return $"auto-review-retry stopped {capBoundary}: blocked-at-cap for Reviewer task {reviewerTask.Id.Value[..8]}; " +
            $"candidate_sha={candidateSha}; surviving_stable_ids={stableIds}; findings: {findings}. " +
            "operator decision required: continue work, waive the applicable criterion as an explicit override, split the goal, or supersede the requirement. " +
            $"The goal remains non-terminal and cannot advance to acceptance. Full reviewer output: {outputArtifact}";
    }

    private void RecordSuppressedAutoReviewRetryFindings(
        Goal goal,
        TaskSpec reviewerTask,
        IReadOnlyList<string> suppressedFindings)
    {
        foreach (var finding in suppressedFindings)
        {
            _recordTaskNote(
                goal.Id,
                reviewerTask.Id,
                $"Suppressed auto-review-retry finding matching operator criteria correction: {TrimForConductorMessage(finding)}");
        }
    }

    private static ReviewRetryRoute ResolveReviewerRetryRoute(
        Goal goal,
        TaskSpec reviewerTask,
        string blockerProse)
    {
        try
        {
            var openBlockingFindings = AutoReviewRetryConvergenceBriefBuilder
                .ReadStructuredReviewFindingState(goal, reviewerTask)
                .Where(finding =>
                    finding.State == ReviewFindingState.Open &&
                    finding.Severity == FindingSeverity.Blocking)
                .ToArray();
            return ReviewFindingRouting.Resolve(openBlockingFindings, blockerProse);
        }
        catch (Exception ex) when (
            ex is ReviewFindingConvergenceException or InvalidOperationException or ArgumentException)
        {
            return ReviewFindingRouting.Resolve([], blockerProse);
        }
    }

    private static bool HasCommittedOutput(TaskSpec task)
    {
        if (task.LastVerification?.HasCommittedChanges is true)
        {
            return true;
        }

        var dispatch = task.LastDispatch;
        if (dispatch is null || string.IsNullOrWhiteSpace(dispatch.ResultCommit))
        {
            return false;
        }

        return string.IsNullOrWhiteSpace(dispatch.BaseCommit) ||
            !string.Equals(dispatch.BaseCommit, dispatch.ResultCommit, StringComparison.OrdinalIgnoreCase);
    }

    private bool TryBuildFindingEvidenceRequest(
        Goal goal,
        TaskSpec requestingTask,
        ConductorAutonomyPolicy policy,
        out VerifyingFindingAutoRetryDecision decision)
    {
        decision = VerifyingFindingAutoRetryDecision.None;
        if (!WorkerResultBlockers.TryFindReviewFindingRound(requestingTask.LastVerification, out var round, out _))
        {
            return false;
        }

        var mergedFindings = requestingTask.LastVerification?.MergedReviewFindings ?? [];
        var requestingFindings = round.Findings
            .Where(finding =>
                finding.State == ReviewFindingState.Open &&
                finding.EvidenceRequest is not null)
            .Where(finding => ReviewFindingConvergence.ResolveMergedFinding(
                mergedFindings, round, finding.StableId)?.EvidenceOutcome is null)
            .ToArray();
        if (requestingFindings.Length == 0)
        {
            return false;
        }

        var candidateSha = _getPreReviewEvidenceContext(goal).CandidateSha?.Trim();
        var candidateShaAvailable = ConductorGitRevisionReader.IsValid(candidateSha);
        var telemetryCandidateSha = candidateShaAvailable ? candidateSha! : "unavailable";

        var groups = new List<(string Identity, string Request, FindingEvidenceRequest TypedRequest, List<ReviewFinding> Findings)>();
        foreach (var finding in requestingFindings)
        {
            if (!TryNormalizeFindingEvidenceRequest(
                    finding.EvidenceRequest!, _getFindingEvidenceEngineSettings(goal),
                    out var typedRequest, out var request,
                    out var refusalReason, out var refusalDetail))
            {
                RecordNotHonoured(
                    goal.Id, requestingTask, finding, refusalReason, refusalDetail, telemetryCandidateSha);
                continue;
            }

            var identity = BuildFindingEvidenceIdentity(typedRequest);
            var groupIndex = groups.FindIndex(group => string.Equals(group.Identity, identity, StringComparison.Ordinal));
            if (groupIndex < 0)
            {
                groups.Add((identity, request, typedRequest, [finding]));
            }
            else
            {
                groups[groupIndex].Findings.Add(finding);
            }
        }

        foreach (var cappedGroup in groups.Skip(policy.MaxFocusedEvidenceRunsPerRound))
        foreach (var finding in cappedGroup.Findings)
        {
            RecordNotHonoured(
                goal.Id, requestingTask, finding, FindingEvidenceNotHonouredReason.PerRoundCap,
                $"Distinct evidence request exceeded the configured per-round cap of {policy.MaxFocusedEvidenceRunsPerRound}.",
                telemetryCandidateSha);
        }

        var runnable = groups.Take(policy.MaxFocusedEvidenceRunsPerRound).FirstOrDefault();
        if (runnable.Findings is null)
        {
            decision = BuildFindingEvidenceDeliveryRetry(requestingTask, "All evidence requests were refused with typed outcomes.");
            return true;
        }

        if (!candidateShaAvailable)
        {
            foreach (var group in groups.Take(policy.MaxFocusedEvidenceRunsPerRound))
            foreach (var finding in group.Findings)
            {
                RecordNotHonoured(
                    goal.Id, requestingTask, finding, FindingEvidenceNotHonouredReason.CandidateShaMissing,
                    "No validated candidate SHA was available for the requested evidence run.",
                    telemetryCandidateSha);
            }
            decision = BuildFindingEvidenceDeliveryRetry(requestingTask, "Evidence requests could not run because the candidate SHA was unavailable.");
            return true;
        }

        if (!_focusedEvidenceRunnerConfigured)
        {
            foreach (var group in groups.Take(policy.MaxFocusedEvidenceRunsPerRound))
            foreach (var finding in group.Findings)
            {
                RecordNotHonoured(
                    goal.Id, requestingTask, finding, FindingEvidenceNotHonouredReason.ExecutorUnavailable,
                    "No focused evidence executor was configured.", telemetryCandidateSha);
            }
            decision = BuildFindingEvidenceDeliveryRetry(requestingTask, "Evidence requests could not run because the executor was unavailable.");
            return true;
        }

        if (!TryReconcileFocusedEvidenceAttempt(
                goal, policy, runnable.Request, candidateSha!, "finding-requested", out var evidence, out decision))
        {
            if (decision.ShouldEscalate)
            {
                foreach (var finding in runnable.Findings)
                {
                    RecordNotHonoured(
                        goal.Id, requestingTask, finding, FindingEvidenceNotHonouredReason.RunFailed,
                        decision.Message, telemetryCandidateSha);
                }
                decision = BuildFindingEvidenceDeliveryRetry(requestingTask, "The focused evidence executor failed; a typed refusal was attached.");
            }
            return true;
        }

        if (!evidence.Accepted)
        {
            var reason = evidence.Rejection?.Code switch
            {
                FocusedEvidenceRejectionCode.UnsupportedProject =>
                    FindingEvidenceNotHonouredReason.UnsupportedProject,
                FocusedEvidenceRejectionCode.SourceDiscoveryFailure =>
                    FindingEvidenceNotHonouredReason.SelectionApparatusFailure,
                _ => FindingEvidenceNotHonouredReason.UnparseableSelection
            };
            var detail = evidence.Rejection is null
                ? evidence.Summary
                : $"{evidence.Rejection.Detail}; offending_filter='{evidence.Rejection.OffendingToken}'";
            foreach (var finding in runnable.Findings)
            {
                RecordNotHonoured(
                    goal.Id, requestingTask, finding, reason,
                    detail, telemetryCandidateSha);
            }
            decision = BuildFindingEvidenceDeliveryRetry(requestingTask, "The focused evidence executor did not accept the request.");
            return true;
        }

        var receiptId = CreateFindingEvidenceReceiptId(candidateSha!, runnable.Identity);
        var armReceipts = (evidence.Arms ?? [])
            .Select(arm => new FindingEvidenceArmReceipt(
                arm.Arm,
                arm.Sha,
                arm.Disposition,
                arm.Accepted,
                arm.Passed,
                arm.Summary,
                arm.Checks
                    .SelectMany(check => check.TestResultPaths ?? [])
                    .Concat(arm.Checks.Select(check => check.ArtifactsPath ?? string.Empty))
                    .Where(path => !string.IsNullOrWhiteSpace(path))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                arm.Checks
                    .SelectMany(check => check.FailingTestIdentities ?? [])
                    .Distinct(StringComparer.Ordinal)
                    .ToArray()))
            .ToArray();
        var receipt = new FindingEvidenceReceipt(
            receiptId,
            candidateSha!,
            runnable.TypedRequest,
            evidence.Accepted,
            evidence.IsValidEvidence,
            evidence.Summary,
            armReceipts);
        if (evidence.OutcomeReason == FindingEvidenceOutcomeReason.ApparatusFailure)
        {
            foreach (var finding in runnable.Findings)
            {
                _recordFindingEvidenceOutcome(
                    goal.Id,
                    requestingTask.Id,
                    finding.StableId,
                    new FindingEvidenceOutcome(
                        Honoured: false,
                        ReceiptId: receiptId,
                        Reason: FindingEvidenceNotHonouredReason.SelectionApparatusFailure,
                        Detail: evidence.Summary,
                        ResultReason: FindingEvidenceOutcomeReason.ApparatusFailure),
                    receipt);
                _recordFindingEvidenceRequest(
                    goal.Id,
                    requestingTask.Id,
                    $"finding-evidence disposition=not-honoured; role={requestingTask.RequiredRole}; " +
                    $"task_id={requestingTask.Id}; finding_id={finding.StableId}; candidate_sha={candidateSha}; " +
                    $"receipt_id={receiptId}; reason=selection-apparatus-failure; " +
                    $"detail={TrimForConductorMessage(evidence.Summary)}");
                _recordFindingEvidenceRun(
                    goal.Id,
                    requestingTask.Id,
                    $"finding-evidence apparatus-failure role={requestingTask.RequiredRole}; task_id={requestingTask.Id}; " +
                    $"finding_id={finding.StableId}; candidate_sha={candidateSha}; receipt_id={receiptId}; " +
                    FormatFocusedEvidenceResult(evidence));
            }

            decision = BuildFindingEvidenceDeliveryRetry(
                requestingTask,
                "Focused evidence selected zero tests; its apparatus receipt was attached for correction and reissue.");
            return true;
        }

        foreach (var finding in runnable.Findings)
        {
            _recordFindingEvidenceOutcome(
                goal.Id, requestingTask.Id, finding.StableId,
                new FindingEvidenceOutcome(
                    Honoured: true,
                    ReceiptId: receiptId,
                    ResultReason: evidence.OutcomeReason ?? FindingEvidenceOutcomeReason.Unknown),
                receipt);
            var resultReason = FindingEvidenceOutcomeReasonJsonConverter.ToWireValue(
                evidence.OutcomeReason ?? FindingEvidenceOutcomeReason.Unknown);
            _recordFindingEvidenceRequest(
                goal.Id, requestingTask.Id,
                $"finding-evidence disposition=honoured; role={requestingTask.RequiredRole}; task_id={requestingTask.Id}; " +
                $"finding_id={finding.StableId}; candidate_sha={candidateSha}; receipt_id={receiptId}; reason={resultReason}");
            _recordFindingEvidenceRun(
                goal.Id, requestingTask.Id,
                $"finding-evidence role={requestingTask.RequiredRole}; task_id={requestingTask.Id}; finding_id={finding.StableId}; " +
                $"candidate_sha={candidateSha}; receipt_id={receiptId}; reason={resultReason}; {FormatFocusedEvidenceResult(evidence)}");
        }
        decision = groups.Take(policy.MaxFocusedEvidenceRunsPerRound).Skip(1).Any()
            ? VerifyingFindingAutoRetryDecision.Hold(
                "Focused evidence completed; another distinct request from the same finding round remains pending.")
            : BuildFindingEvidenceDeliveryRetry(
                requestingTask, "Focused evidence completed and its receipt was attached to the requesting finding.");
        return true;
    }

    private void RecordNotHonoured(
        GoalId goalId,
        TaskSpec requestingTask,
        ReviewFinding finding,
        FindingEvidenceNotHonouredReason reason,
        string detail,
        string candidateSha)
    {
        _recordFindingEvidenceOutcome(
            goalId, requestingTask.Id, finding.StableId,
            new FindingEvidenceOutcome(Honoured: false, Reason: reason, Detail: detail), null);
        _recordFindingEvidenceRequest(
            goalId, requestingTask.Id,
            $"finding-evidence disposition=not-honoured; role={requestingTask.RequiredRole}; task_id={requestingTask.Id}; " +
            $"finding_id={finding.StableId}; candidate_sha={candidateSha}; receipt_id=none; " +
            $"reason={FindingEvidenceNotHonouredReasonJsonConverter.ToWireValue(reason)}; detail={TrimForConductorMessage(detail)}");
    }

    private static VerifyingFindingAutoRetryDecision BuildFindingEvidenceDeliveryRetry(TaskSpec task, string summary) =>
        VerifyingFindingAutoRetryDecision.Retry(
            task,
            $"{FindingEvidenceRetryMessagePrefix} role={task.RequiredRole}; task={task.Id.Value[..8]}; {summary} " +
            "Review the finding-bound outcome in this round's context.",
            null,
            RetryRoundKind.Mechanical);

    private static bool TryNormalizeFindingEvidenceRequest(
        FindingEvidenceRequest request,
        AcceptanceGateEngineSettings engineSettings,
        out FindingEvidenceRequest normalized,
        out string executorRequest,
        out FindingEvidenceNotHonouredReason refusalReason,
        out string refusalDetail)
    {
        normalized = new FindingEvidenceRequest([]);
        executorRequest = string.Empty;
        refusalReason = FindingEvidenceNotHonouredReason.UnparseableSelection;
        refusalDetail = "Evidence request must contain at least one project/class selection.";
        if (request.Selections is not { Count: > 0 })
        {
            return false;
        }

        var selections = new List<FindingEvidenceSelection>();
        foreach (var selection in request.Selections)
        {
            if (selection is null)
            {
                refusalDetail = "Evidence selections cannot contain null entries.";
                return false;
            }
            var project = selection.TestProject?.Trim();
            var originalTestClass = selection.TestClass ?? string.Empty;
            var testClass = originalTestClass.Trim();
            if (string.IsNullOrWhiteSpace(project) ||
                string.IsNullOrWhiteSpace(testClass) ||
                originalTestClass.Length > MaxFindingEvidenceFilterLength ||
                !IsSupportedFindingEvidenceFilter(testClass))
            {
                refusalDetail =
                    $"Every evidence selection requires a bounded valid test_project and test_class; " +
                    $"offending_filter='{originalTestClass}'.";
                return false;
            }

            if (!GoalAcceptanceVerifier.TryResolveFocusedEvidenceProject(
                    project,
                    engineSettings,
                    out var resolvedProject))
            {
                refusalReason = FindingEvidenceNotHonouredReason.UnsupportedProject;
                refusalDetail =
                    $"Focused evidence does not support test project '{project}'. Accepted forms: " +
                    $"{GoalAcceptanceVerifier.FocusedEvidenceSupportedProjectForms}.";
                return false;
            }

            var canonicalProject = GoalAcceptanceVerifier.ProjectLabel(resolvedProject);
            selections.Add(new FindingEvidenceSelection(canonicalProject, originalTestClass));
        }

        var distinct = selections
            .Distinct()
            .OrderBy(selection => selection.TestProject, StringComparer.Ordinal)
            .ThenBy(selection => selection.TestClass, StringComparer.Ordinal)
            .ToArray();
        normalized = new FindingEvidenceRequest(distinct);
        executorRequest = string.Join(
            "; ",
            distinct.Select(FormatFindingEvidenceSelection));
        return true;
    }

    private static bool IsSupportedFindingEvidenceFilter(string filter)
    {
        if (EvidenceBareClassNamePattern.IsMatch(filter))
        {
            return true;
        }

        var parenthesisDepth = 0;
        foreach (var character in filter)
        {
            if (character == '(')
            {
                parenthesisDepth++;
            }
            else if (character == ')' && --parenthesisDepth < 0)
            {
                return false;
            }
        }
        if (parenthesisDepth != 0)
        {
            return false;
        }

        var tokens = Regex.Split(filter, @"[&|]");
        if (tokens.Length == 0 || tokens.Any(string.IsNullOrWhiteSpace))
        {
            return false;
        }

        var hasPositiveSelection = false;
        foreach (var rawToken in tokens)
        {
            var token = rawToken.Trim().Trim('(', ')').Trim();
            if (token.Length == 0 ||
                token.Contains('(') ||
                token.Contains(')') ||
                !EvidenceFilterTokenPattern.IsMatch(token))
            {
                return false;
            }

            hasPositiveSelection |= token.Contains("FullyQualifiedName~", StringComparison.OrdinalIgnoreCase);
        }

        return hasPositiveSelection;
    }

    private static string FormatFindingEvidenceSelection(FindingEvidenceSelection selection) =>
        selection.TestProject + ":" + selection.TestClass;

    private static string BuildFindingEvidenceIdentity(FindingEvidenceRequest request) =>
        string.Join("|", request.Selections.Select(selection => $"{selection.TestProject}:{selection.TestClass}"));

    private static string CreateFindingEvidenceReceiptId(string candidateSha, string identity) =>
        "finding-evidence-" + Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes($"{candidateSha}:{identity}")))
            .ToLowerInvariant()[..24];

    private bool TryReconcileFocusedEvidenceAttempt(
        Goal goal,
        ConductorAutonomyPolicy policy,
        string request,
        string? candidateSha,
        string source,
        out FocusedEvidenceRunResult evidence,
        out VerifyingFindingAutoRetryDecision decision)
    {
        evidence = null!;
        decision = VerifyingFindingAutoRetryDecision.None;
        var candidate = ConductorParallelAcceptanceCandidate.Create(
            goal,
            slotIndex: 0,
            fileScopes: [],
            branchHeadSha: candidateSha?.Trim(),
            mainHeadSha: null);
        var attemptDecision = _focusedEvidenceAttemptCoordinator.EvaluateFocusedEvidence(
            candidate,
            policy,
            request,
            _runDualArmFocusedEvidence);
        if (attemptDecision.Kind is
            ConductorParallelAcceptanceAttemptDecisionKind.Started or
            ConductorParallelAcceptanceAttemptDecisionKind.Running)
        {
            decision = VerifyingFindingAutoRetryDecision.Hold(
                $"Background {source} focused evidence is running in attempt {attemptDecision.Attempt.AttemptId}.");
            return false;
        }

        if (attemptDecision.Kind == ConductorParallelAcceptanceAttemptDecisionKind.TerminalWithoutRun ||
            attemptDecision.Run?.Exception is
                DotnetBuildSlotsBusyException or
                BuildLockBlockedException or
                OperationCanceledException)
        {
            _focusedEvidenceAttemptCoordinator.MarkReconciled(attemptDecision.Attempt);
            decision = VerifyingFindingAutoRetryDecision.Hold(
                $"Background {source} focused evidence did not run ({attemptDecision.Attempt.Outcome}); " +
                $"retry on next conduct tick. attempt={attemptDecision.Attempt.AttemptId}: " +
                (attemptDecision.Attempt.Detail ?? "no result artifact was produced"));
            return false;
        }

        _focusedEvidenceAttemptCoordinator.MarkReconciled(attemptDecision.Attempt);
        if (attemptDecision.Run?.Exception is { } backgroundFailure)
        {
            decision = VerifyingFindingAutoRetryDecision.Escalate(
                $"BACKGROUND_FOCUSED_EVIDENCE_FAILED: {source} focused evidence run failed. " +
                $"attempt={attemptDecision.Attempt.AttemptId}: {backgroundFailure.Message}");
            return false;
        }

        evidence = attemptDecision.Run?.FocusedEvidence ?? new FocusedEvidenceRunResult(
            request,
            Accepted: false,
            Passed: false,
            Summary: $"background {source} focused evidence {attemptDecision.Attempt.Outcome}: " +
                (attemptDecision.Attempt.Detail ?? "no result artifact was produced"),
            Checks: []);
        return true;
    }

    private static int CountReviewerContractRepairsInCurrentRound(Goal goal, TaskSpec reviewerTask)
    {
        var currentRoundStartedAt = GetCurrentReviewerRoundStart(goal, reviewerTask);
        return goal.Timeline.Count(evt =>
            evt.TaskId == reviewerTask.Id &&
            evt.Kind == ProgressKind.TaskRetried &&
            evt.Message.StartsWith(ReviewContractRepairRetryMessagePrefix, StringComparison.Ordinal) &&
            evt.OccurredAt >= currentRoundStartedAt);
    }

    private static DateTimeOffset GetCurrentReviewerRoundStart(Goal goal, TaskSpec reviewerTask)
    {
        // A fresh review starts at any non-reviewer retry or any reviewer retry that is not one of
        // the bounded mechanical receipt/contract repairs. Mechanical retries remain in the same
        // round so neither budget can be reset by alternating the two repair paths.
        return goal.Timeline
            .Where(evt =>
                evt.Kind == ProgressKind.TaskRetried &&
                evt.TaskId is not null &&
                (evt.TaskId != reviewerTask.Id ||
                    !MechanicalReviewerRetryMessagePrefixes.Any(prefix =>
                        evt.Message.StartsWith(prefix, StringComparison.Ordinal))))
            .Select(evt => evt.OccurredAt)
            .DefaultIfEmpty(DateTimeOffset.MinValue)
            .Max();
    }

    private static string FormatFocusedEvidenceResult(FocusedEvidenceRunResult evidence)
    {
        var checks = evidence.Checks.Count == 0
            ? "checks: none"
            : "checks: " + string.Join("; ", evidence.Checks.Select(FormatFocusedEvidenceCheck));
        var outcome = FindingEvidenceOutcomeReasonJsonConverter.ToWireValue(
            evidence.OutcomeReason ?? FindingEvidenceOutcomeReason.Unknown);
        return $"request='{TrimForConductorMessage(evidence.Request)}'; accepted={evidence.Accepted}; passed={evidence.Passed}; outcome={outcome}; " +
            $"summary={TrimForConductorMessage(evidence.Summary)}; {checks}";
    }

    private static string FormatFocusedEvidenceCheck(AcceptanceCheckResult check)
    {
        var receipt = string.IsNullOrWhiteSpace(check.ArtifactsPath)
            ? "receipt=none"
            : $"receipt={check.ArtifactsPath}";
        var summary = string.IsNullOrWhiteSpace(check.ResultSummary)
            ? string.Empty
            : $"; summary={TrimForConductorMessage(check.ResultSummary)}";
        var executed = check.ExecutedTestCount is null
            ? string.Empty
            : $" executed={check.ExecutedTestCount}";
        return $"{check.Name} passed={check.Passed} exit={check.ExitCode}{executed} {receipt}{summary}";
    }

    private static string FormatVerifyingRoleOutputArtifact(TaskSpec task)
    {
        var verification = task.LastVerification;
        if (!string.IsNullOrWhiteSpace(verification?.StandardOutputPath))
        {
            return verification.StandardOutputPath!;
        }

        return $"{task.RequiredRole.ToString().ToLowerInvariant()} task {task.Id.Value[..8]} verification output";
    }

    private static string BuildTerminalEscalationReason(Goal goal, GoalLifecycleState state)
    {
        var failedTaskIds = goal.Tasks
            .Where(task => task.Status == WorkTaskStatus.Failed)
            .Select(task => task.Id)
            .ToHashSet();
        var latestTaskFailure = state == GoalLifecycleState.Failed &&
                                goal.Status is not (GoalStatus.Cancelled or GoalStatus.Superseded)
            ? goal.Timeline.LastOrDefault(item =>
                item.Kind == ProgressKind.TaskFailed &&
                item.TaskId is { } taskId &&
                failedTaskIds.Contains(taskId))
            : null;
        return latestTaskFailure is null
            ? $"Goal is in {state} state; operator action required"
            : $"Goal is in {state} state; TaskFailed: {TrimForConductorMessage(latestTaskFailure.Message)}; operator action required";
    }

    private static string TrimForConductorMessage(string value)
    {
        var normalized = Regex.Replace(value.Trim(), @"\s+", " ");
        const int maxLength = 800;
        return normalized.Length <= maxLength
            ? normalized
            : normalized[..maxLength] + "...";
    }

    private sealed record VerifyingFindingTrigger(
        TaskSpec TriggeringTask,
        string Finding,
        IReadOnlyList<string> SuppressedFindings,
        TaskSpec? TargetTask);

    private sealed record VerifyingFindingAutoRetryDecision(
        bool ShouldHold,
        bool ShouldEscalate,
        TaskSpec? TargetTask,
        string Message,
        string? WarningMessage,
        RetryRoundKind? RoundKind)
    {
        public static VerifyingFindingAutoRetryDecision None { get; } = new(false, false, null, string.Empty, null, null);

        public static VerifyingFindingAutoRetryDecision Hold(string message) =>
            new(true, false, null, message, null, null);

        public static VerifyingFindingAutoRetryDecision Retry(
            TaskSpec targetTask,
            string message,
            string? warningMessage,
            RetryRoundKind? roundKind = null) =>
            new(false, false, targetTask, message, warningMessage, roundKind);

        public static VerifyingFindingAutoRetryDecision Escalate(string message) =>
            new(false, true, null, message, null, null);
    }

    internal ConductorParallelAcceptanceCandidate? TryBuildParallelAcceptanceCandidate(
        Goal goal,
        ConductorAutonomyPolicy policy,
        int slotIndex)
    {
        if (policy.GetTransitionDecision(GoalLifecycleState.Verified) == ConductorTransitionDecision.Escalate)
        {
            return null;
        }

        if (GoalLifecycle.ResolveState(goal, GetFacts(goal)) is not (GoalLifecycleState.Verified or GoalLifecycleState.Verifying))
        {
            return null;
        }

        if (!HasCompletedPassedVerificationForAllTasks(goal))
        {
            return null;
        }

        var slotCount = GetAcceptanceSlotCount(goal);
        if (slotIndex < 0 || slotIndex >= slotCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(slotIndex),
                slotIndex,
                $"Acceptance slot index must be 0 through {slotCount - 1} for goal {goal.Id.Value[..8]}.");
        }

        return ConductorParallelAcceptanceCandidate.Create(
            goal,
            slotIndex,
            _getLandingFileScopes(goal),
            TryResolveAcceptanceBranchHead(goal),
            _executionDirectory is null ? null : TryResolveGitHead(_executionDirectory));
    }

    internal GateReadyCandidateProjectionResult ProjectGateReadyCandidate(
        Goal goal,
        ConductorAutonomyPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(goal);
        ArgumentNullException.ThrowIfNull(policy);

        GoalLifecycleState lifecycleState;
        try
        {
            lifecycleState = GoalLifecycle.ResolveState(goal, GetFacts(goal));
        }
        catch
        {
            return ExcludedGateReadyCandidate(GateReadyCandidateExclusionReason.LifecycleNotReady);
        }

        bool gateSatisfied;
        try
        {
            gateSatisfied = _isVerificationGateSatisfied(goal);
        }
        catch
        {
            return ExcludedGateReadyCandidate(GateReadyCandidateExclusionReason.GateNotReady);
        }

        ChangeRiskTier? changeRiskTier;
        ConductorTransitionDecision? autoPromotionDisposition;
        try
        {
            changeRiskTier = _classifyChangeRisk(goal);
            autoPromotionDisposition = changeRiskTier.HasValue
                ? policy.GetTransitionDecision(GoalLifecycleState.Merged, changeRiskTier.Value)
                : null;
        }
        catch
        {
            changeRiskTier = null;
            autoPromotionDisposition = null;
        }

        var input = new GateReadyCandidateInput(
            goal.Id,
            lifecycleState,
            gateSatisfied,
            changeRiskTier,
            autoPromotionDisposition);
        return _gateReadyCandidateProjector?.Project(input) ??
            ExcludedGateReadyCandidate(GateReadyCandidateExclusionReason.RevisionUnknown);
    }

    private static GateReadyCandidateProjectionResult.Excluded ExcludedGateReadyCandidate(
        GateReadyCandidateExclusionReason reason) => new(reason);

    internal bool AcceptanceCohortsEnabled =>
        _runAcceptanceCohortOverride is not null ||
        (_cohortKernel is not null &&
         _cohortWorkspace is not null &&
         _cohortAcceptanceVerifier is not null &&
         _cohortAcceptanceStore is not null);

    private void RecoverCohortLandingEffects(
        AgentOrchestratorKernel kernel,
        OrchestratorWorkspace workspace,
        IGoalLifecycleEventWriter eventWriter,
        CohortAcceptanceStore store)
    {
        foreach (var recovery in store.RecoverPreparedLandings(workspace.ExecutionDirectory))
        {
            var goalsById = kernel.Goals.ToDictionary(goal => goal.Id);
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
                    var lease = _tryAcquireEvidenceMutationLease(goal, "conductor:cohort-recovery");
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
                foreach (var goal in resolvedGoals)
                {
                    GoalOperationJournal.Completed(
                        workspace.ExecutionDirectory,
                        goal,
                        "conductor:land",
                        $"Recovered shared cohort receipt {recovery.Receipt.ReceiptId} after main advanced.");
                    eventWriter.AppendGoalLanded(
                        goal.Id,
                        $"cohort/{recovery.Receipt.Identity.Value}",
                        GoalWorktrees.BranchName(goal.Id));
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
                        $"cohort/{recovery.Receipt.Identity.Value}",
                        MainAdvanced: true,
                        "Recovered exact tested cohort landing after main advanced.",
                        recovery.CombinedCommitRevision,
                        changedFiles);
                    _pendingRecoveredLandingReceipts.Add((
                        new ConductorLandingReceipt(
                            goal.Id.Value,
                            changedFiles,
                            recovery.CombinedCommitRevision),
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

    internal GoalId? SelectForcedCohortCandidate(IReadOnlyList<Goal> orderedGoals)
    {
        if (_cohortAcceptanceStore is null) return null;
        return orderedGoals.FirstOrDefault(goal =>
            _cohortAcceptanceStore.ReadOvertakeCount(goal.Id) >=
            ConductorBatchLoop.ParallelAcceptanceBoundedOvertakeLimit)?.Id;
    }

    internal IReadOnlySet<string> ReadSuppressedCohortPairs() =>
        _cohortAcceptanceStore?.ReadSuppressedPairs() ?? new HashSet<string>(StringComparer.Ordinal);

    internal void RecordCohortAdmissionFairness(
        IReadOnlyList<Goal> orderedGoals,
        ConductorAcceptanceCohortSelection selection)
    {
        if (_cohortAcceptanceStore is null || orderedGoals.Count == 0) return;
        var admitted = selection.Members.Select(member => member.GoalId).ToHashSet();
        var oldest = orderedGoals[0].Id;
        _cohortAcceptanceStore.ApplyAdmissionFairness(admitted, oldest);
    }

    internal void ResetCohortFairness(GoalId goalId) =>
        _cohortAcceptanceStore?.ResetOvertake(goalId);

    internal ConductorAcceptanceCohortRunResult RunAcceptanceCohort(
        ConductorAcceptanceCohortSelection selection,
        IReadOnlyList<Goal> orderedGoals,
        ConductorAutonomyPolicy policy,
        CancellationToken cancellationToken = default,
        Action? onGateAdmitted = null)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(orderedGoals);
        if (_runAcceptanceCohortOverride is not null)
        {
            return _runAcceptanceCohortOverride(selection, orderedGoals, policy);
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
                bindings);
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

        using var integrationScope = integration;
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
        AcceptanceVerificationResult? verification = null;

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
            var gateClock = Stopwatch.StartNew();
            var outcome = AcceptanceCohortGateOutcome.InfrastructureFailure;
            IReadOnlyList<string> failedChecks = [];
            int? gateExitCode = null;
            IReadOnlyList<string> gateTestResultPaths = [];
            DotnetBuildEnvironmentLease? stableSlotLease = null;
            var gateExecutionComplete = false;
            try
            {
                stableSlotLease = _parallelAcceptanceAttemptCoordinator.AcquireCohortStableSlotLease(
                    identity.Value,
                    cancellationToken);
                onGateAdmitted?.Invoke();
                verification = _cohortAcceptanceVerifier.RunAsync(
                    integration.Path,
                    goalId: null,
                    changedFiles: bindings.SelectMany(member => member.LandingPaths).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                    stableSlotIndex: stableSlotLease.Environment.BuildPermitIndex,
                    stableSlotLease: stableSlotLease,
                    cancellationToken: cancellationToken).GetAwaiter().GetResult();
                gateExitCode = verification.ExitCode;
                gateTestResultPaths = NormalizeCohortTestResultPaths(verification.TestResultPaths);
                outcome = ClassifyCohortVerification(verification);
                failedChecks = verification.Checks?
                    .Where(check => !check.Passed && !check.Advisory)
                    .Select(check => check.Name)
                    .ToArray() ?? [];
                gateExecutionComplete = true;
                gateClock.Stop();
                receipt = _cohortAcceptanceStore.SaveGateReceipt(new AcceptanceCohortReceipt(
                    $"cohort-receipt-v2-{identity.Value[(AcceptanceCohortIdentity.Version.Length + 1)..]}",
                    identity,
                    outcome,
                    DateTimeOffset.UtcNow,
                    checked((long)gateClock.Elapsed.TotalMilliseconds),
                    failedChecks,
                    GateExitCode: gateExitCode,
                    GateTestResultPaths: gateTestResultPaths,
                    ValidForLanding: outcome == AcceptanceCohortGateOutcome.Passed));
            }
            catch (Exception ex) when (!gateExecutionComplete && ex is (
                AcceptanceInfrastructureDeferredException or DotnetBuildSlotsBusyException or
                BuildLockBlockedException or OperationCanceledException or IOException or
                InvalidDataException))
            {
                outcome = AcceptanceCohortGateOutcome.InfrastructureFailure;
                failedChecks = [$"infrastructure:{ex.GetType().Name}:{BoundCohortDetail(ex.Message)}"];
            }
            finally
            {
                stableSlotLease?.Dispose();
            }
            if (receipt is null)
            {
                gateClock.Stop();
                receipt = _cohortAcceptanceStore.SaveGateReceipt(new AcceptanceCohortReceipt(
                    $"cohort-receipt-v2-{identity.Value[(AcceptanceCohortIdentity.Version.Length + 1)..]}",
                    identity,
                    outcome,
                    DateTimeOffset.UtcNow,
                    checked((long)gateClock.Elapsed.TotalMilliseconds),
                    failedChecks,
                    GateExitCode: gateExitCode,
                    GateTestResultPaths: gateTestResultPaths,
                    ValidForLanding: false));
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
        if (receipt.Outcome is AcceptanceCohortGateOutcome.InfrastructureFailure or AcceptanceCohortGateOutcome.Invalidated)
        {
            return CohortReprojection(
                receipt,
                $"indeterminate cohort outcome={receipt.Outcome}; attribution=none; fresh Ready projection required");
        }
        if (receipt.Outcome == AcceptanceCohortGateOutcome.Failed &&
            receipt.Attribution == AcceptanceCohortAttributionOutcome.NotApplicable)
        {
            var first = RunCohortPartition(bindings[0], 0);
            var second = RunCohortPartition(bindings[1], 1);
            var attribution = ConductorAcceptanceCohortAttribution.Classify(first.Outcome, second.Outcome);
            var innocentGoalId = attribution switch
            {
                AcceptanceCohortAttributionOutcome.FirstMemberFailed => bindings[1].GoalId,
                AcceptanceCohortAttributionOutcome.SecondMemberFailed => bindings[0].GoalId,
                _ => (GoalId?)null
            };
            receipt = _cohortAcceptanceStore.SaveAttribution(
                identity.Value,
                attribution,
                [first, second],
                ConductorAcceptanceCohortSelector.PairFingerprint(selection.Members[0], selection.Members[1]),
                innocentGoalId);
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

        return CohortHeld(
            goals,
            policy,
            receipt,
            receipt.Outcome == AcceptanceCohortGateOutcome.Failed
                ? $"deterministic RED; attribution={receipt.Attribution}"
                : $"cohort infrastructure outcome={receipt.Outcome}; no attribution or landing");

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

        AcceptanceCohortPartitionReceipt RunCohortPartition(
            AcceptanceCohortMemberBinding member,
            int memberOrdinal)
        {
            var clock = Stopwatch.StartNew();
            string? treeRevision = null;
            string partitionManifest = identity.ManifestIdentity;
            IReadOnlyList<string> testResultPaths = [];
            AcceptanceCohortGateOutcome outcome;
            try
            {
                using var partition = GoalWorktrees.CreateAcceptancePartitionWorkspace(
                    _cohortWorkspace.ExecutionDirectory,
                    identity.ObservedMainRevision,
                    member);
                treeRevision = partition.TreeRevision;
                partitionManifest = _cohortAcceptanceVerifier.ComputeEffectivePlanIdentity(
                    partition.Path,
                    member.LandingPaths);
                var result = _cohortAcceptanceVerifier.RunAsync(
                    partition.Path,
                    member.GoalId,
                    member.LandingPaths,
                    cancellationToken: cancellationToken).GetAwaiter().GetResult();
                testResultPaths = NormalizeCohortTestResultPaths(result.TestResultPaths);
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
                testResultPaths);
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

    internal static AcceptanceCohortGateOutcome ClassifyCohortVerification(AcceptanceVerificationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Skipped || result.ExitCode is null)
        {
            return AcceptanceCohortGateOutcome.InfrastructureFailure;
        }
        IReadOnlyList<string> normalizedTestResultPaths;
        try
        {
            normalizedTestResultPaths = NormalizeCohortTestResultPaths(result.TestResultPaths);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return AcceptanceCohortGateOutcome.InfrastructureFailure;
        }
        if (!AcceptanceCohortGateEvidence.HasCoherentTrxEvidence(normalizedTestResultPaths))
        {
            return AcceptanceCohortGateOutcome.InfrastructureFailure;
        }
        return result.Passed && result.ExitCode == 0
            ? AcceptanceCohortGateOutcome.Passed
            : AcceptanceCohortGateOutcome.Failed;
    }

    private static IReadOnlyList<string> NormalizeCohortTestResultPaths(IReadOnlyList<string>? paths) =>
        (paths ?? [])
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();

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

    internal ConductorParallelAcceptanceRunResult RunParallelLandingAcceptance(
        ConductorParallelAcceptanceCandidate candidate,
        ConductorAutonomyPolicy policy,
        DotnetBuildEnvironmentLease? stableSlotLease,
        CancellationToken cancellationToken)
    {
        var effectiveCandidate = candidate;
        using var evidenceMutationLease = _tryAcquireEvidenceMutationLease(
            candidate.Goal,
            "conductor:parallel-acceptance");
        if (evidenceMutationLease is null)
        {
            return ConductorParallelAcceptanceRunResult.Early(
                candidate,
                ReplacementEvidenceMutationHeld(candidate.Goal, candidate.GoalPrefix, policy),
                null);
        }
        try
        {
            var early = RebaseBeforeAcceptance(
                candidate.Goal,
                candidate.GoalPrefix,
                policy,
                applySideEffects: false,
                out var earlyOutcome);
            if (early is not null)
            {
                return ConductorParallelAcceptanceRunResult.Early(candidate, early, earlyOutcome);
            }

            effectiveCandidate = RefreshParallelAcceptanceCandidate(candidate);
            return ConductorParallelAcceptanceRunResult.Accepted(
                effectiveCandidate,
                _runAcceptanceVerification(
                    effectiveCandidate.Goal,
                    effectiveCandidate.SlotIndex,
                    stableSlotLease,
                    cancellationToken));
        }
        catch (Exception ex)
        {
            return ConductorParallelAcceptanceRunResult.Fault(effectiveCandidate, ex);
        }
    }

    internal ConductorParallelAcceptanceRunResult RunPreReviewFocusedEvidence(
        ConductorParallelAcceptanceCandidate candidate,
        string request,
        DotnetBuildEnvironmentLease? stableSlotLease,
        CancellationToken cancellationToken) =>
        ConductorParallelAcceptanceRunResult.Focused(
            candidate,
            _runFocusedEvidence(candidate.Goal, request, stableSlotLease, cancellationToken));

    internal ConductorParallelAcceptanceRunResult? RunParallelLandingAcceptancePreSlot(
        ConductorParallelAcceptanceCandidate candidate,
        ConductorAutonomyPolicy policy)
    {
        if (_executionDirectory is null)
        {
            return null;
        }

        var effectiveCandidate = RefreshParallelAcceptanceCandidate(candidate);
        var branchHeadSha = effectiveCandidate.BranchHeadSha;
        if (string.IsNullOrWhiteSpace(branchHeadSha) ||
            !IsCommitReachableFromMain(_executionDirectory, branchHeadSha))
        {
            return null;
        }

        var skippedAt = DateTimeOffset.UtcNow;
        var mergeCommitSha = RecoverMainMergeCommitForBranchTip(_executionDirectory, branchHeadSha);
        var detail = $"Acceptance skipped:skip-already-merged goalId={effectiveCandidate.Goal.Id.Value}; branchRef={GoalWorktrees.BranchName(effectiveCandidate.Goal.Id)}; mergeCommitSha={mergeCommitSha}; skippedAtUtc={skippedAt:O}.";
        GoalOperationJournal.AcceptanceSkippedAlreadyMerged(
            _executionDirectory,
            effectiveCandidate.Goal,
            "conductor:acceptance",
            branchHeadSha,
            effectiveCandidate.MainHeadSha,
            detail,
            skippedAt);
        return ConductorParallelAcceptanceRunResult.Early(
            effectiveCandidate,
            new ConductorAdvanceResult(
                effectiveCandidate.Goal.Id.Value,
                effectiveCandidate.GoalPrefix,
                policy.Name,
                new ConductorAdvanceOutcome.Done(GoalLifecycleState.Verified)),
            new ConductorParallelAcceptanceEarlyOutcome(
                "skip-already-merged",
                GoalLifecycleState.Verified,
                detail));
    }

    private ConductorParallelAcceptanceCandidate RefreshParallelAcceptanceCandidate(
        ConductorParallelAcceptanceCandidate candidate) =>
        ConductorParallelAcceptanceCandidate.Create(
            candidate.Goal,
            candidate.SlotIndex,
            candidate.ScopePaths,
            TryResolveAcceptanceBranchHead(candidate.Goal),
            _executionDirectory is null ? null : TryResolveGitHead(_executionDirectory));

    internal static bool IsAcceptanceAttemptCancelled(OrchestratorWorkspace workspace, GoalId goalId)
    {
        try
        {
            var latest = SqliteOrchestratorStateRepository.OpenReadOnly(workspace.SqliteStatePath)
                .LoadGoalAsync(goalId)
                .GetAwaiter()
                .GetResult();
            return latest is null ||
                latest.Status is GoalStatus.Active or GoalStatus.Parked or GoalStatus.AcceptanceFailed or GoalStatus.Cancelled or GoalStatus.Superseded or GoalStatus.Failed;
        }
        catch
        {
            return false;
        }
    }

    internal ConductorAdvanceResult CompleteParallelLandingAcceptance(
        ConductorParallelAcceptanceCandidate candidate,
        ConductorAutonomyPolicy policy,
        AcceptanceVerificationSummary acceptance)
    {
        using var evidenceMutationLease = _tryAcquireEvidenceMutationLease(
            candidate.Goal,
            "conductor:parallel-land");
        if (evidenceMutationLease is null)
            return ReplacementEvidenceMutationHeld(candidate.Goal, candidate.GoalPrefix, policy);

        acceptance = NormalizeNamedFailedChecksForRetry(acceptance);
        if (!acceptance.Passed || acceptance.UnmetCriteria.Count > 0)
        {
            return CompleteLandingAfterAcceptance(candidate.Goal, candidate.GoalPrefix, policy, acceptance);
        }

        var rebase = RebaseBeforeMerge(candidate.Goal, candidate.GoalPrefix, policy);
        return rebase ?? CompleteLandingAfterAcceptance(candidate.Goal, candidate.GoalPrefix, policy, acceptance);
    }

    internal ConductorAdvanceResult EscalateParallelLandingAcceptance(
        ConductorParallelAcceptanceCandidate candidate,
        ConductorAutonomyPolicy policy,
        string reason) =>
        Escalate(candidate.Goal, candidate.GoalPrefix, policy, GoalLifecycleState.Verified, reason);

    internal ConductorAdvanceResult EscalateParallelLandingAcceptance(
        Goal goal,
        ConductorAutonomyPolicy policy,
        string reason) =>
        Escalate(goal, goal.Id.Value[..8], policy, GoalLifecycleState.Verified, reason);

    private static bool HasCompletedPassedVerificationForAllTasks(Goal goal) =>
        goal.Tasks.Count > 0 &&
        goal.Tasks.All(task =>
            task.Status == WorkTaskStatus.Cancelled ||
            (task.Status == WorkTaskStatus.Completed &&
             task.LastVerification is { Succeeded: true }));

    internal static AcceptanceVerificationSummary NormalizeNamedFailedChecksForRetry(AcceptanceVerificationSummary acceptance)
    {
        if (acceptance.Passed ||
            acceptance.RequiredUnmetCriteria.Count > 0 ||
            acceptance.FailedChecks is not { Count: > 0 } failedChecks)
        {
            return acceptance;
        }

        var output = string.Join(Environment.NewLine, failedChecks);
        var summary = $"Named failing acceptance checks: {string.Join(", ", failedChecks)}";
        var check = new AcceptanceCheckResult(
            "acceptance failed checks",
            false,
            1,
            string.IsNullOrWhiteSpace(acceptance.FailureDetail) ? output : acceptance.FailureDetail,
            ResultSummary: summary);
        return new AcceptanceVerificationSummary(
            false,
            [check],
            acceptance.FailureDetail,
            acceptance.FailedChecks,
            acceptance.BranchHeadSha,
            acceptance.MainHeadSha,
            acceptance.TestResultPaths,
            acceptance.CheckAttributions,
            acceptance.BaselineAttestation);
    }

    internal static void ReconcileCleanBaselineAttention(
        ICollaborationItemStore store,
        Goal goal,
        string? mainHeadSha,
        CleanTestBaselineReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(goal);
        ArgumentNullException.ThrowIfNull(receipt);

        var currentCorrelationKey =
            CleanBaselineRedCorrelationKeyPrefix + (mainHeadSha?.Trim().ToLowerInvariant() ?? "unknown");
        var activeCorrelationKey = receipt.Attestation == CleanBaselineAttestation.AttestedRed
            ? currentCorrelationKey
            : null;
        if (activeCorrelationKey is not null)
        {
            store.RaiseAsync(
                CollaborationItemType.Decision,
                goal.Id.Value,
                $"Red clean-test baseline at {FormatShortSha(mainHeadSha)}",
                CleanTestBaseline.FormatJournalDetail(receipt),
                activeCorrelationKey,
                CancellationToken.None).GetAwaiter().GetResult();
        }

        var staleItems = store.ListAsync(cancellationToken: CancellationToken.None)
            .GetAwaiter()
            .GetResult()
            .Where(item =>
                item.CorrelationKey is { Length: > 0 } key &&
                key.StartsWith(CleanBaselineRedCorrelationKeyPrefix, StringComparison.Ordinal) &&
                !string.Equals(key, activeCorrelationKey, StringComparison.Ordinal) &&
                (receipt.Attestation != CleanBaselineAttestation.Unattested ||
                 !string.Equals(key, currentCorrelationKey, StringComparison.Ordinal)))
            .ToArray();
        foreach (var item in staleItems)
        {
            store.TryResolveAsync(
                item.CorrelationKey!,
                $"clean-test baseline no longer active at main {FormatShortSha(mainHeadSha)}",
                CancellationToken.None).GetAwaiter().GetResult();
        }
    }

    internal ConductorAdvanceResult ReplayParallelLandingEarlyOutcome(
        ConductorParallelAcceptanceCandidate candidate,
        ConductorAutonomyPolicy policy,
        ConductorAdvanceResult earlyResult,
        ConductorParallelAcceptanceEarlyOutcome? earlyOutcome)
    {
        if (earlyOutcome is null)
        {
            return earlyResult;
        }

        return earlyOutcome.Kind switch
        {
            ConductorParallelAcceptanceEarlyOutcome.MissingBranchRetiredKind =>
                ReplayMissingBranchRetirement(candidate, policy, earlyOutcome),
            ConductorParallelAcceptanceEarlyOutcome.PreLandingEscalatedKind =>
                Escalate(candidate.Goal, candidate.GoalPrefix, policy, earlyOutcome.State, earlyOutcome.Detail),
            _ => earlyResult
        };
    }

    private ConductorAdvanceResult ReplayMissingBranchRetirement(
        ConductorParallelAcceptanceCandidate candidate,
        ConductorAutonomyPolicy policy,
        ConductorParallelAcceptanceEarlyOutcome earlyOutcome)
    {
        _recordMissingBranchRetirement(candidate.Goal, earlyOutcome.Detail);
        return MakeResult(
            candidate.Goal.Id.Value,
            candidate.GoalPrefix,
            policy,
            new ConductorAdvanceOutcome.Done(earlyOutcome.State));
    }

    private ConductorAdvanceResult ExecuteCreateWorkspace(Goal goal, string goalPrefix, ConductorAutonomyPolicy policy)
    {
        try
        {
            var path = _createWorkspace(goal);
            return MakeResult(goal.Id.Value, goalPrefix, policy,
                new ConductorAdvanceOutcome.Executed(GoalLifecycleState.Created, $"Workspace created: {path}"));
        }
        catch (EvidenceMutationLeaseUnavailableException ex)
        {
            return MakeResult(goal.Id.Value, goalPrefix, policy,
                new ConductorAdvanceOutcome.Held(GoalLifecycleState.Created, ex.Message));
        }
    }

    private ConductorAdvanceResult ExecuteDispatchAndStart(
        Goal goal,
        string goalPrefix,
        ConductorAutonomyPolicy policy,
        GoalLifecycleState fromState)
    {
        var running = _getRunningPaidWorkerCount();
        var workerAdmission = GetWorkerAdmissionSnapshot(policy);
        var workerCap = workerAdmission.EffectiveWorkerCap;

        if (running >= workerCap)
        {
            var reservedGateSlot = workerAdmission.ReservedGateSlots > 0 &&
                                   workerCap < policy.MaxConcurrentPaidWorkers;
            var admissionClamped = workerCap < workerAdmission.ConfiguredWorkerCap;
            if (admissionClamped)
            {
                var reason = reservedGateSlot ? "reserved-gate-slot" : "worker-admission-capacity";
                Console.WriteLine(
                    $"ADMISSION goal={goalPrefix} result=deferred reason={reason} cap={workerCap} running={running} " +
                    $"configuredCap={workerAdmission.ConfiguredWorkerCap} admissionCapacity={workerAdmission.AdmissionCapacity}");
            }

            EmitPhaseTiming("dispatch-prep", goal, TimeSpan.Zero, $"tasks={CountAssignedTasks(goal)} result=held-cap running={running}");
            return MakeResult(goal.Id.Value, goalPrefix, policy,
                new ConductorAdvanceOutcome.Held(fromState,
                    reservedGateSlot
                        ? $"At worker cap ({running}/{workerCap}) with a gate-ready goal reserving a stable slot; will advance when a slot opens"
                        : admissionClamped
                            ? $"At worker admission capacity ({running}/{workerCap}); configured cap {workerAdmission.ConfiguredWorkerCap} is clamped; will advance when a slot opens"
                            : $"At worker cap ({running}/{workerCap}); will advance when a slot opens"));
        }

        if (fromState == GoalLifecycleState.WorkspaceReady &&
            HasAssignedDeveloperReadyForDispatch(goal))
        {
            using var integrationEvidenceMutationLease = _tryAcquireEvidenceMutationLease(
                goal,
                "conductor:developer-branch-integration");
            if (integrationEvidenceMutationLease is null)
            {
                return MakeResult(
                    goal.Id.Value,
                    goalPrefix,
                    policy,
                    new ConductorAdvanceOutcome.Held(
                        fromState,
                        $"Goal evidence mutation is blocked by concurrent acceptance or replacement for {goal.Id.Value}."));
            }

            var integration = _integrateMainBeforeDeveloperDispatch(goal);
            if (!integration.CanDispatch)
            {
                return Escalate(goal, goalPrefix, policy, fromState, integration.Message);
            }
        }

        if (TryRunPreReviewEvidenceStage(goal, goalPrefix, policy, fromState, out var preReviewResult))
        {
            return preReviewResult;
        }

        var start = fromState == GoalLifecycleState.Dispatched ? _startRecordedDispatches : _dispatchAndStart;
        var startClock = Stopwatch.StartNew();
        var outcome = start(goal, policy);
        startClock.Stop();
        EmitPhaseTiming("dispatch-prep", goal, startClock.Elapsed, $"tasks={CountAssignedTasks(goal)} result={outcome.Category}");
        if (outcome.Category == DispatchStartOutcomeCategory.RecoverableSandboxPrep)
        {
            if (!TryRecoverSandboxPrep(outcome, goalPrefix, out var recoveryFailure))
            {
                return Escalate(goal, goalPrefix, policy, fromState, recoveryFailure);
            }

            var retryStart = fromState == GoalLifecycleState.WorkspaceReady
                ? _startRecordedDispatches
                : start;
            startClock.Restart();
            outcome = retryStart(goal, policy);
            startClock.Stop();
            EmitPhaseTiming("dispatch-prep", goal, startClock.Elapsed, $"tasks={CountAssignedTasks(goal)} result={outcome.Category} retry=sandbox-prep");
        }

        if (outcome.Category == DispatchStartOutcomeCategory.SpawnFailed)
        {
            var firstFailure = outcome;
            var remediationClock = Stopwatch.StartNew();
            var remediationResult = RunDispatchRemediation();
            remediationClock.Stop();
            EmitGoalPhaseTiming(
                "dispatch-remediation",
                goal,
                remediationClock.Elapsed,
                $"result={remediationResult}");
            var retryStart = fromState == GoalLifecycleState.WorkspaceReady
                ? _startRecordedDispatches
                : start;
            startClock.Restart();
            outcome = retryStart(goal, policy);
            startClock.Stop();
            EmitPhaseTiming("dispatch-prep", goal, startClock.Elapsed, $"tasks={CountAssignedTasks(goal)} result={outcome.Category} retry=spawn-failed");
            if (outcome.Category == DispatchStartOutcomeCategory.EmptyBatch)
            {
                outcome = firstFailure;
            }
        }

        if (outcome.Category == DispatchStartOutcomeCategory.Deferred)
        {
            return MakeResult(goal.Id.Value, goalPrefix, policy,
                new ConductorAdvanceOutcome.Held(fromState, outcome.Reason!));
        }

        if (outcome.Category == DispatchStartOutcomeCategory.Started)
        {
            return MakeResult(goal.Id.Value, goalPrefix, policy,
                new ConductorAdvanceOutcome.Executed(fromState, "Subscription dispatch started"));
        }

        // When all ready tasks are blocked or deferred, hold rather than escalate so the conductor
        // retries on the next tick. Uses the canonical DispatchReadinessEvaluator so this decision
        // always agrees with GoalReadinessPreflight and CrossGoalSubscriptionStartPlanner.
        if (outcome.Category == DispatchStartOutcomeCategory.EmptyBatch)
        {
            var readiness = _evaluateReadiness(goal);
            if (readiness is DispatchReadinessDeferred deferred)
            {
                return MakeResult(goal.Id.Value, goalPrefix, policy,
                    new ConductorAdvanceOutcome.Held(fromState,
                        $"All assigned tasks deferred by provider cooldown; {deferred.Reason}. Will retry next tick."));
            }

            if (TryDescribeCancelledPredecessorBlocker(goal, out var terminalBlocker))
            {
                return Escalate(
                    goal,
                    goalPrefix,
                    policy,
                    fromState,
                    $"STRUCTURAL_TASK_BLOCKER: {terminalBlocker}. Operator recovery is required; retrying cannot complete a cancelled predecessor.");
            }

            if (readiness is not DispatchReadinessBlocked { HasCandidates: false })
            {
                return MakeResult(goal.Id.Value, goalPrefix, policy,
                    new ConductorAdvanceOutcome.Held(fromState,
                        FormatAssignedTasksBlockedReason(goal, readiness, outcome.Reason)));
            }
        }

        return Escalate(goal, goalPrefix, policy, fromState, outcome.Reason!);
    }

    private bool TryRunPreReviewEvidenceStage(
        Goal goal,
        string goalPrefix,
        ConductorAutonomyPolicy policy,
        GoalLifecycleState fromState,
        out ConductorAdvanceResult result)
    {
        result = default!;
        var reviewerTask = goal.Tasks.FirstOrDefault(task => task.RequiredRole == AgentRole.Reviewer);
        if (reviewerTask is null ||
            reviewerTask.Status != WorkTaskStatus.Assigned ||
            !TasksBefore(goal, reviewerTask).All(task => task.Status == WorkTaskStatus.Completed))
        {
            return false;
        }

        var context = _getPreReviewEvidenceContext(goal);
        if (string.IsNullOrWhiteSpace(context.CandidateSha))
        {
            result = Escalate(
                goal,
                goalPrefix,
                policy,
                fromState,
                "PRE_REVIEW_MAPPING_NEEDS_INPUT: current candidate HEAD could not be resolved; Reviewer dispatch is blocked.");
            return true;
        }

        var round = GetCurrentReviewerRoundNumber(goal, reviewerTask);
        var currentReceipt = reviewerTask.PreReviewEvidenceReceipt;
        if (currentReceipt is { } current &&
            current.MatchesCurrentCandidate(goal.Id.Value, context.CandidateSha, context.SelectedFocusedTests))
        {
            if (current.Disposition is PreReviewEvidenceDisposition.Green or PreReviewEvidenceDisposition.NoApplicableTests)
            {
                return false;
            }
            // Red or inconclusive evidence caused an upstream retry. Once that task completes,
            // re-run the deterministic evidence even when the candidate SHA did not change;
            // otherwise the stale non-green receipt can never be replaced by a current result.
        }

        if (context.NoApplicableTests)
        {
            RecordPreReviewReceipt(
                goal,
                reviewerTask,
                context,
                round,
                PreReviewEvidenceDisposition.NoApplicableTests,
                [],
                [],
                evidencePointer: null);
            return false;
        }

        if (context.MappingNeedsInput || string.IsNullOrWhiteSpace(context.FocusedRequest))
        {
            var receipt = RecordPreReviewReceipt(
                goal,
                reviewerTask,
                context,
                round,
                PreReviewEvidenceDisposition.MappingNeedsInput,
                [],
                [],
                evidencePointer: null);
            if (TryRoutePreReviewEvidenceToTester(
                    goal,
                    reviewerTask,
                    goalPrefix,
                    policy,
                    $"pre-review mapping requires Tester selection for candidate {context.CandidateSha}: {context.MappingReason}",
                    out result))
            {
                return true;
            }

            result = Escalate(
                goal,
                goalPrefix,
                policy,
                fromState,
                $"PRE_REVIEW_MAPPING_NEEDS_INPUT: deterministic test-impact mapping requires typed operator/Tester selection; " +
                $"Reviewer dispatch is blocked for candidate {context.CandidateSha}. Reason: {context.MappingReason}. " +
                $"Receipt round={receipt.ReviewerRound}.");
            return true;
        }

        var candidate = ConductorParallelAcceptanceCandidate.Create(
            goal,
            slotIndex: 0,
            fileScopes: [],
            branchHeadSha: context.CandidateSha,
            mainHeadSha: null);
        var attemptDecision = _focusedEvidenceAttemptCoordinator.EvaluateFocusedEvidence(
            candidate,
            policy,
            context.FocusedRequest,
            _runFocusedEvidence);
        if (attemptDecision.Kind is
            ConductorParallelAcceptanceAttemptDecisionKind.Started or
            ConductorParallelAcceptanceAttemptDecisionKind.Running)
        {
            result = MakeResult(
                goal.Id.Value,
                goalPrefix,
                policy,
                new ConductorAdvanceOutcome.Held(
                    fromState,
                    _focusedEvidenceAttemptCoordinator.DescribeFocusedEvidenceHold(attemptDecision.Attempt),
                    $"pre-review-evidence:{attemptDecision.Attempt.AttemptId}"));
            return true;
        }

        if (attemptDecision.Kind == ConductorParallelAcceptanceAttemptDecisionKind.TerminalWithoutRun ||
            attemptDecision.Run?.Exception is
                DotnetBuildSlotsBusyException or
                BuildLockBlockedException or
                OperationCanceledException)
        {
            _focusedEvidenceAttemptCoordinator.MarkReconciled(attemptDecision.Attempt);
            result = MakeResult(
                goal.Id.Value,
                goalPrefix,
                policy,
                new ConductorAdvanceOutcome.Held(
                    fromState,
                    $"Background pre-review evidence did not run ({attemptDecision.Attempt.Outcome}); " +
                    $"retry on next conduct tick. attempt={attemptDecision.Attempt.AttemptId}: " +
                    (attemptDecision.Attempt.Detail ?? "no result artifact was produced")));
            return true;
        }

        _focusedEvidenceAttemptCoordinator.MarkReconciled(attemptDecision.Attempt);
        if (attemptDecision.Run?.Exception is { } backgroundFailure)
        {
            result = Escalate(
                goal,
                goalPrefix,
                policy,
                fromState,
                $"PRE_REVIEW_EVIDENCE_FAILED: background focused evidence run failed. " +
                $"attempt={attemptDecision.Attempt.AttemptId}: {backgroundFailure.Message}");
            return true;
        }

        var evidence = attemptDecision.Run?.FocusedEvidence ?? new FocusedEvidenceRunResult(
            context.FocusedRequest,
            Accepted: false,
            Passed: false,
            Summary: $"background pre-review evidence {attemptDecision.Attempt.Outcome}: " +
                (attemptDecision.Attempt.Detail ?? "no result artifact was produced"),
            Checks: []);
        var evidencePointer = BuildPreReviewEvidencePointer(evidence);
        if (!evidence.Accepted)
        {
            RecordPreReviewReceipt(
                goal,
                reviewerTask,
                context,
                round,
                PreReviewEvidenceDisposition.MappingNeedsInput,
                evidence.Checks,
                [],
                evidencePointer);
            if (TryRoutePreReviewEvidenceToTester(
                    goal,
                    reviewerTask,
                    goalPrefix,
                    policy,
                    $"pre-review focused-evidence request was rejected for candidate {context.CandidateSha}: {FormatFocusedEvidenceResult(evidence)}",
                    out result))
            {
                return true;
            }

            result = Escalate(
                goal,
                goalPrefix,
                policy,
                fromState,
                $"PRE_REVIEW_MAPPING_NEEDS_INPUT: mapped focused evidence request was rejected; Reviewer dispatch is blocked. " +
                $"{FormatFocusedEvidenceResult(evidence)}");
            return true;
        }

        if (evidence.Passed)
        {
            if (!TryValidatePreReviewEvidenceCoverage(context, evidence, out var mappingFailure))
            {
                RecordPreReviewReceipt(
                    goal,
                    reviewerTask,
                    context,
                    round,
                    PreReviewEvidenceDisposition.MappingNeedsInput,
                    evidence.Checks,
                    [],
                    evidencePointer);
                var mismatch = $"pre-review evidence mapping failure for candidate {context.CandidateSha}: {mappingFailure}";
                if (TryRoutePreReviewEvidenceToTester(
                        goal,
                        reviewerTask,
                        goalPrefix,
                        policy,
                        mismatch,
                        out result))
                {
                    return true;
                }

                if (currentReceipt is { Disposition: PreReviewEvidenceDisposition.MappingNeedsInput } &&
                    string.Equals(currentReceipt.CandidateSha, context.CandidateSha, StringComparison.OrdinalIgnoreCase))
                {
                    var suppressedCount = 1 + goal.Timeline.Count(evt =>
                        evt.Kind == ProgressKind.PreReviewMappingEscalationSuppressed &&
                        evt.TaskId == reviewerTask.Id &&
                        evt.Message.Contains($"candidate_sha={context.CandidateSha}", StringComparison.OrdinalIgnoreCase) &&
                        evt.Message.Contains(
                            $"disposition={PreReviewEvidenceDisposition.MappingNeedsInput}",
                            StringComparison.Ordinal));
                    _recordPreReviewMappingEscalationSuppressed(
                        goal.Id,
                        reviewerTask.Id,
                        context.CandidateSha,
                        suppressedCount);
                    result = MakeResult(
                        goal.Id.Value,
                        goalPrefix,
                        policy,
                        new ConductorAdvanceOutcome.Held(
                            fromState,
                            $"PRE_REVIEW_MAPPING_NEEDS_INPUT repeat suppressed for candidate {context.CandidateSha}; " +
                            $"suppressed_count={suppressedCount}."));
                    return true;
                }

                result = Escalate(
                    goal,
                    goalPrefix,
                    policy,
                    fromState,
                    $"PRE_REVIEW_MAPPING_NEEDS_INPUT: {mismatch}; no Tester task is available. " +
                    $"Add one with: {BuildAddTesterCommand(goalPrefix, context.CandidateSha)}");
                return true;
            }

            RecordPreReviewReceipt(
                goal,
                reviewerTask,
                context,
                round,
                PreReviewEvidenceDisposition.Green,
                evidence.Checks,
                [],
                evidencePointer);
            return false;
        }

        if (evidence.OutcomeReason == FindingEvidenceOutcomeReason.ApparatusFailure)
        {
            RecordPreReviewReceipt(
                goal,
                reviewerTask,
                context,
                round,
                PreReviewEvidenceDisposition.MappingNeedsInput,
                evidence.Checks,
                [],
                evidencePointer);
            var apparatusDetail =
                $"pre-review focused selection apparatus failure for candidate {context.CandidateSha}; " +
                $"the run executed zero tests and is not candidate-failure evidence; pointer={evidencePointer ?? "none"}";
            if (TryRoutePreReviewEvidenceToTester(
                    goal,
                    reviewerTask,
                    goalPrefix,
                    policy,
                    apparatusDetail,
                    out result))
            {
                return true;
            }

            result = Escalate(
                goal,
                goalPrefix,
                policy,
                fromState,
                $"PRE_REVIEW_SELECTION_APPARATUS_FAILURE: {apparatusDetail}; no Tester task is available.");
            return true;
        }

        var failingTests = ExtractFailingTestIdentities(evidence.Checks);
        if (failingTests.Count == 0)
        {
            RecordPreReviewReceipt(
                goal,
                reviewerTask,
                context,
                round,
                PreReviewEvidenceDisposition.MappingNeedsInput,
                evidence.Checks,
                [],
                evidencePointer);
            if (TryRoutePreReviewEvidenceToTester(
                    goal,
                    reviewerTask,
                    goalPrefix,
                    policy,
                    $"pre-review checks were red but yielded no typed TRX failure identities for candidate {context.CandidateSha}; " +
                    $"pointer={evidencePointer ?? "none"}",
                    out result))
            {
                return true;
            }

            result = Escalate(
                goal,
                goalPrefix,
                policy,
                fromState,
                "PRE_REVIEW_MAPPING_NEEDS_INPUT: focused checks were red but produced no exact failing test identities; " +
                $"Reviewer and Developer dispatch are blocked pending typed evidence. Pointer={evidencePointer ?? "none"}.");
            return true;
        }

        RecordPreReviewReceipt(
            goal,
            reviewerTask,
            context,
            round,
            PreReviewEvidenceDisposition.Red,
            evidence.Checks,
            failingTests,
            evidencePointer);
        var developerTask = TasksBefore(goal, reviewerTask)
            .LastOrDefault(task => task.RequiredRole == AgentRole.Developer);
        if (developerTask is null)
        {
            result = Escalate(
                goal,
                goalPrefix,
                policy,
                fromState,
                $"PRE_REVIEW_RED: no responsible Developer task exists. Failing tests: {string.Join(", ", failingTests)}. " +
                $"Pointer={evidencePointer ?? "none"}.");
            return true;
        }

        _retryTask(
            goal.Id,
            developerTask.Id,
            $"pre-review focused-test repair: candidate {context.CandidateSha}; exact failing tests: " +
            $"{string.Join(", ", failingTests)}; evidence pointer: {evidencePointer ?? "none"}",
            RetryRoundKind.Mechanical);
        var retryState = GoalLifecycle.ResolveState(goal, GetFacts(goal));
        result = ExecuteDispatchAndStart(goal, goalPrefix, policy, retryState);
        return true;
    }

    private bool TryRoutePreReviewEvidenceToTester(
        Goal goal,
        TaskSpec reviewerTask,
        string goalPrefix,
        ConductorAutonomyPolicy policy,
        string reason,
        out ConductorAdvanceResult result)
    {
        var testerTask = TasksBefore(goal, reviewerTask)
            .LastOrDefault(task => task.RequiredRole == AgentRole.Tester);
        if (testerTask is null)
        {
            result = default!;
            return false;
        }

        _retryTask(
            goal.Id,
            testerTask.Id,
            reason,
            RetryRoundKind.Mechanical);
        var retryState = GoalLifecycle.ResolveState(goal, GetFacts(goal));
        result = ExecuteDispatchAndStart(goal, goalPrefix, policy, retryState);
        return true;
    }

    internal static PreReviewEvidenceContext BuildPreReviewEvidenceContext(
        string? candidateSha,
        IReadOnlyList<string> changedFiles)
    {
        var plan = RepositoryTestImpactPlanner.Plan(changedFiles);
        if (!plan.RequiresBuild &&
            plan.Checks.Count > 0 &&
            plan.Checks.All(check => check.Command.Count == 0))
        {
            var generatedArtifactsBlock = plan.Summary.Contains(
                "Generated artifacts",
                StringComparison.OrdinalIgnoreCase);
            return new PreReviewEvidenceContext(
                candidateSha,
                [],
                null,
                plan.Summary,
                NoApplicableTests: !generatedArtifactsBlock,
                MappingNeedsInput: generatedArtifactsBlock);
        }

        var focusedChecks = plan.Checks
            .Where(check => FindArgument(check.Command, "--filter") >= 0)
            .ToArray();
        if (focusedChecks.Length == 0)
        {
            return new PreReviewEvidenceContext(
                candidateSha,
                [],
                null,
                $"{plan.Summary} No filtered test target mapped; project-wide checks are deferred to the acceptance gate.",
                NoApplicableTests: true,
                MappingNeedsInput: false);
        }

        var selected = focusedChecks.Select(check => check.CommandLine).ToArray();
        var requests = new List<string>();
        foreach (var check in focusedChecks)
        {
            var filterIndex = FindArgument(check.Command, "--filter");
            var project = check.Command.FirstOrDefault(argument =>
                argument.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase));
            if (string.IsNullOrWhiteSpace(project))
            {
                return new PreReviewEvidenceContext(
                    candidateSha,
                    selected,
                    null,
                    plan.Summary,
                    NoApplicableTests: false,
                    MappingNeedsInput: true);
            }

            var alias = project.Contains("Core.Tests", StringComparison.OrdinalIgnoreCase)
                ? "Core.Tests"
                : project.Contains("Infrastructure.Tests", StringComparison.OrdinalIgnoreCase)
                    ? "Infrastructure.Tests"
                    : project.Contains("Dashboard.Tests", StringComparison.OrdinalIgnoreCase)
                        ? "Dashboard.Tests"
                        : null;
            if (alias is null)
            {
                return new PreReviewEvidenceContext(
                    candidateSha,
                    selected,
                    null,
                    $"Mapped project is not supported by the focused evidence broker: {project}",
                    NoApplicableTests: false,
                    MappingNeedsInput: true);
            }

            if (filterIndex >= 0 && filterIndex + 1 >= check.Command.Count)
            {
                return new PreReviewEvidenceContext(
                    candidateSha,
                    selected,
                    null,
                    $"Mapped test command has an empty --filter argument: {check.CommandLine}",
                    NoApplicableTests: false,
                    MappingNeedsInput: true);
            }

            requests.Add($"{alias}: {check.Command[filterIndex + 1]}");
        }

        return new PreReviewEvidenceContext(
            candidateSha,
            requests,
            string.Join("; ", requests),
            plan.Summary,
            NoApplicableTests: false,
            MappingNeedsInput: requests.Count == 0);
    }

    private PreReviewEvidenceReceipt RecordPreReviewReceipt(
        Goal goal,
        TaskSpec reviewerTask,
        PreReviewEvidenceContext context,
        int round,
        PreReviewEvidenceDisposition disposition,
        IReadOnlyList<AcceptanceCheckResult> checks,
        IReadOnlyList<string> failingTests,
        string? evidencePointer)
    {
        var receipt = new PreReviewEvidenceReceipt(
            goal.Id.Value,
            round,
            context.CandidateSha!,
            context.SelectedFocusedTests,
            disposition,
            checks.Count(check => check.Passed),
            checks.Count(check => !check.Passed),
            checks.Select((check, index) => new PreReviewEvidenceCheckReceipt(
                check.Name,
                ResolvePreReviewReceiptTarget(context, index, checks.Count),
                check.Passed,
                check.ExitCode,
                check.ArtifactsPath,
                check.TestResultPaths)).ToArray(),
            failingTests,
            context.MappingReason,
            evidencePointer,
            DateTimeOffset.UtcNow);
        _recordPreReviewEvidence(goal.Id, reviewerTask.Id, receipt);
        return receipt;
    }

    private static bool TryValidatePreReviewEvidenceCoverage(
        PreReviewEvidenceContext context,
        FocusedEvidenceRunResult evidence,
        out string failure)
    {
        failure = string.Empty;
        if (evidence.Checks.Count == context.SelectedFocusedTests.Count)
        {
            return true;
        }

        failure = $"cardinality mismatch: planned={context.SelectedFocusedTests.Count} actual={evidence.Checks.Count}";
        return false;
    }

    private static string ResolvePreReviewReceiptTarget(
        PreReviewEvidenceContext context,
        int index,
        int checkCount)
    {
        return checkCount == context.SelectedFocusedTests.Count
            ? context.SelectedFocusedTests[index]
            : "(unmapped: check/command cardinality mismatch)";
    }

    private static string BuildAddTesterCommand(string goalPrefix, string candidateSha) =>
        $"add-task --goal {goalPrefix} Tester Resolve pre-review mapping for candidate {candidateSha} --before-role Reviewer";

    private static IReadOnlyList<TaskSpec> TasksBefore(Goal goal, TaskSpec task)
        => goal.Tasks.TakeWhile(candidate => candidate.Id != task.Id).ToArray();

    private static int GetCurrentReviewerRoundNumber(Goal goal, TaskSpec reviewerTask) =>
        1 + goal.Timeline.Count(evt =>
            evt.Kind == ProgressKind.TaskRetried &&
            evt.TaskId is not null &&
            (evt.TaskId != reviewerTask.Id ||
                !MechanicalReviewerRetryMessagePrefixes.Any(prefix =>
                    evt.Message.StartsWith(prefix, StringComparison.Ordinal))));

    private static int FindArgument(IReadOnlyList<string> arguments, string value)
    {
        for (var index = 0; index < arguments.Count; index++)
        {
            if (arguments[index].Equals(value, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return -1;
    }

    private static IReadOnlyList<string> ExtractFailingTestIdentities(
        IEnumerable<AcceptanceCheckResult> checks) =>
        checks
            .SelectMany(check => check.FailingTestIdentities ?? [])
            .Where(identity => identity.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    private static string? BuildPreReviewEvidencePointer(FocusedEvidenceRunResult evidence) =>
        evidence.Checks
            .SelectMany(check =>
                (check.TestResultPaths ?? [])
                    .Concat(string.IsNullOrWhiteSpace(check.ArtifactsPath) ? [] : [check.ArtifactsPath!]))
            .FirstOrDefault(path => !string.IsNullOrWhiteSpace(path));

    private bool TryRecoverSandboxPrep(DispatchStartOutcome outcome, string goalPrefix, out string failureReason)
    {
        if (outcome.SandboxPrepRecoveryAction is not { } action)
        {
            failureReason = outcome.Reason ?? "Low-IL sandbox prep recovery action was missing.";
            return false;
        }

        try
        {
            if (_recoverSandboxPrep(action))
            {
                failureReason = string.Empty;
                return true;
            }
        }
        catch (Exception ex)
        {
            failureReason = $"Low-IL sandbox prep recovery failed for goal {goalPrefix}: {ex.Message}";
            return false;
        }

        failureReason = $"Low-IL sandbox prep recovery failed for goal {goalPrefix}: {action.Reason}";
        return false;
    }

    private void EmitPhaseTiming(string phase, Goal goal, TimeSpan elapsed, string detail)
    {
        foreach (var task in goal.Tasks.Where(task => task.Status == WorkTaskStatus.Assigned))
        {
            PhaseTimingSink?.Invoke(
                $"phase={phase} goal={goal.Id.Value[..8]} task={task.Id.Value[..8]} role={task.RequiredRole} elapsed_ms={(long)elapsed.TotalMilliseconds} {detail}");
        }
    }

    private void EmitGoalPhaseTiming(string phase, Goal goal, TimeSpan elapsed, string detail)
    {
        var elapsedMilliseconds = (long)Math.Ceiling(elapsed.TotalMilliseconds);
        PhaseTimingSink?.Invoke(
            $"phase={phase} goal={goal.Id.Value[..8]} elapsed_ms={elapsedMilliseconds} {detail}");
    }

    private string RunDispatchRemediation()
    {
        if (_buildServerShutdownRanThisTick)
        {
            return "skipped-tick-latch";
        }

        _buildServerShutdownRanThisTick = true;
        return _buildServerShutdown(_buildServerShutdownTimeout);
    }

    private static string RunBoundedBuildServerShutdown(Action shutdown, TimeSpan timeout)
    {
        try
        {
            var shutdownTask = Task.Run(shutdown);
            if (shutdownTask.Wait(timeout))
            {
                return "ran";
            }

            _ = shutdownTask.ContinueWith(
                static task => _ = task.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            return "timeout";
        }
        catch
        {
            return "error";
        }
    }

    internal static string RunBuildServerShutdown(string workingDirectory, TimeSpan timeout)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = workingDirectory
            };
            startInfo.ArgumentList.Add("build-server");
            startInfo.ArgumentList.Add("shutdown");
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return "error";
            }

            process.OutputDataReceived += static (_, _) => { };
            process.ErrorDataReceived += static (_, _) => { };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            var timeoutMilliseconds = (int)Math.Clamp(
                Math.Ceiling(timeout.TotalMilliseconds),
                1,
                int.MaxValue);
            if (process.WaitForExit(timeoutMilliseconds))
            {
                return process.ExitCode == 0
                    ? "ran exit=0"
                    : $"error exit={process.ExitCode}";
            }

            try
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(1_000);
            }
            catch
            {
                // The process may have exited between the timed wait and tree kill.
            }

            return "timeout";
        }
        catch
        {
            return "error";
        }
    }

    private static void AppendGateProgressEvent(
        ConductEventLogWriter writer,
        GoalId goalId,
        AcceptanceGateProgress progress)
    {
        if (!writer.AppendRequired(
                "gate-progress",
                goalId.Value[..8],
                FormatGateProgressConductEvent(progress)))
        {
            throw new IOException(
                $"Required gate progress event could not be appended for goal {goalId.Value[..8]}.");
        }
    }

    private static string FormatGateProgressConductEvent(AcceptanceGateProgress progress) =>
        $"PHASE_PROGRESS goal={progress.GoalId?[..Math.Min(8, progress.GoalId.Length)] ?? "unknown"} phase={progress.Phase} " +
        $"elapsed_ms={(long)progress.Elapsed.TotalMilliseconds} target={FormatConductToken(progress.CurrentTarget)} " +
        $"child_pid={progress.ChildProcessId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown"} " +
        $"output_bytes={progress.OutputBytes} heartbeat={FormatConductToken(progress.HeartbeatPath)}";

    private static string FormatConductToken(string value) =>
        value.IndexOfAny([' ', '\t', '\r', '\n', '"']) < 0
            ? value
            : $"\"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\"";

    private static int CountAssignedTasks(Goal goal) =>
        goal.Tasks.Count(task => task.Status == WorkTaskStatus.Assigned);

    private static bool HasAssignedDeveloperReadyForDispatch(Goal goal) =>
        goal.Tasks.Any(task =>
            task.RequiredRole == AgentRole.Developer &&
            task.Status == WorkTaskStatus.Assigned &&
            !goal.Tasks.Any(candidate =>
                GoalManagementCommandService.IsEarlierSdlcStageOf(
                    candidate.RequiredRole,
                    task.RequiredRole) &&
                candidate.Status != WorkTaskStatus.Completed));

    private static string FormatNoRecordedDispatchStartedReason(ProcessBatchPlan plan)
    {
        var skippedReason = plan.Items
            .Where(item => item.Status == ProcessBatchItemStatus.Skipped)
            .Select(item => item.Reason)
            .FirstOrDefault(reason => !string.IsNullOrWhiteSpace(reason));
        return skippedReason is null
            ? "Dispatch recorded but no process was startable."
            : $"Dispatch recorded but no process was startable: {skippedReason}";
    }

    // Turns an empty subscription dispatch batch into an ACTIONABLE escalation. When the parallel
    // planner held every ready task back for operator approval (e.g. a high-risk ownership write-set
    // like scripts/ or src/Infrastructure under a non-permissive policy), surface those reasons so
    // the operator knows what to approve — instead of the generic "no ready batch" that hides why
    // nothing dispatched and forces a manual dig (see conductor-high-risk-ownership-gap).
    internal static string DescribeEmptyBatch(
        ParallelExecutionPlan plan,
        IReadOnlyList<ReadyBlockedDiagnostic>? blockedDiagnostics = null)
    {
        var diagnosticReasons = blockedDiagnostics?
            .Select(FormatReadyBlockedDiagnostic)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList() ?? [];
        if (diagnosticReasons.Count > 0)
        {
            return "No tasks dispatched; assigned tasks were excluded from the ready batch: "
                + string.Join("; ", diagnosticReasons);
        }

        var approvalReasons = plan.Decisions
            .Where(decision => decision.Disposition == ParallelExecutionDisposition.RequiresOperatorApproval)
            .SelectMany(decision => decision.Reasons)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return approvalReasons.Count > 0
            ? "No tasks dispatched; all ready tasks require operator approval (run under a policy that "
                + "auto-approves high-risk ownership, or approve manually): "
                + string.Join("; ", approvalReasons)
            : "No tasks in ready batch; goal may have no assigned or ready tasks";
    }

    private static string FormatReadyBlockedDiagnostic(ReadyBlockedDiagnostic diagnostic)
    {
        var details = diagnostic.Details?
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .ToArray() ?? [];
        var detail = details.Length > 0
            ? $": {string.Join(", ", details)}"
            : string.Empty;
        return $"task {diagnostic.TaskNumber} {diagnostic.TaskId} provider={diagnostic.Provider} reason={diagnostic.Reason}{detail}";
    }

    private static string FormatAssignedTasksBlockedReason(
        Goal goal,
        DispatchReadinessVerdict readiness,
        string? emptyBatchReason)
    {
        var taskReasons = goal.Tasks
            .Where(task => task.Status == WorkTaskStatus.Assigned)
            .Select(task => FormatAssignedTaskBlocker(goal, task, readiness, emptyBatchReason))
            .ToArray();
        var blockers = taskReasons.Length == 0
            ? "no Assigned tasks remained when the batch was evaluated"
            : string.Join("; ", taskReasons);
        return $"Assigned tasks exist but no ready batch formed for goal {goal.Id.Value}; will retry next tick. Blockers: {blockers}.";
    }

    private static string FormatAssignedTaskBlocker(
        Goal goal,
        TaskSpec task,
        DispatchReadinessVerdict readiness,
        string? emptyBatchReason)
    {
        if (task.LastProcess is { IsRunning: true })
        {
            return $"task {task.Id.Value} ({task.RequiredRole}) blocked: task already has a running process";
        }

        var predecessor = goal.Tasks.FirstOrDefault(candidate =>
            GoalManagementCommandService.IsEarlierSdlcStageOf(candidate.RequiredRole, task.RequiredRole) &&
            candidate.Status != WorkTaskStatus.Completed);
        if (predecessor is not null)
        {
            return $"task {task.Id.Value} ({task.RequiredRole}) blocked: predecessor {predecessor.Id.Value} is {predecessor.Status}, not Completed";
        }

        var readinessReason = readiness switch
        {
            DispatchReadinessDeferred deferred => $"readiness gate returned false: {deferred.Reason}",
            DispatchReadinessBlocked blocked => $"readiness gate returned false: {blocked.Reason}",
            DispatchReadinessReady => string.IsNullOrWhiteSpace(emptyBatchReason)
                ? "batch formation returned no dispatch"
                : $"batch formation returned no dispatch: {emptyBatchReason}",
            _ => "batch formation returned no dispatch"
        };
        return $"task {task.Id.Value} ({task.RequiredRole}) blocked: {readinessReason}";
    }

    private static bool TryDescribeCancelledPredecessorBlocker(Goal goal, out string blocker)
    {
        foreach (var task in goal.Tasks.Where(task => task.Status == WorkTaskStatus.Assigned))
        {
            var predecessor = goal.Tasks.FirstOrDefault(candidate =>
                GoalManagementCommandService.IsEarlierSdlcStageOf(candidate.RequiredRole, task.RequiredRole) &&
                candidate.Status == WorkTaskStatus.Cancelled);
            if (predecessor is null)
            {
                continue;
            }

            blocker =
                $"task {task.Id.Value} ({task.RequiredRole}) blocked: predecessor {predecessor.Id.Value} is Cancelled, not Completed";
            return true;
        }

        blocker = string.Empty;
        return false;
    }

    private static TimeSpan ComputeEmptyOutputBackoff(ConductorAutonomyPolicy policy, int retryCount)
    {
        if (policy.EmptyOutputRetryInitialDelaySeconds <= 0 ||
            policy.EmptyOutputRetryMaxDelaySeconds <= 0)
        {
            return TimeSpan.Zero;
        }

        var exponent = Math.Max(0, retryCount - 1);
        var seconds = policy.EmptyOutputRetryInitialDelaySeconds *
            Math.Pow(policy.EmptyOutputRetryBackoffMultiplier, exponent);
        return TimeSpan.FromSeconds(Math.Min(seconds, policy.EmptyOutputRetryMaxDelaySeconds));
    }

    private static bool TryGetDispatchRecoveryAction(TaskVerificationRecord? verification, out DispatchRecoveryAction action)
    {
        action = default;
        if (verification is null)
        {
            return false;
        }

        var diagnostic = ExtractDispatchRecoveryDiagnostic(verification);
        if (diagnostic.Length == 0)
        {
            return false;
        }

        foreach (var candidate in Enum.GetValues<DispatchRecoveryAction>())
        {
            if (diagnostic.Contains($"action='{DispatchRecoveryPolicy.ToActionName(candidate)}'", StringComparison.Ordinal))
            {
                action = candidate;
                return true;
            }
        }

        return false;
    }

    private static DispatchRecoveryAction GetDispatchRecoveryAction(TaskVerificationRecord verification) =>
        TryGetDispatchRecoveryAction(verification, out var action)
            ? action
            : throw new InvalidOperationException("Verification does not contain a dispatch recovery action.");

    private static bool IsRetryableStaleRecovery(TaskVerificationRecord verification)
    {
        if (!TryGetDispatchRecoveryAction(verification, out var action))
            return false;

        if (action == DispatchRecoveryAction.RetryStale)
            return true;

        var diagnostic = ExtractDispatchRecoveryDiagnostic(verification);
        return action == DispatchRecoveryAction.MarkStale &&
            diagnostic.Contains("stale retry budget remaining=", StringComparison.Ordinal) &&
            !diagnostic.Contains("blocker='", StringComparison.Ordinal);
    }

    private static string ExtractDispatchRecoveryDiagnostic(TaskVerificationRecord verification)
    {
        var lines = verification.StandardError.Split(
            ["\r\n", "\n"],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.LastOrDefault(line => line.Contains("Dispatch recovery policy action='", StringComparison.Ordinal)) ?? string.Empty;
    }

    // Appends a bounded tail of the acceptance build/test output to an escalation/journal line so an
    // operator (or the conductor's own retry diagnostics) can see WHY acceptance failed — the detail
    // was previously dropped, leaving only a generic "Acceptance verification failed".
    private static string FormatFailureTail(string? outputTail)
    {
        if (string.IsNullOrWhiteSpace(outputTail))
        {
            return string.Empty;
        }

        var trimmed = outputTail.Trim();
        const int maxChars = 600;
        var tail = trimmed.Length > maxChars ? "..." + trimmed[^maxChars..] : trimmed;
        return $" Acceptance output tail: {tail}";
    }

    private static bool IsBlockingTimeoutCheck(string checkName) =>
        checkName.StartsWith("acceptance-check-timeout:", StringComparison.OrdinalIgnoreCase);

    private static string? TryResolveGitHead(string path)
    {
        var result = GitCli.Run(path, "rev-parse", "HEAD");
        return result.Succeeded ? result.Output.Trim() : null;
    }

    private static (string BranchHead, string MainHead, string Fingerprint) ReadLandingRecheckEvidence(
        string worktreePath)
    {
        var revisions = ConductorGitRevisionReader.ReadRequiredPair(worktreePath);
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

    private static bool IsCommitReachableFromMain(string executionDirectory, string commitSha) =>
        GitCli.Run(executionDirectory, "merge-base", "--is-ancestor", commitSha, "main").ExitCode == 0;

    private static string RecoverMainMergeCommitForBranchTip(string executionDirectory, string branchHeadSha)
    {
        var ancestry = GitCli.Run(executionDirectory, "log", "--format=%H", "--reverse", "--ancestry-path", $"{branchHeadSha}..main");
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

    private ConductorAdvanceResult ExecuteLanding(Goal goal, string goalPrefix, ConductorAutonomyPolicy policy)
    {
        using var evidenceMutationLease = _tryAcquireEvidenceMutationLease(goal, "conductor:acceptance-and-land");
        if (evidenceMutationLease is null)
            return ReplacementEvidenceMutationHeld(goal, goalPrefix, policy);

        if (!HasCompletedPassedVerificationForAllTasks(goal))
        {
            return MakeResult(goal.Id.Value, goalPrefix, policy,
                new ConductorAdvanceOutcome.Held(
                    GoalLifecycleState.Verified,
                    "Goal is not ready for acceptance: complete every task with a passed verification before accepting this gate."));
        }

        var early = RebaseBeforeAcceptance(goal, goalPrefix, policy, applySideEffects: true, out _);
        if (early is not null)
        {
            return early;
        }

        // Gate 2: acceptance verification (test suite quality check) on the integrated worktree.
        AcceptanceVerificationSummary acceptance;
        try
        {
            acceptance = _runAcceptanceVerification(goal, null, null, CancellationToken.None);
        }
        catch (AcceptanceInfrastructureDeferredException ex)
        {
            return MakeResult(goal.Id.Value, goalPrefix, policy,
                new ConductorAdvanceOutcome.Held(
                    GoalLifecycleState.Verified,
                    $"Acceptance infrastructure deferred ({ex.ReasonCode}); retry on next conduct tick. {ex.Message}"));
        }
        catch (DotnetBuildSlotsBusyException ex)
        {
            return MakeResult(goal.Id.Value, goalPrefix, policy,
                new ConductorAdvanceOutcome.Held(
                    GoalLifecycleState.Verified,
                    $"Stable dotnet build slots busy; retry on next conduct tick. {FormatSlotsBusy(ex.SlotsBusy)}"));
        }
        catch (BuildLockBlockedException ex)
        {
            return MakeResult(goal.Id.Value, goalPrefix, policy,
                new ConductorAdvanceOutcome.Held(
                    GoalLifecycleState.Verified,
                    $"Build artifact lock blocked acceptance; retry on next conduct tick. {FormatBuildLockBlocked(ex.Attribution)}"));
        }

        return CompleteLandingAfterAcceptance(goal, goalPrefix, policy, acceptance);
    }

    private static string FormatBuildLockBlocked(BuildLockAttribution attribution)
    {
        var holders = attribution.Holders.Count == 0
            ? "unknown"
            : string.Join(", ", attribution.Holders.Select(holder =>
                $"pid {holder.ProcessId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown"} {holder.ProcessName ?? "unknown"}"));
        return $"path={attribution.Path}; holders: {holders}";
    }

    private ConductorAdvanceResult ReplacementEvidenceMutationHeld(
        Goal goal,
        string goalPrefix,
        ConductorAutonomyPolicy policy) =>
        MakeResult(
            goal.Id.Value,
            goalPrefix,
            policy,
            new ConductorAdvanceOutcome.Held(
                GoalLifecycleState.Verified,
                "Acceptance or landing is blocked by concurrent source-backlog replacement."));

    private sealed class NoopEvidenceMutationLease : IDisposable
    {
        internal static readonly NoopEvidenceMutationLease Instance = new();

        public void Dispose()
        {
        }
    }

    private ConductorAdvanceResult? RebaseBeforeAcceptance(
        Goal goal,
        string goalPrefix,
        ConductorAutonomyPolicy policy,
        bool applySideEffects,
        out ConductorParallelAcceptanceEarlyOutcome? earlyOutcome)
    {
        // Gate 1: rebase the goal branch onto current main FIRST, so every later gate (acceptance,
        // criteria, landing) operates on the ACTUAL integrated result that will land — not the
        // pre-integration branch. A goal can pass its own tests yet break once integrated with changes
        // that landed meanwhile; verifying the un-rebased branch and only rebasing at the end could
        // land such a textually-clean-but-semantically-broken integration. Rebasing first also avoids
        // a wasted (expensive) acceptance run when the branch cannot integrate at all.
        return RebaseOrRetire(goal, goalPrefix, policy, "pre-landing", applySideEffects, out earlyOutcome);
    }

    private ConductorAdvanceResult? RebaseBeforeMerge(Goal goal, string goalPrefix, ConductorAutonomyPolicy policy)
    {
        // In a parallel acceptance batch, a sibling goal may advance main after this goal's
        // acceptance finished. Re-check the branch immediately before the serialized merge.
        return RebaseOrRetire(goal, goalPrefix, policy, "pre-merge", applySideEffects: true, out _);
    }

    internal LandingEscalationRecheckResult RecheckPreLandingRebaseConflict(Goal goal)
        => _recheckPreLandingRebaseConflict(goal);

    private ConductorAdvanceResult? RebaseOrRetire(
        Goal goal,
        string goalPrefix,
        ConductorAutonomyPolicy policy,
        string phase,
        bool applySideEffects,
        out ConductorParallelAcceptanceEarlyOutcome? earlyOutcome)
    {
        earlyOutcome = null;
        var rebase = _rebaseOntoMain(goal);

        // No retry here, deliberately. An earlier version of this method retried a non-conflict failure once,
        // on the theory that those failures were transient races against main advancing. That theory was
        // WRONG: the nine occurrences that motivated it were a deterministic git exit 128, because the
        // pre-landing rebase runs inside the hermetic acceptance child, which had no git identity and so
        // could not create the commits a rebase replays. Retrying a deterministic failure cannot help, and it
        // doubled the cost of every genuine one. The identity fix belongs in the environment, not here.
        if (rebase.UpdatedBranch)
        {
            return null;
        }

        if (rebase.Status == GoalWorktreeRebaseStatus.MissingBranch)
        {
            var detail = $"Conductor tick retired missing goal branch before landing because the goal artifact could not be rebased: {rebase.Message}";
            earlyOutcome = ConductorParallelAcceptanceEarlyOutcome.MissingBranchRetired(GoalLifecycleState.CleanedUp, detail);
            if (applySideEffects)
            {
                _recordMissingBranchRetirement(goal, detail);
            }

            return MakeResult(goal.Id.Value, goalPrefix, policy, new ConductorAdvanceOutcome.Done(GoalLifecycleState.CleanedUp));
        }

        var rebaseReason = rebase.Status == GoalWorktreeRebaseStatus.Conflict
            ? $"{phase} rebase conflict ({string.Join(", ", rebase.ConflictFiles)}); use 'workspace rebase' to resolve"
            : $"{phase} rebase failed: {rebase.Message}";
        earlyOutcome = ConductorParallelAcceptanceEarlyOutcome.PreLandingEscalated(GoalLifecycleState.Verified, rebaseReason);
        return applySideEffects
            ? Escalate(goal, goalPrefix, policy, GoalLifecycleState.Verified, rebaseReason)
            : MakeResult(goal.Id.Value, goalPrefix, policy, new ConductorAdvanceOutcome.Escalated(GoalLifecycleState.Verified, rebaseReason));
    }

    private ConductorAdvanceResult CompleteLandingAfterAcceptance(
        Goal goal,
        string goalPrefix,
        ConductorAutonomyPolicy policy,
        AcceptanceVerificationSummary acceptance)
    {
        if (acceptance.RequiredUnmetCriteria.Any(check =>
            string.Equals(
                check.FailureClassification,
                AcceptanceFailureClassifications.GateEnvironmentInterference,
                StringComparison.Ordinal)))
        {
            return MakeResult(
                goal.Id.Value,
                goalPrefix,
                policy,
                new ConductorAdvanceOutcome.Held(
                    GoalLifecycleState.Verified,
                    "Acceptance gate environmental interference; re-gate on the next conduct tick without dispatching a worker."));
        }

        if (!acceptance.Passed && acceptance.RequiredUnmetCriteria.Count == 0)
        {
            var failedChecks = acceptance.FailedChecks ?? [];
            var timedOut = failedChecks.Any(IsBlockingTimeoutCheck);
            if (!timedOut && acceptance.FailedChecks is { Count: > 0 })
            {
                _recordAcceptanceFailure(
                    goal,
                    failedChecks,
                    acceptance.BranchHeadSha,
                    acceptance.MainHeadSha,
                    acceptance.CheckAttributions,
                    acceptance.BaselineAttestation);
            }

            return Escalate(goal, goalPrefix, policy, GoalLifecycleState.Verified,
                (timedOut
                    ? "Acceptance verification timed out; rerun acceptance after clearing the blocker."
                    : "Acceptance verification failed; review and fix before landing.") +
                FormatFailureTail(acceptance.FailureDetail));
        }

        if (acceptance.Passed)
        {
            _clearAcceptanceFailure(goal);
        }

        if (acceptance.RequiredUnmetCriteria.Count > 0)
        {
            var criteria = FormatUnmetCriteria(acceptance.RequiredUnmetCriteria);
            var task = SelectTaskForCriterionRetry(goal);
            if (task is null)
            {
                return Escalate(goal, goalPrefix, policy, GoalLifecycleState.Verified,
                    $"Acceptance criteria unmet but no completed task is available to retry: {criteria}; review/land manually");
            }

            if (goal.AutomaticAcceptanceRetryCount < policy.MaxCriterionRetries)
            {
                var retryFeedback = FormatCriterionRetryFeedback(acceptance.RequiredUnmetCriteria);
                var retryCount = _recordCriterionRetryFeedback(
                    goal.Id,
                    task.Id,
                    retryFeedback);
                var retryMessage = $"Acceptance criteria unmet; retrying task with feedback (attempt {retryCount}/{policy.MaxCriterionRetries}): " +
                    string.Join(Environment.NewLine, retryFeedback);
                _retryTask(goal.Id, task.Id, retryMessage, null);
                return MakeResult(goal.Id.Value, goalPrefix, policy,
                    new ConductorAdvanceOutcome.Executed(GoalLifecycleState.Verified, retryMessage));
            }

            return Escalate(goal, goalPrefix, policy, GoalLifecycleState.Verified,
                $"Acceptance criteria unmet after {goal.AutomaticAcceptanceRetryCount} retries: {criteria}; review/land manually");
        }

        foreach (var task in goal.Tasks)
        {
            _clearCriterionRetryFeedback(goal.Id, task.Id);
        }

        // Gate 3: land via integration branch (the branch is already rebased onto main by Gate 1).
        var landingFileScopes = _getLandingFileScopes(goal);
        var mutationBlockReason = LandingMutationBlocker?.Invoke();
        if (!string.IsNullOrWhiteSpace(mutationBlockReason))
        {
            return MakeResult(
                goal.Id.Value,
                goalPrefix,
                policy,
                new ConductorAdvanceOutcome.Held(
                    GoalLifecycleState.Verified,
                    $"Landing held at mutation boundary: {mutationBlockReason}"));
        }

        var landResult = _land(goal, policy);
        if (landResult.Decision is LandingDecision.Escalate escalate)
        {
            if (LandingExecutor.IsMutationHoldEscalation(escalate.Reason))
            {
                return MakeResult(
                    goal.Id.Value,
                    goalPrefix,
                    policy,
                    new ConductorAdvanceOutcome.Held(GoalLifecycleState.Verified, escalate.Reason));
            }

            if (LandingExecutor.IsOwnershipHoldEscalation(escalate.Reason))
            {
                return MakeResult(goal.Id.Value, goalPrefix, policy,
                    new ConductorAdvanceOutcome.Escalated(GoalLifecycleState.Verified, escalate.Reason));
            }

            return Escalate(goal, goalPrefix, policy, GoalLifecycleState.Verified, escalate.Reason);
        }

        RecordAdvisoryAcceptanceNotes(goal, acceptance.AdvisoryUnmetCriteria);

        if (landResult.MainAdvanced)
        {
            // Schedule any required generation handoff before fallible advisory/post-landing work.
            // Main has already advanced, so losing this receipt would permanently miss the relaunch.
            SuccessfulLandingSink?.Invoke(new ConductorLandingReceipt(
                goal.Id.Value,
                landResult.ChangedFiles ?? landingFileScopes,
                landResult.MergeCommitSha));

            // Gate 4: advisory semantic acceptance runs only after deterministic acceptance and
            // successful landing. It records judge receipts for observability but never gates landing.
            _runAdvisorySemanticAcceptance(goal, acceptance);
            _afterSuccessfulLanding(goal, landResult);
        }

        return MakeResult(goal.Id.Value, goalPrefix, policy,
            new ConductorAdvanceOutcome.Executed(GoalLifecycleState.Verified, $"Landed: {landResult.Message}"));
    }

    private ConductorAdvanceResult ExecuteRecord(Goal goal, string goalPrefix, ConductorAutonomyPolicy policy)
    {
        _record(goal);
        return MakeResult(goal.Id.Value, goalPrefix, policy,
            new ConductorAdvanceOutcome.Executed(GoalLifecycleState.Merged, "Recorded to dogfood log"));
    }

    private ConductorAdvanceResult ExecuteCleanup(Goal goal, string goalPrefix, ConductorAutonomyPolicy policy)
    {
        GoalWorktreeRemoveResult cleanup;
        try
        {
            cleanup = _cleanup(goal);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            _completeGoal(goal);
            return MakeResult(goal.Id.Value, goalPrefix, policy,
                new ConductorAdvanceOutcome.Executed(
                    GoalLifecycleState.Recorded,
                    $"Workspace cleanup deferred after removal failure; retry later. {ex.Message}"));
        }

        _completeGoal(goal);
        return MakeResult(goal.Id.Value, goalPrefix, policy,
            new ConductorAdvanceOutcome.Executed(GoalLifecycleState.Recorded, cleanup.Message));
    }

    private ConductorAdvanceResult Escalate(
        Goal goal,
        string goalPrefix,
        ConductorAutonomyPolicy policy,
        GoalLifecycleState state,
        string reason)
    {
        var escalationClock = Stopwatch.StartNew();
        var sinkResult = LandingEscalationWriteResult.Error;
        try
        {
            sinkResult = _writeEscalation(goal, state, reason);
        }
        finally
        {
            escalationClock.Stop();
            EmitGoalPhaseTiming(
                "escalation-write",
                goal,
                escalationClock.Elapsed,
                $"json_ms={sinkResult.JsonElapsedMilliseconds} json={sinkResult.JsonOutcome} " +
                $"collab_ms={sinkResult.CollaborationElapsedMilliseconds} collab={sinkResult.CollaborationOutcome} " +
                $"channel_ms={sinkResult.ChannelElapsedMilliseconds} channel={sinkResult.ChannelOutcome}");
        }
        return MakeResult(goal.Id.Value, goalPrefix, policy,
            new ConductorAdvanceOutcome.Escalated(state, reason));
    }

    private static ConductorAdvanceResult MakeResult(
        string goalId,
        string goalPrefix,
        ConductorAutonomyPolicy policy,
        ConductorAdvanceOutcome outcome) =>
        new(goalId, goalPrefix, policy.Name, outcome);

    private static TaskSpec? SelectTaskForCriterionRetry(Goal goal) =>
        goal.Tasks.LastOrDefault(task => task.Status == WorkTaskStatus.Completed && task.RequiredRole == AgentRole.Developer) ??
        goal.Tasks.LastOrDefault(task => task.Status == WorkTaskStatus.Completed);

    private void RecordAdvisoryAcceptanceNotes(Goal goal, IReadOnlyList<AcceptanceCheckResult> advisoryCriteria)
    {
        if (advisoryCriteria.Count == 0)
        {
            return;
        }

        var task = SelectTaskForCriterionRetry(goal);
        if (task is null)
        {
            return;
        }

        _recordTaskNote(
            goal.Id,
            task.Id,
            $"Advisory acceptance criteria observed during landing (non-gating): {FormatUnmetCriteria(advisoryCriteria)}");
    }

    private static string FormatUnmetCriteria(IReadOnlyList<AcceptanceCheckResult> criteria) =>
        string.Join("; ", criteria.Select(FormatUnmetCriterion));

    private static string FormatCleanupDiagnostic(GoalWorktreeRemoveResult cleanup)
    {
        var parts = new List<string> { cleanup.Message };
        if (!string.IsNullOrWhiteSpace(cleanup.LeftoverPath))
        {
            parts.Add($"leftover={cleanup.LeftoverPath}");
        }

        if (cleanup.LockHolders.Count > 0)
        {
            parts.Add("lockHolders=" + string.Join(", ", cleanup.LockHolders.Select(FormatLockHolder)));
        }

        if (cleanup.CleanupBackoff is not null)
        {
            parts.Add(GoalWorktrees.FormatCleanupBackoff(cleanup.CleanupBackoff));
        }

        if (!string.IsNullOrWhiteSpace(cleanup.ResumeCommand))
        {
            parts.Add($"resume={cleanup.ResumeCommand}");
        }

        return string.Join(" ", parts);
    }

    private static string[] InferRecordedFileScopes(Goal goal)
    {
        var scopes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var task in goal.Tasks)
        {
            var text = $"{goal.Objective}\n{task.Description}\n{task.VerificationPlan}";
            foreach (var token in text.Split([' ', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var normalized = token.Replace('\\', '/').TrimEnd('.', ',', ';', ':', ')', ']');
                if (normalized.Contains('/') && !string.IsNullOrWhiteSpace(Path.GetExtension(normalized)))
                {
                    scopes.Add(normalized.TrimStart('/'));
                }
            }
        }

        return scopes.Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string FormatLockHolder(WorktreeLockHolder holder) =>
        string.IsNullOrWhiteSpace(holder.CommandLine)
            ? $"pid={holder.ProcessId} name={holder.ProcessName}"
            : $"pid={holder.ProcessId} name={holder.ProcessName} command=\"{holder.CommandLine}\"";

    private static string[] FormatCriterionRetryFeedback(IReadOnlyList<AcceptanceCheckResult> criteria)
    {
        var concreteEvidence = ExtractConcreteRetryEvidence(criteria);
        if (concreteEvidence.Count == 0)
        {
            return criteria.Select(FormatUnmetCriterion).ToArray();
        }

        var cappedEvidence = concreteEvidence.Take(MaxCriterionRetryEvidenceLines).ToList();
        if (concreteEvidence.Count > MaxCriterionRetryEvidenceLines)
        {
            cappedEvidence.Add($"... truncated {concreteEvidence.Count - MaxCriterionRetryEvidenceLines} acceptance evidence line(s)");
        }

        var feedback = new List<string>
        {
            "Concrete acceptance failure evidence:",
        };
        feedback.AddRange(cappedEvidence);
        feedback.Add("Acceptance criteria summary:");
        feedback.AddRange(criteria.Select(FormatUnmetCriterion));
        return feedback.ToArray();
    }

    private static List<string> ExtractConcreteRetryEvidence(IReadOnlyList<AcceptanceCheckResult> criteria)
    {
        var outputEvidence = criteria
            .SelectMany(ExtractConcreteOutputEvidence)
            .ToList();
        if (outputEvidence.Count == 0)
        {
            return [];
        }

        var evidence = criteria
            .Where(criterion => !criterion.Passed)
            .Select(FormatFailedCheckEvidence)
            .ToList();
        evidence.AddRange(outputEvidence);
        return evidence;
    }

    private static string FormatFailedCheckEvidence(AcceptanceCheckResult criterion) =>
        $"failed check: {criterion.Name} (exit code {criterion.ExitCode})";

    private static IEnumerable<string> ExtractConcreteOutputEvidence(AcceptanceCheckResult criterion)
    {
        foreach (var line in SplitEvidenceLines(criterion.OutputTail))
        {
            if (AcceptanceRetryEvidencePattern.IsMatch(line))
            {
                yield return line;
            }
        }
    }

    private static IEnumerable<string> SplitEvidenceLines(string text) =>
        text
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.TrimEnd())
            .Where(line => !string.IsNullOrWhiteSpace(line));

    private static string FormatUnmetCriterion(AcceptanceCheckResult criterion)
    {
        var summary = string.IsNullOrWhiteSpace(criterion.ResultSummary)
            ? criterion.OutputTail
            : criterion.ResultSummary;
        return string.IsNullOrWhiteSpace(summary)
            ? criterion.Name
            : $"{criterion.Name}: {summary.Trim()}";
    }
}

internal sealed record LandingEscalationRecheckResult(
    bool ConditionResolved,
    string Status,
    string Observation,
    string EvidenceFingerprint,
    bool TerminalUnsatisfiable = false);

internal sealed record ConductorLandingReceipt(
    string GoalId,
    IReadOnlyList<string> ChangedFiles,
    string? LandingSha = null);
