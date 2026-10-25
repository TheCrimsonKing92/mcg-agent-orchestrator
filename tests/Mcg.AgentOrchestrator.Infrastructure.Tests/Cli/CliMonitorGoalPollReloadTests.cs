using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: each case owns its databases, output, mutable clock and delay delegate.
public sealed class CliMonitorGoalPollReloadTests : CliTaskQueryTestSupport
{
    [Fact]
    public void Poll_AfterProgressWrite_PrintsMarkerExactlyOnce()
    {
        var result = RunPolling(writeProgress: true);
        Assert.DoesNotContain("second poll marker", result.BeforeDelay);
        var lines = Lines(result.AfterDelay);
        Assert.Single(lines.Where(line => line.Contains("second poll marker", StringComparison.Ordinal)));
        Assert.Single(lines.Where(line => line.Contains("monitor.timeout", StringComparison.Ordinal)));
        Assert.Contains("monitor.timeout", lines[^1]);
    }

    [Fact]
    public void Poll_WithoutProgressWrite_PrintsOnlyTimeoutAfterFirstDelay()
    {
        var result = RunPolling(writeProgress: false);
        Assert.DoesNotContain("second poll marker", result.BeforeDelay);
        Assert.Contains("monitor.timeout", Assert.Single(Lines(result.AfterDelay)));
    }

    private static (string BeforeDelay, string AfterDelay) RunPolling(bool writeProgress)
    {
        var root = CreateTempDirectory();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            StateDbMigrations.EnsureUpToDate(workspace.SqliteStatePath);
            var clock = new MutableClock();
            var initialTime = clock.UtcNow;
            var kernel = new AgentOrchestratorKernel(clock);
            var task = new TaskSpec(TaskId.New(), "Polling task", AgentRole.Developer);
            var goal = kernel.CreateGoal(new GoalId("abc10000aaaaaaaaaaaaaaaaaaaaaaaa"), "Polling target", [task]);
            kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
            kernel.RecordTaskDispatch(goal.Id, task.Id,
                new TaskDispatchRecord("codex-cli", "seeded-dispatch-command", root, clock.UtcNow));
            kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Running, "initial poll progress");
            new SqliteOrchestratorStateRepository(workspace.SqliteStatePath).SaveAsync(kernel).GetAwaiter().GetResult();
            var queries = SqliteOrchestratorStateRepository.OpenReadOnly(workspace.SqliteStatePath);
            using var output = new StringWriter();
            var delays = new List<TimeSpan>();
            var beforeDelay = string.Empty;
            Task Delay(TimeSpan duration, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                delays.Add(duration);
                if (delays.Count == 1)
                {
                    beforeDelay = output.ToString();
                    if (writeProgress)
                    {
                        var writer = new SqliteOrchestratorStateRepository(workspace.SqliteStatePath);
                        var updated = writer.LoadAsync().GetAwaiter().GetResult();
                        updated.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Running, "second poll marker");
                        writer.SaveAsync(updated).GetAwaiter().GetResult();
                    }
                }
                clock.UtcNow += duration;
                return Task.CompletedTask;
            }

            string[] args = ["goals", "subscribe", "abc10000", "--timeout", "2s"];
            Assert.True(CliReadOnlyCommandRunner.IsReadOnlyCommand(args));
            var error = Assert.Throws<CliExitException>(() => CliMonitorGoalQueryCommand.Execute(
                args, queries, workspace, AgentCatalog.Default().Agents, WorkerProfileCatalog.Default(),
                clock, Delay, output));
            Assert.Equal(124, error.ExitCode);
            Assert.Equal(2, delays.Count);
            Assert.All(delays, duration => Assert.Equal(TimeSpan.FromSeconds(1), duration));
            Assert.Contains("goal.snapshot", beforeDelay);
            using var snapshot = JsonDocument.Parse(Lines(beforeDelay)[0]);
            Assert.Equal(initialTime, snapshot.RootElement.GetProperty("timestamp").GetDateTimeOffset());
            Assert.False(File.Exists(workspace.RunEventStorePath));
            return (beforeDelay, output.ToString()[beforeDelay.Length..]);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string[] Lines(string output) =>
        output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private sealed class MutableClock : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.Parse("2026-09-24T00:00:00+00:00");
    }
}
