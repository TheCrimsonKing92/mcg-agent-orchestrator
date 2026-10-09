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
        using var host = Process.GetProcessById(record.ProcessId);
        var dispatchFile = DispatchFile(record);
        var test = Assert.IsAssignableFrom<IXunitTest>(TestContext.Current.Test);
        Assert.Same(record, task.LastProcess);
        Assert.True(File.Exists(dispatchFile));
        Assert.False(host.HasExited);
        Assert.True(DispatchProcessLeakGuard.HasHost(test.UniqueID, record.ProcessId, dispatchFile),
            "Assembly guard did not observe the real dispatch launch.");

        var failure = Assert.Throws<Xunit.Sdk.XunitException>(() =>
            new DispatchProcessLeakGuardAttribute().After(
                GetType().GetMethod(nameof(After_UnawaitedRealDispatch_FailsWithPidAndDispatchFile))!, test));
        Assert.Contains($"pid={record.ProcessId}", failure.Message);
        Assert.Contains($"dispatch={dispatchFile}", failure.Message);
        Assert.True(host.HasExited, "The reported leak must be reaped before assembly cleanup.");
    }

    [Fact]
    public async Task After_AwaitedRealDispatch_Passes()
    {
        var root = InfrastructureTestSupport.CreateTempDirectory();
        var (kernel, goal, task) = PrepareDispatch(root);
        using var owned = new TestOwnedDispatchProcesses(kernel, goal);
        var record = new BackgroundDispatchRunner().StartLatestDispatch(
            kernel, goal.Id, task.Id, OrchestratorWorkspace.ForDirectory(root).LogDirectory);
        var test = Assert.IsAssignableFrom<IXunitTest>(TestContext.Current.Test);
        Assert.Same(record, task.LastProcess);
        Assert.True(DispatchProcessLeakGuard.HasHost(test.UniqueID, record.ProcessId, DispatchFile(record)));
        AdvanceLoopTests.ReleaseBlockingWorkers(root);
        await TestOwnedDispatchProcesses.AwaitCompletionAsync(record);
        Assert.True(File.Exists(record.ExitCodePath));
        Assert.Equal("0", File.ReadAllText(record.ExitCodePath).Trim());
        new DispatchProcessLeakGuardAttribute().After(
            GetType().GetMethod(nameof(After_AwaitedRealDispatch_Passes))!, test);
    }

    [Fact]
    public async Task After_ConcurrentDispatches_FailsOnlyLeakingOwner()
    {
        var leakingReady = new TaskCompletionSource<TaskProcessRecord>(TaskCreationOptions.RunContinuationsAsynchronously);
        var awaitedPassed = new TaskCompletionSource<TaskProcessRecord>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var leaking = Task.Run(async () =>
        {
            var scopeId = Guid.NewGuid().ToString("N");
            DispatchProcessLeakGuard.Begin(scopeId);
            var root = InfrastructureTestSupport.CreateTempDirectory();
            var (kernel, goal, task) = PrepareDispatch(root);
            using var owned = new TestOwnedDispatchProcesses(kernel, goal);
            try
            {
                var record = new BackgroundDispatchRunner().StartLatestDispatch(
                    kernel, goal.Id, task.Id, OrchestratorWorkspace.ForDirectory(root).LogDirectory);
                Assert.True(DispatchProcessLeakGuard.HasHost(scopeId, record.ProcessId, DispatchFile(record)));
                leakingReady.SetResult(record);
                var peer = await TestHangGuard.WaitAsync(awaitedPassed.Task.WaitAsync(cancellation.Token), "awaited peer guard");
                using var host = Process.GetProcessById(record.ProcessId);
                Assert.False(host.HasExited);
                var failure = Assert.Throws<Xunit.Sdk.XunitException>(() => DispatchProcessLeakGuard.End(scopeId));
                Assert.Contains($"pid={record.ProcessId}", failure.Message);
                Assert.Contains($"dispatch={DispatchFile(record)}", failure.Message);
                Assert.DoesNotContain($"pid={peer.ProcessId}", failure.Message);
                Assert.True(host.HasExited);
            }
            finally
            {
                cancellation.Cancel();
                owned.Dispose();
                DispatchProcessLeakGuard.End(scopeId);
            }
        }, cancellation.Token);
        var awaited = Task.Run(async () =>
        {
            var scopeId = Guid.NewGuid().ToString("N");
            DispatchProcessLeakGuard.Begin(scopeId);
            var root = InfrastructureTestSupport.CreateTempDirectory();
            var (kernel, goal, task) = PrepareDispatch(root);
            using var owned = new TestOwnedDispatchProcesses(kernel, goal);
            try
            {
                var record = new BackgroundDispatchRunner().StartLatestDispatch(
                    kernel, goal.Id, task.Id, OrchestratorWorkspace.ForDirectory(root).LogDirectory);
                Assert.True(DispatchProcessLeakGuard.HasHost(scopeId, record.ProcessId, DispatchFile(record)));
                var peer = await TestHangGuard.WaitAsync(leakingReady.Task.WaitAsync(cancellation.Token), "leaking peer launch");
                AdvanceLoopTests.ReleaseBlockingWorkers(root);
                await TestOwnedDispatchProcesses.AwaitCompletionAsync(record);
                Assert.Equal("0", File.ReadAllText(record.ExitCodePath).Trim());
                using var peerHost = Process.GetProcessById(peer.ProcessId);
                Assert.False(peerHost.HasExited);
                // This owner passes while the other scope's real host remains live.
                DispatchProcessLeakGuard.End(scopeId);
                awaitedPassed.SetResult(record);
            }
            finally
            {
                owned.Dispose();
                DispatchProcessLeakGuard.End(scopeId);
                if (!awaitedPassed.Task.IsCompletedSuccessfully) cancellation.Cancel();
            }
        }, cancellation.Token);
        try { await TestHangGuard.WaitAsync(Task.WhenAll(leaking, awaited), "concurrent dispatch guard contracts"); }
        finally
        {
            cancellation.Cancel();
            // A hang guard reports a failure; it never grants permission to abandon either owner.
            await Task.WhenAll(leaking, awaited);
        }
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

    [Fact]
    public void Start_ObservationFailure_ReapsUnrecordedDispatch()
    {
        var root = InfrastructureTestSupport.CreateTempDirectory();
        var (kernel, goal, task) = PrepareDispatch(root);
        using var owned = new TestOwnedDispatchProcesses(kernel, goal);
        Process? startedHost = null;
        try
        {
            using var observation = DispatchProcessStartObservation.Observe((host, dispatchFile) =>
            {
                startedHost = Process.GetProcessById(host.Id);
                _ = startedHost.SafeHandle;
                throw new InvalidOperationException("ownership observation failed");
            });
            var failure = Assert.Throws<InvalidOperationException>(() =>
                new BackgroundDispatchRunner().StartLatestDispatch(
                    kernel, goal.Id, task.Id, OrchestratorWorkspace.ForDirectory(root).LogDirectory));
            Assert.Equal("ownership observation failed", failure.Message);
            Assert.NotNull(startedHost);
            Assert.True(startedHost.HasExited, $"Unrecorded dispatch pid={startedHost.Id} was left running.");
            Assert.Null(task.LastProcess);
        }
        finally
        {
            if (startedHost is not null)
            {
                try { TestOwnedDispatchProcesses.StopAndAwait(startedHost); }
                finally { startedHost.Dispose(); }
            }
        }
    }

    [Fact]
    public void Start_DispatchArguments_ReportsExactProcessAndFile()
    {
        using var process = Process.GetCurrentProcess();
        var info = DispatchStartInfo("owned.dispatch.json");
        var observations = new List<(Process Process, string File)>();
        using var observation = DispatchProcessStartObservation.Observe((host, file) => observations.Add((host, file)));
        var result = DispatchProcessStartObservation.Start(info, _ => process);
        Assert.Same(process, result);
        var observed = Assert.Single(observations);
        Assert.Same(process, observed.Process);
        Assert.Equal("owned.dispatch.json", observed.File);
    }

    [Fact]
    public void Start_OrdinaryCommand_DoesNotObserveDispatch()
    {
        using var process = Process.GetCurrentProcess();
        var observed = false;
        var invoked = false;
        using var observation = DispatchProcessStartObservation.Observe((_, _) => observed = true);
        var result = DispatchProcessStartObservation.Start(new ProcessStartInfo("dotnet"), _ =>
        {
            invoked = true;
            return process;
        });
        Assert.True(invoked);
        Assert.Same(process, result);
        Assert.False(observed);
    }

    [Fact]
    public void Observe_NestedScopes_RestoresPreviousObserver()
    {
        using var process = Process.GetCurrentProcess();
        var observations = new List<string>();
        using var outer = DispatchProcessStartObservation.Observe((_, file) => observations.Add("outer:" + file));
        using (DispatchProcessStartObservation.Observe((_, file) => observations.Add("inner:" + file)))
            DispatchProcessStartObservation.Start(DispatchStartInfo("inner.dispatch.json"), _ => process);
        DispatchProcessStartObservation.Start(DispatchStartInfo("outer.dispatch.json"), _ => process);
        Assert.Equal(["inner:inner.dispatch.json", "outer:outer.dispatch.json"], observations);
    }

    private static ProcessStartInfo DispatchStartInfo(string file) => new("dotnet")
    {
        ArgumentList = { "exec", "App.dll", DispatchProcessHost.SubcommandName, file }
    };

    private static string DispatchFile(TaskProcessRecord record) =>
        record.ExitCodePath.Replace(".exit.txt", ".dispatch.json", StringComparison.Ordinal);

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
