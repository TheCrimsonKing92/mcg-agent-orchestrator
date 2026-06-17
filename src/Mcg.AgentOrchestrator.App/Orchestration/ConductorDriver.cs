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
    private readonly Func<Goal, bool> _dispatchAndStart;
    private readonly Func<Goal, bool> _runAcceptanceVerification;
    private readonly Func<Goal, LandingResult> _land;
    private readonly Action<Goal> _record;
    private readonly Action<Goal> _cleanup;
    private readonly Action<Goal, GoalLifecycleState, string> _writeEscalation;
    private readonly Func<Goal, ChangeRiskTier?> _classifyChangeRisk;

    public ConductorDriver(
        AgentOrchestratorKernel kernel,
        OrchestratorWorkspace workspace,
        GoalAcceptanceVerifier acceptanceVerifier,
        IReadOnlyList<AgentDefinition> agents,
        WorkerProfileCatalog profiles)
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
                e.Operation == "conductor:cleanup" && e.Status == GoalOperationStatus.Completed);
            return new GoalLifecycleFacts(workspaceExists, IsBlocked: false, isMerged, isRecorded, isCleanedUp);
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

        _dispatchAndStart = goal =>
        {
            GoalOperationJournal.Begin(dir, goal, "conductor:dispatch", "Starting subscription dispatch.");
            var result = GoalManagementCommandService.StartSubscriptionReadyTasks(kernel, workspace, goal, agents, profiles);
            var started = result.Processes.Tasks.Count > 0;
            if (started)
                GoalOperationJournal.Completed(dir, goal, "conductor:dispatch",
                    $"Dispatched {result.Dispatches.Count} tasks, started {result.Processes.Tasks.Count} processes.");
            else
                GoalOperationJournal.Failed(dir, goal, "conductor:dispatch", "No tasks dispatched.");
            return started;
        };

        _runAcceptanceVerification = goal =>
        {
            var worktreePath = GoalWorktrees.TryResolve(dir, goal.Id);
            if (worktreePath is null) return false;
            GoalOperationJournal.Begin(dir, goal, "conductor:acceptance", "Running acceptance verification.");
            var changedFiles = GoalAcceptanceEvidenceBundleBuilder.GetChangedFiles(worktreePath);
            var verification = acceptanceVerifier.RunAsync(worktreePath, goal.Id, changedFiles).GetAwaiter().GetResult();
            if (verification.Passed)
                GoalOperationJournal.Completed(dir, goal, "conductor:acceptance",
                    $"Acceptance passed (exit {verification.ExitCode}).");
            else
                GoalOperationJournal.Failed(dir, goal, "conductor:acceptance",
                    $"Acceptance failed (exit {verification.ExitCode}).");
            return verification.Passed;
        };

        _land = goal =>
        {
            GoalOperationJournal.Begin(dir, goal, "conductor:land", "Landing goal via integration branch.");
            var result = LandingExecutor.Execute(kernel, goal, workspace);
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
            var result = GoalWorktrees.Remove(dir, goal.Id);
            if (result.IsComplete)
                GoalOperationJournal.Completed(dir, goal, "conductor:cleanup", result.Message);
            else
                GoalOperationJournal.Failed(dir, goal, "conductor:cleanup", result.Message);
        };

        _writeEscalation = (goal, state, reason) =>
            OperatorInbox.RecordLandingEscalation(workspace, goal, reason, $"conductor:{state}");

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
        Func<Goal, bool> dispatchAndStart,
        Func<Goal, bool> runAcceptanceVerification,
        Func<Goal, LandingResult> land,
        Action<Goal> record,
        Action<Goal> cleanup,
        Action<Goal, GoalLifecycleState, string> writeEscalation,
        Func<Goal, ChangeRiskTier?> classifyChangeRisk)
    {
        _getFacts = getFacts;
        _getRunningPaidWorkerCount = getRunningPaidWorkerCount;
        _createWorkspace = createWorkspace;
        _dispatchAndStart = dispatchAndStart;
        _runAcceptanceVerification = runAcceptanceVerification;
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

        var facts = _getFacts(goal);
        var state = GoalLifecycle.ResolveState(goal, facts);

        if (state == GoalLifecycleState.CleanedUp)
            return MakeResult(goalId, goalPrefix, policy, new ConductorAdvanceOutcome.Done(state));

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
            GoalLifecycleState.WorkspaceReady => ExecuteDispatchAndStart(goal, goalPrefix, policy),
            GoalLifecycleState.Dispatched => MakeResult(goalId, goalPrefix, policy,
                new ConductorAdvanceOutcome.Held(state, "Dispatch recorded; awaiting process start on next advance")),
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

    private ConductorAdvanceResult ExecuteCreateWorkspace(Goal goal, string goalPrefix, ConductorAutonomyPolicy policy)
    {
        var path = _createWorkspace(goal);
        return MakeResult(goal.Id.Value, goalPrefix, policy,
            new ConductorAdvanceOutcome.Executed(GoalLifecycleState.Created, $"Workspace created: {path}"));
    }

    private ConductorAdvanceResult ExecuteDispatchAndStart(Goal goal, string goalPrefix, ConductorAutonomyPolicy policy)
    {
        var running = _getRunningPaidWorkerCount();
        if (running >= policy.MaxConcurrentPaidWorkers)
        {
            return MakeResult(goal.Id.Value, goalPrefix, policy,
                new ConductorAdvanceOutcome.Held(GoalLifecycleState.WorkspaceReady,
                    $"At worker cap ({running}/{policy.MaxConcurrentPaidWorkers}); will advance when a slot opens"));
        }

        var started = _dispatchAndStart(goal);
        if (!started)
        {
            return Escalate(goal, goalPrefix, policy, GoalLifecycleState.WorkspaceReady,
                "Dispatch failed to start any tasks; inspect readiness");
        }

        return MakeResult(goal.Id.Value, goalPrefix, policy,
            new ConductorAdvanceOutcome.Executed(GoalLifecycleState.WorkspaceReady, "Subscription dispatch started"));
    }

    private ConductorAdvanceResult ExecuteLanding(Goal goal, string goalPrefix, ConductorAutonomyPolicy policy)
    {
        // Gate 1: acceptance verification (test suite quality check).
        var acceptancePassed = _runAcceptanceVerification(goal);
        if (!acceptancePassed)
        {
            return Escalate(goal, goalPrefix, policy, GoalLifecycleState.Verified,
                "Acceptance verification failed; review and fix before landing");
        }

        // Gate 2: Apply policy AutoPromoteRiskThreshold OVER the engine default — policy can only be stricter.
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

        // Gate 3: land via integration branch.
        var landResult = _land(goal);
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

}
