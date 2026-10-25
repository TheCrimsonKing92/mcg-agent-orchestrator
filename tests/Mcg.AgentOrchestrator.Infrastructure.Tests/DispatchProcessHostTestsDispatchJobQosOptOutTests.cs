using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

// Owns real worker/job members, held until the observation or launch event releases them.
[Xunit.Collection("ProcessSpawning")]
public sealed class DispatchProcessHostTestsDispatchJobQosOptOutTests : WorkerDispatchTestSupport
{
    [Fact]
    public async Task Enabled_SweepsWorkerAndJobMemberOnce()
    {
        await using var fixture = new HostFixture(true);
        var calls = new ConcurrentQueue<(int Pid, uint Control, uint State)>();
        var memberObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var root = 0;
        fixture.Start((pid, members) =>
        {
            root = pid;
            return AcceptanceChildQos.StartSweep(true, pid, TimeSpan.FromMilliseconds(50), members,
                (member, control, state) =>
                {
                    calls.Enqueue((member, control, state));
                    if (member != pid) memberObserved.TrySetResult();
                    return true;
                });
        });
        await fixture.WaitFor(memberObserved.Task, "QoS setter did not observe a second job member.");
        await fixture.ReleaseAndAssertSuccess();

        var recorded = calls.ToArray();
        Assert.Contains(recorded, call => call.Pid == root);
        Assert.Contains(recorded, call => call.Pid != root);
        Assert.All(recorded, call => Assert.Equal((1u, 0u), (call.Control, call.State)));
        Assert.Equal(recorded.Length, recorded.Select(call => call.Pid).Distinct().Count());
    }

    [Fact]
    public async Task Disabled_DoesNotInvokeFactoryOrSetter()
    {
        await using var fixture = new HostFixture(false);
        var factories = 0;
        var setters = 0;
        fixture.Start((pid, members) =>
        {
            Interlocked.Increment(ref factories);
            return AcceptanceChildQos.StartSweep(true, pid, TimeSpan.FromMilliseconds(50), members,
                (_, _, _) => { Interlocked.Increment(ref setters); return true; });
        });
        await fixture.WaitFor(fixture.Launched.Task, "Worker was not attached to its job.");
        await fixture.ReleaseAndAssertSuccess();
        Assert.Equal(0, factories);
        Assert.Equal(0, setters);
    }

    [Fact]
    public async Task ThrowingFactory_PreservesExitArtifact()
    {
        await using var fixture = new HostFixture(true);
        var factories = 0;
        fixture.Start((_, _) =>
        {
            Interlocked.Increment(ref factories);
            throw new IOException("injected QoS startup failure");
        });
        await fixture.WaitFor(fixture.Launched.Task, "Worker did not continue after QoS startup failure.");
        await fixture.ReleaseAndAssertSuccess();
        Assert.Equal(1, factories);
    }

