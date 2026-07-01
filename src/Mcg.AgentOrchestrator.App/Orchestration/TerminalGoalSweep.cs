using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record TerminalGoalSweepRepair(
    string Kind,
    string Evidence,
    string Command);

internal sealed record TerminalGoalSweepBlocker(
    string Kind,
    string Evidence,
    string Command);

internal sealed record TerminalGoalSweepGoalResult(
    GoalId GoalId,
    string GoalPrefix,
    IReadOnlyList<TerminalGoalSweepRepair> Repairs,
    IReadOnlyList<TerminalGoalSweepBlocker> Blockers)
{
    public bool Changed => Repairs.Count > 0;
}

internal sealed record TerminalGoalSweepResult(IReadOnlyList<TerminalGoalSweepGoalResult> Goals)
{
    public bool Changed => Goals.Any(goal => goal.Changed);
    public IReadOnlyList<TerminalGoalSweepBlocker> Blockers => Goals.SelectMany(goal => goal.Blockers).ToArray();
}

internal static class TerminalGoalSweep
{
    public static TerminalGoalSweepResult Run(
        AgentOrchestratorKernel kernel,
        string executionDirectory,
        GoalId? onlyGoalId = null)
    {
        var dispatchRunner = new BackgroundDispatchRunner();
        var results = new List<TerminalGoalSweepGoalResult>();

        foreach (var originalGoal in kernel.Goals.Where(goal => onlyGoalId is null || goal.Id == onlyGoalId).ToArray())
        {
            var repairs = new List<TerminalGoalSweepRepair>();
            var blockers = new List<TerminalGoalSweepBlocker>();
            var prefix = originalGoal.Id.Value[..Math.Min(8, originalGoal.Id.Value.Length)];

            var reconciled = dispatchRunner.SweepExitedProcesses(kernel, originalGoal.Id);
            if (reconciled > 0)
            {
                repairs.Add(new TerminalGoalSweepRepair(
                    "dispatch-exit-reconciled",
                    $"reconciled {reconciled} exited dispatch artifact(s)",
                    "reconcile"));
            }

            var goal = kernel.GetGoal(originalGoal.Id);
            if (onlyGoalId is null &&
                TryBuildGlobalStaleTerminalExclusionEvidence(goal, out var exclusionEvidence))
            {
                blockers.Add(new TerminalGoalSweepBlocker(
                    "stale-terminal-excluded",
                    exclusionEvidence,
                    "excluded"));
                results.Add(new TerminalGoalSweepGoalResult(originalGoal.Id, prefix, repairs, blockers));
                continue;
            }

            var isCompletedGitGoal = goal.Status == GoalStatus.Completed && GoalWorktrees.IsGitWorkTree(executionDirectory);
            var hasGoalBranchArtifact = isCompletedGitGoal &&
                (GoalWorktrees.TryResolve(executionDirectory, goal.Id) is not null ||
                 GoalWorktrees.HasBranch(executionDirectory, goal.Id));
            var branchAlreadyLanded = isCompletedGitGoal &&
                hasGoalBranchArtifact &&
                GoalWorktrees.IsBranchMergedIntoCurrent(executionDirectory, goal.Id);
            var hasTerminalTaskDesync = TryBuildTerminalTaskDesyncEvidence(goal, out var desyncEvidence);
            var blockedByDirtyWorktree = false;

            if (hasTerminalTaskDesync &&
                TryBuildTerminalDirtyWorktreeBlocker(goal, executionDirectory, prefix, desyncEvidence, out var dirtyEvidence, out var dirtyCommand))
            {
                blockedByDirtyWorktree = true;
                blockers.Add(new TerminalGoalSweepBlocker(
                    "terminal-dirty-worktree",
                    dirtyEvidence,
                    dirtyCommand));
            }
            else if (branchAlreadyLanded)
            {
                foreach (var task in goal.Tasks.Where(task => task.Status is not (WorkTaskStatus.Completed or WorkTaskStatus.Cancelled)).ToArray())
                {
                    var staleStatus = task.Status;
                    kernel.ReportTaskProgress(
                        goal.Id,
                        task.Id,
                        WorkTaskStatus.Cancelled,
                        "Terminal stale-goal sweep cancelled stale task because the goal branch is already landed.");
                    repairs.Add(new TerminalGoalSweepRepair(
                        "landed-task-desync",
                        $"landed goal had stale {staleStatus} task {task.Id.Value[..8]}",
                        $"workspace remove {prefix}"));
                }

                goal = kernel.GetGoal(originalGoal.Id);
            }

            if (!blockedByDirtyWorktree &&
                !branchAlreadyLanded &&
                TryBuildTerminalLiveDispatchBlocker(goal, prefix, out var liveDispatchEvidence, out var liveDispatchCommand))
            {
                blockers.Add(new TerminalGoalSweepBlocker(
                    "terminal-live-dispatch",
                    liveDispatchEvidence,
                    liveDispatchCommand));
            }
            else if (!blockedByDirtyWorktree &&
                     !branchAlreadyLanded &&
                     hasTerminalTaskDesync &&
                     kernel.NormalizeGoalLifecycleState(goal.Id, "terminal stale-goal sweep: reopened terminal goal with non-terminal task(s)."))
            {
                repairs.Add(new TerminalGoalSweepRepair(
                    "terminal-task-desync",
                    desyncEvidence,
                    $"conduct {prefix} --loop"));
                goal = kernel.GetGoal(originalGoal.Id);
            }

            if (!blockedByDirtyWorktree &&
                goal.Status == GoalStatus.Completed &&
                GoalWorktrees.IsGitWorkTree(executionDirectory))
            {
                if (!GoalWorktrees.IsBranchMergedIntoCurrent(executionDirectory, goal.Id))
                {
                    blockers.Add(new TerminalGoalSweepBlocker(
                        "completed-branch-unmerged",
                        $"completed goal still has unmerged branch {GoalWorktrees.BranchName(goal.Id)}",
                        $"acceptance {prefix}"));
                    results.Add(new TerminalGoalSweepGoalResult(originalGoal.Id, prefix, repairs, blockers));
                    continue;
                }

                var removeResult = GoalWorktrees.Remove(executionDirectory, goal.Id, kernel);
                if (removeResult.Message.Contains("kept because it has unmerged commits", StringComparison.OrdinalIgnoreCase))
                {
                    blockers.Add(new TerminalGoalSweepBlocker(
                        "completed-branch-unmerged",
                        removeResult.Message,
                        $"acceptance {prefix}"));
                }
                else if (!removeResult.Message.Contains("already clean", StringComparison.OrdinalIgnoreCase))
                {
                    repairs.Add(new TerminalGoalSweepRepair(
                        "merged-branch-cleanup",
                        removeResult.Message,
                        $"workspace remove {prefix}"));
                }
            }

            if (repairs.Count > 0 || blockers.Count > 0)
            {
                results.Add(new TerminalGoalSweepGoalResult(originalGoal.Id, prefix, repairs, blockers));
            }
        }

        return new TerminalGoalSweepResult(results);
    }

