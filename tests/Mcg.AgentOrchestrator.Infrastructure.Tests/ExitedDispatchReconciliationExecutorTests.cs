using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: snapshots and recording delegates are local to each test.
public sealed class ExitedDispatchReconciliationExecutorTests
{
    [Xunit.Fact]
    public void Execute_RefreshedGoals_PreservesOriginalIdsAndEffectOrder()
    {
        var snapshot = Snapshot(Task(exited: true), Task(exited: false), Task(exited: true));
        var initial = Read(snapshot);
        var firstRead = Read(snapshot with
        {
            Tasks = [snapshot.Tasks[0], snapshot.Tasks[1] with { LastProcess = Process() },
                snapshot.Tasks[2] with { LastProcess = null }]
        });
        var lastRead = Read(snapshot with { Objective = "Last read" });
        Assert.True(IsExited(initial.Tasks[0]));
        Assert.False(IsExited(initial.Tasks[1]));
        Assert.True(IsExited(initial.Tasks[2]));
        Assert.True(IsExited(firstRead.Tasks[1]));
        Assert.False(IsExited(firstRead.Tasks[2]));
        var effects = new List<string>();
        var reconciles = new List<(Goal Goal, TaskId Id)>();
        var reads = new List<Goal>();

        var result = ExitedDispatchReconciliationExecutor.Execute(initial,
            (goal, id) =>
            {
                effects.Add($"reconcile({id.Value})");
                reconciles.Add((goal, id));
                return true;
            },
            goal =>
            {
                effects.Add("read");
                reads.Add(goal);
                return reads.Count == 1 ? firstRead : lastRead;
            });

        Assert.Equal(new[] { $"reconcile({initial.Tasks[0].Id.Value})", "read",
            $"reconcile({initial.Tasks[2].Id.Value})", "read" }, effects);
        Assert.Equal(new[] { initial.Tasks[0].Id, initial.Tasks[2].Id },
            reconciles.Select(call => call.Id));
        Assert.Same(initial, reconciles[0].Goal);
        Assert.Same(firstRead, reconciles[1].Goal);
        Assert.Same(initial, reads[0]);
        Assert.Same(firstRead, reads[1]);
        Assert.Same(lastRead, result);
    }

    [Xunit.Fact]
    public void Execute_NoExitedTask_ReturnsInputWithoutEffects()
    {
        var goal = Read(Snapshot(Task(exited: false)));
        Assert.False(IsExited(goal.Tasks.Single()));

        AssertNoEffects(goal);
    }

    [Xunit.Fact]
    public void Execute_AppliedCompletion_ReturnsInputWithoutEffects()
    {
        var process = Process();
        var verification = new TaskVerificationSnapshot(process.Command, process.WorkingDirectory,
            process.ExitCode!.Value, "", "", process.CompletedAt!.Value);
        var goal = Read(Snapshot(Task(exited: true) with { VerificationHistory = [verification] }));
        var task = goal.Tasks.Single();
        Assert.NotNull(task.LastProcess!.CompletedAt);
        Assert.True(DispatchProcessCompletionState.HasAlreadyBeenApplied(task, task.LastProcess));
        Assert.False(IsExited(task));

        AssertNoEffects(goal);
    }

    [Xunit.Fact]
    public void Execute_FalseReconcileResult_PreservesTrueResultSequence()
    {
        var goal = Read(Snapshot(Task(exited: true), Task(exited: false), Task(exited: true)));

        var trueEffects = Record(true);
        var falseEffects = Record(false);

        Assert.Equal(new[] { $"reconcile({goal.Tasks[0].Id.Value})", "read",
            $"reconcile({goal.Tasks[2].Id.Value})", "read" }, trueEffects);
        Assert.Equal(trueEffects, falseEffects);

        List<string> Record(bool reconcileResult)
        {
            var effects = new List<string>();
            var result = ExitedDispatchReconciliationExecutor.Execute(goal,
                (current, id) =>
                {
                    Assert.Same(goal, current);
                    effects.Add($"reconcile({id.Value})");
                    return reconcileResult;
                },
                current => { effects.Add("read"); return current; });
            Assert.Same(goal, result);
            return effects;
        }
    }

    private static void AssertNoEffects(Goal goal)
    {
        var reconcileCalls = 0;
        var readCalls = 0;

        var result = ExitedDispatchReconciliationExecutor.Execute(goal,
            (_, _) => { reconcileCalls++; return true; },
            current => { readCalls++; return current; });

        Assert.Equal(0, reconcileCalls);
        Assert.Equal(0, readCalls);
        Assert.Same(goal, result);
    }

    private static bool IsExited(TaskSpec task) => task.LastProcess is { } process &&
        DispatchProcessCompletionState.IsExitedWithoutAppliedCompletion(task, process);

    private static Goal Read(GoalSnapshot snapshot) =>
        AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot([snapshot], [])).Goals.Single();

    private static GoalSnapshot Snapshot(params TaskSnapshot[] tasks) =>
        new(GoalId.New().Value, "Reconciliation", GoalStatus.Active, tasks, []);

    private static TaskSnapshot Task(bool exited) => new(TaskId.New().Value, "Reconcile task",
        AgentRole.Developer, WorkTaskStatus.Assigned, null, null, null, [], null,
        exited ? Process() : null);

    private static TaskProcessSnapshot Process()
    {
        var completedAt = new DateTimeOffset(2026, 10, 4, 0, 0, 0, TimeSpan.Zero);
        return new(12345, "test.exe", @"C:\tmp", @"C:\tmp\stdout", @"C:\tmp\stderr",
            @"C:\tmp\exit", completedAt.AddMinutes(-1), completedAt, 0);
    }
}
