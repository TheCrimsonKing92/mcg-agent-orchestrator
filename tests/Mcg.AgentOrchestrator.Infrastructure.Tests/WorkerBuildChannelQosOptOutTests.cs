using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: fixture-owned repositories, canonical stores, build storage and contained compiler children.
public sealed class WorkerBuildChannelQosOptOutTests
{
    [Fact]
    public async Task Execute_Enabled_OptsOutRegisteredChildAndLogsOnce()
    {
        using var fixture = new WorkerBuildRequestFixture();
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(fixture.StatePath)!, "conductor-policy.json"),
            (ConductorAutonomyPolicy.Conservative with { AcceptanceChildrenOptOutExecutionThrottling = true }).ToJson());
        var calls = new ConcurrentQueue<(int Pid, uint Control, uint State)>();
        var host = new WorkerBuildRequestHost(fixture.Reference, storageRoot: fixture.Storage,
            executionQosSetter: (pid, control, state) => { calls.Enqueue((pid, control, state)); return true; },
            executionQosInterval: TimeSpan.FromMilliseconds(50));
        using var current = Process.GetCurrentProcess();
        var response = await host.ExecuteAsync(new WorkerBuildRequest(1, "qos", [WorkerBuildRequestFixture.Project], "Debug"),
            WorkerBuildRequestChannel.ReadIdentity(current), CancellationToken.None);

        Assert.Equal(WorkerBuildOutcome.Success, response.Outcome);
        var invocation = Assert.Single(fixture.Operations.Load(fixture.GoalId.Value)
            .GetTask(fixture.GoalId, fixture.TaskId).WorkerBuildInvocations);
        Assert.Equal(WorkerBuildOutcome.Success, invocation.Outcome);
        var child = Assert.Single(invocation.Children!);
        var recorded = calls.ToArray();
        Assert.Equal(child.Pid, recorded.First().Pid);
        Assert.Single(recorded.Where(call => call.Pid == child.Pid));
        Assert.All(recorded, call => Assert.Equal((1u, 0u), (call.Control, call.State)));
        Assert.Equal(recorded.Length, recorded.Select(call => call.Pid).Distinct().Count());
        using var line = JsonDocument.Parse(Assert.Single(File.ReadAllLines(LogPath(fixture))));
        Assert.Equal("build-qos", line.RootElement.GetProperty("eventKind").GetString());
        Assert.Equal(fixture.GoalId.Value, line.RootElement.GetProperty("goalId").GetString());
        Assert.Equal($"BUILD_QOS kind=worker-build opted_out={recorded.Length} tried={recorded.Length}",
            line.RootElement.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Execute_AbsentPolicy_PreservesSuccessWithoutOptOut()
    {
        using var fixture = new WorkerBuildRequestFixture();
        var setters = 0;
        var host = new WorkerBuildRequestHost(fixture.Reference, storageRoot: fixture.Storage,
            executionQosSetter: (_, _, _) => { Interlocked.Increment(ref setters); return true; },
            executionQosInterval: TimeSpan.FromMilliseconds(50));
        using var current = Process.GetCurrentProcess();
        var response = await host.ExecuteAsync(new WorkerBuildRequest(1, "qos", [WorkerBuildRequestFixture.Project], "Debug"),
            WorkerBuildRequestChannel.ReadIdentity(current), CancellationToken.None);

        Assert.Equal(WorkerBuildOutcome.Success, response.Outcome);
        Assert.Equal(0, setters);
        Assert.False(File.Exists(LogPath(fixture)));
    }

    private static string LogPath(WorkerBuildRequestFixture fixture) =>
        Path.Combine(Path.GetDirectoryName(fixture.StatePath)!, "logs", ConductEventLogWriter.CurrentFileName);
}