    private static bool TryBuildGlobalStaleTerminalExclusionEvidence(Goal goal, out string evidence)
    {
        evidence = string.Empty;
        if (!IsGlobalStaleTerminalStatus(goal.Status))
        {
            return false;
        }

        var dispatchableTasks = goal.Tasks
            .Where(task => IsStaleTerminalAssignedTaskStatus(task.Status))
            .Select(task => $"{task.Id.Value[..8]}:{task.Status}")
            .ToArray();
        if (dispatchableTasks.Length == 0)
        {
            return false;
        }

        evidence = $"goalId={goal.Id.Value}; goalState={goal.Status}; action=excluded; dispatchableTasks={string.Join(",", dispatchableTasks)}";
        return true;
    }

    private static bool TryBuildTerminalTaskDesyncEvidence(Goal goal, out string evidence)
    {
        evidence = string.Empty;
        if (!IsTerminalSweepStatus(goal.Status))
        {
            return false;
        }

        var dispatchableTasks = goal.Tasks
            .Where(task => IsStaleTerminalAssignedTaskStatus(task.Status))
            .Select(task => $"{task.Id.Value[..8]}:{task.Status}")
            .ToArray();
        if (dispatchableTasks.Length == 0)
        {
            return false;
        }

        evidence = $"goalState={goal.Status}; dispatchableTasks={string.Join(",", dispatchableTasks)}";
        return true;
    }

    private static bool TryBuildTerminalDirtyWorktreeBlocker(
        Goal goal,
        string executionDirectory,
        string goalPrefix,
        string desyncEvidence,
        out string evidence,
        out string command)
    {
        evidence = string.Empty;
        command = string.Empty;
        if (!GoalWorktrees.IsGitWorkTree(executionDirectory))
        {
            return false;
        }

        var worktree = GoalWorktrees.TryResolve(executionDirectory, goal.Id);
        if (worktree is null || GoalWorktrees.IsWorktreeClean(executionDirectory, goal.Id))
        {
            return false;
        }

        evidence = $"{desyncEvidence}; worktreeDirty=true; worktree={worktree}";
        command = $"goal-recovery {goalPrefix}";
        return true;
    }

    private static bool TryBuildTerminalLiveDispatchBlocker(
        Goal goal,
        string goalPrefix,
        out string evidence,
        out string command)
    {
        evidence = string.Empty;
        command = string.Empty;
        if (!IsTerminalSweepStatus(goal.Status))
        {
            return false;
        }

        var liveTasks = goal.Tasks
            .Where(task => task.Status == WorkTaskStatus.Running &&
                           task.LastProcess is { IsRunning: true } process &&
                           process.TrackedProcessIds.Any(IsProcessAlive))
            .Select(task =>
            {
                var process = task.LastProcess!;
                var livePids = process.TrackedProcessIds.Where(IsProcessAlive).ToArray();
                return new
                {
                    Task = task,
                    TaskNumber = TaskDisplayNumber.Resolve(goal, task.Id),
                    Process = process,
                    LivePids = livePids
                };
            })
            .ToArray();
        if (liveTasks.Length == 0)
        {
            return false;
        }

        evidence =
            $"goalState={goal.Status}; liveDispatchTasks={string.Join(",", liveTasks.Select(item => $"{item.Task.Id.Value[..8]}:{item.Task.Status}:pid={item.Process.ProcessId}:livePids={string.Join("+", item.LivePids)}"))}";
        command = liveTasks.Length == 1
            ? $"refresh-dispatch {goalPrefix} {liveTasks[0].TaskNumber}"
            : $"refresh-dispatches {goalPrefix}";
        return true;
    }

    private static bool IsTerminalSweepStatus(GoalStatus status) =>
        status is GoalStatus.Completed or GoalStatus.Cancelled or GoalStatus.Failed or GoalStatus.Superseded;

    private static bool IsGlobalStaleTerminalStatus(GoalStatus status) =>
        status is GoalStatus.Completed or GoalStatus.Cancelled or GoalStatus.Failed;

    private static bool IsStaleTerminalAssignedTaskStatus(WorkTaskStatus status) =>
        status is WorkTaskStatus.Assigned or WorkTaskStatus.Running or WorkTaskStatus.WaitingForHuman;

    private static bool IsProcessAlive(int processId)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}
