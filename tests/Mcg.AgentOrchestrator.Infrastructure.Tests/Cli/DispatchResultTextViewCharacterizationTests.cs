using System.Globalization;
using Mcg.AgentOrchestrator.App.Application;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: AsyncLocal console capture, async-context culture, and local fixtures.
// Expected strings derive from ConsoleViews.DispatchAndNextActions.cs at base
// cfa0d1ef03fff2bfa57d81fd67644dfb3676f6ba (before extraction).
public sealed class DispatchResultTextViewCharacterizationTests
{
    private static readonly DateTimeOffset Timestamp =
        new(2026, 10, 30, 8, 5, 0, TimeSpan.Zero);

    [Fact]
    public void PrintProcessBatchResult_Empty_PreservesExactText()
    {
        var (_, goal) = CreateGoal();
        var result = new ProcessBatchExecutionResult(Plan(goal), []);

        var actual = Capture(() => DispatchResultTextView.PrintProcessBatchResult(goal, result));

        Assert.Equal(string.Join(Environment.NewLine,
            "", "StartDispatches: ready=0 skipped=0 changed=0", "", ""), actual);
    }

    [Fact]
    public void PrintProcessBatchResult_ProcessVariants_PreservesExactText()
    {
        var (kernel, goal) = CreateGoal();
        RecordDispatch(kernel, goal, goal.Tasks[0]);
        RecordDispatch(kernel, goal, goal.Tasks[1]);
        kernel.RecordTaskProcessStarted(goal.Id, goal.Tasks[0].Id,
            new TaskProcessRecord(4101, "worker", "fixture", "out", "err", "exit",
                Timestamp, null, null));
        kernel.RecordTaskProcessStarted(goal.Id, goal.Tasks[1].Id,
            new TaskProcessRecord(4102, "worker", "fixture", "out", "err", "exit",
                Timestamp, Timestamp.AddMinutes(1), 7));
        var result = new ProcessBatchExecutionResult(Plan(goal, ready: 3), goal.Tasks);

        var actual = Capture(() => DispatchResultTextView.PrintProcessBatchResult(goal, result));

        Assert.Equal(string.Join(Environment.NewLine,
            "", "StartDispatches: ready=3 skipped=0 changed=3",
            "  Task 1 running pid=4101", "  Task 2 exit=7", "  Task 3 exit=n/a",
            "", ""), actual);
    }

    [Fact]
    public void PrintProcessBatchResult_FailuresAndSkips_PreservesExactText()
    {
        var (_, goal) = CreateGoal();
        var skipped = new ProcessBatchPlanItem(goal.Tasks[2].Id, AgentRole.Reviewer,
            "Review", WorkTaskStatus.Pending, ProcessBatchItemStatus.Skipped, "Wait for evidence");
        var plan = Plan(goal, ready: 1) with { SkippedCount = 1, Items = [skipped] };
        var result = new ProcessBatchExecutionResult(plan, [],
            StartFailures: [new(goal.Tasks[1].Id, "Launcher refused")]);

        var actual = Capture(() => DispatchResultTextView.PrintProcessBatchResult(goal, result));

        Assert.Equal(string.Join(Environment.NewLine,
            "", "StartDispatches: ready=1 skipped=1 changed=1",
            "Start failures:", "  Task 2: Launcher refused",
            "Skipped:", "  Task 3: Wait for evidence", "", ""), actual);
    }

    [Fact]
    public void PrintSubscriptionStartResult_Empty_PreservesExactText()
    {
        var (_, goal) = CreateGoal();
        var result = new SubscriptionStartResult([], new(Plan(goal), []), new([], []), []);

        var actual = Capture(() => DispatchResultTextView.PrintSubscriptionStartResult(goal, result));

        Assert.Equal(string.Join(Environment.NewLine,
            "", "Subscription dispatches created: 0", "",
            "StartDispatches: ready=0 skipped=0 changed=0", "", ""), actual);
    }

