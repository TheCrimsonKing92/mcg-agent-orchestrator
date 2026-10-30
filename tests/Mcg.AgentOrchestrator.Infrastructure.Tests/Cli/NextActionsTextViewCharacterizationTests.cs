using System.Globalization;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: AsyncLocal console capture, async-context culture, and local fixtures.
// Expected strings derive from ConsoleViews.DispatchAndNextActions.cs at base
// cfa0d1ef03fff2bfa57d81fd67644dfb3676f6ba (before extraction).
public sealed class NextActionsTextViewCharacterizationTests
{
    [Fact]
    public void Print_NoActions_PreservesExactText()
    {
        var goal = CreateGoal();
        var actions = new GoalNextActions(goal.Id, goal.Objective, goal.Status, []);

        var actual = Capture(() => NextActionsTextView.Print(goal, actions,
            new WorkerProfileCatalog([]), conductorDisposition: IdleDisposition(goal),
            processSnapshotFactory: () => ProcessCommandLineSnapshot.Empty));

        Assert.Equal(string.Join(Environment.NewLine,
            "", "Goal abcdef01 Draft: Next action sample",
            "Disposition: state='Idle' confidence='High' action='monitor' blockers='none' reason='No work pending' fresh='2026-10-30 08:05:00Z'",
            "Next actions:", "", ""), actual);
    }

    [Fact]
    public void Print_MultipleActions_PreservesExactText()
    {
        var goal = CreateGoal();
        var actions = new GoalNextActions(goal.Id, goal.Objective, goal.Status, [
            new(NextActionKind.MonitorGoal, null, null, "Inspect progress"),
            new(NextActionKind.DelegatePendingTask, goal.Tasks[0].Id, null, "Assign a worker")]);

        var actual = Capture(() => NextActionsTextView.Print(goal, actions,
            new WorkerProfileCatalog([]), conductorDisposition: IdleDisposition(goal),
            processSnapshotFactory: () => ProcessCommandLineSnapshot.Empty));

        Assert.Equal(string.Join(Environment.NewLine,
            "", "Goal abcdef01 Draft: Next action sample",
            "Disposition: state='Idle' confidence='High' action='monitor' blockers='none' reason='No work pending' fresh='2026-10-30 08:05:00Z'",
            "Next actions:", "  1. MonitorGoal: Inspect progress", "     command: monitor",
            "  2. DelegatePendingTask: Assign a worker", "     command: delegate",
            "Run: monitor", "", ""), actual);
    }

    [Fact]
    public void PrintOperatorDisposition_NoBlockers_PreservesExactText()
    {
        var disposition = IdleDisposition(CreateGoal());

        var actual = Capture(() => NextActionsTextView.PrintOperatorDisposition(disposition));

        Assert.Equal(string.Join(Environment.NewLine,
            "Disposition: state='Idle' confidence='High' action='monitor' blockers='none' reason='No work pending' fresh='2026-10-30 08:05:00Z'",
            ""), actual);
    }

    [Fact]
    public void PrintOperatorDisposition_MultipleBlockers_PreservesExactText()
    {
        var disposition = IdleDisposition(CreateGoal()) with
        {
            State = OperatorDispositionState.Blocked,
            Confidence = OperatorDispositionConfidence.Low,
            Reason = "Waiting for operator",
            NextSafeCommand = "pending",
            Blockers = ["approval", "dependency"]
        };

        var actual = Capture(() => NextActionsTextView.PrintOperatorDisposition(disposition));

        Assert.Equal(string.Join(Environment.NewLine,
            "Disposition: state='Blocked' confidence='Low' action='pending' blockers='approval,dependency' reason='Waiting for operator' fresh='2026-10-30 08:05:00Z'",
            ""), actual);
    }

    private static Goal CreateGoal() => new(new GoalId("abcdef01-rest"), "Next action sample",
        [new(new TaskId("12345678-rest"), "Implement", AgentRole.Developer)]);

    private static GoalOperatorDisposition IdleDisposition(Goal goal) => new(
        goal.Id, OperatorDispositionState.Idle, OperatorDispositionConfidence.High,
        "No work pending", "monitor", new DateTimeOffset(2026, 10, 30, 8, 5, 0, TimeSpan.Zero),
        [], [], []);

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
