using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

// Parallel-safe: each case owns a temporary workspace and uses AsyncLocal console capture.
public sealed class ExperimentStopExtensionTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 3, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Extend_PreservesOriginalJsonAndAppendsTargetHistory()
    {
        WithWorkspace(workspace =>
        {
            var store = new ExperimentStore(workspace.ExperimentStorePath);
            var record = store.AddAsync(Spec() with
                { StopRule = new(3, ExperimentStopUnit.Goals) }).GetAwaiter().GetResult();
            Assert.Equal("{\"count\":3,\"unit\":\"goals\"}", ReadStopJson(workspace.ExperimentStorePath, record.Id));
            Assert.Null(record.Spec.StopRule.Extensions);
            Assert.True(store.ExtendStopRuleAsync(record.Id, 8, "More goals needed", At).GetAwaiter().GetResult());
            var extended = store.ResolveAsync(record.Id).GetAwaiter().GetResult()!;
            Assert.Equal(3, extended.Spec.StopRule.Count);
            Assert.Equal(8, ExperimentStopTarget.Effective(extended.Spec.StopRule));
            var first = Assert.Single(extended.Spec.StopRule.Extensions!);
            Assert.Equal(new ExperimentStopExtension(3, 8, "More goals needed", At), first);
            Assert.True(store.ExtendStopRuleAsync(record.Id, 12, "Confirm the trend", At.AddDays(1)).GetAwaiter().GetResult());
            var again = store.ResolveAsync(record.Id).GetAwaiter().GetResult()!;
            Assert.Equal(3, again.Spec.StopRule.Count);
            Assert.Equal(12, ExperimentStopTarget.Effective(again.Spec.StopRule));
            Assert.Equal(new[] { first, new ExperimentStopExtension(8, 12, "Confirm the trend", At.AddDays(1)) },
                again.Spec.StopRule.Extensions);
        });
    }

    [Fact]
    public void Extend_RefusalsLeaveStoredJsonUnchanged()
    {
        WithWorkspace(workspace =>
        {
            var store = new ExperimentStore(workspace.ExperimentStorePath);
            var record = store.AddAsync(Spec() with
                { StopRule = new(3, ExperimentStopUnit.Goals) }).GetAwaiter().GetResult();
            Assert.True(store.ExtendStopRuleAsync(record.Id, 8, "More goals", At).GetAwaiter().GetResult());
            var before = ReadStopJson(workspace.ExperimentStorePath, record.Id);
            foreach (var count in new[] { 8, 5 })
            {
                Assert.Throws<ArgumentException>(() => store.ExtendStopRuleAsync(record.Id, count, "More goals", At).GetAwaiter().GetResult());
                Assert.Equal(before, ReadStopJson(workspace.ExperimentStorePath, record.Id));
            }
            Assert.Throws<ArgumentException>(() => store.ExtendStopRuleAsync(record.Id, 12, " \t ", At).GetAwaiter().GetResult());
            Assert.Equal(before, ReadStopJson(workspace.ExperimentStorePath, record.Id));
            store.DecideAsync(record.Id, ExperimentOutcomeState.Inconclusive, "evidence", "Stop").GetAwaiter().GetResult();
            Assert.Throws<InvalidOperationException>(() => store.ExtendStopRuleAsync(record.Id, 12, "More goals", At).GetAwaiter().GetResult());
            Assert.Equal(before, ReadStopJson(workspace.ExperimentStorePath, record.Id));
            var unknown = new string('f', 32);
            Assert.Null(ReadStopJson(workspace.ExperimentStorePath, unknown));
            Assert.Throws<InvalidOperationException>(() => store.ExtendStopRuleAsync(unknown, 12, "More goals", At).GetAwaiter().GetResult());
            Assert.Null(ReadStopJson(workspace.ExperimentStorePath, unknown));
            Assert.Equal(before, ReadStopJson(workspace.ExperimentStorePath, record.Id));
        });
    }

    [Fact]
    public void Reading_UsesExtendedTargetOverTheSameObservedGoals()
    {
        WithWorkspace(workspace =>
        {
            var kernel = Seed(workspace);
            var store = new ExperimentStore(workspace.ExperimentStorePath);
            var record = store.AddAsync(Spec()).GetAwaiter().GetResult();
            var before = ExperimentReading.Evaluate(record, kernel.Goals, new Dictionary<string, DateTimeOffset>(), [], At, null);
            Assert.Equal(2, before.ObservedCount);
            Assert.True(before.StopRuleMet);
            Assert.True(store.ExtendStopRuleAsync(record.Id, 5, "More goals", At).GetAwaiter().GetResult());
            var updated = store.ResolveAsync(record.Id).GetAwaiter().GetResult()!;
            var after = ExperimentReading.Evaluate(updated, kernel.Goals, new Dictionary<string, DateTimeOffset>(), [], At, null);
            Assert.Equal(before.ObservedCount, after.ObservedCount);
            Assert.False(after.StopRuleMet);
            // Keep the prescribed main-file revert compilable so it fails on behavior.
            Assert.Equal(5, Assert.IsType<int>(after.GetType().GetProperty("StopTarget")?.GetValue(after)));
            var output = CaptureConsole(() => ExperimentReading.Render(after));
            Assert.Contains("stop rule: 2 of 5 goals (not met)", output.Split(Environment.NewLine));
        });
    }

    private static string? ReadStopJson(string path, string id)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT stop_rule_json FROM experiments WHERE id=$id";
        command.Parameters.AddWithValue("$id", id);
        return command.ExecuteScalar() as string;
    }

    private static ExperimentSpec Spec() => new("A shorter brief reduces rounds per landing",
        new(ExperimentInterventionKind.BriefOrPromptChange, "Remove repeated preamble"),
        new(ExperimentBaselineKind.BeforeAfterWindow, At.AddDays(-2), At.AddDays(-1)),
        ["rounds-per-landing", "landings-per-hour"],
        new("productive-rounds", new("productive-rounds", "<", -10)),
        new(2, ExperimentStopUnit.Goals),
        new([new("rounds-per-landing", "<", 0)], [new("rounds-per-landing", ">", 0)]));

    private static AgentOrchestratorKernel Seed(OrchestratorWorkspace workspace)
    {
        var snapshots = Enumerable.Range(0, 2).Select(index =>
        {
            var id = new string((char)('a' + index), 32);
            var task = new TaskSnapshot($"{id}-0", "Developer", AgentRole.Developer, WorkTaskStatus.Completed,
                null, null, null, [], null, null, DispatchHistory:
                [new TaskDispatchSnapshot("worker", "command", workspace.RootDirectory, At.AddHours(-index - 1))]);
            return new GoalSnapshot(id, id, GoalStatus.Completed, [task], []);
        }).ToArray();
        var kernel = AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot(snapshots, []));
        StateDbMigrations.EnsureUpToDate(workspace.SqliteStatePath);
        new SqliteOrchestratorStateRepository(workspace.SqliteStatePath).SaveAsync(kernel).GetAwaiter().GetResult();
        return kernel;
    }

    private static void WithWorkspace(Action<OrchestratorWorkspace> action)
    {
        var root = Path.Combine(Path.GetTempPath(), "experiment-extension-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);
        try { action(OrchestratorWorkspace.ForDirectory(root)); }
        finally { Directory.Delete(root, true); }
    }
}
