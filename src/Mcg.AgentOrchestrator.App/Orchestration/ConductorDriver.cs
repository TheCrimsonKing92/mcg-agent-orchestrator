using System.Diagnostics;
using Mcg.AgentOrchestrator.App.Dashboard.Api;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed class ConductorDriver
{
    private readonly Func<Goal, GoalLifecycleFacts> _getFacts;
    private readonly Func<int> _getRunningPaidWorkerCount;
    private readonly Func<Goal, string> _createWorkspace;
    private readonly Func<Goal, ConductorAutonomyPolicy, DispatchStartOutcome> _dispatchAndStart;
    private readonly Func<Goal, ConductorAutonomyPolicy, DispatchStartOutcome> _startRecordedDispatches;
    private readonly Action _buildServerShutdown;
    private readonly Func<Goal, AcceptanceVerificationSummary> _runAcceptanceVerification;
    private readonly Func<GoalId, TaskId, string, TaskSpec> _retryTask;
    private readonly Func<GoalId, TaskId, IReadOnlyList<string>, int> _recordCriterionRetryFeedback;
    private readonly Action<GoalId, TaskId> _clearCriterionRetryFeedback;
    private readonly Func<Goal, GoalWorktreeRebaseResult> _rebaseOntoMain;
    private readonly Func<Goal, ConductorAutonomyPolicy, LandingResult> _land;
    private readonly Action<Goal> _record;
    private readonly Action<Goal> _cleanup;
    private readonly Action<Goal, GoalLifecycleState, string> _writeEscalation;
    private readonly Func<Goal, ChangeRiskTier?> _classifyChangeRisk;

    public ConductorDriver(
        AgentOrchestratorKernel kernel,
        OrchestratorWorkspace workspace,
        IGoalAcceptanceVerifier acceptanceVerifier,
        IReadOnlyList<AgentDefinition> agents,
        WorkerProfileCatalog profiles,
        IOperatorChannel? channel = null,
        IModelProviderRegistry? providers = null)
    {
        var dir = workspace.ExecutionDirectory;

        _getFacts = goal =>
        {
            var workspaceExists = GoalWorktrees.TryResolve(dir, goal.Id) is not null;
            var journal = GoalOperationJournal.Read(dir, goal.Id);
            var isMerged = journal.LatestByOperation.Any(e =>
                e.Operation == "conductor:land" && e.Status == GoalOperationStatus.Completed);
            var isRecorded = journal.LatestByOperation.Any(e =>
                e.Operation == "conductor:record" && e.Status == GoalOperationStatus.Completed);
            var isCleanedUp = journal.LatestByOperation.Any(e =>
                e.Operation == "conductor:cleanup" && e.Status == GoalOperationStatus.Completed)
                // A completed goal whose worktree is gone was landed + cleaned up outside the conductor
                // (e.g. via the `acceptance` command, which merges + removes the workspace without
                // writing the conductor journal). Treat it as terminal so the loop doesn't re-run
                // acceptance on a missing worktree and spam ghost escalations every tick.
                || (!workspaceExists && goal.Status == GoalStatus.Completed);
            var hasOpenClarification = GoalRefinementGate.HasOpenClarification(workspace, goal);
            return new GoalLifecycleFacts(workspaceExists, IsBlocked: false, isMerged, isRecorded, isCleanedUp, hasOpenClarification);
        };

        _getRunningPaidWorkerCount = () =>
            kernel.Goals.Sum(g => g.Tasks.Count(t => t.LastProcess is { IsRunning: true }));

        _createWorkspace = goal =>
        {
            GoalOperationJournal.Begin(dir, goal, "conductor:workspace-create", GoalWorktrees.BranchName(goal.Id));
            var path = GoalWorktrees.Ensure(dir, goal.Id);
            GoalOperationJournal.Completed(dir, goal, "conductor:workspace-create", path);
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
                    approveHighRiskOwnership: policy.AllowsAutonomousHighRiskOwnership);
            }
            catch (Exception ex)
            {
                var exceptionReason = $"Subscription dispatch start failed: {ex.Message}";
                GoalOperationJournal.Failed(dir, goal, "conductor:dispatch", exceptionReason);
                return DispatchStartOutcome.SpawnFailed(exceptionReason);
            }
            if (result.Processes.Tasks.Count > 0)
            {
                GoalOperationJournal.Completed(dir, goal, "conductor:dispatch",
                    $"Dispatched {result.Dispatches.Count} tasks, started {result.Processes.Tasks.Count} processes.");
                return DispatchStartOutcome.Started();
            }
            var reason = result.Dispatches.Count == 0
                ? DescribeEmptyBatch(result.ParallelPlan)
                : $"Dispatched {result.Dispatches.Count} task(s) but no processes started (spawn failed)";
            GoalOperationJournal.Failed(dir, goal, "conductor:dispatch", reason);
            return result.Dispatches.Count == 0
                ? DispatchStartOutcome.EmptyBatch(reason)
                : DispatchStartOutcome.SpawnFailed(reason);
        };

        _startRecordedDispatches = (goal, _) =>
        {
            GoalOperationJournal.Begin(dir, goal, "conductor:dispatch-start", "Starting recorded dispatch.");
            ProcessBatchExecutionResult result;
            try
            {
                result = GoalManagementCommandService.StartDispatches(kernel, workspace, goal);
            }
            catch (Exception ex)
            {
                var exceptionReason = $"Recorded dispatch start failed: {ex.Message}";
                GoalOperationJournal.Failed(dir, goal, "conductor:dispatch-start", exceptionReason);
                return DispatchStartOutcome.SpawnFailed(exceptionReason);
            }

            if (result.Tasks.Count > 0)
            {
                GoalOperationJournal.Completed(dir, goal, "conductor:dispatch-start",
                    $"Started {result.Tasks.Count} recorded dispatch process(es).");
                return DispatchStartOutcome.Started();
            }

            var reason = FormatNoRecordedDispatchStartedReason(result.Plan);
            GoalOperationJournal.Failed(dir, goal, "conductor:dispatch-start", reason);
            return DispatchStartOutcome.EmptyBatch(reason);
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

        _runAcceptanceVerification = goal =>
        {
            var worktreePath = GoalWorktrees.TryResolve(dir, goal.Id);
            if (worktreePath is null) return AcceptanceVerificationSummary.Failed;
            GoalOperationJournal.Begin(dir, goal, "conductor:acceptance", "Running acceptance verification.");
            var changedFiles = GoalAcceptanceEvidenceBundleBuilder.GetChangedFiles(worktreePath);
            var verification = acceptanceVerifier.RunAsync(worktreePath, goal.Id, changedFiles).GetAwaiter().GetResult();
            var unmetCriteria = verification.Checks?
                .Where(check => check.Advisory && !check.Passed)
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
                verification.Passed ? null : verification.OutputTail);
        };

        _retryTask = kernel.RetryTask;
        _recordCriterionRetryFeedback = kernel.RecordCriterionRetryFeedback;
        _clearCriterionRetryFeedback = kernel.ClearCriterionRetryFeedback;

        _rebaseOntoMain = goal => GoalWorktrees.TryRebaseOntoMain(dir, goal.Id);

        _land = (goal, policy) =>
        {
            GoalOperationJournal.Begin(dir, goal, "conductor:land", "Landing goal via integration branch.");
            var result = LandingExecutor.Execute(kernel, goal, workspace, channel, policy);
            if (result.MainAdvanced)
                GoalOperationJournal.Completed(dir, goal, "conductor:land", result.Message);
            else
                GoalOperationJournal.Failed(dir, goal, "conductor:land", result.Message);
            return result;
        };

        _record = goal =>
        {
            GoalOperationJournal.Begin(dir, goal, "conductor:record", "Recording to dogfood log.");
            var entry = DogfoodLogRenderer.Render(goal);
            var logPath = Path.Combine(dir, "DOGFOOD_LOG.md");
            if (File.Exists(logPath))
                File.AppendAllText(logPath, Environment.NewLine + Environment.NewLine + entry.Render());
            GoalOperationJournal.Completed(dir, goal, "conductor:record", logPath);
        };

        _cleanup = goal =>
        {
            GoalOperationJournal.Begin(dir, goal, "conductor:cleanup", "Removing goal workspace.");
            var result = GoalWorktrees.Remove(dir, goal.Id, kernel);
            if (result.IsComplete)
                GoalOperationJournal.Completed(dir, goal, "conductor:cleanup", result.Message);
            else
                GoalOperationJournal.Failed(dir, goal, "conductor:cleanup", result.Message);
        };

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
    }

    internal ConductorDriver(
        Func<Goal, GoalLifecycleFacts> getFacts,
        Func<int> getRunningPaidWorkerCount,
        Func<Goal, string> createWorkspace,
        Func<Goal, DispatchStartOutcome> dispatchAndStart,
        Func<Goal, DispatchStartOutcome>? startRecordedDispatches,
        Action? buildServerShutdown,
        Func<Goal, AcceptanceVerificationSummary> runAcceptanceVerification,
        Func<GoalId, TaskId, string, TaskSpec>? retryTask,
        Func<GoalId, TaskId, IReadOnlyList<string>, int>? recordCriterionRetryFeedback,
        Action<GoalId, TaskId>? clearCriterionRetryFeedback,
        Func<Goal, GoalWorktreeRebaseResult> rebaseOntoMain,
        Func<Goal, ConductorAutonomyPolicy, LandingResult> land,
        Action<Goal> record,
        Action<Goal> cleanup,
        Action<Goal, GoalLifecycleState, string> writeEscalation,
        Func<Goal, ChangeRiskTier?> classifyChangeRisk)
    {
        _getFacts = getFacts;
        _getRunningPaidWorkerCount = getRunningPaidWorkerCount;
        _createWorkspace = createWorkspace;
        _dispatchAndStart = (goal, _) => dispatchAndStart(goal);
        _startRecordedDispatches = startRecordedDispatches is null
            ? _dispatchAndStart
            : (goal, _) => startRecordedDispatches(goal);
        _buildServerShutdown = buildServerShutdown ?? (() => { });
        _runAcceptanceVerification = runAcceptanceVerification;
        _retryTask = retryTask ?? ((_, _, _) => throw new InvalidOperationException("Retry delegate was not configured."));
        _recordCriterionRetryFeedback = recordCriterionRetryFeedback ?? ((_, _, _) => throw new InvalidOperationException("Criterion retry feedback delegate was not configured."));
        _clearCriterionRetryFeedback = clearCriterionRetryFeedback ?? ((_, _) => { });
        _rebaseOntoMain = rebaseOntoMain;
        _land = land;
        _record = record;
        _cleanup = cleanup;
        _writeEscalation = writeEscalation;
        _classifyChangeRisk = classifyChangeRisk;
    }

    public ConductorAdvanceResult AdvanceOnce(Goal goal, ConductorAutonomyPolicy policy)
    {
        var goalId = goal.Id.Value;
        var goalPrefix = goalId[..8];

        var facts = GetFacts(goal);
        var state = GoalLifecycle.ResolveState(goal, facts);

        if (state == GoalLifecycleState.CleanedUp)
            return MakeResult(goalId, goalPrefix, policy, new ConductorAdvanceOutcome.Done(state));

        // A goal failed ONLY because a task hit a transient empty-output dispatch flake (the worker
        // exited 0 but produced nothing — a known intermittent claude/codex headless behaviour) is
        // self-healed by re-dispatching that task, bounded by MaxTransientDispatchRetries, instead of
        // escalating to a human. Without this, a single flaky empty response kills an otherwise-healthy
        // goal — exactly what blocked an end-to-end autonomous run. A genuine failure (non-zero exit or
        // any output) is NOT matched here and still escalates.
        if (state == GoalLifecycleState.Failed)
        {
            var flakedTask = goal.Tasks.FirstOrDefault(t =>
                t.Status == WorkTaskStatus.Failed &&
                t.LastVerification is { } latest && DispatchFailureClassifier.IsTransientEmptyOutputDispatchFlake(latest) &&
                t.VerificationHistory.Count(DispatchFailureClassifier.IsTransientEmptyOutputDispatchFlake) <= MaxTransientDispatchRetries);
            if (flakedTask is not null)
            {
                _retryTask(goal.Id, flakedTask.Id,
                    "Auto-retry transient dispatch flake: worker exited 0 with no output (verification could not be confirmed)");
                return MakeResult(goal.Id.Value, goalPrefix, policy,
                    new ConductorAdvanceOutcome.Executed(
                        GoalLifecycleState.Failed,
                        $"Auto-retried transient empty-output dispatch flake on task {flakedTask.Id.Value[..8]}"));
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
                new ConductorAdvanceOutcome.Held(state, "All tasks done; awaiting task verification gates — auto-reconcile will advance goal to Completed")),
            GoalLifecycleState.Verified => ExecuteLanding(goal, goalPrefix, policy),
            GoalLifecycleState.Merged => ExecuteRecord(goal, goalPrefix, policy),
            GoalLifecycleState.Recorded => ExecuteCleanup(goal, goalPrefix, policy),
            _ => Escalate(goal, goalPrefix, policy, state, $"Unhandled lifecycle state {state}")
        };
    }

    internal GoalLifecycleFacts GetFacts(Goal goal) => _getFacts(goal);

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
        if (outcome.Category == DispatchStartOutcomeCategory.SpawnFailed)
        {
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

        return Escalate(goal, goalPrefix, policy, fromState, outcome.Reason!);
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

    // Cap on auto-retrying a transient empty-output dispatch flake before escalating to a human; a
    // worker that keeps exiting 0 with no output is a genuine problem, not a flake.
    private const int MaxTransientDispatchRetries = 2;

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
        // Gate 1: rebase the goal branch onto current main FIRST, so every later gate (acceptance,
        // criteria, landing) operates on the ACTUAL integrated result that will land — not the
        // pre-integration branch. A goal can pass its own tests yet break once integrated with changes
        // that landed meanwhile; verifying the un-rebased branch and only rebasing at the end could
        // land such a textually-clean-but-semantically-broken integration. Rebasing first also avoids
        // a wasted (expensive) acceptance run when the branch cannot integrate at all.
        var rebase = _rebaseOntoMain(goal);
        if (!rebase.UpdatedBranch)
        {
            var rebaseReason = rebase.Status == GoalWorktreeRebaseStatus.Conflict
                ? $"pre-landing rebase conflict ({string.Join(", ", rebase.ConflictFiles)}); use 'workspace rebase' to resolve"
                : $"pre-landing rebase failed: {rebase.Message}";
            return Escalate(goal, goalPrefix, policy, GoalLifecycleState.Verified, rebaseReason);
        }

        // Gate 2: acceptance verification (test suite quality check) on the integrated worktree.
        var acceptance = _runAcceptanceVerification(goal);
        if (!acceptance.Passed)
        {
            return Escalate(goal, goalPrefix, policy, GoalLifecycleState.Verified,
                "Acceptance verification failed; review and fix before landing." +
                FormatFailureTail(acceptance.FailureDetail));
        }

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
                var retryCount = _recordCriterionRetryFeedback(
                    goal.Id,
                    task.Id,
                    acceptance.UnmetCriteria.Select(FormatUnmetCriterion).ToArray());
                var retryMessage = $"Acceptance criteria unmet; retrying task with feedback (attempt {retryCount}/{policy.MaxCriterionRetries}): {criteria}";
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
        _cleanup(goal);
        return MakeResult(goal.Id.Value, goalPrefix, policy,
            new ConductorAdvanceOutcome.Executed(GoalLifecycleState.Recorded, "Workspace cleaned up"));
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
