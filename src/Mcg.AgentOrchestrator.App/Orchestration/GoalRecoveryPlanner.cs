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
    IReadOnlyList<string> WorktreeDirtyPaths,
    string? WorktreeStatusError,
    bool HasBranchDiff,
    RepositoryChangeSummary ChangeSummary,
    RepositoryTestImpactPlan TestImpactPlan,
    GoalOperationJournalSummary OperationJournal,
    DotnetBuildLeaseStatus BuildLease,
    GoalWorktreeCleanupBackoff? CleanupBackoff,
    int PendingHumanInputCount,
    IReadOnlyList<GoalRecoveryTaskFinding> TaskFindings,
    IReadOnlyList<string> RecommendedActions);

internal sealed record GoalRecoveryTaskFinding(
    int TaskNumber,
    TaskId TaskId,
    AgentRole Role,
    WorkTaskStatus Status,
    string Finding,
    string SuggestedCommand,
    DispatchRecoveryDecision? RecoveryDecision = null);

internal static class GoalRecoveryPlanner
{
    public static GoalRecoveryReport Build(
        AgentOrchestratorKernel kernel,
        Goal goal,
        string executionDirectory,
        bool includeCleanupBackoff = true,
        Func<ProcessCommandLineSnapshot>? processSnapshotFactory = null,
        GoalWorktreeCleanupHooks? cleanupHooks = null)
    {
        var worktree = GoalWorktrees.TryResolve(executionDirectory, goal.Id);
        GitCli.WorktreeStatusInspection? worktreeInspection = worktree is null
            ? null
            : GitCli.InspectWorktreeStatus(worktree);
        bool? dirty = worktreeInspection is null || !worktreeInspection.Value.Succeeded
            ? null
            : worktreeInspection.Value.IsDirty;
        IReadOnlyList<string> dirtyPaths = worktreeInspection is { Succeeded: true }
            ? worktreeInspection.Value.CommitWorthyPaths
            : [];
        string? worktreeStatusError = worktreeInspection is { Succeeded: false }
            ? worktreeInspection.Value.Error
            : null;
        var hasDiff = GoalWorktrees.TryGetBranchDiff(executionDirectory, goal.Id) is not null;
        var changeSummary = RepositoryChangeClassifier.Classify(worktree is null ? Array.Empty<string>() : TryGetChangedFiles(worktree));
        var testImpactPlan = worktree is null
            ? RepositoryTestImpactPlanner.Plan(changeSummary)
            : RepositoryTestImpactPlanner.Plan(changeSummary, worktree);
        var operationJournal = GoalOperationJournal.Read(executionDirectory, goal.Id);
        var buildLease = DotnetBuildEnvironmentManager.InspectGoalLease(goal.Id, cleanupHooks?.BuildStorageRoot);
        var cleanupBackoff = includeCleanupBackoff
            ? GoalWorktrees.TryGetCleanupBackoff(executionDirectory, goal.Id, cleanupHooks)
            : null;
        var pendingInput = kernel.BuildHumanInputWorklist(goal.Id).OpenCount;
        var findings = new List<GoalRecoveryTaskFinding>();
        var processInspection = new ProcessInspectionSnapshotScope(
            processSnapshotFactory ?? ProcessCommandLines.SnapshotOperation);

        for (var index = 0; index < goal.Tasks.Count; index++)
        {
            var task = goal.Tasks[index];
            AddTaskFindings(findings, goal, task, index + 1, processInspection);
        }

        var actions = BuildRecommendedActions(goal, worktree, dirty, worktreeStatusError, hasDiff, buildLease, cleanupBackoff, pendingInput, findings);
        actions.InsertRange(0, BuildJournalRecommendedActions(operationJournal));
        return new GoalRecoveryReport(
            goal.Id,
            goal.Objective,
            goal.Status,
            worktree,
            worktree is not null,
            dirty,
            dirtyPaths,
            worktreeStatusError,
            hasDiff,
            changeSummary,
            testImpactPlan,
            operationJournal,
            buildLease,
            cleanupBackoff,
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

    private static void AddTaskFindings(
        List<GoalRecoveryTaskFinding> findings,
        Goal goal,
        TaskSpec task,
        int taskNumber,
        ProcessInspectionSnapshotScope processInspection)
    {
        if (task.LastProcess is { CompletedAt: null } process && task.LastVerification is null)
        {
            var recoveryDecision = DispatchRecoveryView.Evaluate(goal, task, processInspection.Get())!;
            var apparatusHold = recoveryDecision.Action == DispatchRecoveryAction.Hold &&
                !string.IsNullOrWhiteSpace(recoveryDecision.Blocker);
            var alive = !apparatusHold &&
                recoveryDecision.Action != DispatchRecoveryAction.MarkStale &&
                recoveryDecision.Action != DispatchRecoveryAction.RetryStale &&
                recoveryDecision.Action != DispatchRecoveryAction.BudgetExhausted &&
                recoveryDecision.Action != DispatchRecoveryAction.ReconcileFromExit &&
                recoveryDecision.Action != DispatchRecoveryAction.PreserveInterruptedWork;
            findings.Add(new GoalRecoveryTaskFinding(
                taskNumber,
                task.Id,
                task.RequiredRole,
                task.Status,
                apparatusHold
                    ? $"recorded process pid={process.ProcessId} is not alive; recovery held for apparatus blocker={recoveryDecision.Blocker} evidence={recoveryDecision.EvidencePath}; refresh records a bounded hold observation and escalates a repeated hold"
                    : alive
                    ? $"recorded process is still alive pid={process.ProcessId}; recovery action={recoveryDecision.ActionName} evidence={recoveryDecision.EvidencePath}"
                    : $"recorded process pid={process.ProcessId} is not alive; recovery action={recoveryDecision.ActionName} evidence={recoveryDecision.EvidencePath}",
                $"refresh-dispatch {taskNumber}",
                recoveryDecision));
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
            var hasDirtyRecovery = DispatchFailureClassifier.TryBuildDirtyDispatchRecovery(task, out var dirtyRecovery);
            findings.Add(new GoalRecoveryTaskFinding(
                taskNumber,
                task.Id,
                task.RequiredRole,
                task.Status,
                hasDirtyRecovery
                    ? $"latest dispatch failed but left dirty worker output for orchestrator commit: {dirtyRecovery.Label}; changed files [{string.Join(", ", dirtyRecovery.ChangedFiles)}]"
                    : $"latest verification failed exit={verification.ExitCode}: {verification.Command}",
                hasDirtyRecovery
                    ? $"task {taskNumber}"
                    : $"retry {taskNumber} <note>"));
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
        string? worktreeStatusError,
        bool hasDiff,
        DotnetBuildLeaseStatus buildLease,
        GoalWorktreeCleanupBackoff? cleanupBackoff,
        int pendingInput,
        List<GoalRecoveryTaskFinding> findings)
    {
        var actions = new List<string>();
        if (pendingInput > 0)
        {
            actions.Add("answer pending human input before resuming automation");
        }

        if (worktree is null && goal.Tasks.Any(task => task.RequiredRole is AgentRole.Developer or AgentRole.Tester))
        {
            actions.Add("workspace create");
        }

        if (dirty == true)
        {
            actions.Add("inspect worktree dirty state before dispatching more file work");
        }
        else if (!string.IsNullOrWhiteSpace(worktreeStatusError))
        {
            actions.Add("inspect worktree status failure before dispatching more file work");
        }

        actions.AddRange(findings
            .Select(finding => finding.SuggestedCommand)
            .Distinct(StringComparer.OrdinalIgnoreCase));

        if (goal.Status == GoalStatus.Completed && hasDiff)
        {
            actions.Add("acceptance");
        }

        if (buildLease.CanCleanup)
        {
            actions.Add("build-lease-cleanup --confirm-build-lease-cleanup");
        }

        if (cleanupBackoff is not null)
        {
            actions.Add(cleanupBackoff.IsBudgetExhausted
                ? $"workspace remove {goal.Id.Value[..8]} ({GoalWorktrees.FormatCleanupBackoff(cleanupBackoff)})"
                : $"workspace remove {goal.Id.Value[..8]} after {GoalWorktrees.FormatCleanupBackoff(cleanupBackoff)}");
        }

        if (!IsTerminal(goal.Status) && (findings.Count > 0 || dirty == true || worktreeStatusError is not null))
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

    private static string[] TryGetChangedFiles(string worktree)
    {
        var result = GitCli.Run(worktree, "diff", "--name-only", BuildDiffSpec(worktree));
        return result.ExitCode == 0
            ? result.Output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [];
    }

    private static string BuildDiffSpec(string workingDirectory)
    {
        if (GitCli.Run(workingDirectory, "rev-parse", "--verify", "main").Succeeded)
        {
            return "main...HEAD";
        }

        if (GitCli.Run(workingDirectory, "rev-parse", "--verify", "master").Succeeded)
        {
            return "master...HEAD";
        }

        return GitCli.Run(workingDirectory, "rev-parse", "--verify", "HEAD~1").Succeeded
            ? "HEAD~1...HEAD"
            : "HEAD";
    }

}