    [Fact]
    public void PrintSubscriptionStartResult_DispatchesAndPlan_PreservesExactText()
    {
        var (kernel, goal) = CreateGoal();
        RecordDispatch(kernel, goal, goal.Tasks[0]);
        RecordDispatch(kernel, goal, goal.Tasks[1]);
        var parallel = new ParallelExecutionPlan([], [
            new(goal.Tasks[0].Id.Value.ToUpperInvariant(), ParallelExecutionDisposition.Concurrent,
                2, ["Independent", "Slot available"]),
            new("unmapped-intent", ParallelExecutionDisposition.Serialized, null, ["Dependency"])]);
        var result = new SubscriptionStartResult(
            [new(goal.Tasks[0], "first.prompt.md"), new(goal.Tasks[1], "second.prompt.md")],
            new(Plan(goal, ready: 2), []), parallel, []);

        var actual = Capture(() => DispatchResultTextView.PrintSubscriptionStartResult(goal, result));

        Assert.Equal(string.Join(Environment.NewLine,
            "", "Task 1 profile fixture-worker: first.prompt.md",
            "Task 2 profile fixture-worker: second.prompt.md",
            "Subscription dispatches created: 2", "Parallel plan:",
            "  Task 1: Concurrent batch=2 - Independent; Slot available",
            "  Task unmapped-intent: Serialized batch=n/a - Dependency", "",
            "StartDispatches: ready=2 skipped=0 changed=0", "", ""), actual);
    }

    [Fact]
    public void PrintDispatchState_None_PreservesExactText()
    {
        var state = EmptyDispatchState();

        var actual = Capture(() => DispatchResultTextView.PrintDispatchState(state));

        Assert.Equal(string.Join(Environment.NewLine,
            "dispatch-state: state='None' action='wait' live=False child_pid=none exit_artifact=False dirty_worktree=unknown",
            "recovery: action='hold' evidence='none' reason='No dispatch'", ""), actual);
    }

    [Fact]
    public void PrintDispatchState_LiveWithBlockerAndIndent_PreservesExactText()
    {
        var state = EmptyDispatchState() with
        {
            Kind = DispatchStateKind.Running,
            ProcessTree = new(4101, 4102, [4101, 4102], [new(4102, true, "worker")], "worker"),
            Worktree = new("fixture", true, true, true, "abc123", 0, [" M file.cs"], null),
            RecoveryDecision = new(DispatchRecoveryAction.Hold, "hold", "heartbeat.json",
                "Child active", "retry budget")
        };

        var actual = Capture(() => DispatchResultTextView.PrintDispatchState(state, "     "));

        Assert.Equal(string.Join(Environment.NewLine,
            "     dispatch-state: state='Running' action='wait' live=True child_pid=4102 exit_artifact=False dirty_worktree=True",
            "     recovery: action='hold' evidence='heartbeat.json' reason='Child active' blocker='retry budget'",
            ""), actual);
    }

    private static (AgentOrchestratorKernel Kernel, Goal Goal) CreateGoal()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Dispatch sample", [
            new(new TaskId("abcdef01-task"), "Implement", AgentRole.Developer),
            new(new TaskId("abcdef02-task"), "Test", AgentRole.Tester),
            new(new TaskId("abcdef03-task"), "Review", AgentRole.Reviewer)]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        return (kernel, goal);
    }

    private static ProcessBatchPlan Plan(Goal goal, int ready = 0) =>
        new(goal.Id, goal.Objective, goal.Status, ProcessBatchActionKind.StartDispatches, ready, 0, []);

    private static void RecordDispatch(AgentOrchestratorKernel kernel, Goal goal, TaskSpec task) =>
        kernel.RecordTaskDispatch(goal.Id, task.Id,
            new TaskDispatchRecord("fixture-worker", "worker", "fixture", Timestamp));

    private static DispatchAuthoritativeState EmptyDispatchState() => new(
        DispatchStateKind.None, "wait",
        new(DispatchRecoveryAction.Hold, "hold", "none", "No dispatch"),
        new(0, null, [], [], null),
        new("out", false, 0, "err", false, 0, "exit", false, "heartbeat", false),
        new("heartbeat", false, "none", 0, null, [], "none", null, null, null, null, 0, 0),
        new("fixture", false, false, null, null, null, [], null),
        new(TimeSpan.Zero, TimeSpan.Zero, 0), "No dispatch");

    private static string Capture(Action print)
    {
        var previousCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
            return AsyncLocalConsoleRouter.Capture(print);
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }
    }
}
