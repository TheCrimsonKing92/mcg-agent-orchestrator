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
    RepositoryTestImpactDegradation? TestImpactDegradation = null,
    RepositoryTestImpactHeadroom? TestImpactHeadroom = null)
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
    internal static DispatchReadinessVerdict EvaluateConductorReadiness(
        AgentOrchestratorKernel kernel, Goal goal, IReadOnlyList<AgentDefinition> agents,
        WorkerProfileCatalog profiles, DateTimeOffset now) =>
        DispatchReadinessAssessment.Evaluate(goal, kernel.Goals, agents, profiles, now).Verdict;

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
    private readonly string _integrationBranch;
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
        _integrationBranch = workspace.IntegrationBranch;
        _resolveAcceptanceHeads = goal =>
            (TryResolveAcceptanceBranchHead(goal), TryResolveGitHead(dir));
        _getAcceptanceSlotCount = _ => ConductorBatchLoop.DefaultParallelAcceptanceCapacity;
        _getWorkerAdmissionCapacity = () => ConductorBatchLoop.WorkerAdmissionCapacity;
        _parallelAcceptanceEnabled = true;
        _cohortCleanupHooks = cleanupHooks ?? new GoalWorktreeCleanupHooks();
        _workerBuildArtifactsPath = goalId => DotnetBuildEnvironmentManager.GoalArtifactsPath(goalId, _cohortCleanupHooks.BuildStorageRoot);
        _parallelAcceptanceAttemptCoordinator = new ConductorParallelAcceptanceAttemptCoordinator(
            Path.Combine(workspace.OrchestratorDirectory, "acceptance-gate-attempts"), _integrationBranch,
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
            Path.Combine(workspace.OrchestratorDirectory, "pre-review-evidence-attempts"), _integrationBranch,
            dir,
            conductEventLogWriter: new ConductEventLogWriter(workspace.ConductEventsLogPath),
            buildStorageRoot: _cohortCleanupHooks.BuildStorageRoot);
        var eventWriter = new GoalLifecycleEventWriter(workspace.GoalLifecycleEventsDirectory, integrationBranch: workspace.IntegrationBranch);
        _cohortKernel = kernel;
        ConfigureCandidateIdentity(kernel, workspace.ConductEventsLogPath);
        _cohortWorkspace = workspace;
        _runDeveloperCompletionStructuralPreflight = path =>
            DeveloperCompletionStructuralPreflight.Evaluate(path, workspace.ProjectHomeDirectoryOrNull);
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
        var dispatchLifecycleGate = new CriticalDispatchLifecycleEventGate(eventWriter);
        kernel.SetEventWriter(dispatchLifecycleGate);
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
            var path = GoalWorktrees.Ensure(dir, goal.Id, _cohortCleanupHooks, workspace.IntegrationBranch);
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
            SubscriptionStartResult result;
            using var dispatchCheckpoint = CriticalDispatchLifecycleCheckpoint.Begin(
                dispatchLifecycleGate, kernel, goal.Id, persistCriticalDispatchStart,
                goalId => DispatchRecordWriteSucceededSink?.Invoke(goalId));
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
                    checkpointBeforeWorkerStart: dispatchCheckpoint?.BeforeWorkerStart,
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

                if (!ex.ProcessMayHaveStarted && dispatchCheckpoint?.HasCommitted != true)
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
            using var dispatchCheckpoint = CriticalDispatchLifecycleCheckpoint.Begin(
                dispatchLifecycleGate, kernel, goal.Id, persistCriticalDispatchStart,
                goalId => DispatchRecordWriteSucceededSink?.Invoke(goalId));
            try
            {
                result = new GoalDispatchOperations().StartDispatches(
                    kernel,
                    workspace,
                    goal,
                    checkpointBeforeWorkerStart: dispatchCheckpoint?.BeforeWorkerStart,
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
        _buildServerShutdown = RunRetryOnlyRemediation;

        _runAcceptanceVerification = (goal, stableSlotIndex, stableSlotLease, cancellationToken, attemptOptions) =>
        {
            var worktreePath = GoalWorktrees.TryResolve(dir, goal.Id);
            if (worktreePath is null) return AcceptanceVerificationSummary.Failed;
            var slotSuffix = stableSlotIndex.HasValue ? $" on stable slot {stableSlotIndex.Value}" : string.Empty;
            var acceptanceAttemptStartedAt = DateTimeOffset.UtcNow;
            GoalOperationJournal.Begin(dir, goal, "conductor:acceptance", $"Running acceptance verification{slotSuffix}.");
            var changedFiles = GoalAcceptanceEvidenceBundleBuilder.GetChangedFiles(worktreePath, workspace.IntegrationBranch);
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
                    workspace.ConductEventsLogPath);
                var executionOptions = attemptOptions with
                {
                    ProjectHomeDirectory = workspace.ProjectHomeDirectoryOrNull,
                    IntegrationBranch = workspace.IntegrationBranch,
                    ProgressSink = progress => AppendGateProgressEvent(gateProgressEventWriter, goal.Id, progress),
                    RemoteLaneEventSink = detail => AppendRemoteLaneEvent(gateProgressEventWriter, goal.Id.Value[..8], detail),
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
                runBaselineArm, cancellationToken, workspace.IntegrationBranch, negativeControl, revertPaths, mutation,
                NegativeControlRevertSetResolver.Resolve(goal), workspace.ProjectHomeDirectoryOrNull);
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

        _integrateMainBeforeDeveloperDispatch = new FailedRoundCheckpointPreDispatch(kernel, dir, workspace.IntegrationBranch).IntegrateMainBeforeDeveloperDispatch;
        _integrateMainBeforeReadOnlyDispatch = (goal, role) =>
            IntegrateMainBeforeReadOnlyDispatch(dir, goal, role, workspace.IntegrationBranch);
        _recordPreDispatchIntegrationReceipt = new PreDispatchIntegrationReceiptRecorder(kernel).Record;
        _rebaseOntoMain = goal => GoalWorktrees.TryRebaseOntoMain(dir, goal.Id, CreateAdditiveConflictMergeOptions(kernel, goal, workspace.ConductEventsLogPath), workspace.IntegrationBranch);
        PreLandingMergeConflictProbe = goal => GoalWorktrees.ProbeAdditiveConflictMerge(dir, goal.Id, CreateAdditiveConflictMergeOptions(kernel, goal, workspace.ConductEventsLogPath).FrozenPaths, workspace.IntegrationBranch);
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

            var evidence = ReadLandingRecheckEvidence(worktreePath, workspace.IntegrationBranch);
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
            RemoteGitMirror.TryStartBackgroundProcessing(kernel, dir, goal.Id, _integrationBranch);
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
                var result = GitCli.Run(dir, "diff", "--name-only", $"{workspace.IntegrationBranch}...{branch}");
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
        _evaluateReadiness = goal => EvaluateConductorReadiness(kernel, goal, agents, profiles, DateTimeOffset.UtcNow);
        _getLandingFileScopes = goal =>
        {
            var worktreePath = GoalWorktrees.TryResolve(dir, goal.Id);
            var changedFiles = worktreePath is null
                ? Array.Empty<string>()
                : GoalAcceptanceEvidenceBundleBuilder.GetChangedFiles(worktreePath, workspace.IntegrationBranch);
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
                    : GoalAcceptanceEvidenceBundleBuilder.GetChangedFiles(worktreePath, workspace.IntegrationBranch);
            },
            kernel.RecordGoalPolicyDecision, new SliceBatchSiblingDependencyCoordinator(dir));
        SliceBatchParentExecutionGuard = new SliceBatchParentExecutionGuard(() => kernel.Goals);
        _isVerificationGateSatisfied = goal => kernel.BuildVerificationGate(goal.Id).IsSatisfied;
        _gateReadyCandidateProjector = GateReadyCandidateProjector.CreateForRepository(dir, workspace.IntegrationBranch);
        _getPreReviewEvidenceContext = goal => BuildPreReviewEvidenceContext(goal, dir, workspace.IntegrationBranch);
        _getFindingEvidenceEngineSettings = goal => AcceptanceGateEngineSettings.Load(
            GoalWorktrees.TryResolve(dir, goal.Id) ?? dir, workspace.ProjectHomeDirectoryOrNull);
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
        Goal goal, string integrationBranch) => IntegrateMainBeforeDispatch(executionDirectory, goal, AgentRole.Developer, integrationBranch);

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
        _buildServerShutdown = buildServerShutdown is null
            ? RunRetryOnlyRemediation
            : timeout => RunBoundedBuildServerShutdown(buildServerShutdown, timeout);
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
        _integrationBranch = TrunkBranchName.Default;
        _parallelAcceptanceEnabled =
            runAcceptanceVerificationWithSlot is not null ||
            runAcceptanceVerificationWithLease is not null;
        _parallelAcceptanceAttemptCoordinator = parallelAcceptanceAttemptCoordinator
            ?? new ConductorParallelAcceptanceAttemptCoordinator(
                Path.Combine(OrchestratorTempRoot.GetPurposeDirectory("conductor-acceptance-attempts"), Guid.NewGuid().ToString("N")), _integrationBranch,
                runInline: true,
                acquireStableSlotLease: (_, _) => null);
        _focusedEvidenceAttemptCoordinator = focusedEvidenceAttemptCoordinator
            ?? new ConductorParallelAcceptanceAttemptCoordinator(
                Path.Combine(OrchestratorTempRoot.GetPurposeDirectory("conductor-focused-evidence-attempts"), Guid.NewGuid().ToString("N")), _integrationBranch,
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

        if (TryDecideLifecycleEntry(goal, goalPrefix, policy, state, out var lifecycleEntryResult, out var lifecycleEntryDecision))
            return lifecycleEntryResult;
        if (lifecycleEntryDecision.DiscriminatingEvidence == "failed-recovery")
            return ExecuteFailedGoalRecovery(goal, goalPrefix, policy, state);

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
        out FailedGoalFindingObservation decision, out ConductorParallelAcceptanceAttemptDecisionKind? attemptKind,
        ConductorParallelAcceptanceAttempt? existingAttempt = null)
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
            attemptDecision = existingAttempt is not null
                ? _focusedEvidenceAttemptCoordinator.ObserveExistingAttempt(existingAttempt, candidate)
                : _focusedEvidenceAttemptCoordinator.EvaluateFocusedEvidence(
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

    internal bool AcceptanceCohortsEnabled =>
        _runAcceptanceCohortOverride is not null ||
        (_cohortKernel is not null &&
         _cohortWorkspace is not null &&
         _cohortAcceptanceVerifier is not null &&
         _cohortAcceptanceStore is not null);

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

        var currentGoal = goal;
        var execution = DispatchStartExecutor.Execute(
            goal, goalPrefix, policy, fromState, _dispatchAndStart, _startRecordedDispatches,
            _recoverSandboxPrep, RunDispatchRemediation, () => currentGoal = GetCurrentGoal(currentGoal),
            timing =>
            {
                if (timing.Phase == DispatchStartExecutor.PrepPhase)
                    EmitPhaseTiming(timing.Phase, timing.Goal, timing.Outcome!, timing.Elapsed, timing.Detail);
                else
                    EmitGoalPhaseTiming(timing.Phase, timing.Goal, timing.Elapsed, timing.Detail);
            });
        goal = GetCurrentGoal(currentGoal);
        if (execution.RecoveryFailureReason is { } recoveryFailure)
        {
            return Escalate(goal, goalPrefix, policy, fromState, recoveryFailure);
        }
        var outcome = execution.Outcome;

        startFacts = startFacts with { StartOutcomeCategory = outcome.Category.ToString(), StartOutcomeReason = outcome.Reason ?? "" };
        if (outcome.Category == DispatchStartOutcomeCategory.Deferred)
        {
            var deferred = DispatchStartResult(goal, goalPrefix, policy, fromState, startFacts);
            return deferred with { Outcome = ((ConductorAdvanceOutcome.Held)deferred.Outcome) with { Owner = outcome.HoldOwner } };
        }
        if (outcome.Category == DispatchStartOutcomeCategory.Started)
        {
            var startedDecision = DispatchStartPolicy.Evaluate(startFacts);
            if (startedDecision.Action != DispatchStartAction.Proceed)
                throw new InvalidOperationException($"Dispatch start decision for goal {goalPrefix} was {startedDecision.Action}, expected Proceed for a started dispatch.");
            SliceBatchAdmissionEvaluator?.RecordAdmitted(goal);
            return MakeResult(goal.Id.Value, goalPrefix, policy,
                new ConductorAdvanceOutcome.Executed(fromState, "Subscription dispatch started") { Decision = startedDecision.ToRecord() });
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

            if (TryDescribeCancelledReviewerBlocker(goal, out var reviewerBlocker))
                return DispatchStartResult(goal, goalPrefix, policy, fromState,
                    startFacts with { CancelledPredecessorBlocker = reviewerBlocker });

            if (readiness is not DispatchReadinessBlocked { HasCandidates: false })
            {
                return DispatchStartResult(goal, goalPrefix, policy, fromState, startFacts with
                    { AssignedTasksBlockedReason = FormatAssignedTasksBlockedReason(goal, readiness, outcome.Reason) });
            }
        }

        return DispatchStartResult(goal, goalPrefix, policy, fromState, startFacts);
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
