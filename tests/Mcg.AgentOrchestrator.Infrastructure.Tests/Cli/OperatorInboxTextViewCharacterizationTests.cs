using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Xunit;

// Parallel-safe: AsyncLocal console capture and immutable, local fixtures.
public sealed class OperatorInboxTextViewCharacterizationTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Print_EmptyWithoutFilter_PreservesExactText(string? filter)
    {
        var report = new OperatorInboxReport(filter, 0, 0, 0, []);

        var actual = AsyncLocalConsoleRouter.Capture(() =>
            OperatorInboxTextView.PrintOperatorInbox(report));

        Assert.Equal(string.Join(Environment.NewLine,
            "",
            "Operator inbox: 0 open; 0 acknowledged",
            "  none",
            "",
            ""), actual);
    }

    [Fact]
    public void Print_EmptyWithFilter_PreservesExactText()
    {
        var report = new OperatorInboxReport("goal-abc", 0, 0, 0, []);

        var actual = AsyncLocalConsoleRouter.Capture(() =>
            OperatorInboxTextView.PrintOperatorInbox(report));

        Assert.Equal(string.Join(Environment.NewLine,
            "",
            "Operator inbox: 0 open; 0 acknowledged",
            "Goal filter: goal-abc",
            "  none",
            "",
            ""), actual);
    }

    [Fact]
    public void Print_GoalAndAcknowledgedTask_PreservesExactText()
    {
        var raised = new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);
        var goalItem = new OperatorInboxItem("inbox-goal", OperatorInboxKind.HumanInput,
            OperatorInboxSeverity.Blocker, "goal-id", "goal-abc", "Objective", null, null,
            "Choose a path", "Decision needed", "Two options", "Answer the question",
            "answer question choice", "test", raised, false, null, null);
        var taskItem = goalItem with
        {
            Id = "inbox-task", Kind = OperatorInboxKind.FailedTask,
            Severity = OperatorInboxSeverity.Warning, TaskId = "task-id", TaskNumber = 7,
            Acknowledged = true, AcknowledgedAt = raised, AcknowledgementNote = "Seen"
        };
        var report = new OperatorInboxReport("goal-abc", 2, 1, 1, [goalItem, taskItem]);

        var actual = AsyncLocalConsoleRouter.Capture(() =>
            OperatorInboxTextView.PrintOperatorInbox(report));

        Assert.Equal(string.Join(Environment.NewLine,
            "",
            "Operator inbox: 1 open; 1 acknowledged",
            "Goal filter: goal-abc",
            "  inbox-goal [Blocker] HumanInput goal-abc/goal",
            "     title: Choose a path",
            "     message: Decision needed",
            "     evidence: Two options",
            "     action: Answer the question",
            "     command: answer question choice",
            "  inbox-task [Warning] FailedTask acknowledged goal-abc/task 7",
            "     title: Choose a path",
            "     message: Decision needed",
            "     evidence: Two options",
            "     action: Answer the question",
            "     command: answer question choice",
            "",
            ""), actual);
    }

    [Fact]
    public void ExtractedOwner_LeavesNoPartialFileOrConsoleViewsForwarders()
    {
        var cliRoot = Path.Combine(VerifiedRepositoryRoot.Find(), "src", "Mcg.AgentOrchestrator.App", "Cli");
        Assert.False(File.Exists(Path.Combine(cliRoot, "ConsoleViews.OperatorInbox.cs")));
        var source = File.ReadAllText(Path.Combine(cliRoot, "OperatorInboxTextView.cs"));
        Assert.Contains("internal static class OperatorInboxTextView", source);
        Assert.DoesNotMatch(@"\bpartial\s+class\b", source);
    }
}
