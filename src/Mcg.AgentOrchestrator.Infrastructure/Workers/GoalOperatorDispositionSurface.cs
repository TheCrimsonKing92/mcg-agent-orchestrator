using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

public enum OperatorDispositionState
{
    Idle,
    Wait,
    Retry,
    Accept,
    Recover,
    Blocked,
    ProductBug
}

public enum OperatorDispositionConfidence
{
    Low,
    Medium,
    High
}

public sealed record OperatorEvidencePointer(string Kind, string Path, string Detail);

public sealed record DispatchOperatorDisposition(
    TaskId TaskId,
    AgentRole Role,
    WorkTaskStatus TaskStatus,
    OperatorDispositionState State,
    OperatorDispositionConfidence Confidence,
    string Reason,
    string NextSafeCommand,
    DateTimeOffset? FreshAt,
    IReadOnlyList<string> Blockers,
    IReadOnlyList<OperatorEvidencePointer> Evidence,
    DispatchAuthoritativeState? DispatchState);

public sealed record GoalOperatorDisposition(
    GoalId GoalId,
    OperatorDispositionState State,
    OperatorDispositionConfidence Confidence,
    string Reason,
    string NextSafeCommand,
    DateTimeOffset FreshAt,
    IReadOnlyList<string> Blockers,
    IReadOnlyList<OperatorEvidencePointer> Evidence,
    IReadOnlyList<DispatchOperatorDisposition> Dispatches);

public sealed class GoalOperatorDispositionSurface
{
    private readonly IClock _clock;
    private readonly DispatchStateSurface _dispatchSurface;

    public GoalOperatorDispositionSurface(IClock? clock = null, DispatchStateSurface? dispatchSurface = null)
    {
        _clock = clock ?? new SystemClock();
        _dispatchSurface = dispatchSurface ?? new DispatchStateSurface(_clock);
    }

    public GoalOperatorDisposition Evaluate(
        Goal goal,
        int pendingHumanInputCount,
        bool verificationSatisfied,
        string? executionDirectory = null)
    {
        var dispatches = goal.Tasks.Select(task => EvaluateTask(goal, task)).ToList();
        var blockers = new List<string>();
        var evidence = new List<OperatorEvidencePointer>();

        var operationallyTerminal = IsTerminal(goal.Status);

        if (operationallyTerminal && pendingHumanInputCount > 0)
        {
            blockers.Add("stale-terminal-human-wait");
            evidence.Add(new OperatorEvidencePointer("human-input", goal.Id.Value, $"{pendingHumanInputCount} pending human input request(s) on terminal goal"));
            return Build(goal, OperatorDispositionState.ProductBug, OperatorDispositionConfidence.High,
                "terminal goal still has pending human input; persisted goal state and wait state disagree",
                $"terminal-goal-sweep {goal.Id.Value[..8]}", blockers, evidence, dispatches);
        }

        if (TryFindCleanupDebt(goal, executionDirectory, operationallyTerminal, out var cleanupReason, out var cleanupEvidence))
        {
            blockers.Add("goal-worktree-cleanup-debt");
            evidence.Add(cleanupEvidence);
            return Build(goal, OperatorDispositionState.Recover, OperatorDispositionConfidence.High,
                cleanupReason, $"workspace remove {goal.Id.Value[..8]}", blockers, evidence, dispatches);
        }

        var actionable = dispatches
            .Where(dispatch => dispatch.State is not OperatorDispositionState.Idle)
            .OrderBy(dispatch => Rank(dispatch.State))
            .FirstOrDefault();
        if (actionable is not null)
        {
            return Build(goal, actionable.State, actionable.Confidence, actionable.Reason, actionable.NextSafeCommand,
                actionable.Blockers, actionable.Evidence, dispatches);
        }

        if (verificationSatisfied && goal.Tasks.Count > 0 && goal.Tasks.All(task => task.Status == WorkTaskStatus.Completed))
        {
            return Build(goal, OperatorDispositionState.Accept, OperatorDispositionConfidence.High,
                "all tasks completed and verification gate is satisfied", $"acceptance {goal.Id.Value[..8]}", blockers, evidence, dispatches);
        }

        return Build(goal, OperatorDispositionState.Idle, OperatorDispositionConfidence.Medium,
            "no dispatch recovery action is currently required", "next", blockers, evidence, dispatches);
    }