    [Fact]
    public void Runner_SerializesOnlyTheOptOutDifference()
    {
        var root = CreateSeededDispatchRepository();
        var logs = Directory.CreateTempSubdirectory("r-").FullName;
        var processes = new List<Process>();
        try
        {
            DispatchProcessHost.DispatchRunParameters Capture(Func<bool>? enabled, string logName)
            {
                var kernel = new AgentOrchestratorKernel();
                var goal = kernel.CreateGoal("Dispatch QoS serialization", [
                    new TaskSpec(TaskId.New(), "Run a local fixture.", AgentRole.Developer)
                ]);
                kernel.ActivateGoal(goal.Id, [TestSubscriptionAgent("developer", "Developer", AgentRole.Developer)]);
                var task = goal.Tasks.Single();
                kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord(
                    "local", "Write-Output fixture", root, DateTimeOffset.Parse("2026-10-25T12:29:00Z")));
                DispatchProcessHost.DispatchRunParameters? captured = null;
                var runner = new BackgroundDispatchRunner(isStillRunning: _ => false, disableProcessStart: false,
                    startProcess: info =>
                    {
                        captured = DispatchProcessHost.ReadParameters(info.ArgumentList.Last());
                        var start = new ProcessStartInfo(WorkerShell.Executable)
                        {
                            UseShellExecute = false,
                            CreateNoWindow = true
                        };
                        foreach (var argument in WorkerShell.BaseArguments()) start.ArgumentList.Add(argument);
                        start.ArgumentList.Add(WaitForReleaseCommand(root));
                        var process = Process.Start(start) ?? throw new InvalidOperationException("Fixture host did not start.");
                        processes.Add(process);
                        return process;
                    }) { ExecutionThrottlingOptOut = enabled };
                var result = runner.TryStartLatestDispatch(kernel, goal.Id, task.Id, Path.Combine(logs, logName));
                Assert.NotNull(result.ProcessRecord);
                return Assert.IsType<DispatchProcessHost.DispatchRunParameters>(captured);
            }

            var first = Capture(() => true, "enabled");
            var second = Capture(null, "default");
            Assert.True(first.OptOutExecutionThrottling);
            Assert.False(second.OptOutExecutionThrottling);
            Assert.Equal(first.SandboxLowIntegrity, second.SandboxLowIntegrity);
            Assert.Equal(first.SandboxWorktreeWritable, second.SandboxWorktreeWritable);
            Assert.Equal(first.Provider, second.Provider);
            Assert.Equal(first.SandboxInstanceName, second.SandboxInstanceName);
            Assert.Equal(first.MandatoryContextFiles, second.MandatoryContextFiles);
            // Independent dispatches have different artifact paths; preserve every behavioral member.
            Assert.Equal(second, first with
            {
                OptOutExecutionThrottling = false,
                StdoutPath = second.StdoutPath,
                StderrPath = second.StderrPath,
                ExitCodePath = second.ExitCodePath,
                HeartbeatPath = second.HeartbeatPath,
                ChildExitRecordPath = second.ChildExitRecordPath,
                HostDiagnosticPath = second.HostDiagnosticPath,
                StartGatePath = second.StartGatePath,
                MandatoryContextFiles = second.MandatoryContextFiles
            });
        }
        finally
        {
            File.WriteAllText(Path.Combine(root, "release"), "release");
            foreach (var process in processes)
            {
                try { WorkerProcessJobs.TryKillOrFallback(process.Id); }
                finally { process.Dispose(); }
            }
            SharedTestSupport.RemoveTempDirectory(root);
            SharedTestSupport.RemoveTempDirectory(logs);
        }
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    [InlineData("missing", false)]
    [InlineData("invalid", false)]
    public void Policy_ReadsOnlyTheStateDirectory(string policy, bool expected)
    {
        var root = Directory.CreateTempSubdirectory("r-").FullName;
        try
        {
            if (policy != "missing")
                File.WriteAllText(Path.Combine(root, "conductor-policy.json"), policy == "invalid"
                    ? "not json" : (ConductorAutonomyPolicy.Conservative with
                    {
                        AcceptanceChildrenOptOutExecutionThrottling = policy == "true"
                    }).ToJson());
            Assert.Equal(expected, DispatchJobExecutionQos.IsEnabled(Path.Combine(root, "state.db")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static string WaitForReleaseCommand(string root)
    {
        var directory = root.Replace("'", "''", StringComparison.Ordinal);
        // File notifications wake the shell; the bound only retries a missed filesystem notification.
        return $"$w = [IO.FileSystemWatcher]::new('{directory}', 'release'); " +
            $"try {{ while (!(Test-Path -LiteralPath '{directory}/release')) {{ " +
            "[void]$w.WaitForChanged([IO.WatcherChangeTypes]::Created, 1000) } } finally { $w.Dispose() }; exit 0";
    }

    private sealed class HostFixture : IAsyncDisposable
    {
        private readonly string root = Directory.CreateTempSubdirectory("r-").FullName;
        private readonly DispatchProcessHost.DispatchRunParameters parameters;
        private Task<int>? run;
        internal TaskCompletionSource Launched { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal HostFixture(bool enabled)
        {
            var childCommand = Convert.ToBase64String(Encoding.Unicode.GetBytes(WaitForReleaseCommand(root)));
            var shell = WorkerShell.Executable.Replace("'", "''", StringComparison.Ordinal);
            var command = $"$s = [Diagnostics.ProcessStartInfo]::new('{shell}'); " +
                "$s.UseShellExecute = $false; $s.CreateNoWindow = $true; " +
                $"$s.Arguments = '-NoProfile -EncodedCommand {childCommand}'; " +
                "$child = [Diagnostics.Process]::Start($s); " +
                "try { $child.WaitForExit(); exit $child.ExitCode } finally { $child.Dispose() }";
            parameters = new(command, root, Path.Combine(root, "out.log"), Path.Combine(root, "err.log"),
                Path.Combine(root, "exit.txt"), Path.Combine(root, "heartbeat.json"),
                DisableSharedCompilation: false, Provider: WorkerSandboxProvider.Claude,
                HostDiagnosticPath: Path.Combine(root, "host.err.log"), OptOutExecutionThrottling: enabled);
        }

        internal void Start(Func<int, Func<int, IReadOnlyList<int>?>, IDisposable?> factory)
        {
            var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            run = completion.Task;
            new Thread(() =>
            {
                try
                {
                    completion.TrySetResult(DispatchProcessHost.RunWithHeartbeatHooksForTests(parameters,
                        new() { AfterTaskIntentAdmission = _ => Launched.TrySetResult() }, factory));
                }
                catch (Exception error) { completion.TrySetException(error); }
            }) { IsBackground = true }.Start();
        }

        internal async Task WaitFor(Task signal, string missingEvent)
        {
            try { await signal.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken); }
            catch (TimeoutException error)
            {
                throw new InvalidOperationException(
                    DispatchProcessHostTestsLaunchedHeartbeat.DescribeHostState(missingEvent, root, run), error);
            }
        }

        internal async Task ReleaseAndAssertSuccess()
        {
            File.WriteAllText(Path.Combine(root, "release"), "release");
            await WaitFor(run!, "Dispatch host did not exit after release.");
            Assert.Equal(0, await run!);
            Assert.True(DispatchExitArtifacts.TryRead(parameters.ExitCodePath, out var artifact));
            Assert.Equal(0, artifact.ExitCode);
            Assert.Equal(DispatchExitArtifactOrigin.Native, artifact.Origin);
        }

        public async ValueTask DisposeAsync()
        {
            File.WriteAllText(Path.Combine(root, "release"), "release");
            if (run is not null)
            {
                try { await run.WaitAsync(TimeSpan.FromSeconds(60)); }
                catch when (run.IsCompleted) { }
            }
            Directory.Delete(root, recursive: true);
        }
    }
}
