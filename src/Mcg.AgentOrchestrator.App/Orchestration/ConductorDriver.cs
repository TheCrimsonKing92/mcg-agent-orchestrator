using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
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
    bool MappingNeedsInput,
    IReadOnlyList<string>? SourceCleanupPaths = null,
    RepositoryTestImpactDegradation? TestImpactDegradation = null)
{
    public bool RequiresSourceCleanup => SourceCleanupPaths is { Count: > 0 };
}

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
    IReadOnlyList<string> ConflictPaths,
    string? OriginalCandidateSha = null,
    string? IntegratedMainSha = null,
    string? ResultingCandidateSha = null)
{
    internal bool CanDispatch =>
        Status is DeveloperBranchIntegrationStatus.Current or DeveloperBranchIntegrationStatus.Integrated;
}

internal sealed partial class ConductorDriver
{
    private sealed record JournalLifecycleFacts(bool IsMerged, bool IsRecorded, bool IsCleanedUp);

    private sealed record CohortGateRun(
        DateTimeOffset StartedAt,
        IReadOnlySet<string> MemberGoalIds,
        string PairFingerprint,
        TaskCompletionSource Completion,
        string? AttemptMetadataPath = null);

    internal sealed class EvidenceMutationLeaseUnavailableException(string message)
        : InvalidOperationException(message);

    private const int MaxCriterionRetryEvidenceLines = 30;
    private static readonly TimeSpan DefaultBuildServerShutdownTimeout = TimeSpan.FromSeconds(5);
    internal static readonly TimeSpan EvidenceMutationLeaseDuration = TimeSpan.FromMinutes(30);
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
    private readonly Func<Goal, AgentRole, DeveloperBranchIntegrationResult>? _integrateMainBeforeReadOnlyDispatch;
    private readonly Action<Goal, DeveloperBranchIntegrationResult> _recordPreDispatchIntegrationReceipt;
    private readonly Func<Goal, ConductorAutonomyPolicy, DispatchStartOutcome> _dispatchAndStart;
    private readonly Func<Goal, ConductorAutonomyPolicy, DispatchStartOutcome> _startRecordedDispatches;
    private readonly Func<Goal, TaskId, bool> _reconcileExitedDispatch;
    private readonly Action<Goal, FailedGoalRecoveryDecision>? _beforeFailedGoalRecoveryEffect;
    private readonly Func<TimeSpan, string> _buildServerShutdown;
    private readonly TimeSpan _buildServerShutdownTimeout;
    private readonly Func<Goal, int?, DotnetBuildEnvironmentLease?, CancellationToken, AcceptanceRunExecutionOptions, AcceptanceVerificationSummary> _runAcceptanceVerification;
    private readonly Action<Goal, AcceptanceVerificationSummary> _runAdvisorySemanticAcceptance;
    private readonly Func<Goal, string, DotnetBuildEnvironmentLease?, CancellationToken, FocusedEvidenceRunResult> _runFocusedEvidence;
    private readonly Func<Goal, string, DotnetBuildEnvironmentLease?, CancellationToken, FocusedEvidenceRunResult> _runDualArmFocusedEvidence;
    private readonly bool _focusedEvidenceRunnerConfigured;
    private readonly Func<Goal, PreReviewEvidenceContext> _getPreReviewEvidenceContext;
    private readonly Func<Goal, AcceptanceGateEngineSettings> _getFindingEvidenceEngineSettings;
    private readonly Func<Goal, string, string, IReadOnlyList<string>> _resolveFindingEvidenceSiblingClasses;
    private readonly Action<GoalId, TaskId, PreReviewEvidenceReceipt> _recordPreReviewEvidence;
    private readonly Action<GoalId, TaskId, string, int> _recordPreReviewMappingEscalationSuppressed;
    private readonly Func<GoalId, TaskId, string, RetryRoundKind?, RetryCause, TaskSpec> _retryTask;
    private readonly Action<GoalId, TaskId, string> _recordTaskNote;
    private readonly Action<GoalId, TaskId, string> _recordFindingEvidenceRequest;
    private readonly Action<GoalId, TaskId, string> _recordFindingEvidenceRun;
    private readonly Action<GoalId, TaskId, string, IReadOnlyList<string>, string, AgentRole, string, string> _recordFindingEvidenceSuppressed;
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
    private readonly Action<Goal, GoalLifecycleState> _resolveParkedWaitEscalations;
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
    private readonly GoalWorktreeCleanupHooks _cohortCleanupHooks = new();
    private readonly IGoalAcceptanceVerifier? _cohortAcceptanceVerifier;
    private readonly IGoalLifecycleEventWriter? _cohortEventWriter;
    private readonly CohortAcceptanceStore? _cohortAcceptanceStore;
    private readonly MergeTrainAcceptanceStore? _mergeTrainAcceptanceStore;
    private readonly Func<
        ConductorAcceptanceCohortSelection,
        IReadOnlyList<Goal>,
        ConductorAutonomyPolicy,
        ConductorAcceptanceCohortRunResult>? _runAcceptanceCohortOverride;
    private readonly ConcurrentDictionary<string, CohortGateRun> _cohortGateRuns = new(StringComparer.Ordinal);
    private Action<Action> _startCohortGateBackground;
    private readonly Func<
        ConductorMergeTrainSelection,
        IReadOnlyList<Goal>,
        ConductorAutonomyPolicy,
        ConductorMergeTrainRunResult>? _runMergeTrainOverride;
    private readonly Func<Goal, int> _getAcceptanceSlotCount;
    private readonly Func<int> _getWorkerAdmissionCapacity;
    private readonly Func<bool> _hasGateReadyGoal;
    private readonly Func<Goal, string?> _tryBuildAwaitingClarificationEscalationReason;
    private readonly Func<Goal, string, IDisposable?> _tryAcquireEvidenceMutationLease;
    private readonly Func<Goal, GoalEvidenceOperationStart>? _tryBeginDeveloperIntegrationEvidenceOperation;
    private readonly Func<Goal, GoalEvidenceLeaseFact?>? _tryRecoverTerminalDeveloperIntegrationLease;
    private readonly Func<Goal, ReconcileAcceptanceLeaseState?> _getEvidenceMutationLease;
    private readonly Func<Goal, (string? BranchHeadSha, string? MainHeadSha)> _resolveAcceptanceHeads;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly string? _executionDirectory;
    private readonly ConductorParallelAcceptanceAttemptCoordinator _parallelAcceptanceAttemptCoordinator;
    private readonly ConductorParallelAcceptanceAttemptCoordinator _focusedEvidenceAttemptCoordinator;
    private readonly bool _parallelAcceptanceEnabled;
    private readonly List<(ConductorLandingReceipt Receipt, string CohortId, string ReceiptId)>
        _pendingRecoveredLandingReceipts = [];
    private readonly List<(ConductorLandingReceipt Receipt, string TrainId, string ReceiptId)>
        _pendingRecoveredMergeTrainLandingReceipts = [];
    private Action<ConductorLandingReceipt>? _successfulLandingSink;
    private bool _buildServerShutdownRanThisTick;
    private bool _isConductorTick;
    private AgentOrchestratorKernel? _conductorTickKernel;
    private int _conductorTick;

