using System.Reflection;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: Capture routes output through AsyncLocal; fixtures have no shared state.
public sealed class TaskTextViewCharacterizationTests
{
    private static readonly DateTimeOffset Start = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

    // Literal formats derived from ConsoleViews.TaskDetails.cs at 9ee8a17b8.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PrintTask_WithoutRecords_PreservesFullLineSequence(bool assigned)
    {
        var (_, goal, task) = Fixture(assigned);
        Assert.Null(task.LastExecution);
        Assert.Null(task.LastDispatch);
        Assert.Null(task.LastProcess);
        Assert.Null(task.LastVerification);

        Assert.Equal(new[]
        {
            $"12345678 [{(assigned ? "Assigned" : "Pending")}] Developer: Render task details",
            $"Assigned agent: {(assigned ? task.AssignedAgentId!.Value : "unassigned")}",
            "Verification plan: none",
            "Task timeline:",
            $"  {Start.AddSeconds(1):u} TaskNote: First task event.",
            $"  {Start.AddSeconds(2):u} TaskNote: Newest task event."
        }, CaptureLines(() => TaskTextView.PrintTask(goal, task)));
    }

    [Fact]
    public void PrintTask_ExecutionAndDispatch_PreservesBlocksAndContextUsage()
    {
        var (kernel, goal, task) = Fixture(execution: true);
        var dispatchedAt = Start.AddMinutes(5);
        kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord(
            "local-worker", "run cmd", @"C:\work", dispatchedAt,
            ProviderName: "Ollama", ModelName: "qwen-local", PromptCharacterCount: 2048,
            ContextPackageReceipt: new WorkerContextPackageReceipt("test", [],
                ProviderUsageValue.Reported(90000), ProviderUsageValue.Reported(60000),
                ProviderUsageValue.Reported(2000), RenderedPromptBytes: 512, ModelInputTokenEstimate: 30000)));

