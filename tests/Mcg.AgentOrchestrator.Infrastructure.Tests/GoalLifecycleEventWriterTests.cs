using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.Infrastructure.Tests;

public sealed class GoalLifecycleEventWriterTests
{
    [Xunit.Fact]
    public void KernelTimelineEventsWriteAppendOnlyJsonlInOrder()
    {
        var root = CreateTempDirectory();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            var kernel = new AgentOrchestratorKernel(new TestClock(new DateTimeOffset(2026, 7, 8, 1, 2, 3, TimeSpan.Zero)));
            var writer = new GoalLifecycleEventWriter(workspace.GoalLifecycleEventsDirectory);
            kernel.SetEventWriter(writer);

            var goal = kernel.CreateGoal(
                "Event stream goal",
                [new TaskSpec(TaskId.New(), "Implement event stream", AgentRole.Developer)]);
            kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
            var task = goal.Tasks[0];
            kernel.RecordTaskDispatch(
                goal.Id,
                task.Id,
                new TaskDispatchRecord("worker", "codex exec", root, DateTimeOffset.UtcNow));
            kernel.RecordDispatchExecutionResult(
                goal.Id,
                task.Id,
                new TaskVerificationRecord(
                    "codex exec",
                    root,
                    0,
                    "WORKER_RESULT: files: none commands: none tests: pass commit: none blockers: none model_fit: test skills: none confidence: high END_WORKER_RESULT",
                    "",
                    DateTimeOffset.UtcNow));
            kernel.CompleteGoal(goal.Id, "verified");
            kernel.RecordGoalPolicyDecision(goal.Id, "landed");
            kernel.RequestHumanInput(goal.Id, task.Id, "Need operator decision.");

            var path = Path.Combine(workspace.GoalLifecycleEventsDirectory, $"{goal.Id.Value}.jsonl");
            var lines = File.ReadAllLines(path);

            Xunit.Assert.Equal([
                "GoalCreated",
                "TaskDelegated",
                "TaskDispatched",
                "TaskVerified",
                "TaskCompleted",
                "GoalLifecycleDecision",
                "GoalLifecycleDecision",
                "GoalEscalated"
            ], lines.Select(EventType).ToArray());
            Xunit.Assert.Equal(Enumerable.Range(0, lines.Length), lines.Select(Cursor));
            Xunit.Assert.All(lines, line => JsonDocument.Parse(line).Dispose());
            Xunit.Assert.EndsWith(Path.Combine(".orchestrator", "goal-events"), workspace.GoalLifecycleEventsDirectory);
            Xunit.Assert.False(Directory.Exists(Path.Combine(root, ".orchestrator", "events")));
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Xunit.Fact]
    public void WriterContinuesCursorWhenAppendingToExistingJsonl()
    {
        var root = CreateTempDirectory();
        try
        {
            var eventsDirectory = Path.Combine(root, ".orchestrator", "goal-events");
            Directory.CreateDirectory(eventsDirectory);
            var goalId = GoalId.New();
            var path = Path.Combine(eventsDirectory, $"{goalId.Value}.jsonl");
            File.WriteAllText(path, "{\"cursor\":0,\"eventType\":\"GoalCreated\"}" + Environment.NewLine);

            var writer = new GoalLifecycleEventWriter(eventsDirectory);
            writer.AppendCleanedUp(goalId);

            var lines = File.ReadAllLines(path);
            Xunit.Assert.Equal(2, lines.Length);
            Xunit.Assert.Equal(0, Cursor(lines[0]));
            Xunit.Assert.Equal(1, Cursor(lines[1]));
            Xunit.Assert.Equal("CleanedUp", EventType(lines[1]));
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Xunit.Fact]
    public void GoalEventsCommandReplaysJsonlForPrefix()
    {
        var root = CreateTempDirectory();
        try
        {
            var eventsDirectory = Path.Combine(root, ".orchestrator", "goal-events");
            Directory.CreateDirectory(eventsDirectory);
            var goalId = GoalId.New();
            File.WriteAllLines(
                Path.Combine(eventsDirectory, $"{goalId.Value}.jsonl"),
                [
                    "{\"cursor\":0,\"eventType\":\"GoalCreated\"}",
                    "{\"cursor\":1,\"eventType\":\"TaskDelegated\"}"
                ]);

            using var output = new StringWriter();
            GoalEventsCommand.RunAsync(eventsDirectory, goalId.Value[..8], follow: false, output, cancellationToken: CancellationToken.None).GetAwaiter().GetResult();

            Xunit.Assert.Equal(
                "{\"cursor\":0,\"eventType\":\"GoalCreated\"}" + Environment.NewLine +
                "{\"cursor\":1,\"eventType\":\"TaskDelegated\"}" + Environment.NewLine,
                output.ToString());
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Xunit.Fact]
    public void GoalEventsCliCommandReplaysJsonlForPrefix()
    {
        var root = CreateTempDirectory();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            Directory.CreateDirectory(workspace.GoalLifecycleEventsDirectory);
            var goalId = GoalId.New();
            File.WriteAllText(
                Path.Combine(workspace.GoalLifecycleEventsDirectory, $"{goalId.Value}.jsonl"),
                "{\"cursor\":0,\"eventType\":\"GoalCreated\"}" + Environment.NewLine);
            var kernel = new AgentOrchestratorKernel();
            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var providers = new InMemoryModelProviderRegistry([]);
            var profiles = WorkerProfileCatalog.Default();
            Goal? currentGoal = null;

            var output = CaptureConsole(() => CliCommandDispatcher.ExecuteCommand(
                ["goal-events", goalId.Value[..8]],
                kernel,
                workspace,
                ref agents,
                providers,
                ref profiles,
                ref currentGoal));

            Xunit.Assert.Equal("{\"cursor\":0,\"eventType\":\"GoalCreated\"}" + Environment.NewLine, output);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Xunit.Fact]
    public async Task GoalEventsCommandFollowsAppendedJsonl()
    {
        var root = CreateTempDirectory();
        try
        {
            var eventsDirectory = Path.Combine(root, ".orchestrator", "goal-events");
            Directory.CreateDirectory(eventsDirectory);
            var goalId = GoalId.New();
            var path = Path.Combine(eventsDirectory, $"{goalId.Value}.jsonl");
            File.WriteAllText(path, "{\"cursor\":0,\"eventType\":\"GoalCreated\"}" + Environment.NewLine);
            using var output = new StringWriter();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

            var followTask = GoalEventsCommand.RunAsync(eventsDirectory, goalId.Value[..8], follow: true, output, cancellationToken: cts.Token);
            await WaitUntilAsync(() => output.ToString().Contains("GoalCreated", StringComparison.Ordinal), cts.Token);
            await File.AppendAllTextAsync(path, "{\"cursor\":1,\"eventType\":\"TaskCompleted\"}" + Environment.NewLine, cts.Token);
            await WaitUntilAsync(() => output.ToString().Contains("TaskCompleted", StringComparison.Ordinal), cts.Token);
            cts.Cancel();

            await Xunit.Assert.ThrowsAnyAsync<OperationCanceledException>(() => followTask);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    private static string EventType(string line)
    {
        using var document = JsonDocument.Parse(line);
        return document.RootElement.GetProperty("eventType").GetString()!;
    }

    private static int Cursor(string line)
    {
        using var document = JsonDocument.Parse(line);
        return document.RootElement.GetProperty("cursor").GetInt32();
    }

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken cancellationToken)
    {
        while (!condition())
        {
            await Task.Delay(50, cancellationToken);
        }
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "mcg-goal-events-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static string CaptureConsole(Action action)
    {
        var original = Console.Out;
        using var writer = new StringWriter();
        try
        {
            Console.SetOut(writer);
            action();
            return writer.ToString();
        }
        finally
        {
            Console.SetOut(original);
        }
    }

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    private sealed class TestClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }
}