    private DispatchOperatorDisposition EvaluateTask(Goal goal, TaskSpec task)
    {
        var taskNumber = GetTaskNumber(goal, task);
        var evidence = new List<OperatorEvidencePointer>();
        var blockers = new List<string>();
        DispatchAuthoritativeState? dispatchState = null;
        if (task.LastDispatch is not null || task.LastProcess is not null)
        {
            dispatchState = _dispatchSurface.Evaluate(goal.Id, task);
            AddDispatchEvidence(evidence, dispatchState);
        }

        if (task.Status == WorkTaskStatus.Failed)
        {
            return BuildTask(task, OperatorDispositionState.Retry, OperatorDispositionConfidence.High,
                $"task {taskNumber} failed; retry from recorded failure evidence",
                $"retry {taskNumber} <note>", blockers, evidence, dispatchState);
        }

        if (dispatchState is not null)
        {
            if (task.Status == WorkTaskStatus.Completed && dispatchState.Worktree.IsDirty == true)
            {
                blockers.Add("dirty-worktree");
                return BuildTask(task, OperatorDispositionState.Blocked, OperatorDispositionConfidence.High,
                    $"task {taskNumber} completed but its worktree has uncommitted changes",
                    $"task {taskNumber}", blockers, evidence, dispatchState);
            }

            return dispatchState.Kind switch
            {
                DispatchStateKind.ActiveTestChild or DispatchStateKind.Running =>
                    BuildTask(task, OperatorDispositionState.Wait, OperatorDispositionConfidence.High,
                        $"task {taskNumber} has live owned worker process state; wait for heartbeat or exit artifact",
                        "wait", blockers, evidence, dispatchState),
                DispatchStateKind.ExitedAwaitingReconcile =>
                    BuildTask(task, OperatorDispositionState.Recover, OperatorDispositionConfidence.High,
                        $"task {taskNumber} worker exited and needs reconciliation",
                        $"refresh-dispatch {taskNumber}", blockers, evidence, dispatchState),
                DispatchStateKind.StaleCleanup or DispatchStateKind.WedgedProcess =>
                    BuildTask(task, OperatorDispositionState.Recover, OperatorDispositionConfidence.High,
                        $"task {taskNumber} dispatch state is {dispatchState.Kind}; {dispatchState.RecoveryDecision.Reason}",
                        BuildRecoveryCommand(taskNumber, dispatchState.RecommendedAction), blockers, evidence, dispatchState),
                _ => BuildTask(task, OperatorDispositionState.Idle, OperatorDispositionConfidence.Medium,
                    $"task {taskNumber} has no active dispatch recovery requirement", "next", blockers, evidence, dispatchState)
            };
        }

        return BuildTask(task, OperatorDispositionState.Idle, OperatorDispositionConfidence.Medium,
            $"task {taskNumber} has no dispatch evidence", "next", blockers, evidence, dispatchState);
    }

    private GoalOperatorDisposition Build(
        Goal goal,
        OperatorDispositionState state,
        OperatorDispositionConfidence confidence,
        string reason,
        string command,
        IReadOnlyList<string> blockers,
        IReadOnlyList<OperatorEvidencePointer> evidence,
        IReadOnlyList<DispatchOperatorDisposition> dispatches) =>
        new(goal.Id, state, confidence, reason, command, _clock.UtcNow, blockers.ToList(), evidence.ToList(), dispatches);

