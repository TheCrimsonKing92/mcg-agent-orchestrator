using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using System.Diagnostics;

public sealed class ConductorDriverTestsOperatorDispositionSnapshots
{
    [Fact(DisplayName = "Conductor_operator_disposition_build_uses_one_process_table_snapshot_for_large_store")]
    public void ConductorOperatorDispositionBuildUsesOneProcessTableSnapshotForLargeStore()
    {
        var kernel = CreateSyntheticKernel(goalCount: 500, tasksPerGoal: 3);
        var snapshotReads = 0;
        var stopwatch = Stopwatch.StartNew();

        var snapshots = ConductorOperatorDispositionSnapshots.Build(
            kernel,
            executionDirectory: null,
            createCommandLineSnapshot: () =>
            {
                snapshotReads++;
                return ProcessCommandLineSnapshot.Empty;
            });

        stopwatch.Stop();
        var elapsedMs = stopwatch.Elapsed.TotalMilliseconds;

        Assert.Equal(500, snapshots.Count);
        Assert.Equal(1, snapshotReads);
        Assert.Equal(300, snapshots.Sum(snapshot => snapshot.Dispatches.Count));
        Assert.Equal(400, snapshots.Count(snapshot => snapshot.Dispatches.Count == 0));
        Assert.True(elapsedMs >= 0, $"elapsed_ms={elapsedMs:0.0}");
    }

    private static AgentOrchestratorKernel CreateSyntheticKernel(int goalCount, int tasksPerGoal)
    {
        var root = CreateTempDirectory();
        var now = DateTimeOffset.Parse("2026-07-10T00:00:00Z");
        var goals = Enumerable.Range(0, goalCount)
            .Select(goalIndex =>
            {
                var goalId = GoalId.New().Value;
                var status = goalIndex % 5 == 0 ? GoalStatus.Active : GoalStatus.Completed;
                return new GoalSnapshot(
                    goalId,
                    $"Synthetic disposition goal {goalIndex}",
                    status,
                    Enumerable.Range(0, tasksPerGoal)
                        .Select(taskIndex => CreateSyntheticTask(root, now, goalIndex, taskIndex))
                        .ToArray(),
                    [],
                    DependsOn: null);
            })
            .ToArray();

        return AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot(goals, []));
    }

    private static TaskSnapshot CreateSyntheticTask(
        string root,
        DateTimeOffset now,
        int goalIndex,
        int taskIndex)
    {
        var taskId = TaskId.New().Value;
        var pid = 900000 + (goalIndex * 3) + taskIndex;
        var logs = Path.Combine(root, "logs");
        var stdout = Path.Combine(logs, $"{goalIndex}-{taskIndex}.out.log");
        var stderr = Path.Combine(logs, $"{goalIndex}-{taskIndex}.err.log");
        var exit = Path.Combine(logs, $"{goalIndex}-{taskIndex}.exit.txt");
        return new TaskSnapshot(
            taskId,
            "Synthetic dispatch task.",
            AgentRole.Developer,
            WorkTaskStatus.Running,
            AssignedAgentId: "developer",
            LastExecution: null,
            LastVerification: null,
            VerificationHistory: null,
            LastDispatch: new TaskDispatchSnapshot(
                "codex-cli",
                "codex exec prompt",
                root,
                now.AddMinutes(-5)),
            LastProcess: new TaskProcessSnapshot(
                pid,
                "codex exec prompt",
                root,
                stdout,
                stderr,
                exit,
                now.AddMinutes(-5),
                CompletedAt: null,
                ExitCode: null,
                OwnedProcessIds: [pid]));
    }
}
