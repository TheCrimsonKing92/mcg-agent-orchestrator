using System.Diagnostics;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit.v3;

[Collection("EnvMutation")]
public sealed class AdvanceLoopTestsDispatchProcessLeakGuard
{
    [Fact]
    public void After_UnawaitedRealDispatch_FailsWithPidAndDispatchFile()
    {
        var root = InfrastructureTestSupport.CreateTempDirectory();
        var (kernel, goal, task) = PrepareDispatch(root);
        using var owned = new TestOwnedDispatchProcesses(kernel, goal);
        var record = new BackgroundDispatchRunner().StartLatestDispatch(
            kernel, goal.Id, task.Id, OrchestratorWorkspace.ForDirectory(root).LogDirectory);
        Assert.Same(record, task.LastProcess);
        var dispatchFile = record.ExitCodePath.Replace(".exit.txt", ".dispatch.json", StringComparison.Ordinal);
        Assert.True(File.Exists(dispatchFile));

        // Prove enumeration and the real host path before exercising the registered hook.
        var snapshot = ProcessCommandLines.Snapshot([record.ProcessId]);
        Assert.Null(snapshot.Failure);
        Assert.True(snapshot.TryGetRecord(record.ProcessId, out var host));
        Assert.Equal(ProcessInspectionStatus.Available, host.Status);
        Assert.Equal(Environment.ProcessId, host.ParentProcessId);
        Assert.Contains(DispatchProcessHost.SubcommandName, host.CommandLine!);

        var test = Assert.IsAssignableFrom<IXunitTest>(TestContext.Current.Test);
        Assert.True(DispatchProcessLeakGuard.HasScope(test.UniqueID), "Assembly Before hook did not run.");
        var failure = Assert.Throws<Xunit.Sdk.XunitException>(() =>
            new DispatchProcessLeakGuardAttribute().After(
                GetType().GetMethod(nameof(After_UnawaitedRealDispatch_FailsWithPidAndDispatchFile))!, test));
        Assert.Contains($"pid={record.ProcessId}", failure.Message);
        Assert.Contains($"dispatch={dispatchFile}", failure.Message);
    }

    [Fact]
    public async Task After_AwaitedRealDispatch_Passes()
    {
        var root = InfrastructureTestSupport.CreateTempDirectory();
        var (kernel, goal, task) = PrepareDispatch(root);
        using var owned = new TestOwnedDispatchProcesses(kernel, goal);
        var record = new BackgroundDispatchRunner().StartLatestDispatch(
            kernel, goal.Id, task.Id, OrchestratorWorkspace.ForDirectory(root).LogDirectory);
        Assert.Same(record, task.LastProcess);
        AdvanceLoopTests.ReleaseBlockingWorkers(root);
        await TestOwnedDispatchProcesses.AwaitCompletionAsync(record);
        Assert.True(File.Exists(record.ExitCodePath));
        Assert.Equal("0", File.ReadAllText(record.ExitCodePath).Trim());

        var test = Assert.IsAssignableFrom<IXunitTest>(TestContext.Current.Test);
        Assert.True(DispatchProcessLeakGuard.HasScope(test.UniqueID), "Assembly Before hook did not run.");
        new DispatchProcessLeakGuardAttribute().After(
            GetType().GetMethod(nameof(After_AwaitedRealDispatch_Passes))!, test);
    }

    [Theory]
    [InlineData("dotnet exec App.dll __dispatch-run \"C:\\test root\\host.dispatch.json\"", "C:\\test root\\host.dispatch.json")]
    [InlineData("dotnet exec App.dll __dispatch-run C:\\test\\host.dispatch.json", "C:\\test\\host.dispatch.json")]
    public void FindLeaks_OwnedHost_ParsesDispatchArgument(string command, string expected)
    {
        var start = DateTimeOffset.UnixEpoch;
        var record = new ProcessInspectionRecord(11, 10, "dotnet", null, start, command, ProcessInspectionStatus.Available);
        var snapshot = new ProcessCommandLineSnapshot(new Dictionary<int, ProcessInspectionRecord> { [11] = record });
        var leak = Assert.Single(DispatchProcessLeakGuard.FindLeaks(start, start, 10, snapshot));
        Assert.Equal(11, leak.ProcessId);
        Assert.Equal(expected, leak.DispatchFile);
    }

    [Theory]
    [InlineData(9, 0, ProcessInspectionStatus.Available)] // Another parent.
    [InlineData(10, -1, ProcessInspectionStatus.Available)] // Pre-existing host.
    [InlineData(10, 1, ProcessInspectionStatus.Available)] // Started after the test returned.
    [InlineData(10, 0, ProcessInspectionStatus.Exited)]
    [InlineData(10, 0, ProcessInspectionStatus.DeadOrRecycled)]
    public void FindLeaks_UnownedOrExitedHost_IgnoresIt(int parent, int seconds, ProcessInspectionStatus status)
    {
        var start = DateTimeOffset.UnixEpoch;
        var record = new ProcessInspectionRecord(11, parent, "dotnet", null, start.AddSeconds(seconds),
            "dotnet exec App.dll __dispatch-run host.dispatch.json", status);
        var snapshot = new ProcessCommandLineSnapshot(new Dictionary<int, ProcessInspectionRecord> { [11] = record });
        Assert.Empty(DispatchProcessLeakGuard.FindLeaks(start, start, 10, snapshot));
    }

    [Fact]
    public void Dispose_UnawaitedDispatch_ReapsRecordedHost()
    {
        var root = InfrastructureTestSupport.CreateTempDirectory();
        var (kernel, goal, task) = PrepareDispatch(root);
        using var owned = new TestOwnedDispatchProcesses(kernel, goal);
        var record = new BackgroundDispatchRunner().StartLatestDispatch(
            kernel, goal.Id, task.Id, OrchestratorWorkspace.ForDirectory(root).LogDirectory);
        using var host = Process.GetProcessById(record.ProcessId);
        Assert.False(host.HasExited);

        owned.Dispose();

        Assert.True(host.HasExited, $"Test-owned dispatch host pid={record.ProcessId} did not exit during disposal.");
        Assert.True(task.LastProcess!.WasCancelled);
    }

    private static (AgentOrchestratorKernel Kernel, Goal Goal, TaskSpec Task) PrepareDispatch(string root)
    {
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Block until explicitly released", AgentRole.Developer);
        var goal = kernel.CreateGoal("Dispatch leak guard contract", [task]);
        var agent = new AgentDefinition(new AgentId("guard-developer"), "Guard developer", AgentRole.Developer,
            new ModelProfile("OpenAI", "gpt-5.4-mini", ModelCapability.Text, SubscriptionMode.ApiKey),
            ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
            Subscription: new SubscriptionLaunchProfile("codex-cli"));
        kernel.ActivateGoal(goal.Id, [agent]);
        kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("local",
            AdvanceLoopTests.BlockingWorkerCommand(root, "Write-Output released"), root, DateTimeOffset.UtcNow));
        return (kernel, goal, task);
    }
}
