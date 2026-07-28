using System.Diagnostics;
using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.App.Dashboard.Api;
using Mcg.AgentOrchestrator.App.SubscriptionPlanning;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed class ConductorDriver
{
    private const int MaxCriterionRetryEvidenceLines = 30;

    // Reviewer evidence-on-demand is bounded per review round to break request loops while still
    // letting a reviewer legitimately request focused receipts for more than one changed area
    // (multi-file infra goals commonly need 2-3 suites). The guard previously allowed exactly one
    // request across the whole reviewer phase, which escalated a legitimate second suite as a
    // "repeat" (the mechanical evidence re-dispatch retries the reviewer task itself, so it never
    // advances the round boundary). Requests beyond this bound escalate normally.
    private const int MaxReviewerEvidenceRequestsPerRound = 3;

    // Prefix of the message the conductor writes when it mechanically re-dispatches the reviewer
    // task to attach evidence-on-demand receipts within the SAME round. Retries carrying this prefix
    // must NOT advance the evidence-round boundary (otherwise the per-round bound would never apply);
    // any other reviewer retry (operator recover, fresh review) begins a new evidence round.
    private const string ReviewerEvidenceRetryMessagePrefix = "reviewer evidence-on-demand:";
    private static readonly Regex AcceptanceRetryEvidencePattern = new(
        @"error CS\d+|error MSB\d+|\[FAIL\]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly Func<Goal, GoalLifecycleFacts> _getFacts;
    private readonly Func<int> _getRunningPaidWorkerCount;
    private readonly Func<Goal, string> _createWorkspace;
    private readonly Func<Goal, ConductorAutonomyPolicy, DispatchStartOutcome> _dispatchAndStart;
    private readonly Func<Goal, ConductorAutonomyPolicy, DispatchStartOutcome> _startRecordedDispatches;
    private readonly Action _buildServerShutdown;
    private readonly Func<Goal, int?, DotnetBuildEnvironmentLease?, CancellationToken, AcceptanceVerificationSummary> _runAcceptanceVerification;
    private readonly Action<Goal, AcceptanceVerificationSummary> _runAdvisorySemanticAcceptance;
    private readonly Func<Goal, string, FocusedEvidenceRunResult> _runFocusedEvidence;
    private readonly Func<GoalId, TaskId, string, RetryRoundKind?, TaskSpec> _retryTask;
    private readonly Action<GoalId, TaskId, string> _recordTaskNote;
    private readonly Action<GoalId, TaskId, string> _recordReviewerEvidenceRequestReceived;
    private readonly Action<GoalId, TaskId, string> _recordReviewerEvidenceRunRecorded;
    private readonly Func<GoalId, TaskId, IReadOnlyList<string>, int> _recordCriterionRetryFeedback;
    private readonly Action<GoalId, TaskId> _clearCriterionRetryFeedback;
    private readonly Action<Goal, IReadOnlyList<string>, string?, string?> _recordAcceptanceFailure;
    private readonly Action<Goal> _clearAcceptanceFailure;
    private readonly Func<Goal, GoalWorktreeRebaseResult> _rebaseOntoMain;
    private readonly Func<Goal, ConductorAutonomyPolicy, LandingResult> _land;
    private readonly Action<Goal, LandingResult> _afterSuccessfulLanding;
    private readonly Action<Goal> _record;
    private readonly Func<Goal, GoalWorktreeRemoveResult> _cleanup;
    private readonly Action<Goal> _completeGoal;
    private readonly Action<Goal, GoalLifecycleState, string> _writeEscalation;
    private readonly Func<Goal, ChangeRiskTier?> _classifyChangeRisk;
    private readonly Action<TimeSpan> _emptyOutputBackoffDelay;
    private readonly Func<Goal, DispatchReadinessVerdict> _evaluateReadiness;
    private readonly Func<Goal, string, bool> _normalizeLifecycleState;
    private readonly Func<WorkerSandboxPrepRecoverableAction, bool> _recoverSandboxPrep;
    private readonly Action<Goal, string> _recordMissingBranchRetirement;
    private readonly Func<Goal, IReadOnlyList<string>> _getLandingFileScopes;
    private readonly Func<Goal, int> _getAcceptanceSlotCount;
    private readonly Func<int> _getWorkerAdmissionCapacity;
    private readonly Func<bool> _hasGateReadyGoal;
    private readonly Func<Goal, string?> _tryBuildAwaitingClarificationEscalationReason;
    private readonly string? _executionDirectory;
    private readonly ConductorParallelAcceptanceAttemptCoordinator _parallelAcceptanceAttemptCoordinator;
    private readonly bool _parallelAcceptanceEnabled;

    internal Action<string>? PhaseTimingSink { get; set; }
    internal Action<ConductorLandingReceipt>? SuccessfulLandingSink { get; set; }

    public ConductorDriver(
        AgentOrchestratorKernel kernel,
        OrchestratorWorkspace workspace,
        IGoalAcceptanceVerifier acceptanceVerifier,
        IReadOnlyList<AgentDefinition> agents,
        WorkerProfileCatalog profiles,
        IOperatorChannel? channel = null,
        IModelProviderRegistry? providers = null,
        Action<AgentOrchestratorKernel, IReadOnlyCollection<GoalId>>? persistCriticalDispatchStart = null)
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
        var eventWriter = new GoalLifecycleEventWriter(workspace.GoalLifecycleEventsDirectory);
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
            GoalOperationJournal.Begin(dir, goal, "conductor:workspace-create", GoalWorktrees.BranchName(goal.Id));
            var path = GoalWorktrees.Ensure(dir, goal.Id);
            GoalOperationJournal.Completed(dir, goal, "conductor:workspace-create", path);
            worktreeSnapshot[goal.Id] = path;
            RefreshJournal(goal.Id);
            return path;
        };

        _dispatchAndStart = (goal, policy) =>
        {
            GoalOperationJournal.Begin(dir, goal, "conductor:dispatch", "Starting subscription dispatch.");
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
                        : (checkpointKernel, goalId, taskId) =>
                            ConductorBatchLoop.PersistCriticalDispatchStartOrThrow(
                                persistCriticalDispatchStart,
                                checkpointKernel,
                                goalId,
                                taskId));
            }
            catch (Exception ex)
            {
                if (IsCriticalDispatchRecordWriteFailure(ex))
                    throw;

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

        _startRecordedDispatches = (goal, _) =>
        {
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
                        : (checkpointKernel, goalId, taskId) =>
                            ConductorBatchLoop.PersistCriticalDispatchStartOrThrow(
                                persistCriticalDispatchStart,
                                checkpointKernel,
                                goalId,
                                taskId));
            }
            catch (Exception ex)
            {
                if (IsCriticalDispatchRecordWriteFailure(ex))
                    throw;

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

        _buildServerShutdown = () =>
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
                    WorkingDirectory = dir
                };
                startInfo.ArgumentList.Add("build-server");
                startInfo.ArgumentList.Add("shutdown");
                using var process = Process.Start(startInfo);
                if (process is null) return;
                process.StandardOutput.ReadToEnd();
                process.StandardError.ReadToEnd();
                process.WaitForExit(30_000);
            }
            catch { }
        };

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
                    GoalOperationJournal.TryExtractBaseBuildCacheReceipt(verification));
            else
                GoalOperationJournal.AcceptanceFailed(dir, goal, "conductor:acceptance", branchHeadSha, mainHeadSha,
                    $"Acceptance failed for candidate {FormatAcceptanceCandidate(branchHeadSha, mainHeadSha)} (exit {verification.ExitCode}).{FormatFailureTail(verification.OutputTail)}",
                    acceptanceAttemptStartedAt,
                    GoalOperationJournal.TryExtractBaseBuildCacheReceipt(verification));
            return new AcceptanceVerificationSummary(
                verification.Passed,
                unmetCriteria,
                verification.Passed ? null : verification.OutputTail,
                failedChecks,
                branchHeadSha,
                mainHeadSha,
                testResultPaths);
        };

        _runFocusedEvidence = (goal, request) =>
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

            GoalOperationJournal.Begin(dir, goal, "conductor:reviewer-evidence", $"Running focused reviewer evidence: {request}");
            var result = acceptanceVerifier.RunFocusedEvidenceAsync(worktreePath, goal.Id, request).GetAwaiter().GetResult();
            if (result.Accepted && result.Passed)
            {
                GoalOperationJournal.Completed(dir, goal, "conductor:reviewer-evidence", result.Summary);
            }
            else
            {
                GoalOperationJournal.Failed(dir, goal, "conductor:reviewer-evidence", result.Summary);
            }

            return result;
        };

        _retryTask = (goalId, taskId, message, retryRoundKind) =>
            kernel.RetryTask(goalId, taskId, message, retryRoundKind: retryRoundKind);
        _recordTaskNote = (goalId, taskId, message) =>
        {
            kernel.RecordTaskNote(goalId, taskId, message);
        };
        _recordReviewerEvidenceRequestReceived = (goalId, taskId, message) =>
            kernel.RecordReviewerEvidenceRequestReceived(goalId, taskId, message);
        _recordReviewerEvidenceRunRecorded = (goalId, taskId, message) =>
            kernel.RecordReviewerEvidenceRunRecorded(goalId, taskId, message);
        _recordCriterionRetryFeedback = kernel.RecordCriterionRetryFeedback;
        _clearCriterionRetryFeedback = kernel.ClearCriterionRetryFeedback;
        _recordAcceptanceFailure = (goal, failedChecks, branchHeadSha, mainHeadSha) =>
            kernel.RecordAcceptanceFailure(goal.Id, failedChecks, branchHeadSha, mainHeadSha);
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

        _rebaseOntoMain = goal => GoalWorktrees.TryRebaseOntoMain(dir, goal.Id);

        _land = (goal, policy) =>
        {
            GoalOperationJournal.Begin(dir, goal, "conductor:land", "Landing goal via integration branch.");
            var result = LandingExecutor.Execute(
                kernel,
                goal,
                workspace,
                channel,
                policy,
                eventWriter);
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
                dir);
            GoalOperationJournal.Completed(dir, goal, "conductor:backlog-close",
                closed ? "Closed linked source backlog item." : "No linked source backlog item closed.");
            RefreshJournal(goal.Id);
        };

        _record = goal =>
        {
            GoalOperationJournal.Begin(dir, goal, "conductor:record", "Recording to SQLite dogfood log.");
            var entry = DogfoodLogRenderer.Render(goal);
            new DogfoodLogStore(workspace.DogfoodLogStorePath)
                .UpsertAsync(new DogfoodLogAppend(
                    goal.Id.Value,
                    entry.Header,
                    entry.Summary,
                    entry.OperatorGate,
                    entry.ModelFit,
                    entry.Render()))
                .GetAwaiter()
                .GetResult();
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
            OperatorInbox.RecordLandingEscalation(workspace, goal, reason, source, channel);
            eventWriter.AppendGoalEscalated(goal.Id, state, reason, source);
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
        Func<int>? getWorkerAdmissionCapacity = null)
    {
        _getFacts = getFacts;
        _getRunningPaidWorkerCount = getRunningPaidWorkerCount;
        _createWorkspace = createWorkspace;
        _dispatchAndStart = (goal, _) => dispatchAndStart(goal);
        _startRecordedDispatches = startRecordedDispatches is null
            ? _dispatchAndStart
            : (goal, _) => startRecordedDispatches(goal);
        _buildServerShutdown = buildServerShutdown ?? (() => { });
        _runAcceptanceVerification = runAcceptanceVerificationWithLease
            ?? (runAcceptanceVerificationWithSlot is not null
                ? ((goal, slot, _, _) => runAcceptanceVerificationWithSlot(goal, slot))
                : ((goal, _, _, _) => runAcceptanceVerification(goal)));
        _runAdvisorySemanticAcceptance = runAdvisorySemanticAcceptance ?? ((_, _) => { });
        _runFocusedEvidence = runFocusedEvidence ?? ((_, request) => new FocusedEvidenceRunResult(
            request,
            Accepted: false,
            Passed: false,
            Summary: "focused evidence runner was not configured",
            Checks: []));
        _retryTask = retryTaskWithRoundKind
            ?? (retryTask is not null
                ? ((goalId, taskId, message, _) => retryTask(goalId, taskId, message))
                : ((_, _, _, _) => throw new InvalidOperationException("Retry delegate was not configured.")));
        _recordTaskNote = recordTaskNote ?? ((_, _, _) => { });
        _recordReviewerEvidenceRequestReceived = recordReviewerEvidenceRequestReceived ?? ((_, _, _) => { });
        _recordReviewerEvidenceRunRecorded = recordReviewerEvidenceRunRecorded ?? ((_, _, _) => { });
        _recordCriterionRetryFeedback = recordCriterionRetryFeedback ?? ((_, _, _) => throw new InvalidOperationException("Criterion retry feedback delegate was not configured."));
        _clearCriterionRetryFeedback = clearCriterionRetryFeedback ?? ((_, _) => { });
        _recordAcceptanceFailure = recordAcceptanceFailure ?? ((_, _, _, _) => { });
        _clearAcceptanceFailure = clearAcceptanceFailure ?? (_ => { });
        _rebaseOntoMain = rebaseOntoMain;
        _land = land;
        _afterSuccessfulLanding = afterSuccessfulLanding ?? ((_, _) => { });
        _record = record;
        _cleanup = cleanup;
        _completeGoal = completeGoal ?? (_ => { });
        _writeEscalation = writeEscalation;
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
        _getAcceptanceSlotCount = getAcceptanceSlotCount ?? (_ => ConductorBatchLoop.DefaultParallelAcceptanceCapacity);
        _getWorkerAdmissionCapacity = getWorkerAdmissionCapacity ?? (() => ConductorBatchLoop.WorkerAdmissionCapacity);
        _hasGateReadyGoal = hasGateReadyGoal ?? (() => false);
        _tryBuildAwaitingClarificationEscalationReason =
            tryBuildAwaitingClarificationEscalationReason ?? (_ => null);
        _executionDirectory = null;
        _parallelAcceptanceEnabled =
            runAcceptanceVerificationWithSlot is not null ||
            runAcceptanceVerificationWithLease is not null;
        _parallelAcceptanceAttemptCoordinator = parallelAcceptanceAttemptCoordinator
            ?? new ConductorParallelAcceptanceAttemptCoordinator(
                Path.Combine(Path.GetTempPath(), "mcg-conductor-acceptance-attempts", Guid.NewGuid().ToString("N")),
                runInline: true);
    }

    internal ConductorParallelAcceptanceAttemptCoordinator ParallelAcceptanceAttemptCoordinator =>
        _parallelAcceptanceAttemptCoordinator;

    internal string? ExecutionDirectory => _executionDirectory;

    internal bool ParallelAcceptanceEnabled => _parallelAcceptanceEnabled;

    internal int GetAcceptanceSlotCount(Goal goal) => _getAcceptanceSlotCount(goal);

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

        var reason = result.Dispatches.Count == 0
            ? DescribeEmptyBatch(result.ParallelPlan, result.BlockedDiagnostics)
            : $"Dispatched {result.Dispatches.Count} task(s) but no processes started (spawn failed)";
        return result.Dispatches.Count == 0
            ? DispatchStartOutcome.EmptyBatch(reason)
            : DispatchStartOutcome.SpawnFailed(reason);
    }

    internal static bool IsCriticalDispatchRecordWriteFailure(Exception ex) =>
        ex.Message.Contains("DISPATCH_RECORD_WRITE_FAILED", StringComparison.Ordinal);

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

            var flakedTask = goal.Tasks.FirstOrDefault(t =>
                t.Status == WorkTaskStatus.Failed &&
                t.LastVerification is { } latest && DispatchFailureClassifier.Classify(t, latest).Kind == DispatchOutcomeKind.EmptyOutputFlake &&
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
                var note = attemptInCycle == policy.MaxEmptyOutputDispatchRetries
                    ? $"Auto-recover+re-admit empty-output dispatch flake cycle {cycle}/{policy.MaxEmptyOutputAutoRecoverCycles}; " +
                        $"task produced zero-byte stdout with exit {flakedTask.LastVerification!.ExitCode}"
                    : $"Auto-retry empty-output dispatch flake {attemptInCycle}/{policy.MaxEmptyOutputDispatchRetries} " +
                        $"in recovery cycle {cycle}/{policy.MaxEmptyOutputAutoRecoverCycles}; " +
                        $"task produced zero-byte stdout with exit {flakedTask.LastVerification!.ExitCode}";
                _retryTask(goal.Id, flakedTask.Id, note, null);
                // Immediately dispatch in the same tick after recovery, bypassing the next-tick
                // WorkspaceReady path. If ownership blocks dispatch under Conservative policy,
                // ExecuteDispatchAndStart returns Held (not Escalate) so the goal stays eligible.
                return ExecuteDispatchAndStart(goal, goalPrefix, policy, GoalLifecycleState.WorkspaceReady);
            }

            if (TryBuildVerifyingFindingAutoRetry(goal, policy, out var autoRetry))
            {
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
                $"Goal is in {state} state; operator action required");
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

    private bool TryBuildVerifyingFindingAutoRetry(
        Goal goal,
        ConductorAutonomyPolicy policy,
        out VerifyingFindingAutoRetryDecision decision)
    {
        decision = VerifyingFindingAutoRetryDecision.None;
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

        if (triggeringTask.RequiredRole == AgentRole.Reviewer &&
            WorkerResultBlockers.TryFindEvidenceRequest(triggeringTask.LastVerification, out var evidenceRequest))
        {
            var priorEvidenceRequests = CountReviewerEvidenceRequestsInCurrentRound(goal, triggeringTask);
            _recordReviewerEvidenceRequestReceived(
                goal.Id,
                triggeringTask.Id,
                $"Reviewer evidence request received: {evidenceRequest}. Full reviewer output: {outputArtifact}");

            if (priorEvidenceRequests >= MaxReviewerEvidenceRequestsPerRound)
            {
                decision = VerifyingFindingAutoRetryDecision.Escalate(
                    $"Reviewer exceeded the evidence-on-demand limit ({MaxReviewerEvidenceRequestsPerRound} focused runs) in the same review round for task {triggeringTask.Id.Value[..8]}; " +
                    $"normal escalation required. Request: {TrimForConductorMessage(evidenceRequest)}. Full reviewer output: {outputArtifact}");
                return true;
            }

            var evidence = _runFocusedEvidence(goal, evidenceRequest);
            var evidenceMessage = FormatFocusedEvidenceResult(evidence);
            _recordReviewerEvidenceRunRecorded(goal.Id, triggeringTask.Id, evidenceMessage);

            if (!evidence.Accepted)
            {
                decision = VerifyingFindingAutoRetryDecision.Escalate(
                    $"Reviewer evidence request rejected for task {triggeringTask.Id.Value[..8]}; normal escalation required. " +
                    $"{evidenceMessage}. Full reviewer output: {outputArtifact}");
                return true;
            }

            if (!evidence.Passed)
            {
                decision = VerifyingFindingAutoRetryDecision.Escalate(
                    $"Reviewer requested focused evidence failed for task {triggeringTask.Id.Value[..8]}; normal escalation required. " +
                    $"{evidenceMessage}. Full reviewer output: {outputArtifact}");
                return true;
            }

            var evidenceRetryMessage =
                $"{ReviewerEvidenceRetryMessagePrefix} Reviewer task {triggeringTask.Id.Value[..8]} requested focused test evidence; " +
                $"conductor ran it without reopening upstream Developer/Tester work. {evidenceMessage}. " +
                $"Re-review the same round using these receipts.";
            decision = VerifyingFindingAutoRetryDecision.Retry(
                triggeringTask,
                evidenceRetryMessage,
                null,
                RetryRoundKind.Mechanical);
            return true;
        }

        if (triggeringTask.RequiredRole == AgentRole.Reviewer &&
            IsOperatorOwnedReviewBlocker(trigger.Finding))
        {
            decision = VerifyingFindingAutoRetryDecision.Escalate(
                $"Reviewer needs-work blocker requires operator-owned evidence; auto-review-retry skipped for task {triggeringTask.Id.Value[..8]}. " +
                $"Findings: {TrimForConductorMessage(trigger.Finding)}. Full reviewer output: {outputArtifact}");
            return true;
        }

        var targetRole = triggeringTask.RequiredRole == AgentRole.Tester
            ? AgentRole.Developer
            : InferReviewRetryTargetRole(trigger.Finding);
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

        var round = CountPriorAutoReviewRetries(goal) + 1;
        if (round >= policy.ReviewAutoRetryStopRound)
        {
            decision = VerifyingFindingAutoRetryDecision.Escalate(
                $"auto-review-retry stopped at review round {round}/{policy.ReviewAutoRetryStopRound} for task {targetTask.Id.Value[..8]}; " +
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

        if (task.RequiredRole == AgentRole.Reviewer &&
            WorkerResultBlockers.TryFindUnsuppressedNeedsWorkVerdict(
                task.LastVerification,
                goal.EffectiveAcceptanceCriteriaCorrections,
                out var blocker,
                out var suppressedFindings))
        {
            return new VerifyingFindingTrigger(task, blocker, suppressedFindings, null);
        }

        if (task.RequiredRole != AgentRole.Tester ||
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

    private static AgentRole InferReviewRetryTargetRole(string blocker)
    {
        var text = blocker.ToLowerInvariant();
        return text.Contains("tester", StringComparison.Ordinal) ||
            text.Contains("test-execution", StringComparison.Ordinal) ||
            text.Contains("test execution", StringComparison.Ordinal) ||
            text.Contains("test receipt", StringComparison.Ordinal) ||
            text.Contains("verification command", StringComparison.Ordinal)
            ? AgentRole.Tester
            : AgentRole.Developer;
    }

    private static bool IsOperatorOwnedReviewBlocker(string blocker)
    {
        var text = blocker.ToLowerInvariant();
        return text.Contains("operator-owned", StringComparison.Ordinal) ||
            text.Contains("operator owned", StringComparison.Ordinal) ||
            text.Contains("operator receipt", StringComparison.Ordinal) ||
            text.Contains("operator receipts", StringComparison.Ordinal) ||
            text.Contains("measurement mandate", StringComparison.Ordinal) ||
            text.Contains("measurement mandates", StringComparison.Ordinal) ||
            text.Contains("operator evidence", StringComparison.Ordinal) ||
            text.Contains("human_input", StringComparison.Ordinal) ||
            text.Contains("human input", StringComparison.Ordinal);
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

    private static int CountPriorAutoReviewRetries(Goal goal) =>
        goal.Timeline.Count(evt =>
            evt.Kind == ProgressKind.TaskRetried &&
            evt.Message.Contains("auto-review-retry", StringComparison.OrdinalIgnoreCase));

    private static int CountReviewerEvidenceRequestsInCurrentRound(Goal goal, TaskSpec reviewerTask)
    {
        // The current evidence round starts at the most recent retry that begins a FRESH review:
        // any non-reviewer (Developer/Tester) retry, or a non-mechanical retry of the reviewer task
        // itself (operator recover / fresh review). The mechanical evidence re-dispatch retries the
        // reviewer task only to attach receipts within the SAME round, so it must not advance the boundary.
        var currentRoundStartedAt = goal.Timeline
            .Where(evt =>
                evt.Kind == ProgressKind.TaskRetried &&
                evt.TaskId is not null &&
                (evt.TaskId != reviewerTask.Id ||
                    !evt.Message.StartsWith(ReviewerEvidenceRetryMessagePrefix, StringComparison.Ordinal)))
            .Select(evt => evt.OccurredAt)
            .DefaultIfEmpty(DateTimeOffset.MinValue)
            .Max();

        return goal.Timeline.Count(evt =>
            evt.TaskId == reviewerTask.Id &&
            evt.Kind == ProgressKind.ReviewerEvidenceRequestReceived &&
            evt.OccurredAt >= currentRoundStartedAt);
    }

    private static string FormatFocusedEvidenceResult(FocusedEvidenceRunResult evidence)
    {
        var checks = evidence.Checks.Count == 0
            ? "checks: none"
            : "checks: " + string.Join("; ", evidence.Checks.Select(FormatFocusedEvidenceCheck));
        return $"request='{TrimForConductorMessage(evidence.Request)}'; accepted={evidence.Accepted}; passed={evidence.Passed}; " +
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
        return $"{check.Name} passed={check.Passed} exit={check.ExitCode} {receipt}{summary}";
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
        bool ShouldEscalate,
        TaskSpec? TargetTask,
        string Message,
        string? WarningMessage,
        RetryRoundKind? RoundKind)
    {
        public static VerifyingFindingAutoRetryDecision None { get; } = new(false, null, string.Empty, null, null);

        public static VerifyingFindingAutoRetryDecision Retry(
            TaskSpec targetTask,
            string message,
            string? warningMessage,
            RetryRoundKind? roundKind = null) =>
            new(false, targetTask, message, warningMessage, roundKind);

        public static VerifyingFindingAutoRetryDecision Escalate(string message) =>
            new(true, null, message, null, null);
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

    internal ConductorParallelAcceptanceRunResult RunParallelLandingAcceptance(
        ConductorParallelAcceptanceCandidate candidate,
        ConductorAutonomyPolicy policy,
        DotnetBuildEnvironmentLease? stableSlotLease,
        CancellationToken cancellationToken)
    {
        var effectiveCandidate = candidate;
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

    private static bool IsAcceptanceAttemptCancelled(OrchestratorWorkspace workspace, GoalId goalId)
    {
        try
        {
            var latest = new SqliteOrchestratorStateRepository(workspace.SqliteStatePath)
                .LoadAsync()
                .GetAwaiter()
                .GetResult()
                .Goals
                .FirstOrDefault(goal => goal.Id == goalId);
            return latest is null ||
                latest.Status is GoalStatus.Parked or GoalStatus.AcceptanceFailed or GoalStatus.Cancelled or GoalStatus.Superseded or GoalStatus.Failed;
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

    private static AcceptanceVerificationSummary NormalizeNamedFailedChecksForRetry(AcceptanceVerificationSummary acceptance)
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
            acceptance.MainHeadSha);
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
        var path = _createWorkspace(goal);
        return MakeResult(goal.Id.Value, goalPrefix, policy,
            new ConductorAdvanceOutcome.Executed(GoalLifecycleState.Created, $"Workspace created: {path}"));
    }

    private ConductorAdvanceResult ExecuteDispatchAndStart(
        Goal goal,
        string goalPrefix,
        ConductorAutonomyPolicy policy,
        GoalLifecycleState fromState)
    {
        var running = _getRunningPaidWorkerCount();
        var workerCap = policy.MaxConcurrentPaidWorkers;
        if (_hasGateReadyGoal())
        {
            // Reserve one paid-worker admission slot for the ready gate. The pool this draws
            // from is the worker-admission capacity, deliberately decoupled from the
            // parallel-acceptance width / build-concurrency slot count so a running gate does
            // not starve paid-worker admission down to 1.
            workerCap = Math.Min(workerCap, Math.Max(0, _getWorkerAdmissionCapacity() - 1));
        }

        if (running >= workerCap)
        {
            var reservedGateSlot = workerCap < policy.MaxConcurrentPaidWorkers;
            if (reservedGateSlot)
            {
                Console.WriteLine(
                    $"ADMISSION goal={goalPrefix} result=deferred reason=reserved-gate-slot cap={workerCap} running={running}");
            }

            EmitPhaseTiming("dispatch-prep", goal, TimeSpan.Zero, $"tasks={CountAssignedTasks(goal)} result=held-cap running={running}");
            return MakeResult(goal.Id.Value, goalPrefix, policy,
                new ConductorAdvanceOutcome.Held(fromState,
                    reservedGateSlot
                        ? $"At worker cap ({running}/{workerCap}) with a gate-ready goal reserving a stable slot; will advance when a slot opens"
                        : $"At worker cap ({running}/{policy.MaxConcurrentPaidWorkers}); will advance when a slot opens"));
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
            if (outcome.Reason?.Contains("DISPATCH_RECORD_WRITE_FAILED", StringComparison.Ordinal) == true)
            {
                return Escalate(goal, goalPrefix, policy, fromState, outcome.Reason);
            }

            var firstFailure = outcome;
            _buildServerShutdown();
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

            if (readiness is not DispatchReadinessBlocked { HasCandidates: false })
            {
                return MakeResult(goal.Id.Value, goalPrefix, policy,
                    new ConductorAdvanceOutcome.Held(fromState,
                        FormatAssignedTasksBlockedReason(goal, readiness, outcome.Reason)));
            }
        }

        return Escalate(goal, goalPrefix, policy, fromState, outcome.Reason!);
    }

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
                _recordAcceptanceFailure(goal, failedChecks, acceptance.BranchHeadSha, acceptance.MainHeadSha);
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
        var landResult = _land(goal, policy);
        if (landResult.Decision is LandingDecision.Escalate escalate)
        {
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
                landingFileScopes));

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
        _writeEscalation(goal, state, reason);
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

internal sealed record ConductorLandingReceipt(
    string GoalId,
    IReadOnlyList<string> ChangedFiles);
