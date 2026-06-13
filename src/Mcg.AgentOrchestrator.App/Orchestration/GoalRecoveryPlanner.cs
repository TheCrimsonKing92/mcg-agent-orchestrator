using System.Diagnostics;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record GoalRecoveryReport(
    GoalId GoalId,
    string Objective,
    GoalStatus Status,
    string? WorktreePath,
    bool WorktreeExists,
    bool? WorktreeDirty,
    bool HasBranchDiff,
    RepositoryChangeSummary ChangeSummary,
    RepositoryTestImpactPlan TestImpactPlan,
    GoalOperationJournalSummary OperationJournal,
    DotnetBuildLeaseStatus BuildLease,
    int PendingHumanInputCount,
    IReadOnlyList<GoalRecoveryTaskFinding> TaskFindings,
    IReadOnlyList<string> RecommendedActions);

internal sealed record GoalRecoveryTaskFinding(
    int TaskNumber,
    TaskId TaskId,
    AgentRole Role,
    WorkTaskStatus Status,
    string Finding,
    string SuggestedCommand);

internal static class GoalRecoveryPlanner
{
    public static GoalRecoveryReport Build(
        AgentOrchestratorKernel kernel,
        Goal goal,
        string executionDirectory)
    {
        var worktree = GoalWorktrees.TryResolve(executionDirectory, goal.Id);
        var dirty = worktree is null ? null : TryIsWorktreeDirty(worktree);
        var hasDiff = GoalWorktrees.TryGetBranchDiff(executionDirectory, goal.Id) is not null;
        var changeSummary = RepositoryChangeClassifier.Classify(worktree is null ? Array.Empty<string>() : TryGetChangedFiles(worktree));
        var testImpactPlan = RepositoryTestImpactPlanner.Plan(changeSummary);
        var operationJournal = GoalOperationJournal.Read(executionDirectory, goal.Id);
        var buildLease = DotnetBuildEnvironmentManager.InspectGoalLease(goal.Id);
        var pendingInput = kernel.BuildHumanInputWorklist(goal.Id).OpenCount;
        var findings = new List<GoalRecoveryTaskFinding>();

        for (var index = 0; index < goal.Tasks.Count; index++)
        {
            var task = goal.Tasks[index];
            AddTaskFindings(findings, goal, task, index + 1);
        }

        var actions = BuildRecommendedActions(goal, worktree, dirty, hasDiff, buildLease, pendingInput, findings);
        actions.InsertRange(0, BuildJournalRecommendedActions(operationJournal));
        return new GoalRecoveryReport(
            goal.Id,
            goal.Objective,
            goal.Status,
            worktree,
            worktree is not null,
            dirty,
            hasDiff,
            changeSummary,
            testImpactPlan,
            operationJournal,
            buildLease,
            pendingInput,
            findings,
            actions);
    }