    private DispatchOperatorDisposition BuildTask(
        TaskSpec task,
        OperatorDispositionState state,
        OperatorDispositionConfidence confidence,
        string reason,
        string command,
        IReadOnlyList<string> blockers,
        IReadOnlyList<OperatorEvidencePointer> evidence,
        DispatchAuthoritativeState? dispatchState) =>
        new(task.Id, task.RequiredRole, task.Status, state, confidence, reason, command,
            dispatchState?.Heartbeat.LastObservedAt ?? task.LastProcess?.StartedAt ?? task.LastDispatch?.DispatchedAt,
            blockers.ToList(), evidence.ToList(), dispatchState);

    private static void AddDispatchEvidence(List<OperatorEvidencePointer> evidence, DispatchAuthoritativeState state)
    {
        if (!string.IsNullOrWhiteSpace(state.Artifacts.StandardOutputPath))
        {
            evidence.Add(new OperatorEvidencePointer("stdout", state.Artifacts.StandardOutputPath, $"{state.Artifacts.StandardOutputBytes} bytes"));
        }

        if (!string.IsNullOrWhiteSpace(state.Artifacts.StandardErrorPath))
        {
            evidence.Add(new OperatorEvidencePointer("stderr", state.Artifacts.StandardErrorPath, $"{state.Artifacts.StandardErrorBytes} bytes"));
        }

        if (!string.IsNullOrWhiteSpace(state.Artifacts.ExitCodePath))
        {
            evidence.Add(new OperatorEvidencePointer("exit-code", state.Artifacts.ExitCodePath, state.Artifacts.ExitCodeExists ? "present" : "missing"));
        }

        if (!string.IsNullOrWhiteSpace(state.Artifacts.HeartbeatPath))
        {
            evidence.Add(new OperatorEvidencePointer("heartbeat", state.Artifacts.HeartbeatPath, state.Heartbeat.IsAvailable ? state.Heartbeat.State : state.Heartbeat.UnavailableReason ?? "unavailable"));
        }

        if (!string.IsNullOrWhiteSpace(state.Worktree.WorkingDirectory))
        {
            evidence.Add(new OperatorEvidencePointer("worktree", state.Worktree.WorkingDirectory, state.Worktree.IsDirty == true ? "dirty" : "clean-or-unknown"));
        }
    }

    private static bool TryFindCleanupDebt(
        Goal goal,
        string? executionDirectory,
        bool operationallyTerminal,
        out string reason,
        out OperatorEvidencePointer evidence)
    {
        reason = string.Empty;
        evidence = new OperatorEvidencePointer("worktree", string.Empty, string.Empty);
        if (string.IsNullOrWhiteSpace(executionDirectory) || !operationallyTerminal)
        {
            return false;
        }

        var worktree = GoalWorktrees.TryResolve(executionDirectory, goal.Id);
        if (worktree is null)
        {
            return false;
        }

        reason = "terminal goal still has a registered goal worktree; cleanup or acceptance is incomplete";
        evidence = new OperatorEvidencePointer("worktree", worktree, "registered after terminal goal state");
        return true;
    }

    private static string BuildRecoveryCommand(int taskNumber, string action) =>
        action switch
        {
            "refresh-dispatch" => $"refresh-dispatch {taskNumber}",
            "hold" => "wait",
            "none" => "next",
            _ => $"goal-recovery apply {taskNumber} --action {action}"
        };

    private static int GetTaskNumber(Goal goal, TaskSpec task)
    {
        for (var index = 0; index < goal.Tasks.Count; index++)
        {
            if (goal.Tasks[index].Id == task.Id)
            {
                return index + 1;
            }
        }

        return 0;
    }

    private static int Rank(OperatorDispositionState state) =>
        state switch
        {
            OperatorDispositionState.ProductBug => 0,
            OperatorDispositionState.Blocked => 1,
            OperatorDispositionState.Recover => 2,
            OperatorDispositionState.Retry => 3,
            OperatorDispositionState.Wait => 4,
            OperatorDispositionState.Accept => 5,
            _ => 6
        };

    private static bool IsTerminal(GoalStatus status) =>
        status is GoalStatus.Completed or GoalStatus.Failed or GoalStatus.Cancelled or GoalStatus.Superseded;
}
