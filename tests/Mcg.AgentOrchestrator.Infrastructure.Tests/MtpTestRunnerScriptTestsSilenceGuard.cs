using System.Diagnostics;
using System.Text;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(TestCollections.ProcessSpawning)]
public sealed class MtpTestRunnerScriptTestsSilenceGuard
{
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan Ceiling = TimeSpan.FromSeconds(2);

    [Xunit.Theory]
    [Xunit.InlineData(false, true)]
    [Xunit.InlineData(true, false)]
    public async Task ChunkObserver_BufferedOrForwarded_ControlsOuterSilence(bool forward, bool stalled)
    {
        var time = new OutputTimeProvider();
        long bytes = 0;
        using var monitor = new AcceptanceLaneStallMonitor("nested", time, AcceptanceLaneStallPolicy.StallLimit, _ => { });
        GateHeartbeatSnapshot Snapshot() => new(null, "verification-check", "nested", null, null, null, "running",
            DateTimeOffset.MinValue, DateTimeOffset.MaxValue, DateTimeOffset.MinValue, bytes, 0, bytes);
        monitor.Observe(Snapshot());
        var captured = new StringBuilder();
        var stamped = new StringBuilder();
        const string chunk = "nested café 中文"; // Deliberately repeated, with no newline.
        foreach (var seconds in new[] { 300, 590, 604 })
        {
            time.Seconds = seconds;
            captured.Append(await RealProcessSilenceGuard.ReadChunksAsync(new StringReader(chunk),
                text => stamped.Append(text), observeOutput: forward ? text => bytes += Encoding.UTF8.GetByteCount(text) : null));
            monitor.Observe(Snapshot());
        }
        Assert.Equal(string.Concat(Enumerable.Repeat(chunk, 3)), captured.ToString());
        Assert.Equal(captured.ToString(), stamped.ToString());
        Assert.Equal(forward ? Encoding.UTF8.GetByteCount(captured.ToString()) : 0, bytes);
        Assert.Equal(stalled, monitor.Stopped);
    }

    [Xunit.Fact]
    public async Task ChunkObserver_EmptyInput_StillTripsOuterSilence()
    {
        var time = new OutputTimeProvider();
        using var monitor = new AcceptanceLaneStallMonitor("nested", time, AcceptanceLaneStallPolicy.StallLimit, _ => { });
        var snapshot = new GateHeartbeatSnapshot(null, "verification-check", "nested", null, null, null, "running",
            DateTimeOffset.MinValue, DateTimeOffset.MaxValue, DateTimeOffset.MinValue, 0, 0, 0);
        monitor.Observe(snapshot);
        var callbacks = 0;
        Assert.Equal(string.Empty, await RealProcessSilenceGuard.ReadChunksAsync(new StringReader(string.Empty),
            _ => callbacks++, observeOutput: _ => callbacks++));
        time.Seconds = 604;
        monitor.Observe(snapshot);
        Assert.Equal(0, callbacks);
        Assert.True(monitor.Stopped);
    }

    [Xunit.Fact]
    public async Task OutputObserver_PartialStreams_ArriveBeforeChildExit()
    {
        const string text = "café 中文";
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sync = new object();
        await using var child = await Child.Start(
            $"[Console]::Out.Write('{text}'); [Console]::Out.Flush(); [Console]::Error.Write('{text}'); [Console]::Error.Flush(); AwaitFile 'release'",
            observeOutput: (stream, chunk) =>
            {
                lock (sync)
                {
                    Assert.Contains(stream, new[] { "stdout", "stderr" });
                    (stream == "stdout" ? stdout : stderr).Append(chunk);
                    if (stdout.ToString() == text && stderr.ToString() == text) ready.TrySetResult();
                }
            });
        await child.Observe(ready.Task);
        Assert.False(child.Result.IsCompleted);
        child.Release("release");
        var result = await child.Completion;
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(text, result.Stdout);
        Assert.Equal(text, result.Stderr);
        Assert.Equal(result.Stdout, stdout.ToString());
        Assert.Equal(result.Stderr, stderr.ToString());
    }