    private static string[] BuildJournalRecommendedActions(GoalOperationJournalSummary journal)
    {
        if (journal.InterruptedOperations.Count == 0)
        {
            return [];
        }

        return journal.InterruptedOperations
            .Select(entry => entry.Operation switch
            {
                "workspace:create" => "workspace create",
                "run-goal" => "run-goal --confirm-batch-start",
                "acceptance" => "acceptance",
                "workspace:remove" => "workspace remove",
                _ => $"inspect interrupted operation {entry.Operation}"
            })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static void AddTaskFindings(List<GoalRecoveryTaskFinding> findings, Goal goal, TaskSpec task, int taskNumber)
    {
        if (task.LastProcess is { IsRunning: true } process)
        {
            var alive = IsProcessAlive(process.ProcessId);
            findings.Add(new GoalRecoveryTaskFinding(
                taskNumber,
                task.Id,
                task.RequiredRole,
                task.Status,
                alive
                    ? $"recorded process is still alive pid={process.ProcessId}"
                    : $"recorded process pid={process.ProcessId} is not alive; refresh should reconcile durable state",
                alive ? $"refresh-dispatch {taskNumber}" : $"refresh-dispatch {taskNumber}"));
        }
        else if (task.Status == WorkTaskStatus.Running && task.LastDispatch is not null)
        {
            findings.Add(new GoalRecoveryTaskFinding(
                taskNumber,
                task.Id,
                task.RequiredRole,
                task.Status,
                "task has a recorded dispatch but no running process record",
                $"start-dispatch {taskNumber} --confirm-dispatch-start"));
        }

        if (task.Status == WorkTaskStatus.Completed && task.LastVerification is null)
        {
            findings.Add(new GoalRecoveryTaskFinding(
                taskNumber,
                task.Id,
                task.RequiredRole,
                task.Status,
                "task completed without verification evidence",
                $"verify {taskNumber} <command>"));
        }

        if (task.LastVerification is { Succeeded: false } verification)
        {
            findings.Add(new GoalRecoveryTaskFinding(
                taskNumber,
                task.Id,
                task.RequiredRole,
                task.Status,
                $"latest verification failed exit={verification.ExitCode}: {verification.Command}",
                $"retry {taskNumber} <note>"));
        }

        if (task.Status == WorkTaskStatus.Failed)
        {
            findings.Add(new GoalRecoveryTaskFinding(
                taskNumber,
                task.Id,
                task.RequiredRole,
                task.Status,
                "task status is failed",
                $"retry {taskNumber} <note>"));
        }

        if (task.Status == WorkTaskStatus.Assigned && task.LastDispatch is null)
        {
            findings.Add(new GoalRecoveryTaskFinding(
                taskNumber,
                task.Id,
                task.RequiredRole,
                task.Status,
                "assigned task has no dispatch or execution evidence yet",
                $"subscription-dispatch {taskNumber}"));
        }
    }

    private static List<string> BuildRecommendedActions(
        Goal goal,
        string? worktree,
        bool? dirty,
        bool hasDiff,
        DotnetBuildLeaseStatus buildLease,
        int pendingInput,
        List<GoalRecoveryTaskFinding> findings)
    {
        var actions = new List<string>();
        if (pendingInput > 0)
        {
            actions.Add("answer pending human input before resuming automation");
        }

        actions.AddRange(findings
            .Select(finding => finding.SuggestedCommand)
            .Distinct(StringComparer.OrdinalIgnoreCase));

        if (worktree is null && goal.Tasks.Any(task => task.RequiredRole is AgentRole.Developer or AgentRole.Tester))
        {
            actions.Add("workspace create");
        }

        if (dirty == true)
        {
            actions.Add("inspect worktree dirty state before dispatching more file work");
        }

        if (goal.Status == GoalStatus.Completed && hasDiff)
        {
            actions.Add("acceptance");
        }

        if (buildLease.CanCleanup)
        {
            actions.Add("build-lease-cleanup --confirm-build-lease-cleanup");
        }

        if (!IsTerminal(goal.Status) && (findings.Count > 0 || dirty == true))
        {
            actions.Add($"park-goal {goal.Id.Value[..8]} <reason> --confirm-goal-park");
        }

        if (actions.Count == 0)
        {
            actions.Add("monitor");
        }

        return actions;
    }

    private static bool IsTerminal(GoalStatus status) =>
        status is GoalStatus.Completed or GoalStatus.Failed or GoalStatus.Cancelled or GoalStatus.Superseded;

    private static bool IsProcessAlive(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
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

    private static bool? TryIsWorktreeDirty(string worktree)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "git",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = worktree
            };
            startInfo.ArgumentList.Add("status");
            startInfo.ArgumentList.Add("--porcelain");

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return null;
            }

            var output = process.StandardOutput.ReadToEnd();
            _ = process.StandardError.ReadToEnd();
            return process.WaitForExit(10000) && process.ExitCode == 0
                ? !string.IsNullOrWhiteSpace(output)
                : null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private static string[] TryGetChangedFiles(string worktree)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "git",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = worktree
            };
            startInfo.ArgumentList.Add("diff");
            startInfo.ArgumentList.Add("--name-only");
            startInfo.ArgumentList.Add(BuildDiffSpec(worktree));

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return [];
            }

            var output = process.StandardOutput.ReadToEnd();
            _ = process.StandardError.ReadToEnd();
            return process.WaitForExit(10000) && process.ExitCode == 0
                ? output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                : [];
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
        {
            return [];
        }
    }

    private static string BuildDiffSpec(string workingDirectory)
    {
        if (GitSucceeds(workingDirectory, "rev-parse", "--verify", "main"))
        {
            return "main...HEAD";
        }

        if (GitSucceeds(workingDirectory, "rev-parse", "--verify", "master"))
        {
            return "master...HEAD";
        }

        return GitSucceeds(workingDirectory, "rev-parse", "--verify", "HEAD~1")
            ? "HEAD~1...HEAD"
            : "HEAD";
    }

    private static bool GitSucceeds(string workingDirectory, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            return false;
        }

        _ = process.StandardOutput.ReadToEnd();
        _ = process.StandardError.ReadToEnd();
        return process.WaitForExit(10000) && process.ExitCode == 0;
    }

}