        Assert.Equal(new[]
        {
            "12345678 [Running] Developer: Render task details",
            $"Assigned agent: {task.AssignedAgentId!.Value}",
            "Verification plan: none",
            "Last execution: Ollama/qwen-local by Local developer",
            "Stop reason: stop",
            "Usage: input=100 output=20 maxOutput=4000",
            "Prompt size: 1234 chars",
            "Output:",
            "done",
            "Last dispatch: worker=local-worker command=run cmd",
            "Dispatch model: Ollama/qwen-local complexity=unknown reasoning=default",
            "Prompt size: 2048 chars",
            "Context usage: input=90000, cached=60000, uncached=30000, output=2000, prompt-bytes=512, estimate=30000, harness-overhead=60000",
            @"Working directory: C:\work",
            $"Dispatched: {dispatchedAt:u}",
            "Task timeline:",
            $"  {Start.AddSeconds(1):u} TaskNote: First task event.",
            $"  {Start.AddSeconds(2):u} TaskNote: Newest task event.",
            $"  {dispatchedAt:u} TaskDispatchRecorded: Dispatched to local-worker using Ollama/qwen-local: run cmd"
        }, CaptureLines(() => TaskTextView.PrintTask(goal, task)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Refresh_WithoutRecords_PreservesPositionsAndOptionalNewestHistory(bool history)
    {
        var (_, goal, task) = Fixture();
        var lines = CaptureLines(() => TaskTextView.PrintRefreshDispatchResult(goal, task,
            new CliArgumentParser.RefreshDispatchOptions([], history, 1)));

        Assert.Equal(history ? 13 : 11, lines.Length);
        Assert.Equal($"Task: id=12345678 status=Assigned role=Developer assigned={task.AssignedAgentId!.Value}", lines[0]);
        Assert.Equal("Dispatch: none", lines[1]);
        Assert.Equal("Process: none", lines[2]);
        Assert.StartsWith("Dispatch state: ", lines[3]);
        Assert.StartsWith("Heartbeat: ", lines[4]);
        Assert.Equal("Exit evidence: none", lines[5]);
        Assert.Equal("Exit classification: none", lines[6]);
        Assert.Equal("Structured blockers: status=unknown value=none", lines[7]);
        Assert.Equal("Latest verification: none", lines[8]);
        Assert.StartsWith("Disposition: ", lines[9]);
        Assert.StartsWith("Next action: ", lines[10]);
        if (history)
        {
            Assert.Equal("Task timeline:", lines[11]);
            Assert.Equal($"  {Start.AddSeconds(2):u} TaskNote: Newest task event.", lines[12]);
        }
        else
        {
            Assert.DoesNotContain("Task timeline:", lines);
        }
    }

    [Fact]
    public void GoalTimeline_PreservesChronologicalGoalAndTaskLines()
    {
        var (_, goal, _) = Fixture();
        Assert.Equal(new[]
        {
            "Timeline:",
            $"  {Start:u} GoalCreated goal: Goal created.",
            $"  {Start.AddSeconds(1):u} TaskNote 12345678: First task event.",
            $"  {Start.AddSeconds(2):u} TaskNote 12345678: Newest task event."
        }, CaptureLines(() => TaskTimelineTextView.PrintTimeline(goal)));
    }

    [Fact]
    public void TaskTimeline_LimitOne_PreservesOnlyNewestEvent()
    {
        var (_, goal, task) = Fixture();
        Assert.Equal(new[] { $"  {Start.AddSeconds(2):u} TaskNote: Newest task event." },
            CaptureLines(() => TaskTimelineTextView.PrintTaskTimeline(goal, task, 1)));
    }

    [Fact]
    public void TaskTimeline_WithoutEvents_PreservesEmptyMessage()
    {
        var (_, goal, task) = Fixture(events: false);
        Assert.DoesNotContain(goal.Timeline, item => item.TaskId == task.Id);
        Assert.Equal(new[] { "  no task timeline events" },
            CaptureLines(() => TaskTimelineTextView.PrintTaskTimeline(goal, task)));
    }

    [Theory]
    [InlineData(null, "unknown")]
    [InlineData("  ", "unknown")]
    [InlineData("a\n  b", "a b")]
    public void OneLine_PreservesFallbackAndWhitespace(string? value, string expected)
    {
        Assert.Equal(expected, TaskTimelineTextView.OneLine(value));
    }

    [Fact]
    public void OneLine_LongWord_PreservesExisting401CharacterTruncation()
    {
        var value = new string('x', 500);
        var actual = TaskTimelineTextView.OneLine(value);
        // HEAD takes 400 - 14 characters but appends a 15-character suffix.
        Assert.Equal(value[..386] + "... [truncated]", actual);
        Assert.Equal(401, actual.Length);
    }

    [Fact]
    public void Delegation_EmptyAndSingleAssignment_PreservesExactLines()
    {
        var (_, goal, task) = Fixture();
        Assert.Equal(new[] { "No pending tasks were delegated." }, CaptureLines(() =>
            TaskTextView.PrintDelegationPlan(goal, new DelegationPlan(goal.Id, []))));
        var agent = task.AssignedAgentId!;
        Assert.Equal(new[] { "Delegated tasks:", $"  task 1 -> {agent.Value} (Developer)" }, CaptureLines(() =>
            TaskTextView.PrintDelegationPlan(goal, new DelegationPlan(goal.Id,
                [new TaskAssignment(task.Id, agent, AgentRole.Developer)]))));
    }

    [Fact]
    public void PendingInput_EmptyKernel_PreservesExactMessage()
    {
        var kernel = new AgentOrchestratorKernel(new FixedClock(Start));
        Assert.Equal(new[] { "No pending human input." },
            CaptureLines(() => TaskTextView.PrintPendingHumanInput(kernel)));
    }

    [Fact]
    public void ExtractedOwners_LeaveNoPartialFilesOrConsoleViewsForwarders()
    {
        var cliRoot = Path.Combine(VerifiedRepositoryRoot.Find(), "src", "Mcg.AgentOrchestrator.App", "Cli");
        Assert.False(File.Exists(Path.Combine(cliRoot, "ConsoleViews.TaskDetails.cs")));
        Assert.False(File.Exists(Path.Combine(cliRoot, "ConsoleViews.ContextUsage.cs")));
        foreach (var owner in new[] { "TaskTextView", "TaskTimelineTextView" })
        {
            var source = File.ReadAllText(Path.Combine(cliRoot, owner + ".cs"));
            Assert.Contains("internal static class " + owner, source);
            Assert.DoesNotMatch(@"\bpartial\s+class\b", source);
        }

        string[] movedNames =
        [
            "PrintDelegationPlan", "PrintTask", "PrintRefreshDispatchResult", "PrintTimeline",
            "PrintTaskTimeline", "PrintPendingHumanInput", "OneLine", "IsOutputTokenLimitHit",
            "PrintContextUsageLine", "FormatContextUsageLine"
        ];
        var members = typeof(ConsoleViews).GetMembers(
            BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public |
            BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
        Assert.DoesNotContain(members, member => movedNames.Contains(member.Name));
    }

    [Fact]
    public void ConsoleViewsRatchet_UsesReducedAtOrUnderBudget()
    {
        var ceiling = Assert.Single(SourceSizeRatchet.SeededClassCeilings,
            row => row.ClassName == "ConsoleViews");
        Assert.True(ceiling.MaximumTotalLineCount <= 2462);
        Assert.True(ceiling.MaximumPartialFileCount <= 33);
        Assert.Empty(SourceSizeRatchet.EvaluateClasses(VerifiedRepositoryRoot.Find(), [ceiling]));
    }

    private static (AgentOrchestratorKernel Kernel, Goal Goal, TaskSpec Task) Fixture(
        bool assigned = true, bool execution = false, bool events = true)
    {
        var clock = new FixedClock(Start);
        var kernel = new AgentOrchestratorKernel(clock);
        var goal = kernel.CreateGoal(new GoalId("goal0001"), "Characterize task output",
            [new TaskSpec(new TaskId("12345678-task"), "Render task details", AgentRole.Developer)]);
        if (assigned)
            kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        var snapshot = kernel.ExportSnapshot();
        var goalSnapshot = Assert.Single(snapshot.Goals);
        var taskSnapshot = Assert.Single(goalSnapshot.Tasks);
        if (execution)
        {
            taskSnapshot = taskSnapshot with
            {
                LastExecution = new TaskExecutionSnapshot(taskSnapshot.AssignedAgentId!, "Local developer",
                    "Ollama", "qwen-local", "done", "stop", 100, 20, Start.AddSeconds(3),
                    MaxOutputTokens: 4000, PromptCharacterCount: 1234)
            };
        }
        // Deliberately store events out of order so sorting and TakeLast are observable.
        ProgressEventSnapshot[] timeline = events
            ? [new(goalSnapshot.Id, taskSnapshot.Id, ProgressKind.TaskNote, "Newest task event.", Start.AddSeconds(2)),
               new(goalSnapshot.Id, null, ProgressKind.GoalCreated, "Goal created.", Start),
               new(goalSnapshot.Id, taskSnapshot.Id, ProgressKind.TaskNote, "First task event.", Start.AddSeconds(1))]
            : [new(goalSnapshot.Id, null, ProgressKind.GoalCreated, "Goal created.", Start)];
        kernel = AgentOrchestratorKernel.FromSnapshot(snapshot with
        {
            Goals = [goalSnapshot with { Tasks = [taskSnapshot], Timeline = timeline }]
        }, new FixedClock(Start.AddMinutes(5)));
        goal = Assert.Single(kernel.Goals);
        return (kernel, goal, Assert.Single(goal.Tasks));
    }

    private static string[] CaptureLines(Action action) =>
        AsyncLocalConsoleRouter.Capture(action).TrimEnd('\r', '\n').Split(Environment.NewLine);

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;
    }
}