    internal Action<string>? PhaseTimingSink { get; set; }
    internal Action<ConductorLandingReceipt>? SuccessfulLandingSink
    {
        get => _successfulLandingSink;
        set
        {
            _successfulLandingSink = value;
            if (value is null ||
                (_pendingRecoveredLandingReceipts.Count == 0 &&
                 _pendingRecoveredMergeTrainLandingReceipts.Count == 0))
            {
                return;
            }
            foreach (var pending in _pendingRecoveredLandingReceipts)
            {
                value(pending.Receipt);
            }
            foreach (var pending in _pendingRecoveredMergeTrainLandingReceipts)
            {
                value(pending.Receipt);
            }
            foreach (var pending in _pendingRecoveredLandingReceipts
                         .DistinctBy(item => item.CohortId))
            {
                _cohortAcceptanceStore?.CompleteLandingEffects(pending.CohortId, pending.ReceiptId);
            }
            foreach (var pending in _pendingRecoveredMergeTrainLandingReceipts
                         .DistinctBy(item => item.TrainId))
            {
                _mergeTrainAcceptanceStore?.CompleteLandingEffects(pending.TrainId, pending.ReceiptId);
            }
            _pendingRecoveredLandingReceipts.Clear();
            _pendingRecoveredMergeTrainLandingReceipts.Clear();
        }
    }
    internal Action<GoalId>? DispatchRecordWriteSucceededSink { get; set; }
    internal Func<string?>? LandingMutationBlocker { get; set; }
    internal SliceBatchAdmissionEvaluator? SliceBatchAdmissionEvaluator { get; set; }
    internal SliceBatchParentExecutionGuard? SliceBatchParentExecutionGuard { get; set; }

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
        Func<GoalId, TaskId, InterruptedDispatchStateRead>? readCurrentInterruptedDispatchState = null,
        bool runAcceptanceAttemptsInCurrentProcess = false,
        Action<GoalSnapshot>? recordDurableGoalBaseline = null,
        GoalWorktreeCleanupHooks? cleanupHooks = null)
    {
        var dir = workspace.ExecutionDirectory;
        _executionDirectory = dir;
        _resolveAcceptanceHeads = goal =>
            (TryResolveAcceptanceBranchHead(goal), TryResolveGitHead(dir));
        _getAcceptanceSlotCount = _ => ConductorBatchLoop.DefaultParallelAcceptanceCapacity;
        _getWorkerAdmissionCapacity = () => ConductorBatchLoop.WorkerAdmissionCapacity;
        _parallelAcceptanceEnabled = true;
        _cohortCleanupHooks = cleanupHooks ?? new GoalWorktreeCleanupHooks();
        _workerBuildArtifactsPath = goalId => DotnetBuildEnvironmentManager.GoalArtifactsPath(goalId, _cohortCleanupHooks.BuildStorageRoot);
        _parallelAcceptanceAttemptCoordinator = new ConductorParallelAcceptanceAttemptCoordinator(
            Path.Combine(workspace.OrchestratorDirectory, "acceptance-gate-attempts"),
            dir,
            tryRunPreSlot: RunParallelLandingAcceptancePreSlot,
            runInline: runAcceptanceAttemptsInCurrentProcess,
            buildStorageRoot: _cohortCleanupHooks.BuildStorageRoot);
        _apparatusRedGate = new ApparatusRedGate(
            Path.Combine(
                workspace.OrchestratorDirectory,
                "acceptance-gate-attempts",
                AcceptanceFailingTestIndex.FileName),
            goal => GoalWorktrees.TryResolve(dir, goal.Id) ?? dir);
        (_acceptanceEventSink, _noTickAcceptancePollDelay, _noTickAcceptancePollTimeout) = CreateProductionAcceptanceWaitConfiguration(workspace);
        _focusedEvidenceAttemptCoordinator = new ConductorParallelAcceptanceAttemptCoordinator(
            Path.Combine(workspace.OrchestratorDirectory, "pre-review-evidence-attempts"),
            dir,
            conductEventLogWriter: new ConductEventLogWriter(workspace.ConductEventsLogPath),
            buildStorageRoot: _cohortCleanupHooks.BuildStorageRoot);
        var eventWriter = new GoalLifecycleEventWriter(workspace.GoalLifecycleEventsDirectory);
        _cohortKernel = kernel;
        ConfigureCandidateIdentity(kernel, workspace.ConductEventsLogPath);
        _cohortWorkspace = workspace;
        _cohortAcceptanceVerifier = acceptanceVerifier;
        _cohortEventWriter = eventWriter;
        var dispatchRunner = OperatorCancelAwareDispatchRunner.ForWorkspace(workspace);
        _reconcileExitedDispatch = (goal, taskId) =>
        {
            dispatchRunner.RefreshLatestProcessWithOutcome(kernel, goal.Id, taskId);
            var task = kernel.GetTask(goal.Id, taskId);
            return task.LastProcess is { } process &&
                   DispatchProcessCompletionState.HasAlreadyBeenApplied(task, process);
        };
        _cohortAcceptanceStore = new CohortAcceptanceStore(
            Path.Combine(workspace.OrchestratorDirectory, "cohort-acceptance.db"));
        _startCohortGateBackground = action =>
        {
            _ = Task.Run(action);
        };
        _mergeTrainAcceptanceStore = new MergeTrainAcceptanceStore(
            Path.Combine(workspace.OrchestratorDirectory, "merge-train-acceptance.db"));
        kernel.SetEventWriter(eventWriter);
        _tryBuildAwaitingClarificationEscalationReason = goal =>
            GoalRefinementGate.TryBuildAwaitingClarificationEscalationReason(workspace, goal, eventWriter, out var reason)
                ? reason
                : null;
        var factGoalIds = kernel.Goals.Select(goal => goal.Id).ToArray();
        var journalFacts = new ConcurrentDictionary<GoalId, JournalLifecycleFacts>();
        var worktreeSnapshot = GoalWorktrees.ResolveAll(dir, factGoalIds)
            .ToDictionary(pair => pair.Key, pair => pair.Value);
        static JournalLifecycleFacts ProjectJournalFacts(GoalOperationJournalSummary journal) => new(
            GoalOperationJournal.HasCompletedLandingEvidence(journal),
            GoalOperationJournal.HasCompletedRecordEvidence(journal),
            GoalOperationJournal.HasCompletedCleanupEvidence(journal));
        JournalLifecycleFacts ReadInitialJournalFacts(GoalId goalId) =>
            ProjectJournalFacts(GoalOperationJournal.ReadActive(dir, goalId));
        void RefreshJournal(GoalId goalId) =>
            journalFacts[goalId] = ProjectJournalFacts(GoalOperationJournal.Read(dir, goalId));
        var evidenceMutationLeaseStore = new ReconcileSweepRemediationStore(workspace.SqliteStatePath);
        var goalEvidenceOperationCoordinator = new GoalEvidenceOperationCoordinator(
            evidenceMutationLeaseStore,
            dir,
            EvidenceMutationLeaseDuration,
            pidProbe: new ConductLockPidProbe());
        _tryBeginDeveloperIntegrationEvidenceOperation = goal =>
            goalEvidenceOperationCoordinator.TryBegin(goal, "conductor:developer-branch-integration");
        _tryRecoverTerminalDeveloperIntegrationLease = goal =>
            goalEvidenceOperationCoordinator.TryRecoverTerminal(goal);
        _getEvidenceMutationLease = goal => evidenceMutationLeaseStore.TryGetAcceptanceLease(
            goal.Id.Value,
            EvidenceMutationLeaseDuration);
        _utcNow = () => DateTimeOffset.UtcNow;
        IDisposable? AcquireEvidenceMutationLease(Goal goal, string operation)
        {
            _tryRecoverTerminalDeveloperIntegrationLease(goal);
            var owner = $"goal-evidence:{operation}:{Environment.ProcessId}:{Guid.NewGuid():N}";
            return evidenceMutationLeaseStore.TryAcquireAcceptanceLease(
                goal.Id.Value,
                owner,
                EvidenceMutationLeaseDuration);
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
            // Initial facts deliberately match ReadAll's active-journal-only population policy.
            // Refreshes remain archive-aware, preserving the existing behavior for completed goals.
            var persistedFacts = journalFacts.GetOrAdd(goal.Id, ReadInitialJournalFacts);
            var hasOpenClarification = GoalRefinementGate.HasOpenClarification(workspace, goal);
            return new GoalLifecycleFacts(
                workspaceExists,
                IsBlocked: false,
                persistedFacts.IsMerged,
                persistedFacts.IsRecorded,
                persistedFacts.IsCleanedUp,
                hasOpenClarification);
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
            // Worktree-add retry clears an orphan directory; that deletion, its warning sink and
            // lock-holder discovery must use the same cleanup owner as conductor cleanup below.
            var path = GoalWorktrees.Ensure(dir, goal.Id, _cohortCleanupHooks);
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
                result = new GoalDispatchOperations().StartSubscriptionReadyTasks(
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
                    conductorPolicy: policy,
                    recordDurableGoalBaseline: recordDurableGoalBaseline,
                    excludedTaskIds: GetUnchangedCandidateExclusions(goal));
            }
            catch (DispatchRecordWriteException ex)
            {
                if (ex.PreservesAuthoritativeState)
                {
                    var reason = ex.InnerException?.Message ?? ex.Message;
                    GoalOperationJournal.Failed(dir, goal, "conductor:dispatch", reason);
                    return DispatchStartOutcome.Deferred(reason);
                }

                if (!ex.ProcessMayHaveStarted && !criticalCheckpointPersisted)
                    kernel.ReplaceGoalWithSnapshot(goalSnapshotBeforeDispatch);
                throw;
            }
            catch (Exception ex)
            {
                var mapped = DispatchStartOutcome.FromDispatchException(
                    ex,
                    "Subscription dispatch start failed");
                if (mapped.Category == DispatchStartOutcomeCategory.Deferred)
                    return mapped;

                GoalOperationJournal.Failed(dir, goal, "conductor:dispatch", mapped.Reason!);
                return mapped;
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
                result = new GoalDispatchOperations().StartDispatches(
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
                    conductorPolicy: policy,
                    recordDurableGoalBaseline: recordDurableGoalBaseline);
            }
            catch (DispatchRecordWriteException ex) when (ex.PreservesAuthoritativeState)
            {
                var reason = ex.InnerException?.Message ?? ex.Message;
                GoalOperationJournal.Failed(dir, goal, "conductor:dispatch-start", reason);
                return DispatchStartOutcome.Deferred(reason);
            }
            catch (Exception ex) when (ex is not DispatchRecordWriteException)
            {
                var mapped = DispatchStartOutcome.FromDispatchException(
                    ex,
                    "Recorded dispatch start failed");
                if (mapped.Category == DispatchStartOutcomeCategory.Deferred)
                    return mapped;

                GoalOperationJournal.Failed(dir, goal, "conductor:dispatch-start", mapped.Reason!);
                return mapped;
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

        _runAcceptanceVerification = (goal, stableSlotIndex, stableSlotLease, cancellationToken, attemptOptions) =>
        {
            var worktreePath = GoalWorktrees.TryResolve(dir, goal.Id);
            if (worktreePath is null) return AcceptanceVerificationSummary.Failed;
            var slotSuffix = stableSlotIndex.HasValue ? $" on stable slot {stableSlotIndex.Value}" : string.Empty;
            var acceptanceAttemptStartedAt = DateTimeOffset.UtcNow;
            GoalOperationJournal.Begin(dir, goal, "conductor:acceptance", $"Running acceptance verification{slotSuffix}.");
            var changedFiles = GoalAcceptanceEvidenceBundleBuilder.GetChangedFiles(worktreePath);
            var branchHeadSha = TryResolveGitHead(worktreePath);
            var mainHeadSha = TryResolveGitHead(dir);
            IReadOnlyList<CleanTestBaselineEvidence> baselineEvidence = [];
            var baselineReceipt = CleanTestBaseline.Unattested(mainHeadSha);
            try
            {
                baselineEvidence = GoalOperationJournal.ReadAcceptanceEvidenceForMain(
                    dir,
                    mainHeadSha ?? string.Empty);
                baselineReceipt = CleanTestBaseline.Resolve(
                    baselineEvidence,
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
            var cancellationProbeState = new AcceptanceAttemptCancellationProbe(() =>
                GetAcceptanceAttemptCancellationDecision(
                    workspace,
                    goal.Id,
                    attemptInvalidationRecorded: () =>
                        _parallelAcceptanceAttemptCoordinator.TryGetLiveInvalidatedAttempt(
                            goal.Id.Value,
                            out _)));
            try
            {
                var gateProgressEventWriter = new ConductEventLogWriter(
                    Path.Combine(dir, ".orchestrator", "logs", ConductEventLogWriter.CurrentFileName));
                var executionOptions = attemptOptions with
                {
                    ProgressSink = progress => AppendGateProgressEvent(gateProgressEventWriter, goal.Id, progress),
                    CancellationProbe = cancellationProbeState.ShouldCancel,
                    BoundaryCancellationProbe = cancellationProbeState.ShouldCancelNow
                };
                verification = AcceptanceExecutionRunner.RunAttempt(
                    acceptanceVerifier, worktreePath, goal.Id, changedFiles, stableSlotIndex,
                    stableSlotLease, cancellationToken, executionOptions);
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
            catch (OperationCanceledException ex) when (
                cancellationProbeState.CancellationDecision?.ShouldCancel == true)
            {
                var decision = cancellationProbeState.CancellationDecision!;
                GoalOperationJournal.AcceptanceBlocked(
                    dir,
                    goal,
                    "conductor:acceptance",
                    "disposition-cancelled",
                    branchHeadSha,
                    mainHeadSha,
                    $"Acceptance blocked:disposition-cancelled for candidate {FormatAcceptanceCandidate(branchHeadSha, mainHeadSha)}: {decision.Cause}",
                    acceptanceAttemptStartedAt);
                throw new AcceptanceAttemptCancelledException(decision, ex);
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
                    (baselineReceipt, checkAttributions) = AttributeAcceptanceFailureWithExecutedBaseline(
                        baselineReceipt,
                        failedChecks,
                        baselineEvidence,
                        goal.Id,
                        mainHeadSha ?? string.Empty,
                        verification.Checks);
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
            return ClassifyInheritedBaselineApparatus(new AcceptanceVerificationSummary(
                verification.Passed,
                unmetCriteria,
                verification.Passed ? null : verification.OutputTail,
                failedChecks,
                branchHeadSha,
                mainHeadSha,
                testResultPaths,
                checkAttributions,
                verification.Passed ? null : CleanTestBaseline.FormatFailureAttestation(baselineReceipt)));
        };

        FocusedEvidenceRunResult RunFocusedEvidence(
            Goal goal,
            string request,
            DotnetBuildEnvironmentLease? stableSlotLease,
            bool runBaselineArm,
            CancellationToken cancellationToken,
            FindingEvidenceNegativeControl? negativeControl = null, IReadOnlyList<string>? revertPaths = null, FindingEvidenceMutation? mutation = null)
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
            var result = AcceptanceExecutionRunner.RunFocusedVerification(
                acceptanceVerifier, worktreePath, goal.Id, request,
                stableSlotLease?.Environment.BuildPermitIndex, stableSlotLease,
                runBaselineArm, cancellationToken, negativeControl, revertPaths, mutation);
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
        _runNegativeControlFocusedEvidence = (goal, request, lease, baseline, token, mode, paths, mutation) =>
            RunFocusedEvidence(goal, request, lease, baseline, token, mode, paths, mutation);
        _focusedEvidenceRunnerConfigured = true;

        _retryTask = (goalId, taskId, message, retryRoundKind, cause) =>
            kernel.RetryTaskAutomatically(goalId, taskId, message, retryRoundKind: retryRoundKind, retryCause: cause);
        _workerBuildRecoveryRetry = kernel.RetryTaskAfterWorkerBuildCheckRecovery;
        ConfigureDeveloperCompletionStructuralPreflightRetry(kernel);
        _recordTaskNote = (goalId, taskId, message) =>
        {
            kernel.RecordTaskNote(goalId, taskId, message);
        };
        _recordFindingEvidenceRequest = (goalId, taskId, message) =>
            kernel.RecordFindingEvidenceRequest(goalId, taskId, message);
        _recordFindingEvidenceRun = (goalId, taskId, message) =>
            kernel.RecordFindingEvidenceRun(goalId, taskId, message);
        _recordFindingEvidenceSuppressed = (goalId, taskId, candidateSha, blockerIds, requestId, owner, reason, identity) =>
            kernel.RecordFindingEvidenceSuppressed(
                goalId, taskId, candidateSha, blockerIds, requestId, owner, reason, identity);
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

        _runAdvisorySemanticAcceptance = (_, _) => { };

        _integrateMainBeforeDeveloperDispatch = goal =>
        {
            var result = IntegrateMainBeforeDeveloperDispatch(dir, goal);
            return result;
        };
        _integrateMainBeforeReadOnlyDispatch = (goal, role) =>
            IntegrateMainBeforeReadOnlyDispatch(dir, goal, role);
        _recordPreDispatchIntegrationReceipt = new PreDispatchIntegrationReceiptRecorder(kernel).Record;
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
            var cleanupBackoff = GoalWorktrees.RecordGoalCleanupNeeded(
                dir,
                goal.Id,
                "remove:conductor-deferred",
                _cohortCleanupHooks);
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

        _resolveParkedWaitEscalations = (goal, state) =>
            OperatorInbox.ResolveParkedWaitLandingEscalationsAsync(workspace, goal, state).GetAwaiter().GetResult();
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
            var plan = SubscriptionPlanBuilder.Build(goal, agents, profiles, providerHoldScope: kernel.Goals);
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
        SliceBatchAdmissionEvaluator = new SliceBatchAdmissionEvaluator(
            () => kernel.Goals,
            goal =>
            {
                var worktreePath = GoalWorktrees.TryResolve(dir, goal.Id);
                return worktreePath is null
                    ? null
                    : GoalAcceptanceEvidenceBundleBuilder.GetChangedFiles(worktreePath);
            },
            kernel.RecordGoalPolicyDecision);
        SliceBatchParentExecutionGuard = new SliceBatchParentExecutionGuard(() => kernel.Goals);
        _isVerificationGateSatisfied = goal => kernel.BuildVerificationGate(goal.Id).IsSatisfied;
        _gateReadyCandidateProjector = GateReadyCandidateProjector.CreateForRepository(dir);
        _getPreReviewEvidenceContext = goal => BuildPreReviewEvidenceContext(goal, dir);
        _getFindingEvidenceEngineSettings = goal => AcceptanceGateEngineSettings.Load(
            GoalWorktrees.TryResolve(dir, goal.Id) ?? dir);
        _resolveFindingEvidenceSiblingClasses = (goal, project, requestedClass) =>
            FocusedEvidenceSiblingClassResolver.ResolveSiblingTestClassNames(
                GoalWorktrees.TryResolve(dir, goal.Id) ?? dir,
                project,
                requestedClass);
        RecoverCohortLandingEffects(kernel, workspace, eventWriter, _cohortAcceptanceStore);
        RecoverMergeTrainLandingEffects(kernel, workspace, eventWriter, _mergeTrainAcceptanceStore);
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
        Goal goal) => IntegrateMainBeforeDispatch(executionDirectory, goal, AgentRole.Developer);

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
        Func<GoalId, TaskId, string, RetryRoundKind?, RetryCause, TaskSpec>? retryTaskWithCause = null,
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
        Action<GoalId, TaskId, string, IReadOnlyList<string>, string, AgentRole, string, string>? recordFindingEvidenceSuppressed = null,
        Func<Goal, AcceptanceGateEngineSettings>? getFindingEvidenceEngineSettings = null,
        Func<Goal, string, string, IReadOnlyList<string>>? resolveFindingEvidenceSiblingClasses = null,
        Func<Goal, bool>? isVerificationGateSatisfied = null,
        GateReadyCandidateProjector? gateReadyCandidateProjector = null,
        Func<
            ConductorAcceptanceCohortSelection,
            IReadOnlyList<Goal>,
            ConductorAutonomyPolicy,
            ConductorAcceptanceCohortRunResult>? runAcceptanceCohort = null,
        Func<
            ConductorMergeTrainSelection,
            IReadOnlyList<Goal>,
            ConductorAutonomyPolicy,
            ConductorMergeTrainRunResult>? runMergeTrain = null,
        Func<Goal, string, IDisposable?>? tryAcquireEvidenceMutationLease = null,
        Func<Goal, DeveloperBranchIntegrationResult>? integrateMainBeforeDeveloperDispatch = null,
        Func<Goal, ReconcileAcceptanceLeaseState?>? getEvidenceMutationLease = null,
        Func<Goal, (string? BranchHeadSha, string? MainHeadSha)>? resolveAcceptanceHeads = null,
        Func<DateTimeOffset>? utcNow = null, string? executionDirectory = null, Action<string, string>? acceptanceEventSink = null,
        Action<TimeSpan>? noTickAcceptancePollDelay = null, TimeSpan? noTickAcceptancePollTimeout = null,
        Func<Goal, GoalEvidenceOperationStart>? tryBeginDeveloperIntegrationEvidenceOperation = null,
        Func<Goal, GoalEvidenceLeaseFact?>? tryRecoverTerminalDeveloperIntegrationLease = null,
        Func<Goal, TaskId, bool>? reconcileExitedDispatch = null,
        ApparatusRedGate? apparatusRedGate = null,
        Action<Goal, FailedGoalRecoveryDecision>? beforeFailedGoalRecoveryEffect = null,
        Action<Goal, DeveloperBranchIntegrationResult>? recordPreDispatchIntegrationReceipt = null,
        Func<GoalId, TaskId, string, TaskSpec>? workerBuildRecoveryRetry = null,
        Func<GoalId, string>? workerBuildArtifactsPath = null,
        Func<Goal, AgentRole, DeveloperBranchIntegrationResult>? integrateMainBeforeReadOnlyDispatch = null,
        Action<Goal, GoalLifecycleState>? resolveParkedWaitEscalations = null)
    {
        _apparatusRedGate = apparatusRedGate;
        _getFacts = getFacts;
        _getRunningPaidWorkerCount = getRunningPaidWorkerCount;
        _createWorkspace = createWorkspace;
        _integrateMainBeforeDeveloperDispatch = integrateMainBeforeDeveloperDispatch ?? (_ =>
            new DeveloperBranchIntegrationResult(
                DeveloperBranchIntegrationStatus.Current,
                "Goal branch is current with main.",
                []));
        _integrateMainBeforeReadOnlyDispatch = integrateMainBeforeReadOnlyDispatch;
        _recordPreDispatchIntegrationReceipt = recordPreDispatchIntegrationReceipt ?? ((_, _) => { });
        _dispatchAndStart = (goal, _) => dispatchAndStart(goal);
        _startRecordedDispatches = startRecordedDispatches is null
            ? _dispatchAndStart
            : (goal, _) => startRecordedDispatches(goal);
        _reconcileExitedDispatch = reconcileExitedDispatch ?? ((_, _) => false);
        _beforeFailedGoalRecoveryEffect = beforeFailedGoalRecoveryEffect;
        _workerBuildRecoveryRetry = workerBuildRecoveryRetry;
        _workerBuildArtifactsPath = workerBuildArtifactsPath ?? (goalId => DotnetBuildEnvironmentManager.GoalArtifactsPath(goalId));
        _buildServerShutdownTimeout = buildServerShutdownTimeout ?? DefaultBuildServerShutdownTimeout;
        _buildServerShutdown = timeout => RunBoundedBuildServerShutdown(
            buildServerShutdown ?? (() => { }),
            timeout);
        _runAcceptanceVerification = runAcceptanceVerificationWithLease is not null
            ? ((goal, slot, lease, token, _) => runAcceptanceVerificationWithLease(goal, slot, lease, token))
            : (runAcceptanceVerificationWithSlot is not null
                ? ((goal, slot, _, _, _) => runAcceptanceVerificationWithSlot(goal, slot))
                : ((goal, _, _, _, _) => runAcceptanceVerification(goal)));
        (_acceptanceEventSink, _noTickAcceptancePollDelay, _noTickAcceptancePollTimeout) = (acceptanceEventSink ?? ((_, _) => { }), noTickAcceptancePollDelay ?? Thread.Sleep, noTickAcceptancePollTimeout ?? DefaultNoTickAcceptancePollTimeout);
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
        _runNegativeControlFocusedEvidence = (goal, request, lease, baseline, token, _, _, _) =>
            (baseline ? _runDualArmFocusedEvidence : _runFocusedEvidence)(goal, request, lease, token);
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
        _resolveFindingEvidenceSiblingClasses = resolveFindingEvidenceSiblingClasses ??
            ((_, _, _) => []);
        _recordPreReviewEvidence = recordPreReviewEvidence ?? ((_, _, _) => { });
        _recordPreReviewMappingEscalationSuppressed = recordPreReviewMappingEscalationSuppressed ?? ((_, _, _, _) => { });
        _retryTask = retryTaskWithCause
            ?? (retryTaskWithRoundKind
            is not null
                ? ((goalId, taskId, message, roundKind, _) => retryTaskWithRoundKind(goalId, taskId, message, roundKind))
                : retryTask is not null
                    ? ((goalId, taskId, message, _, _) => retryTask(goalId, taskId, message))
                    : ((_, _, _, _, _) => throw new InvalidOperationException("Retry delegate was not configured.")));
        _recordTaskNote = recordTaskNote ?? ((_, _, _) => { });
        _recordFindingEvidenceRequest = recordFindingEvidenceRequest ?? recordReviewerEvidenceRequestReceived ?? ((_, _, _) => { });
        _recordFindingEvidenceRun = recordFindingEvidenceRun ?? recordReviewerEvidenceRunRecorded ?? ((_, _, _) => { });
        _recordFindingEvidenceSuppressed = recordFindingEvidenceSuppressed ?? ((_, _, _, _, _, _, _, _) => { });
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
        _resolveParkedWaitEscalations = resolveParkedWaitEscalations ?? ((_, _) => { });
        _writeEscalation = writeEscalationWithResult ?? ((goal, state, reason) =>
        {
            writeEscalation(goal, state, reason);
            return LandingEscalationWriteResult.CompletedWithoutExternalSinks;
        });
        _classifyChangeRisk = classifyChangeRisk;
        _emptyOutputBackoffDelay = emptyOutputBackoffDelay ?? Thread.Sleep;
        _evaluateReadiness = evaluateReadiness ?? (goal =>
            DispatchReadinessRules.HasAssignedDispatchCandidates(goal)
                ? new DispatchReadinessReady()
                : new DispatchReadinessBlocked("No assigned dispatch candidates"));
        _normalizeLifecycleState = normalizeLifecycleState ?? ((_, _) => false);
        _recoverSandboxPrep = recoverSandboxPrep ?? (action => action.Execute());
        _recordMissingBranchRetirement = recordMissingBranchRetirement ?? ((_, _) => { });
        _getLandingFileScopes = getLandingFileScopes ?? InferRecordedFileScopes;
        _isVerificationGateSatisfied = isVerificationGateSatisfied ?? (_ => false);
        _gateReadyCandidateProjector = gateReadyCandidateProjector;
        _runAcceptanceCohortOverride = runAcceptanceCohort;
        _startCohortGateBackground = action =>
        {
            _ = Task.Run(action);
        };
        _runMergeTrainOverride = runMergeTrain;
        _getAcceptanceSlotCount = getAcceptanceSlotCount ?? (_ => ConductorBatchLoop.DefaultParallelAcceptanceCapacity);
        _getWorkerAdmissionCapacity = getWorkerAdmissionCapacity ?? (() => ConductorBatchLoop.WorkerAdmissionCapacity);
        _hasGateReadyGoal = hasGateReadyGoal ?? (() => false);
        _tryBuildAwaitingClarificationEscalationReason =
            tryBuildAwaitingClarificationEscalationReason ?? (_ => null);
        _tryAcquireEvidenceMutationLease =
            tryAcquireEvidenceMutationLease ?? ((_, _) => NoopEvidenceMutationLease.Instance);
        _tryBeginDeveloperIntegrationEvidenceOperation = tryBeginDeveloperIntegrationEvidenceOperation;
        _tryRecoverTerminalDeveloperIntegrationLease = tryRecoverTerminalDeveloperIntegrationLease;
        _getEvidenceMutationLease = getEvidenceMutationLease ?? (_ => null);
        _resolveAcceptanceHeads = resolveAcceptanceHeads ?? (_ => (null, null));
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _executionDirectory = executionDirectory;
        _parallelAcceptanceEnabled =
            runAcceptanceVerificationWithSlot is not null ||
            runAcceptanceVerificationWithLease is not null;
        _parallelAcceptanceAttemptCoordinator = parallelAcceptanceAttemptCoordinator
            ?? new ConductorParallelAcceptanceAttemptCoordinator(
                Path.Combine(OrchestratorTempRoot.GetPurposeDirectory("conductor-acceptance-attempts"), Guid.NewGuid().ToString("N")),
                runInline: true,
                acquireStableSlotLease: (_, _) => null);
        _focusedEvidenceAttemptCoordinator = focusedEvidenceAttemptCoordinator
            ?? new ConductorParallelAcceptanceAttemptCoordinator(
                Path.Combine(OrchestratorTempRoot.GetPurposeDirectory("conductor-focused-evidence-attempts"), Guid.NewGuid().ToString("N")),
                runInline: true,
                acquireStableSlotLease: (_, _) => null);
    }

    internal ConductorParallelAcceptanceAttemptCoordinator ParallelAcceptanceAttemptCoordinator =>
        _parallelAcceptanceAttemptCoordinator;

    internal string? ExecutionDirectory => _executionDirectory;

    internal bool ParallelAcceptanceEnabled => _parallelAcceptanceEnabled;

    internal int GetAcceptanceSlotCount(Goal goal) => _getAcceptanceSlotCount(goal);

    internal void BeginTick() => BeginTick(kernel: null, tick: 0);

    internal void BeginTick(AgentOrchestratorKernel? kernel, int tick)
    {
        _isConductorTick = true;
        _conductorTickKernel = kernel;
        _conductorTick = tick;
        _buildServerShutdownRanThisTick = false;
        SliceBatchParentExecutionGuard?.BeginTick();
        SliceBatchAdmissionEvaluator?.BeginTick();
    }

    internal static DispatchStartOutcome ClassifySubscriptionStartForConductor(SubscriptionStartResult result)
    {
        if (result.Processes.RecoveryActions?.FirstOrDefault() is { } recoveryAction)
        {
            return DispatchStartOutcome.RecoverableSandboxPrep(recoveryAction);
        }

        if (result.Processes.Tasks.Count > 0)
        {
            return DispatchStartOutcome.Started(result.Processes.Tasks);
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
            : FormatPreparedDispatchWithoutStart(result);
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
            return DispatchStartOutcome.Started(result.Tasks);
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

        return DispatchStartOutcome.EmptyBatch(DispatchStartRefusalReasonBuilder.Build(result));
    }

    public ConductorAdvanceResult AdvanceOnce(
        Goal goal,
        ConductorAutonomyPolicy policy,
        string? runningHoldReason = null)
    {
        var goalId = goal.Id.Value;
        var goalPrefix = goalId[..8];

        _normalizeLifecycleState(
            goal,
            $"Conductor reconciled stale goal/task lifecycle state before lifecycle resolution for goal {goalPrefix}.");

        var facts = GetFacts(goal);
        var state = GoalLifecycle.ResolveState(goal, facts);
        try
        {
            _resolveParkedWaitEscalations(goal, state);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"warning: parked-wait landing escalation resolution failed for goal {goalPrefix}: {ex.Message}");
        }

        if (state == GoalLifecycleState.CleanedUp)
            return MakeResult(goalId, goalPrefix, policy, new ConductorAdvanceOutcome.Done(state));

        if (TryDecideLifecycleEntry(goal, goalPrefix, policy, state, out var lifecycleEntryResult))
            return lifecycleEntryResult;

        if (TryRunDeferredNoChangeEvidence(goal, goalPrefix, policy, state, out var deferredNoChangeResult) || TryRouteDeliveredFindingEvidenceToDeveloper(goal, goalPrefix, policy, state, out deferredNoChangeResult)) return deferredNoChangeResult;
        return state switch
        {
            GoalLifecycleState.Created => ExecuteCreateWorkspace(goal, goalPrefix, policy),
            GoalLifecycleState.WorkspaceReady => ExecuteDispatchAndStart(goal, goalPrefix, policy, GoalLifecycleState.WorkspaceReady),
            GoalLifecycleState.Dispatched => ExecuteDispatchAndStart(goal, goalPrefix, policy, GoalLifecycleState.Dispatched),
            GoalLifecycleState.Running => MakeResult(goalId, goalPrefix, policy,
                new ConductorAdvanceOutcome.Held(
                    state,
                    runningHoldReason ?? "Worker process running; auto-reconcile will handle completion")),
            GoalLifecycleState.AwaitingVerification => MakeResult(goalId, goalPrefix, policy,
                new ConductorAdvanceOutcome.Held(state, AwaitingVerificationHoldReasonBuilder.Build(goal))),
            GoalLifecycleState.Verifying => ExecuteVerifying(goal, goalPrefix, policy),
            GoalLifecycleState.Verified => ExecuteLanding(goal, goalPrefix, policy),
            GoalLifecycleState.Merged => ExecuteRecord(goal, goalPrefix, policy),
            GoalLifecycleState.Recorded => ExecuteCleanup(goal, goalPrefix, policy),
            _ => Escalate(goal, goalPrefix, policy, state, $"Unhandled lifecycle state {state}")
        };
    }
    internal GoalLifecycleFacts GetFacts(Goal goal) => _getFacts(goal);

    private bool TryReconcileFocusedEvidenceAttempt(
        Goal goal,
        ConductorAutonomyPolicy policy,
        string request,
        string? candidateSha,
        string source,
        ConductorFocusedEvidenceRequestContext? requestContext,
        out FocusedEvidenceRunResult evidence,
        out ConductorParallelAcceptanceAttempt? evidenceAttempt,
        out FailedGoalFindingObservation decision, out ConductorParallelAcceptanceAttemptDecisionKind? attemptKind)
    {
        evidence = null!;
        evidenceAttempt = null;
        decision = FailedGoalFindingObservation.None;
        attemptKind = null;
        var candidate = ConductorParallelAcceptanceCandidate.Create(
            goal,
            slotIndex: 0,
            fileScopes: [],
            branchHeadSha: candidateSha?.Trim(),
            mainHeadSha: null);
        ConductorParallelAcceptanceAttemptDecision attemptDecision;
        try
        {
            attemptDecision = _focusedEvidenceAttemptCoordinator.EvaluateFocusedEvidence(
                candidate,
                policy,
                request,
                SelectFindingEvidenceRunner(requestContext?.NegativeControl, runBaselineArm: true, requestContext?.RevertPaths, requestContext?.Mutation),
                requestContext);
        }
        catch (AcceptanceArtifactWriterLeaseBusyException ex)
        {
            decision = FailedGoalFindingObservation.Observed(
                FailedGoalFindingObservationKind.FindingEvidencePending,
                $"Background {source} focused-evidence artifact writer is busy; retry on next conduct tick. {ex.Message}");
            return false;
        }
        evidenceAttempt = attemptDecision.Attempt;
        attemptKind = attemptDecision.Kind;
        if (attemptDecision.Kind is
            ConductorParallelAcceptanceAttemptDecisionKind.Started or
            ConductorParallelAcceptanceAttemptDecisionKind.Running)
        {
            decision = FailedGoalFindingObservation.Observed(
                FailedGoalFindingObservationKind.FindingEvidencePending,
                $"Background {source} focused evidence is running in attempt {attemptDecision.Attempt.AttemptId}.") with { HoldOwner = ConductorHoldOwner.BackgroundAttempt };
            return false;
        }
        if (attemptDecision.Kind == ConductorParallelAcceptanceAttemptDecisionKind.TerminalWithoutRun ||
            attemptDecision.Run?.Exception is
                DotnetBuildSlotsBusyException or
                BuildLockBlockedException or
                OperationCanceledException)
        {
            MarkFocusedEvidenceAttemptIfReady(requestContext, attemptDecision);
            decision = FailedGoalFindingObservation.Observed(
                FailedGoalFindingObservationKind.FindingEvidencePending,
                $"Background {source} focused evidence did not run ({attemptDecision.Attempt.Outcome}); " +
                $"retry on next conduct tick. attempt={attemptDecision.Attempt.AttemptId}: " +
                (attemptDecision.Attempt.Detail ?? "no result artifact was produced"));
            return false;
        }
        MarkFocusedEvidenceAttemptIfReady(requestContext, attemptDecision);
        if (attemptDecision.Run?.Exception is { } backgroundFailure)
        {
            decision = FailedGoalFindingObservation.Observed(
                FailedGoalFindingObservationKind.FindingOperatorEvidenceRequired,
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
        TaskSpec? TargetTask,
        bool RequiresCommittedTarget = false,
        IReadOnlyList<ReviewFinding>? DeveloperOwnedFindings = null);

    internal ConductorParallelAcceptanceCandidate? TryBuildParallelAcceptanceCandidate(
        Goal goal,
        ConductorAutonomyPolicy policy,
        int slotIndex)
    {
        if (HasRoutableRecordedCohortAttributionFailure(goal))
        {
            return null;
        }

        if (policy.GetTransitionDecision(GoalLifecycleState.Verified) == ConductorTransitionDecision.Escalate)
        {
            return null;
        }

        if (GoalLifecycle.ResolveState(goal, GetFacts(goal)) is not (GoalLifecycleState.Verified or GoalLifecycleState.Verifying))
        {
            return null;
        }

        if (!AcceptancePrecheck.HasCompletedPassedVerificationForAllTasks(goal))
        {
            return null;
        }

        if (HasActiveApparatusHold(goal, out _) || HasActiveOwnerReviewHold(goal, out _, out _))
        {
            return null;
        }

        var acceptanceHeads = _resolveAcceptanceHeads(goal);

        if (!_parallelAcceptanceAttemptCoordinator.HasLiveAttempt(goal.Id.Value) &&
            TryGetActiveEvidenceMutationLease(goal) is { } lease)
        {
            throw new EvidenceMutationLeaseUnavailableException(FormatEvidenceMutationLeaseHeld(lease));
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
            acceptanceHeads.BranchHeadSha,
            acceptanceHeads.MainHeadSha);
    }

    internal GateReadyCandidateProjectionResult ProjectGateReadyCandidate(
        Goal goal,
        ConductorAutonomyPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(goal);
        ArgumentNullException.ThrowIfNull(policy);

        if (HasActiveOwnerReviewHold(goal, out _, out _))
            return ExcludedGateReadyCandidate(GateReadyCandidateExclusionReason.OwnerReviewHold);

        if (HasActiveApparatusHold(goal, out _))
        {
            return ExcludedGateReadyCandidate(GateReadyCandidateExclusionReason.ApparatusHold);
        }

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

            var resolvedGoals = goals.Cast<Goal>().ToArray(); var evidenceMutationLeases = new Stack<IDisposable>();
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
                if (AcceptanceCriterionEvidence.RebindRecordAndDescribeOutstanding(resolvedGoals, goalId => recovery.Receipt.Identity.Members.Single(member => member.GoalId == goalId).CandidateRevision, kernel, $"cohort-receipt:{recovery.Receipt.ReceiptId}", workspace.ExecutionDirectory) is { } evidenceDiagnostic) { Console.WriteLine($"COHORT_RECOVERY_HELD cohort={recovery.Receipt.Identity.Value} detail={evidenceDiagnostic}"); continue; }
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

    internal IReadOnlySet<string> ReadSuppressedCohortPairs() =>
        _cohortAcceptanceStore?.ReadSuppressedPairs() ?? new HashSet<string>(StringComparer.Ordinal);

    internal void ResetCohortFairness(GoalId goalId) =>
        _cohortAcceptanceStore?.ResetOvertake(goalId);

    internal bool TryGetCohortGateHold(GoalId goalId, out string detail)
    {
        SweepCompletedCohortGateRuns();
        foreach (var pair in _cohortGateRuns)
        {
            if (!pair.Value.MemberGoalIds.Contains(goalId.Value))
            {
                continue;
            }

            detail = FormatCohortGateInFlightDetail(pair.Value, _utcNow());
            return true;
        }

        detail = string.Empty;
        return false;
    }

    internal void RecordMergeTrainAdmissionFairness(ConductorMergeTrainSelection selection)
    {
        if (_cohortAcceptanceStore is null) return;
        foreach (var member in selection.Members)
        {
            _cohortAcceptanceStore.ResetOvertake(member.GoalId);
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
            return RunParallelLandingSourceSizePreflight(effectiveCandidate);
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

    internal static bool IsAcceptanceAttemptCancelled(
        OrchestratorWorkspace workspace,
        GoalId goalId,
        Func<GoalId, GoalStatus?>? loadGoalStatus = null,
        Func<bool>? attemptInvalidationRecorded = null) =>
        GetAcceptanceAttemptCancellationDecision(
            workspace,
            goalId,
            loadGoalStatus,
            attemptInvalidationRecorded).ShouldCancel;

    internal static AcceptanceAttemptCancellationDecision GetAcceptanceAttemptCancellationDecision(
        OrchestratorWorkspace workspace,
        GoalId goalId,
        Func<GoalId, GoalStatus?>? loadGoalStatus = null,
        Func<bool>? attemptInvalidationRecorded = null)
    {
        var invalidated = false;
        if (attemptInvalidationRecorded is not null)
        {
            try
            {
                invalidated = attemptInvalidationRecorded();
            }
            catch
            {
                // Attempt metadata is supplementary stop evidence. Every operator stop also updates the
                // fail-closed goal record, so transient metadata I/O must not recreate spurious cancellation.
            }
        }

        var loadGoalStatusRecord = loadGoalStatus ?? new Func<GoalId, GoalStatus?>(id =>
            SqliteOrchestratorStateRepository.OpenReadOnly(workspace.SqliteStatePath)
                .LoadGoalAsync(id)
                .GetAwaiter()
                .GetResult()
                ?.Status);

        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                var observedStatus = loadGoalStatusRecord(goalId);
                return AcceptanceAttemptCancellation.Decide(
                    observedStatus,
                    goalRecordReadable: true,
                    attemptInvalidationRecorded: invalidated);
            }
            catch
            {
                // Retry immediately: this callback runs at every gate-check boundary and must stay cheap.
            }
        }

        return AcceptanceAttemptCancellation.Decide(
            observedStatus: null,
            goalRecordReadable: false,
            attemptInvalidationRecorded: invalidated);
    }

    internal ConductorAdvanceResult CompleteParallelLandingAcceptance(
        ConductorParallelAcceptanceCandidate candidate,
        ConductorAutonomyPolicy policy,
        AcceptanceVerificationSummary acceptance,
        out bool evidenceMutationLeaseHeld)
    {
        evidenceMutationLeaseHeld = false;
        using var evidenceMutationLease = _tryAcquireEvidenceMutationLease(
            candidate.Goal,
            "conductor:parallel-land");
        if (evidenceMutationLease is null)
        {
            var held = ReplacementEvidenceMutationHeld(candidate.Goal, candidate.GoalPrefix, policy);
            evidenceMutationLeaseHeld = true;
            return held;
        }

        acceptance = NormalizeNamedFailedChecksForRetry(acceptance);
        if (!acceptance.Passed || acceptance.UnmetCriteria.Count > 0)
        {
            return CompleteLandingAfterAcceptance(candidate.Goal, candidate.GoalPrefix, policy, acceptance);
        }

        return CompleteParallelLandingAfterPreMergeRebase(candidate, policy, acceptance);
    }

    internal ConductorAdvanceResult EscalateParallelLandingAcceptance(
        ConductorParallelAcceptanceCandidate candidate,
        ConductorAutonomyPolicy policy,
        string reason, ConductorEscalationKind? kind = null) =>
        Escalate(candidate.Goal, candidate.GoalPrefix, policy, GoalLifecycleState.Verified, reason, kind);

    internal ConductorAdvanceResult EscalateParallelLandingAcceptance(
        Goal goal,
        ConductorAutonomyPolicy policy,
        string reason, ConductorEscalationKind? kind = null) =>
        Escalate(goal, goal.Id.Value[..8], policy, GoalLifecycleState.Verified, reason, kind);

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
        var attributions = acceptance.CheckAttributions;
        var allEnvironmentalApparatus = attributions is { Count: > 0 } &&
            failedChecks.All(name => attributions.Any(attribution =>
                attribution.CheckName.Equals(name, StringComparison.Ordinal) &&
                attribution.Cause == AcceptanceFailureCause.EnvironmentalApparatus));
        var allInherited = allEnvironmentalApparatus && failedChecks.All(name => attributions!.Any(attribution =>
            attribution.CheckName.Equals(name, StringComparison.Ordinal) &&
            attribution.Origin == AcceptanceFailureOrigin.Inherited));
        var check = new AcceptanceCheckResult(
            "acceptance failed checks",
            false,
            1,
            string.IsNullOrWhiteSpace(acceptance.FailureDetail) ? output : acceptance.FailureDetail,
            ResultSummary: summary,
            FailureClassification: allEnvironmentalApparatus
                ? allInherited
                    ? AcceptanceFailureClassifications.InheritedBaselineApparatus
                    : AcceptanceFailureClassifications.GateEnvironmentInterference
                : null);
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

    internal static AcceptanceVerificationSummary ClassifyInheritedBaselineApparatus(
        AcceptanceVerificationSummary acceptance)
    {
        if (acceptance.Passed ||
            acceptance.FailedChecks is not { Count: > 0 } failedChecks ||
            acceptance.CheckAttributions is not { Count: > 0 } attributions ||
            !failedChecks.All(name => attributions.Any(attribution =>
                attribution.CheckName.Equals(name, StringComparison.Ordinal) &&
                attribution.Origin == AcceptanceFailureOrigin.Inherited &&
                attribution.Cause == AcceptanceFailureCause.EnvironmentalApparatus)))
        {
            return acceptance;
        }

        var inheritedChecks = failedChecks.ToHashSet(StringComparer.Ordinal);
        var classifiedChecks = acceptance.UnmetCriteria
            .Select(check => !check.Advisory && inheritedChecks.Contains(check.Name)
                ? check with
                {
                    FailureClassification = AcceptanceFailureClassifications.InheritedBaselineApparatus
                }
                : check)
            .ToArray();
        return new AcceptanceVerificationSummary(
            acceptance.Passed,
            classifiedChecks,
            acceptance.FailureDetail,
            acceptance.FailedChecks,
            acceptance.BranchHeadSha,
            acceptance.MainHeadSha,
            acceptance.TestResultPaths,
            attributions,
            acceptance.BaselineAttestation);
    }

    internal static bool IsEnvironmentalApparatusAcceptanceRun(AcceptanceVerificationSummary acceptance)
    {
        if (acceptance.RequiredUnmetCriteria is not { Count: > 0 } requiredUnmetCriteria)
        {
            return false;
        }

        return requiredUnmetCriteria.All(check =>
            check.FailureClassification is
                AcceptanceFailureClassifications.GateEnvironmentInterference or
                AcceptanceFailureClassifications.AssemblyCleanupFailure or
                AcceptanceFailureClassifications.InheritedBaselineApparatus or AcceptanceFailureClassifications.SharedGateApparatusInvalidated ||
            acceptance.CheckAttributions is { Count: > 0 } attributions &&
            attributions.Any(attribution =>
                attribution.CheckName.Equals(check.Name, StringComparison.Ordinal) &&
                attribution.Cause == AcceptanceFailureCause.EnvironmentalApparatus));
    }

    private static bool IsSameApparatusFailurePair(
        AcceptanceFailureSummary? failure,
        (string? BranchHeadSha, string? MainHeadSha) current)
    {
        if (failure is not { IsEnvironmentalApparatus: true })
        {
            return false;
        }

        return ShaIsUnchangedOrUnknown(failure.BranchHeadSha, current.BranchHeadSha) &&
            ShaIsUnchangedOrUnknown(failure.MainHeadSha, current.MainHeadSha);
    }

    private bool HasActiveApparatusHold(
        Goal goal,
        out (string? BranchHeadSha, string? MainHeadSha) current)
    {
        current = (null, null);
        if (goal.LatestAcceptanceFailure is not { IsEnvironmentalApparatus: true })
        {
            return false;
        }

        current = _resolveAcceptanceHeads(goal);
        return IsSameApparatusFailurePair(goal.LatestAcceptanceFailure, current);
    }

    private static bool ShaIsUnchangedOrUnknown(string? recorded, string? current) =>
        string.IsNullOrWhiteSpace(recorded) ||
        string.IsNullOrWhiteSpace(current) ||
        recorded.Equals(current, StringComparison.OrdinalIgnoreCase);

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

    private ConductorAdvanceResult ExecuteDispatchAndStart(
        Goal goal,
        string goalPrefix,
        ConductorAutonomyPolicy policy,
        GoalLifecycleState fromState)
    {
        var running = _getRunningPaidWorkerCount();
        var workerAdmission = GetWorkerAdmissionSnapshot(policy);
        var workerCap = workerAdmission.EffectiveWorkerCap;
        var facts = new DispatchAdmissionFacts(running, workerAdmission.ConfiguredWorkerCap,
            workerAdmission.AdmissionCapacity, workerAdmission.ReservedGateSlots, workerCap, policy.MaxConcurrentPaidWorkers);
        var decision = DispatchAdmissionPolicy.Evaluate(facts);

        if (decision.Action == DispatchAdmissionAction.Hold)
        {
            var admissionClamped = workerCap < workerAdmission.ConfiguredWorkerCap;
            if (admissionClamped)
            {
                var reason = decision.DiscriminatingEvidence;
                Console.WriteLine(
                    $"ADMISSION goal={goalPrefix} result=deferred reason={reason} cap={workerCap} running={running} " +
                    $"configuredCap={workerAdmission.ConfiguredWorkerCap} admissionCapacity={workerAdmission.AdmissionCapacity}");
            }

            EmitPhaseTiming("dispatch-prep", goal, TimeSpan.Zero, $"tasks={CountAssignedTasks(goal)} result=held-cap running={running}");
            return MakeResult(goal.Id.Value, goalPrefix, policy,
                new ConductorAdvanceOutcome.Held(fromState, decision.Reason) { Decision = decision.ToRecord(), Owner = ConductorHoldOwner.WorkerCapacity });
        }

        if (SliceBatchAdmissionEvaluator?.Evaluate(goal) is { } sliceDecision)
        {
            decision = DispatchAdmissionPolicy.Evaluate(facts with
                { SliceBatchAdmissionAllowed = sliceDecision.IsAllowed, SliceBatchAdmissionReason = sliceDecision.Reason });
            if (decision.Action == DispatchAdmissionAction.Hold)
                return MakeResult(goal.Id.Value, goalPrefix, policy,
                    new ConductorAdvanceOutcome.Held(fromState, decision.Reason) { Decision = decision.ToRecord() });
        }

        var exitedUnappliedTaskIds = goal.Tasks
            .Where(task => task.LastProcess is { } process &&
                           DispatchProcessCompletionState.IsExitedWithoutAppliedCompletion(task, process))
            .Select(task => task.Id)
            .ToArray();
        foreach (var taskId in exitedUnappliedTaskIds)
        {
            _reconcileExitedDispatch(goal, taskId);
            goal = GetCurrentGoal(goal);
        }

        var unreconciledTask = goal.Tasks.FirstOrDefault(task =>
            task.LastProcess is { } process &&
            DispatchProcessCompletionState.IsExitedWithoutAppliedCompletion(task, process));
        var startFacts = new DispatchStartFacts(goal.Id.Value);
        if (unreconciledTask is not null)
        {
            var startDecision = DispatchStartPolicy.Evaluate(startFacts with
                { UnreconciledTaskIdPrefix = unreconciledTask.Id.Value[..8] });
            _recordTaskNote(goal.Id, unreconciledTask.Id, startDecision.Reason);
            return MakeResult(goal.Id.Value, goalPrefix, policy,
                new ConductorAdvanceOutcome.Held(fromState, startDecision.Reason) { Decision = startDecision.ToRecord() });
        }

        if (fromState == GoalLifecycleState.WorkspaceReady &&
            HasAssignedDeveloperReadyForDispatch(goal))
        {
            if (_tryBeginDeveloperIntegrationEvidenceOperation is not null)
            {
                var operationStart = _tryBeginDeveloperIntegrationEvidenceOperation(goal);
                if (operationStart.Scope is null)
                {
                    var status = operationStart.LeaseFact is { } leaseFact
                        ? GoalEvidenceLeaseRecoveryStatuses.Format(leaseFact.RecoveryStatus)
                        : "state-unavailable";
                    return DispatchStartResult(goal, goalPrefix, policy, fromState,
                        startFacts with { LeaseRecoveryStatus = status });
                }

                using (operationStart.Scope)
                {
                    DeveloperBranchIntegrationResult integration;
                    try
                    {
                        integration = _integrateMainBeforeDeveloperDispatch(goal);
                        _recordPreDispatchIntegrationReceipt(goal, integration);
                        startFacts = WithIntegrationFacts(startFacts, integration);
                    }
                    catch (OperationCanceledException ex)
                    {
                        operationStart.Scope.Abort(ex.Message);
                        throw;
                    }
                    catch (Exception ex)
                    {
                        operationStart.Scope.Fail(ex.Message);
                        throw;
                    }

                    if (integration.CanDispatch)
                    {
                        operationStart.Scope.Complete(integration.Message);
                    }
                    else
                    {
                        operationStart.Scope.Fail(integration.Message);
                        return DispatchStartResult(goal, goalPrefix, policy, fromState, startFacts);
                    }
                }
            }
            else
            {
                using var integrationEvidenceMutationLease = _tryAcquireEvidenceMutationLease(
                    goal,
                    "conductor:developer-branch-integration");
                if (integrationEvidenceMutationLease is null)
                {
                    return DispatchStartResult(goal, goalPrefix, policy, fromState,
                        startFacts with { ConcurrentLeaseRefusal = "refused" });
                }

                var integration = _integrateMainBeforeDeveloperDispatch(goal);
                _recordPreDispatchIntegrationReceipt(goal, integration);
                startFacts = WithIntegrationFacts(startFacts, integration);
                if (!integration.CanDispatch)
                {
                    return DispatchStartResult(goal, goalPrefix, policy, fromState, startFacts);
                }
            }
        }

        if (fromState == GoalLifecycleState.WorkspaceReady &&
            TryIntegrateMainBeforeReadOnlyDispatch(goal, goalPrefix, policy, fromState, out var readOnlyIntegrationHold))
        {
            return readOnlyIntegrationHold;
        }

        if (TryRunDeveloperCompletionStructuralPreflight(goal, goalPrefix, policy, fromState, out var structuralPrecheck))
        {
            return structuralPrecheck;
        }
        if (TryRefuseUnchangedCandidateDispatch(goal, goalPrefix, policy, fromState, out var unchangedCandidateHold))
        {
            return unchangedCandidateHold;
        }
        if (TryRunPreReviewEvidenceStage(goal, goalPrefix, policy, fromState, out var preReviewResult))
        {
            return preReviewResult;
        }

        var start = fromState == GoalLifecycleState.Dispatched ? _startRecordedDispatches : _dispatchAndStart;
        var dispatchTimingGoal = goal;
        var startClock = Stopwatch.StartNew();
        var outcome = start(goal, policy);
        startClock.Stop();
        EmitPhaseTiming("dispatch-prep", dispatchTimingGoal, outcome, startClock.Elapsed, $"result={outcome.Category}");
        goal = GetCurrentGoal(goal);
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
            EmitPhaseTiming("dispatch-prep", dispatchTimingGoal, outcome, startClock.Elapsed, $"result={outcome.Category} retry=sandbox-prep");
            goal = GetCurrentGoal(goal);
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
            EmitPhaseTiming("dispatch-prep", dispatchTimingGoal, outcome, startClock.Elapsed, $"result={outcome.Category} retry=spawn-failed");
            goal = GetCurrentGoal(goal);
            if (outcome.Category == DispatchStartOutcomeCategory.EmptyBatch)
            {
                outcome = firstFailure;
            }
        }

        if (outcome.Category == DispatchStartOutcomeCategory.Deferred)
        {
            return MakeResult(goal.Id.Value, goalPrefix, policy,
                new ConductorAdvanceOutcome.Held(fromState, outcome.Reason!) { Owner = outcome.HoldOwner });
        }

        startFacts = startFacts with { StartOutcomeCategory = outcome.Category.ToString(), StartOutcomeReason = outcome.Reason ?? "" };
        if (outcome.Category == DispatchStartOutcomeCategory.Started)
        {
            _ = DispatchStartPolicy.Evaluate(startFacts);
            SliceBatchAdmissionEvaluator?.RecordAdmitted(goal);
            return MakeResult(goal.Id.Value, goalPrefix, policy,
                new ConductorAdvanceOutcome.Executed(fromState, "Subscription dispatch started"));
        }

        // When all ready tasks are blocked or deferred, hold rather than escalate so the conductor
        // retries on the next tick. Uses the canonical DispatchReadinessEvaluator so this decision
        // always agrees with GoalReadinessPreflight and CrossGoalSubscriptionStartPlanner.
        if (outcome.Category == DispatchStartOutcomeCategory.EmptyBatch)
        {
            var readiness = _evaluateReadiness(goal);
            startFacts = WithReadinessFacts(startFacts, readiness);
            if (readiness is DispatchReadinessDeferred)
            {
                return DispatchStartResult(goal, goalPrefix, policy, fromState, startFacts);
            }

            if (TryDescribeCancelledPredecessorBlocker(goal, out var terminalBlocker))
            {
                return DispatchStartResult(goal, goalPrefix, policy, fromState,
                    startFacts with { CancelledPredecessorBlocker = terminalBlocker });
            }

            if (readiness is not DispatchReadinessBlocked { HasCandidates: false })
            {
                return DispatchStartResult(goal, goalPrefix, policy, fromState, startFacts with
                    { AssignedTasksBlockedReason = FormatAssignedTasksBlockedReason(goal, readiness, outcome.Reason) });
            }
        }

        return DispatchStartResult(goal, goalPrefix, policy, fromState, startFacts);
    }

    private Goal GetCurrentGoal(Goal goal) =>
        (_cohortKernel ?? _conductorTickKernel)?.Goals.SingleOrDefault(candidate => candidate.Id == goal.Id) ?? goal;

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
                $"phase={phase} goal={goal.Id.Value[..8]} task={task.Id.Value[..8]} role={task.RequiredRole} elapsed_ms={(long)Math.Ceiling(elapsed.TotalMilliseconds)} {detail}");
        }
    }

    private void EmitPhaseTiming(string phase, Goal goal, DispatchStartOutcome outcome, TimeSpan elapsed, string detail)
    {
        var dispatched = outcome.DispatchedTasks ?? [];
        if (dispatched.Count == 0)
        {
            EmitGoalPhaseTiming(phase, goal, elapsed, detail);
            return;
        }

        foreach (var task in dispatched)
        {
            PhaseTimingSink?.Invoke(
                $"phase={phase} goal={goal.Id.Value[..8]} task={task.TaskId.Value[..8]} role={task.Role} elapsed_ms={(long)Math.Ceiling(elapsed.TotalMilliseconds)} {detail}");
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

    internal static void AppendCohortGateProgressEvents(
        ConductEventLogWriter writer,
        AcceptanceCohortIdentity identity,
        IReadOnlyList<AcceptanceCohortMemberBinding> bindings,
        AcceptanceGateProgress progress)
    {
        var members = string.Join(',', bindings.Select(member => member.GoalId.Value[..8]));
        foreach (var member in bindings)
        {
            var goalId = member.GoalId.Value[..8];
            var detail =
                $"PHASE_PROGRESS goal={goalId} cohort={identity.Value[..Math.Min(18, identity.Value.Length)]} " +
                $"member={goalId} members={members} phase={progress.Phase} " +
                $"elapsed_ms={(long)progress.Elapsed.TotalMilliseconds} target={FormatConductToken(progress.CurrentTarget)} " +
                $"child_pid={progress.ChildProcessId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown"} " +
                $"output_bytes={progress.OutputBytes} heartbeat={FormatConductToken(progress.HeartbeatPath)}";
            if (!writer.AppendRequired("gate-progress", goalId, detail))
            {
                throw new IOException(
                    $"Required cohort gate progress event could not be appended for goal {goalId}.");
            }
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
                DispatchReadinessRules.IsEarlierSdlcStageOf(
                    candidate.RequiredRole,
                    task.RequiredRole) &&
                candidate.Status != WorkTaskStatus.Completed));

    private static string FormatPreparedDispatchWithoutStart(SubscriptionStartResult result)
    {
        const int maxPreparedDiagnostics = 8;
        var preparedState = result.Dispatches
            .Take(maxPreparedDiagnostics)
            .Select(dispatch =>
            {
                var task = dispatch.Task;
                var admission = task.RetryAdmissionHistory.LastOrDefault(receipt =>
                    receipt.LinkedDispatchAt == task.LastDispatch?.DispatchedAt);
                var admissionState = admission is null
                    ? "none"
                    : $"{admission.Decision}/{admission.Route}/{admission.Cause}";
                return $"{task.Id.Value[..8]}:status={task.Status}:admission={admissionState}";
            })
            .ToArray();
        var omitted = result.Dispatches.Count - preparedState.Length;
        var omittedSuffix = omitted > 0 ? $",...(+{omitted})" : string.Empty;
        return $"Prepared {result.Dispatches.Count} dispatch(es) but no process was startable; " +
               $"{DispatchStartRefusalReasonBuilder.Build(result.Processes, result.Dispatches)}; " +
               $"prepared=[{string.Join(',', preparedState)}{omittedSuffix}]";
    }

    private static string FormatSourceCleanupPaths(IReadOnlyList<string>? paths)
    {
        const int maxPaths = 20;
        var available = paths ?? [];
        var listed = string.Join(", ", available.Take(maxPaths).Select(path => $"'{path}'"));
        var omitted = available.Count - Math.Min(available.Count, maxPaths);
        return omitted > 0 ? $"[{listed}, ... (+{omitted})]" : $"[{listed}]";
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
        return $"{NoReadyBatchHoldPrefix} for goal {goal.Id.Value}; will retry next tick. Blockers: {blockers}.";
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
            DispatchReadinessRules.IsEarlierSdlcStageOf(candidate.RequiredRole, task.RequiredRole) &&
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
                DispatchReadinessRules.IsEarlierSdlcStageOf(candidate.RequiredRole, task.RequiredRole) &&
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

    private static string FormatBuildLockBlocked(BuildLockAttribution attribution)
    {
        var holders = attribution.Holders.Count == 0
            ? "unknown"
            : string.Join(", ", attribution.Holders.Select(holder =>
                $"pid {holder.ProcessId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown"} {holder.ProcessName ?? "unknown"}"));
        return $"path={attribution.Path}; holders: {holders}";
    }

    private ReconcileAcceptanceLeaseState? TryGetActiveEvidenceMutationLease(Goal goal)
    {
        _tryRecoverTerminalDeveloperIntegrationLease?.Invoke(goal);
        var lease = _getEvidenceMutationLease(goal);
        return lease is not null && lease.ExpiresAtUtc > _utcNow()
            ? lease
            : null;
    }

    private static string FormatEvidenceMutationLeaseHeld(ReconcileAcceptanceLeaseState lease) =>
        $"acceptance lease held; owner={lease.Owner}; expiresAtUtc={lease.ExpiresAtUtc:O}";

    private ConductorAdvanceResult ReplacementEvidenceMutationHeld(
        Goal goal,
        string goalPrefix,
        ConductorAutonomyPolicy policy)
    {
        var lease = TryGetActiveEvidenceMutationLease(goal);
        var reason = lease is null
            ? "acceptance lease acquisition blocked; owner=unknown; expiresAtUtc=unknown"
            : FormatEvidenceMutationLeaseHeld(lease);
        return MakeResult(
            goal.Id.Value,
            goalPrefix,
            policy,
            new ConductorAdvanceOutcome.Held(
                GoalLifecycleState.Verified,
                reason));
    }

    private sealed class NoopEvidenceMutationLease : IDisposable
    {
        internal static readonly NoopEvidenceMutationLease Instance = new();

        public void Dispose()
        {
        }
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