    [Xunit.Fact]
    public async Task OutputObserver_Failure_ReapsChildAndReportsCapturedOutput()
    {
        var clock = new ManualElapsedClock();
        using var scope = RealProcessSilenceGuard.Begin(() => clock.Elapsed, observe: clock.Observe);
        await using var child = await Child.Start(
            "[Console]::Out.Write('ready'); [Console]::Out.Flush(); AwaitFile 'second'; " +
            "[Console]::Error.Write('failure'); [Console]::Error.Flush(); AwaitFile 'release'",
            observeOutput: (stream, _) => { if (stream == "stderr") throw new IOException("observer sentinel"); });
        await child.Observe(clock.WaitForOutputContaining("ready"));
        child.ExpectedObserverFailure = true;
        child.Release("second");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => child.Completion);
        Assert.IsType<IOException>(error.InnerException);
        Assert.Contains("stdout:ready", error.Message, StringComparison.Ordinal);
        Assert.Contains("stderr:failure", error.Message, StringComparison.Ordinal);
    }

    private sealed class OutputTimeProvider : TimeProvider
    {
        internal int Seconds { get; set; }
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddSeconds(Seconds);
    }

    [Xunit.Fact]
    public async Task DefaultGuardAllowsProgressPastFormerThirtySecondDeadline()
    {
        var clock = new ManualElapsedClock();
        using var scope = RealProcessSilenceGuard.Begin(() => clock.Elapsed, observe: clock.Observe);
        await using var child = await Child.Start("[Console]::Out.WriteLine('working café 中文'); [Console]::Out.Flush(); AwaitFile 'release'");
        await child.Observe(clock.WaitForOutputContaining("working café 中文\r\n"));
        clock.AdvanceTo(TimeSpan.FromSeconds(31));
        await child.Observe(clock.WaitForEvaluationAtOrAfter(clock.Elapsed));
        child.Release("release");
        var result = await child.Completion;
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("working café 中文\r\n", result.Stdout);
        Assert.Equal(string.Empty, result.Stderr);
    }

    [Xunit.Fact]
    public async Task SilentChildIsReapedAfterInjectedSilenceWindow()
    {
        var clock = new ManualElapsedClock();
        using var scope = RealProcessSilenceGuard.Begin(() => clock.Elapsed, Window, Ceiling, clock.Observe);
        await using var child = await Child.Start("AwaitFile 'release'");
        var identity = TestOwnedProcessStop.TryIdentify(child.Pid);
        Assert.NotNull(identity);
        clock.AdvanceTo(TimeSpan.FromSeconds(1.1));
        var error = await Assert.ThrowsAsync<MtpTestRunnerScriptTests.RealProcessHangGuardException>(() => child.Completion);
        Assert.Contains("no output for 1 seconds", error.Message, StringComparison.Ordinal);
        Assert.True(error.RootExited);
        Assert.NotEqual(identity, TestOwnedProcessStop.TryIdentify(child.Pid));
        Assert.False(File.Exists(child.PathFor("release")));
    }

    [Xunit.Fact]
    public async Task StdoutRestartsSilenceWindowAndPreservesCapturedChunks()
    {
        await AssertOutputRestartsWindow("Out");
    }

    [Xunit.Fact]
    public async Task StderrRestartsSilenceWindowAndPreservesCapturedChunks()
    {
        await AssertOutputRestartsWindow("Error");
    }

    private static async Task AssertOutputRestartsWindow(string stream)
    {
        var clock = new ManualElapsedClock();
        using var scope = RealProcessSilenceGuard.Begin(() => clock.Elapsed, Window, Ceiling, clock.Observe);
        await using var child = await Child.Start(
            "[Console]::Out.WriteLine('first'); [Console]::Out.Flush(); AwaitFile 'second'; " +
            $"[Console]::{stream}.Write('second café 中文'); [Console]::{stream}.Flush(); AwaitFile 'release'");
        await child.Observe(clock.WaitForOutputContaining("first\r\n"));
        clock.AdvanceTo(TimeSpan.FromSeconds(0.9));
        await child.Observe(clock.WaitForEvaluationAtOrAfter(clock.Elapsed));
        child.Release("second");
        await child.Observe(clock.WaitForOutputContaining("second café 中文"));
        clock.AdvanceTo(TimeSpan.FromSeconds(1.5));
        await child.Observe(clock.WaitForEvaluationAtOrAfter(clock.Elapsed));
        child.Release("release");
        var result = await child.Completion;
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(stream == "Out" ? "first\r\nsecond café 中文" : "first\r\n", result.Stdout);
        Assert.Equal(stream == "Error" ? "second café 中文" : string.Empty, result.Stderr);
    }

    [Xunit.Fact]
    public async Task TotalCeilingReapsChildDespiteContinuingOutput()
    {
        var clock = new ManualElapsedClock();
        using var scope = RealProcessSilenceGuard.Begin(() => clock.Elapsed, Window, Ceiling, clock.Observe);
        await using var child = await Child.Start(
            "$n = 0; while (-not [IO.File]::Exists([IO.Path]::Combine($root, 'release'))) { [Console]::Out.WriteLine(('tick-' + $n)); [Console]::Out.Flush(); " +
            "AwaitFile ('pulse-' + $n); $n++ }");
        await child.Observe(clock.WaitForOutputContaining("tick-0\r\n"));
        foreach (var step in new[] { 0.5, 0.9, 1.3, 1.7 })
        {
            clock.AdvanceTo(TimeSpan.FromSeconds(step));
            child.ReleaseNextPulse();
            await child.Observe(clock.WaitForOutputAtOrAfter(clock.Elapsed));
            await child.Observe(clock.WaitForEvaluationAtOrAfter(clock.Elapsed));
        }
        clock.AdvanceTo(TimeSpan.FromSeconds(2.1));
        var error = await Assert.ThrowsAsync<MtpTestRunnerScriptTests.RealProcessHangGuardException>(() => child.Completion);
        Assert.Contains("total ceiling of 2 seconds", error.Message, StringComparison.Ordinal);
        Assert.True(error.RootExited);
        Assert.Contains("tick-4\r\n", error.Message, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public async Task ExplicitTimeoutStillUsesTotalDeadlineDespiteOutput()
    {
        var clock = new ManualElapsedClock();
        using var scope = RealProcessSilenceGuard.Begin(() => clock.Elapsed, Window, Ceiling, clock.Observe);
        await using var child = await Child.Start(
            "[Console]::Out.WriteLine('still working'); [Console]::Out.Flush(); AwaitFile 'second'; " +
            "[Console]::Error.Write('recent stderr'); [Console]::Error.Flush(); AwaitFile 'release'",
            TimeSpan.FromSeconds(900));
        await child.Observe(clock.WaitForOutputContaining("still working\r\n"));
        clock.AdvanceTo(TimeSpan.FromSeconds(899));
        await child.Observe(clock.WaitForEvaluationAtOrAfter(clock.Elapsed));
        child.Release("second");
        await child.Observe(clock.WaitForOutputContaining("recent stderr"));
        clock.AdvanceTo(TimeSpan.FromSeconds(901));
        var error = await Assert.ThrowsAsync<MtpTestRunnerScriptTests.RealProcessHangGuardException>(() => child.Completion);
        Assert.Contains("900 seconds", error.Message, StringComparison.Ordinal);
        Assert.Contains("elapsed=", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("no output for", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("total ceiling", error.Message, StringComparison.Ordinal);
        Assert.Contains($"stdout:{Environment.NewLine}still working\r\n", error.Message, StringComparison.Ordinal);
        Assert.Contains($"stderr:{Environment.NewLine}recent stderr", error.Message, StringComparison.Ordinal);
        Assert.True(error.RootExited);
    }

    private sealed class Child : IAsyncDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "mcg-silence-guard-" + Guid.NewGuid().ToString("N"));
        private int _nextPulse;
        internal Task<MtpTestRunnerScriptTests.ProcessResult> Result { get; private set; } = null!;
        internal Task<MtpTestRunnerScriptTests.ProcessResult> Completion =>
            AwaitEvent(Result, "child exit or guard failure");
        internal int Pid { get; private set; }
        internal bool ExpectedObserverFailure { get; set; }
        internal string PathFor(string name) => Path.Combine(_root, name);
        internal void Release(string name) => File.WriteAllText(PathFor(name), "release");
        internal void ReleaseNextPulse() => Release("pulse-" + _nextPulse++);

        internal static async Task<Child> Start(string body, TimeSpan? timeout = null,
            Action<string, string>? observeOutput = null)
        {
            var child = new Child();
            Directory.CreateDirectory(child._root);
            var script = $"$root = '{child._root.Replace("'", "''", StringComparison.Ordinal)}'; " +
                // EncodedCommand serializes module-autoload progress onto stderr as CLIXML.
                "$ProgressPreference = 'SilentlyContinue'; " +
                "[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false); " +
                "function AwaitFile($name) { " +
                "$watcher = New-Object System.IO.FileSystemWatcher; $watcher.Path = $root; " +
                "try { while (-not [IO.File]::Exists([IO.Path]::Combine($root, $name)) -and " +
                "-not [IO.File]::Exists([IO.Path]::Combine($root, 'release'))) { " +
                "$null = $watcher.WaitForChanged([IO.WatcherChangeTypes]::All, 50) } } finally { $watcher.Dispose() } }; " +
                "[IO.File]::WriteAllText([IO.Path]::Combine($root, 'pid.tmp'), [string]$PID); " +
                "[IO.File]::Move([IO.Path]::Combine($root, 'pid.tmp'), [IO.Path]::Combine($root, 'pid')); " + body;
            var startInfo = new ProcessStartInfo("powershell.exe")
            {
                WorkingDirectory = child._root,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-NonInteractive");
            startInfo.ArgumentList.Add("-EncodedCommand");
            startInfo.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
            child.Result = Task.Run(() => MtpTestRunnerScriptTests.Run(startInfo, timeout, observeOutput: observeOutput));
            try
            {
                await child.WaitForPid();
                child.Pid = int.Parse(File.ReadAllText(child.PathFor("pid")), System.Globalization.CultureInfo.InvariantCulture);
                return child;
            }
            catch
            {
                await child.DisposeAsync();
                throw;
            }
        }

        private async Task WaitForPid()
        {
            var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var watcher = new FileSystemWatcher(_root);
            FileSystemEventHandler check = (_, _) => { if (File.Exists(PathFor("pid"))) arrived.TrySetResult(); };
            watcher.Created += check;
            watcher.Renamed += (_, _) => { if (File.Exists(PathFor("pid"))) arrived.TrySetResult(); };
            watcher.EnableRaisingEvents = true;
            if (File.Exists(PathFor("pid"))) arrived.TrySetResult();
            var first = await AwaitEvent(Task.WhenAny(arrived.Task, Result), "child pid file or early exit");
            if (first == Result)
            {
                var result = await Result;
                throw new InvalidOperationException($"Child exited before publishing its pid: {result.Stdout}{result.Stderr}");
            }
            await arrived.Task;
        }

        internal async Task Observe(Task signal)
        {
            var first = await AwaitEvent(Task.WhenAny(signal, Result), "child output/guard signal or early exit");
            if (first == Result)
            {
                var result = await Result;
                throw new InvalidOperationException($"Child exited before the output/guard signal: {result.Stdout}{result.Stderr}");
            }
            await signal;
        }

        private static async Task<T> AwaitEvent<T>(Task<T> task, string eventName)
        {
            try { return await task.WaitAsync(MtpTestRunnerScriptTests.NativeMtpRealProcessHangGuard); }
            catch (TimeoutException error) when (error is not MtpTestRunnerScriptTests.RealProcessHangGuardException)
            {
                throw new TimeoutException($"Did not observe {eventName} before the real-process hang guard.", error);
            }
        }

        public async ValueTask DisposeAsync()
        {
            Release("second");
            Release("release");
            // The ticking child also has a file-controlled exit, including on assertion failures.
            ReleaseNextPulse();
            try { await Completion; }
            catch (MtpTestRunnerScriptTests.RealProcessHangGuardException) { }
            catch (InvalidOperationException) when (ExpectedObserverFailure) { }
            finally { Directory.Delete(_root, recursive: true); }
        }
    }
}
