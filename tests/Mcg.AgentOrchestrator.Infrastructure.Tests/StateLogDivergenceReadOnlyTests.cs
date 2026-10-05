using System.Reflection;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: probe, database, log files and checkpoint connection are fixture-owned.
public sealed class StateLogDivergenceReadOnlyTests
{
    [Fact]
    public async Task EveryRepositoryCallIsAQueryAndDatabaseAndGoalLogsRemainByteIdentical()
    {
        using var fixture = await StateLogDivergenceCheckRunnerTests.Fixture.CreateAsync();
        var paths = Directory.GetFiles(fixture.Workspace.GoalLifecycleEventsDirectory, "*.jsonl")
            .Append(fixture.Workspace.SqliteStatePath).ToArray();
        Assert.Equal(4, paths.Length);
        var before = paths.ToDictionary(path => path, File.ReadAllBytes);
        var repository = DispatchProxy.Create<IOrchestratorStateOutboxRepository, QueryProbe>();
        var probe = (QueryProbe)repository;
        probe.Target = SqliteOrchestratorStateRepository.OpenReadOnly(fixture.Workspace.SqliteStatePath);
        var runner = fixture.Runner(repository);
        Assert.True(runner.OnTick());
        var result = await TestHangGuard.WaitAsync(runner.CurrentRun!, "read-only check completion");
        Assert.Equal(3, result.CheckedGoals);
        Assert.Equal(2, result.EmittedEvents);
        Assert.Equal(1, probe.Calls.Count(call => call == "ListGoalMetadataAsync"));
        Assert.Equal(3, probe.Calls.Count(call => call == "LoadGoalsAsync"));
        Assert.Equal(0, probe.Calls.Count(call => call.StartsWith("Save", StringComparison.Ordinal)));
        Assert.Equal(0, probe.Calls.Count(call => call.StartsWith("Transact", StringComparison.Ordinal)));
        Assert.Equal(0, probe.Calls.Count(call => call.Contains("Outbox", StringComparison.Ordinal)));
        Assert.Equal(4, probe.Calls.Count);
        foreach (var path in paths) Assert.Equal(before[path], File.ReadAllBytes(path));
    }

    // Proxy intercepts the complete writable interface too: a future accidental cast to a
    // repository/outbox interface cannot evade the read-only assertions.
    public class QueryProbe : DispatchProxy
    {
        internal IOrchestratorStateOutboxRepository Target = null!;
        internal readonly List<string> Calls = [];
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            Calls.Add(targetMethod!.Name);
            return targetMethod.Invoke(Target, args);
        }
    }
}
