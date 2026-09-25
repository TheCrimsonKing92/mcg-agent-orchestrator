using System.Text;
using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection("ProcessSpawning")]
public sealed class DispatchProcessHostHeartbeatRaceTests
{
    [Xunit.Fact]
    public void HeartbeatCallbackParkedInObserveSelectedChild_TeardownWaitsBeforeDisposingChild()
    {
        var dir = NewDirectory();
        var gatePath = Path.Combine(dir, "release-child");
        var readyPath = Path.Combine(dir, "child-ready");
        var heartbeatPath = Path.Combine(dir, "heartbeat.json");
        var diagnosticPath = Path.Combine(dir, "host.err.log");
        using var callbackParked = new ManualResetEventSlim();
        using var releaseCallback = new ManualResetEventSlim();
        using var quiescing = new ManualResetEventSlim();
        using var disposalStarted = new ManualResetEventSlim();
        var armed = 0;
        var parked = 0;
        var disposedWhileParked = 0;
        Task<int>? runTask = null;
        try
        {
            var childScript = Path.Combine(dir, "child.ps1");
            File.WriteAllText(childScript,
                $"[IO.File]::WriteAllText('{Escape(readyPath)}', [string]$PID); while (!(Test-Path -LiteralPath '{Escape(gatePath)}')) {{ [void][Math]::Sqrt(1234567) }}{Environment.NewLine}exit 0",
                new UTF8Encoding(false));
            var command = $"& '{Escape(WorkerShell.Executable)}' -NoProfile -NonInteractive -InputFormat None -File '{Escape(childScript)}'; exit 0";
            var parameters = Parameters(dir, command, heartbeatPath, diagnosticPath);
            var hooks = new DispatchProcessHost.HeartbeatTestHooks
            {
                QuiesceTimeout = Timeout.InfiniteTimeSpan,
                ObserveSelectedChild = _ =>
                {
                    if (!Thread.CurrentThread.IsThreadPoolThread || Volatile.Read(ref armed) == 0 ||
                        Interlocked.CompareExchange(ref parked, 1, 0) != 0)
                    {
                        return;
                    }

                    callbackParked.Set();
                    Assert.True(releaseCallback.Wait(TimeSpan.FromSeconds(15)), "The parked heartbeat callback was not released.");
                },
                TeardownPhase = phase =>
                {
                    if (phase == "quiescing") quiescing.Set();
                    if (phase == "disposing-selected-child" && !releaseCallback.IsSet)
                        Interlocked.Exchange(ref disposedWhileParked, 1);
                    if (phase == "disposing-selected-child") disposalStarted.Set();
                }
            };
            runTask = StartHost(parameters, hooks);
            Assert.True(SpinWait.SpinUntil(() => HeartbeatHasChild(heartbeatPath, readyPath), TimeSpan.FromSeconds(10)),
                "The host did not observe the selected child.");
            Volatile.Write(ref armed, 1);
            Assert.True(callbackParked.Wait(TimeSpan.FromSeconds(10)), "A periodic heartbeat did not enter ObserveSelectedChild.");
            File.WriteAllText(gatePath, "release");
            Assert.True(quiescing.Wait(TimeSpan.FromSeconds(10)), "Teardown did not reach the heartbeat quiesce gate.");
            // This bound only lets the old teardown report its disposal event; the assertion checks event order.
            disposalStarted.Wait(TimeSpan.FromSeconds(2));
            Assert.Equal(0, Volatile.Read(ref disposedWhileParked));
            releaseCallback.Set();
            Assert.Equal(0, runTask.GetAwaiter().GetResult());
            Assert.Equal(0, Volatile.Read(ref disposedWhileParked));
            Assert.DoesNotContain("heartbeat callback failed", ReadIfExists(diagnosticPath), StringComparison.Ordinal);
        }
        finally
        {
            releaseCallback.Set();
            try { File.WriteAllText(gatePath, "release"); } catch { }
            try { runTask?.Wait(TimeSpan.FromSeconds(10)); } catch { }
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Xunit.Fact]
    public void HeartbeatCallbackException_IsRecordedOnceAndHeartbeatKeepsFiring()
    {
        var dir = NewDirectory();
        var gatePath = Path.Combine(dir, "release-worker");
        var diagnosticPath = Path.Combine(dir, "host.err.log");
        var heartbeatPath = Path.Combine(dir, "heartbeat.json");
        using var threeCallbacks = new ManualResetEventSlim();
        var callbackCount = 0;
        var unhandled = 0;
        Task<int>? runTask = null;
        UnhandledExceptionEventHandler handler = (_, _) => Interlocked.Exchange(ref unhandled, 1);
        AppDomain.CurrentDomain.UnhandledException += handler;
        try
        {
            var command = $"while (!(Test-Path -LiteralPath '{Escape(gatePath)}')) {{ [void][Math]::Sqrt(1234567) }}; exit 0";
            var hooks = new DispatchProcessHost.HeartbeatTestHooks
            {
                ObserveSelectedChild = _ =>
                {
                    if (!Thread.CurrentThread.IsThreadPoolThread) return;
                    if (Interlocked.Increment(ref callbackCount) >= 3) threeCallbacks.Set();
                    throw new InvalidOperationException("injected heartbeat failure");
                }
            };
            runTask = StartHost(Parameters(dir, command, heartbeatPath, diagnosticPath), hooks);
            Assert.True(threeCallbacks.Wait(TimeSpan.FromSeconds(10)), "The timer did not continue after a caught callback failure.");
            File.WriteAllText(gatePath, "release");
            Assert.Equal(0, runTask.GetAwaiter().GetResult());
            Assert.True(DispatchExitArtifacts.TryRead(Path.Combine(dir, "exit.txt"), out var exitArtifact));
            Assert.Equal(0, exitArtifact.ExitCode);
            using var heartbeat = JsonDocument.Parse(File.ReadAllText(heartbeatPath));
            Assert.Equal("exited", heartbeat.RootElement.GetProperty("state").GetString());
            var failures = File.ReadAllLines(diagnosticPath)
                .Where(line => line.Contains("heartbeat callback failed", StringComparison.Ordinal)).ToArray();
            Assert.Single(failures);
            Assert.Contains("InvalidOperationException", failures[0], StringComparison.Ordinal);
            Assert.Equal(0, Volatile.Read(ref unhandled));
        }
        finally
        {
            AppDomain.CurrentDomain.UnhandledException -= handler;
            try { File.WriteAllText(gatePath, "release"); } catch { }
            try { runTask?.Wait(TimeSpan.FromSeconds(10)); } catch { }
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Xunit.Fact]
    public void HeartbeatQuiesceTimeout_KeepsNotificationHandleAliveUntilCallbackReturns()
    {
        var dir = NewDirectory();
        var gatePath = Path.Combine(dir, "release-worker");
        var diagnosticPath = Path.Combine(dir, "host.err.log");
        using var callbackParked = new ManualResetEventSlim();
        using var releaseCallback = new ManualResetEventSlim();
        using var callbackReturned = new ManualResetEventSlim();
        WaitHandle? quiesceSignal = null;
        var parked = 0;
        Task<int>? runTask = null;
        try
        {
            var command = $"while (!(Test-Path -LiteralPath '{Escape(gatePath)}')) {{ [void][Math]::Sqrt(1234567) }}; exit 0";
            var hooks = new DispatchProcessHost.HeartbeatTestHooks
            {
                QuiesceTimeout = TimeSpan.Zero,
                QuiesceSignalCreated = signal => quiesceSignal = signal,
                ObserveSelectedChild = _ =>
                {
                    if (!Thread.CurrentThread.IsThreadPoolThread ||
                        Interlocked.CompareExchange(ref parked, 1, 0) != 0) return;

                    callbackParked.Set();
                    try { releaseCallback.Wait(); }
                    finally { callbackReturned.Set(); }
                }
            };
            runTask = StartHost(Parameters(dir, command, Path.Combine(dir, "heartbeat.json"), diagnosticPath), hooks);
            Assert.True(callbackParked.Wait(TimeSpan.FromSeconds(10)), "The heartbeat callback did not park.");
            File.WriteAllText(gatePath, "release");
            Assert.True(runTask.Wait(TimeSpan.FromSeconds(10)), "Teardown did not finish after the zero quiesce timeout.");
            Assert.Equal(0, runTask.GetAwaiter().GetResult());
            Assert.NotNull(quiesceSignal);
            // This fails with the former using declaration: teardown closed the event while
            // Timer.Dispose still owed it a signal from the parked callback.
            Assert.False(quiesceSignal.SafeWaitHandle.IsClosed);
            Assert.Contains("heartbeat timer did not quiesce", ReadIfExists(diagnosticPath), StringComparison.Ordinal);
            releaseCallback.Set();
            Assert.True(callbackReturned.Wait(TimeSpan.FromSeconds(10)), "The heartbeat callback did not return.");
            Assert.True(SpinWait.SpinUntil(() => quiesceSignal.SafeWaitHandle.IsClosed, TimeSpan.FromSeconds(10)),
                "The quiesce notification handle was not released after the callback.");
        }
        finally
        {
            releaseCallback.Set();
            try { File.WriteAllText(gatePath, "release"); } catch { }
            try { runTask?.Wait(TimeSpan.FromSeconds(10)); } catch { }
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    private static DispatchProcessHost.DispatchRunParameters Parameters(string dir, string command, string heartbeatPath, string diagnosticPath) =>
        new(command, dir, Path.Combine(dir, "out.log"), Path.Combine(dir, "err.log"),
            Path.Combine(dir, "exit.txt"), heartbeatPath, DisableSharedCompilation: false,
            Provider: WorkerSandboxProvider.Claude, HostDiagnosticPath: diagnosticPath,
            HeartbeatIntervalMilliseconds: 25);

    private static Task<int> StartHost(DispatchProcessHost.DispatchRunParameters parameters, DispatchProcessHost.HeartbeatTestHooks hooks)
    {
        var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { completion.SetResult(DispatchProcessHost.RunWithHeartbeatHooksForTests(parameters, hooks)); }
            catch (Exception failure) { completion.SetException(failure); }
        });
        thread.Start();
        return completion.Task;
    }

    private static bool HeartbeatHasChild(string heartbeatPath, string readyPath)
    {
        if (!File.Exists(heartbeatPath) || !File.Exists(readyPath) ||
            !int.TryParse(File.ReadAllText(readyPath), out var readyPid)) return false;
        try
        {
            using var heartbeat = JsonDocument.Parse(File.ReadAllText(heartbeatPath));
            var childPid = heartbeat.RootElement.GetProperty("childPid");
            return childPid.ValueKind == JsonValueKind.Number && childPid.GetInt32() == readyPid;
        }
        catch (IOException) { return false; }
        catch (JsonException) { return false; }
    }

    private static string NewDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "mcg-dispatch-heartbeat-race", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static string Escape(string path) => path.Replace("'", "''", StringComparison.Ordinal);
    private static string ReadIfExists(string path) => File.Exists(path) ? File.ReadAllText(path) : string.Empty;
}
