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
    private static readonly Regex AcceptanceRetryEvidencePattern = new(
        @"error CS\d+|error MSB\d+|\[FAIL\]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly Func<Goal, GoalLifecycleFacts> _getFacts;
    private readonly Func<int> _getRunningPaidWorkerCount;
    private readonly Func<Goal, string> _createWorkspace;
    private readonly Func<Goal, ConductorAutonomyPolicy, DispatchStartOutcome> _dispatchAndStart;
    private readonly Func<Goal, ConductorAutonomyPolicy, DispatchStartOutcome> _startRecordedDispatches;
    private readonly Action _buildServerShutdown;
    private readonly Func<Goal, int?, AcceptanceVerificationSummary> _runAcceptanceVerification;
    private readonly Action<Goal, AcceptanceVerificationSummary> _runAdvisorySemanticAcceptance;
    private readonly Func<GoalId, TaskId, string, TaskSpec> _retryTask;
    private readonly Func<GoalId, TaskId, IReadOnlyList<string>, int> _recordCriterionRetryFeedback;
    private readonly Action<GoalId, TaskId> _clearCriterionRetryFeedback;
    private readonly Action<Goal, IReadOnlyList<string>> _recordAcceptanceFailure;
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
        var factGoalIds = kernel.Goals.Select(goal => goal.Id).ToArray();
        var journalSnapshot = GoalOperationJournal.ReadAll(dir, factGoalIds)
            .ToDictionary(pair => pair.Key, pair => pair.Value);
        var worktreeSnapshot = GoalWorktrees.ResolveAll(dir, factGoalIds)
            .ToDictionary(pair => pair.Key, pair => pair.Value);
        var openClarificationGoalIds = GoalRefinementGate.OpenClarificationGoalIds(workspace, factGoalIds);
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
            if (goal.Status is GoalStatus.Verified or GoalStatus.Completed)
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
            var hasOpenClarification = openClarificationGoalIds.Contains(goal.Id);
            return new GoalLifecycleFacts(workspaceExists, IsBlocked: false, isMerged, isRecorded, isCleanedUp, hasOpenClarification);
        };

        _getRunningPaidWorkerCount = () =>
            kernel.Goals.Sum(g => g.Tasks.Count(t => t.LastProcess is { IsRunning: true }));

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

        _runAcceptanceVerification = (goal, stableSlotIndex) =>
        {
            var worktreePath = GoalWorktrees.TryResolve(dir, goal.Id);
            if (worktreePath is null) return AcceptanceVerificationSummary.Failed;
            var slotSuffix = stableSlotIndex.HasValue ? $" on stable slot {stableSlotIndex.Value}" : string.Empty;
            GoalOperationJournal.Begin(dir, goal, "conductor:acceptance", $"Running acceptance verification{slotSuffix}.");
            var changedFiles = GoalAcceptanceEvidenceBundleBuilder.GetChangedFiles(worktreePath);
            var verification = acceptanceVerifier.RunAsync(worktreePath, goal.Id, changedFiles, stableSlotIndex).GetAwaiter().GetResult();
            var unmetCriteria = verification.Checks?
                .Where(check => check.Advisory && !check.Passed)
                .ToArray() ?? [];
            var failedChecks = verification.Checks?
                .Where(check => !check.Advisory && !check.Passed)
                .Select(check => check.Name)
                .ToArray() ?? [];
            if (verification.Passed)
                GoalOperationJournal.Completed(dir, goal, "conductor:acceptance",
                    unmetCriteria.Length == 0
                        ? $"Acceptance passed (exit {verification.ExitCode})."
                        : $"Acceptance passed (exit {verification.ExitCode}) with {unmetCriteria.Length} unmet advisory criterion/criteria.");
            else
                GoalOperationJournal.Failed(dir, goal, "conductor:acceptance",
                    $"Acceptance failed (exit {verification.ExitCode}).{FormatFailureTail(verification.OutputTail)}");
            return new AcceptanceVerificationSummary(
                verification.Passed,
                unmetCriteria,
                verification.Passed ? null : verification.OutputTail,
                failedChecks);
        };

        _retryTask = (goalId, taskId, message) => kernel.RetryTask(goalId, taskId, message);
        _recordCriterionRetryFeedback = kernel.RecordCriterionRetryFeedback;
        _clearCriterionRetryFeedback = kernel.ClearCriterionRetryFeedback;
        _recordAcceptanceFailure = (goal, failedChecks) => kernel.RecordAcceptanceFailure(goal.Id, failedChecks);
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
            var result = LandingExecutor.Execute(kernel, goal, workspace, channel, policy);
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

            if (goal.SourceBacklogItemId is null)
            {
                return;
            }

            GoalOperationJournal.Begin(dir, goal, "conductor:backlog-close", "Closing linked source backlog item.");
            var closed = GoalLandingPostActions.AutoCloseSourceBacklogItem(goal, workspace.BacklogStorePath, Console.WriteLine);
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
            GoalOperationJournal.Begin(dir, goal, "conductor:cleanup", "Removing goal workspace.");
            var result = GoalWorktrees.Remove(dir, goal.Id, kernel);
            if (result.IsComplete)
                GoalOperationJournal.Completed(dir, goal, "conductor:cleanup", result.Message);
            else
                GoalOperationJournal.Failed(dir, goal, "conductor:cleanup", result.Message);
            if (result.IsComplete)
                worktreeSnapshot.Remove(goal.Id);
            RefreshJournal(goal.Id);
            return result;
        };
        _completeGoal = goal => kernel.CompleteGoal(goal.Id, "Conductor completed goal after durable landing, recording, and cleanup evidence.");

        _writeEscalation = (goal, state, reason) =>
            OperatorInbox.RecordLandingEscalation(workspace, goal, reason, $"conductor:{state}", channel);

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
        Action<Goal, IReadOnlyList<string>>? recordAcceptanceFailure = null,
        Action<Goal>? clearAcceptanceFailure = null,
        Action<Goal>? completeGoal = null,
        Func<Goal, string, bool>? normalizeLifecycleState = null,
        Func<WorkerSandboxPrepRecoverableAction, bool>? recoverSandboxPrep = null,
        Action<Goal, string>? recordMissingBranchRetirement = null,
        Func<Goal, IReadOnlyList<string>>? getLandingFileScopes = null,
        Func<Goal, int?, AcceptanceVerificationSummary>? runAcceptanceVerificationWithSlot = null)
    {
        _getFacts = getFacts;
        _getRunningPaidWorkerCount = getRunningPaidWorkerCount;
        _createWorkspace = createWorkspace;
        _dispatchAndStart = (goal, _) => dispatchAndStart(goal);
        _startRecordedDispatches = startRecordedDispatches is null
            ? _dispatchAndStart
            : (goal, _) => startRecordedDispatches(goal);
        _buildServerShutdown = buildServerShutdown ?? (() => { });
        _runAcceptanceVerification = runAcceptanceVerificationWithSlot ?? ((goal, _) => runAcceptanceVerification(goal));
        _runAdvisorySemanticAcceptance = runAdvisorySemanticAcceptance ?? ((_, _) => { });
        _retryTask = retryTask ?? ((_, _, _) => throw new InvalidOperationException("Retry delegate was not configured."));
        _recordCriterionRetryFeedback = recordCriterionRetryFeedback ?? ((_, _, _) => throw new InvalidOperationException("Criterion retry feedback delegate was not configured."));
        _clearCriterionRetryFeedback = clearCriterionRetryFeedback ?? ((_, _) => { });
        _recordAcceptanceFailure = recordAcceptanceFailure ?? ((_, _) => { });
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
    }

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
            ? DescribeEmptyBatch(result.ParallelPlan)
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
                    _retryTask(goal.Id, staleRecoveryTask.Id, note);
                    return ExecuteDispatchAndStart(goal, goalPrefix, policy, GoalLifecycleState.WorkspaceReady);
                }

                return Escalate(goal, goalPrefix, policy, state,
                    $"Task {staleRecoveryTask.Id.Value[..8]} blocked by stale dispatch recovery; " +
                    ExtractDispatchRecoveryDiagnostic(staleRecoveryTask.LastVerification!));
            }

            var preflightFailedTask = goal.Tasks.FirstOrDefault(t =>
                t.Status == WorkTaskStatus.Failed &&
                t.LastVerification is { } latest &&
                DispatchFailureClassifier.Classify(t, latest).Kind == DispatchOutcomeKind.PreflightFailure);
            if (preflightFailedTask is not null)
            {
                var outcome = DispatchFailureClassifier.Classify(preflightFailedTask, preflightFailedTask.LastVerification!);
                return Escalate(goal, goalPrefix, policy, state,
                    $"Task {preflightFailedTask.Id.Value[..8]} blocked by {outcome.EvidenceSummary}; operator retry required");
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
                _retryTask(goal.Id, flakedTask.Id, note);
                // Immediately dispatch in the same tick after recovery, bypassing the next-tick
                // WorkspaceReady path. If ownership blocks dispatch under Conservative policy,
                // ExecuteDispatchAndStart returns Held (not Escalate) so the goal stays eligible.
                return ExecuteDispatchAndStart(goal, goalPrefix, policy, GoalLifecycleState.WorkspaceReady);
            }
        }

        // Error states always escalate regardless of policy
        if (state is GoalLifecycleState.Failed
                  or GoalLifecycleState.Blocked
                  or GoalLifecycleState.AwaitingClarification
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
            GoalLifecycleState.Verified => ExecuteLanding(goal, goalPrefix, policy),
            GoalLifecycleState.Merged => ExecuteRecord(goal, goalPrefix, policy),
            GoalLifecycleState.Recorded => ExecuteCleanup(goal, goalPrefix, policy),
            _ => Escalate(goal, goalPrefix, policy, state, $"Unhandled lifecycle state {state}")
        };
    }

    internal GoalLifecycleFacts GetFacts(Goal goal) => _getFacts(goal);

    internal ConductorParallelAcceptanceCandidate? TryBuildParallelAcceptanceCandidate(
        Goal goal,
        ConductorAutonomyPolicy policy,
        int slotIndex)
    {
        if (policy.GetTransitionDecision(GoalLifecycleState.Verified) == ConductorTransitionDecision.Escalate)
        {
            return null;
        }

        if (GoalLifecycle.ResolveState(goal, GetFacts(goal)) != GoalLifecycleState.Verified)
        {
            return null;
        }

        return ConductorParallelAcceptanceCandidate.Create(goal, slotIndex, _getLandingFileScopes(goal));
    }

    internal ConductorParallelAcceptanceRunResult RunParallelLandingAcceptance(
        ConductorParallelAcceptanceCandidate candidate,
        ConductorAutonomyPolicy policy)
    {
        try
        {
            var early = RebaseBeforeAcceptance(candidate.Goal, candidate.GoalPrefix, policy);
            if (early is not null)
            {
                return ConductorParallelAcceptanceRunResult.Early(candidate, early);
            }

            return ConductorParallelAcceptanceRunResult.Accepted(
                candidate,
                _runAcceptanceVerification(candidate.Goal, candidate.SlotIndex));
        }
        catch (Exception ex)
        {
            return ConductorParallelAcceptanceRunResult.Fault(candidate, ex);
        }
    }

    internal ConductorAdvanceResult CompleteParallelLandingAcceptance(
        ConductorParallelAcceptanceCandidate candidate,
        ConductorAutonomyPolicy policy,
        AcceptanceVerificationSummary acceptance)
    {
        if (!acceptance.Passed || acceptance.UnmetCriteria.Count > 0)
        {
            return CompleteLandingAfterAcceptance(candidate.Goal, candidate.GoalPrefix, policy, acceptance);
        }

        var rebase = RebaseBeforeMerge(candidate.Goal, candidate.GoalPrefix, policy);
        return rebase ?? CompleteLandingAfterAcceptance(candidate.Goal, candidate.GoalPrefix, policy, acceptance);
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
        if (running >= policy.MaxConcurrentPaidWorkers)
        {
            return MakeResult(goal.Id.Value, goalPrefix, policy,
                new ConductorAdvanceOutcome.Held(fromState,
                    $"At worker cap ({running}/{policy.MaxConcurrentPaidWorkers}); will advance when a slot opens"));
        }

        var start = fromState == GoalLifecycleState.Dispatched ? _startRecordedDispatches : _dispatchAndStart;
        var outcome = start(goal, policy);
        if (outcome.Category == DispatchStartOutcomeCategory.RecoverableSandboxPrep)
        {
            if (!TryRecoverSandboxPrep(outcome, goalPrefix, out var recoveryFailure))
            {
                return Escalate(goal, goalPrefix, policy, fromState, recoveryFailure);
            }

            var retryStart = fromState == GoalLifecycleState.WorkspaceReady
                ? _startRecordedDispatches
                : start;
            outcome = retryStart(goal, policy);
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
            outcome = retryStart(goal, policy);
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
    internal static string DescribeEmptyBatch(ParallelExecutionPlan plan)
    {
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

    private ConductorAdvanceResult ExecuteLanding(Goal goal, string goalPrefix, ConductorAutonomyPolicy policy)
    {
        var early = RebaseBeforeAcceptance(goal, goalPrefix, policy);
        if (early is not null)
        {
            return early;
        }

        // Gate 2: acceptance verification (test suite quality check) on the integrated worktree.
        var acceptance = _runAcceptanceVerification(goal, null);
        return CompleteLandingAfterAcceptance(goal, goalPrefix, policy, acceptance);
    }

    private ConductorAdvanceResult? RebaseBeforeAcceptance(Goal goal, string goalPrefix, ConductorAutonomyPolicy policy)
    {
        // Gate 1: rebase the goal branch onto current main FIRST, so every later gate (acceptance,
        // criteria, landing) operates on the ACTUAL integrated result that will land — not the
        // pre-integration branch. A goal can pass its own tests yet break once integrated with changes
        // that landed meanwhile; verifying the un-rebased branch and only rebasing at the end could
        // land such a textually-clean-but-semantically-broken integration. Rebasing first also avoids
        // a wasted (expensive) acceptance run when the branch cannot integrate at all.
        return RebaseOrRetire(goal, goalPrefix, policy, "pre-landing");
    }

    private ConductorAdvanceResult? RebaseBeforeMerge(Goal goal, string goalPrefix, ConductorAutonomyPolicy policy)
    {
        // In a parallel acceptance batch, a sibling goal may advance main after this goal's
        // acceptance finished. Re-check the branch immediately before the serialized merge.
        return RebaseOrRetire(goal, goalPrefix, policy, "pre-merge");
    }

    private ConductorAdvanceResult? RebaseOrRetire(
        Goal goal,
        string goalPrefix,
        ConductorAutonomyPolicy policy,
        string phase)
    {
        var rebase = _rebaseOntoMain(goal);
        if (rebase.UpdatedBranch)
        {
            return null;
        }

        if (rebase.Status == GoalWorktreeRebaseStatus.MissingBranch)
        {
            var detail = $"Conductor tick retired missing goal branch before landing because the goal artifact could not be rebased: {rebase.Message}";
            _recordMissingBranchRetirement(goal, detail);
            return MakeResult(goal.Id.Value, goalPrefix, policy, new ConductorAdvanceOutcome.Done(GoalLifecycleState.CleanedUp));
        }

        var rebaseReason = rebase.Status == GoalWorktreeRebaseStatus.Conflict
            ? $"{phase} rebase conflict ({string.Join(", ", rebase.ConflictFiles)}); use 'workspace rebase' to resolve"
            : $"{phase} rebase failed: {rebase.Message}";
        return Escalate(goal, goalPrefix, policy, GoalLifecycleState.Verified, rebaseReason);
    }

    private ConductorAdvanceResult CompleteLandingAfterAcceptance(
        Goal goal,
        string goalPrefix,
        ConductorAutonomyPolicy policy,
        AcceptanceVerificationSummary acceptance)
    {
        if (!acceptance.Passed)
        {
            if (acceptance.FailedChecks is { Count: > 0 })
            {
                _recordAcceptanceFailure(goal, acceptance.FailedChecks);
            }

            return Escalate(goal, goalPrefix, policy, GoalLifecycleState.Verified,
                "Acceptance verification failed; review and fix before landing." +
                FormatFailureTail(acceptance.FailureDetail));
        }

        _clearAcceptanceFailure(goal);

        if (acceptance.UnmetCriteria.Count > 0)
        {
            var criteria = FormatUnmetCriteria(acceptance.UnmetCriteria);
            var task = SelectTaskForCriterionRetry(goal);
            if (task is null)
            {
                return Escalate(goal, goalPrefix, policy, GoalLifecycleState.Verified,
                    $"Acceptance criteria unmet but no completed task is available to retry: {criteria}; review/land manually");
            }

            if (task.CriterionRetryCount < policy.MaxCriterionRetries)
            {
                var retryFeedback = FormatCriterionRetryFeedback(acceptance.UnmetCriteria);
                var retryCount = _recordCriterionRetryFeedback(
                    goal.Id,
                    task.Id,
                    retryFeedback);
                var retryMessage = $"Acceptance criteria unmet; retrying task with feedback (attempt {retryCount}/{policy.MaxCriterionRetries}): " +
                    string.Join(Environment.NewLine, retryFeedback);
                _retryTask(goal.Id, task.Id, retryMessage);
                return MakeResult(goal.Id.Value, goalPrefix, policy,
                    new ConductorAdvanceOutcome.Executed(GoalLifecycleState.Verified, retryMessage));
            }

            return Escalate(goal, goalPrefix, policy, GoalLifecycleState.Verified,
                $"Acceptance criteria unmet after {task.CriterionRetryCount} retries: {criteria}; review/land manually");
        }

        foreach (var task in goal.Tasks)
        {
            _clearCriterionRetryFeedback(goal.Id, task.Id);
        }

        // Gate 3: Apply policy AutoPromoteRiskThreshold OVER the engine default — policy can only be stricter.
        var changeRisk = _classifyChangeRisk(goal);
        if (changeRisk.HasValue)
        {
            var policyAtMerged = policy.GetTransitionDecision(GoalLifecycleState.Merged, changeRisk.Value);
            if (policyAtMerged == ConductorTransitionDecision.Escalate)
            {
                return Escalate(goal, goalPrefix, policy, GoalLifecycleState.Verified,
                    $"Policy '{policy.Name}' restricts auto-promotion for {changeRisk.Value} risk; use 'land' after review");
            }
        }

        // Gate 4: land via integration branch (the branch is already rebased onto main by Gate 1).
        var landResult = _land(goal, policy);
        if (landResult.Decision is LandingDecision.Escalate escalate)
        {
            return Escalate(goal, goalPrefix, policy, GoalLifecycleState.Verified, escalate.Reason);
        }

        if (landResult.MainAdvanced)
        {
            // Gate 5: advisory semantic acceptance runs only after deterministic acceptance and
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
        var cleanup = _cleanup(goal);
        if (!cleanup.IsComplete)
        {
            return MakeResult(goal.Id.Value, goalPrefix, policy,
                new ConductorAdvanceOutcome.Held(
                    GoalLifecycleState.Recorded,
                    $"Workspace cleanup deferred; retry later. {FormatCleanupDiagnostic(cleanup)}"));
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
