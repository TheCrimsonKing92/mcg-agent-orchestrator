using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;
using Fixture = DotnetBuildEnvironmentManagerFocusedChildFixture;

[Xunit.Collection(TestCollections.DotnetBuildSlots)]
public sealed class DotnetBuildEnvironmentManagerTestsFocusedChildFailures
{
    public static bool IsWindows => OperatingSystem.IsWindows();

    [Theory]
    [InlineData("absent")]
    [InlineData("malformed")]
    [InlineData("invalid-schema")]
    public async Task IdentityFailure_PreservesDiagnostics(string fault)
    {
        await using var owner = CreateOwner();
        if (fault != "absent") File.WriteAllText(owner.IdentityPath, fault == "malformed" ? "{private-payload" : "{}");
        var failure = await Assert.ThrowsAsync<Xunit.Sdk.XunitException>(() => owner.RunAsync(() =>
        {
            using var identity = owner.RequireIdentity(waitForPublication: false);
            return Task.CompletedTask;
        }));
        Assert.StartsWith(Fixture.IdentityFailure, failure.Message);
        var diagnostics = ParseDiagnostics(failure.Message);
        Assert.Equal("identity-readiness", diagnostics.GetProperty("phase").GetString());
        Assert.Equal(fault == "absent" ? "absent" : "malformed", diagnostics.GetProperty("identityState").GetString());
        Assert.Equal("not-started", diagnostics.GetProperty("harnessState").GetString());
        Assert.DoesNotContain("private-payload", failure.Message);
        Assert.False(Directory.Exists(owner.Root));
    }

    [Fact]
    public async Task StreamCapture_ExposesBoundedOpenTails()
    {
        var reader = new GatedReader();
        var capture = new Fixture.TailCapture(reader);
        Assert.Equal("open", capture.Snapshot().State);
        Assert.Equal("empty", capture.Snapshot().Content);
        reader.Chunks.Writer.TryWrite(new string('a', 4096) + new string('b', 4096));
        await reader.WaitingForNext.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(new string('b', 4096), capture.Snapshot().Tail);
        Assert.Equal("open", capture.Snapshot().State);
        Assert.Equal("nonempty", capture.Snapshot().Content);
        reader.Chunks.Writer.Complete();
        await capture.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("completed", capture.Snapshot().State);
        Assert.Equal(4096, capture.Snapshot().Tail.Length);
        var empty = new Fixture.TailCapture(new StringReader(""));
        await empty.Completion;
        Assert.Equal("completed", empty.Snapshot().State);
        Assert.Equal("empty", empty.Snapshot().Content);
    }

    [Fact]
    public async Task StreamCapture_ReportsCaptureFailure()
    {
        var reader = new GatedReader();
        var capture = new Fixture.TailCapture(reader);
        reader.Chunks.Writer.TryWrite("partial");
        await reader.WaitingForNext.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("partial", capture.Snapshot().Tail);
        reader.Chunks.Writer.Complete(new IOException("fixture-controlled-failure"));
        await capture.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("capture-failed", capture.Snapshot().State);
        Assert.Equal("partial", capture.Snapshot().Tail);
        Assert.Equal(nameof(IOException), capture.Snapshot().Error);
    }

    [Theory(Skip = "Requires Windows contained launch and exclusive file sharing.", SkipUnless = nameof(IsWindows))]
    [InlineData("absent")]
    [InlineData("malformed")]
    [InlineData("unreadable")]
    public async Task Cleanup_EarlyFailure_ReapsOwnedTree(string fault)
    {
        await using var owner = StartControlled();
        using var descendant = HoldReadyDescendant(owner);
        FileStream? identityLock = null;
        try
        {
            if (fault == "malformed") File.WriteAllText(owner.IdentityPath, "{fixture-malformed");
            if (fault == "unreadable")
            {
                File.WriteAllText(owner.IdentityPath, "{}");
                identityLock = File.Open(owner.IdentityPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            }
            Assert.Equal(fault, Fixture.ObserveIdentity(owner.IdentityPath));
            var failure = await Assert.ThrowsAsync<Xunit.Sdk.XunitException>(() => owner.RunAsync(() =>
            {
                // Readiness is independent of identity; this selects the failure, not a startup timeout.
                using var identity = owner.RequireIdentity(waitForPublication: false);
                return Task.CompletedTask;
            }));
            Assert.StartsWith(Fixture.IdentityFailure, failure.Message);
            var diagnostics = ParseDiagnostics(failure.Message);
            Assert.Equal(fault, diagnostics.GetProperty("identityState").GetString());
            Assert.Equal("alive", diagnostics.GetProperty("harnessState").GetString());
            Assert.True(diagnostics.GetProperty("harnessIdentity").GetProperty("processId").GetInt32() > 0);
            Assert.False(string.IsNullOrWhiteSpace(diagnostics.GetProperty("harnessIdentity").GetProperty("executable").GetString()));
            AssertStream(diagnostics.GetProperty("stdout"), "nonempty");
            AssertStream(diagnostics.GetProperty("stderr"), "empty");
            Assert.DoesNotContain("FOCUSED_TREE_SHELL", failure.Message);
            Assert.True(owner.CustodyResolved, failure.Message);
            Assert.True(owner.Harness.HasExited, failure.Message);
            Assert.True(descendant.WaitForExit(5000), failure.Message);
            if (identityLock is not null)
            {
                // Windows sharing prevents deletion too; that cleanup failure must be appended to the primary.
                Assert.Contains("root-delete-failed:", failure.Message);
                identityLock.Dispose();
                identityLock = null;
                Assert.Null(await owner.CleanupAsync());
            }
            else Assert.Contains("cleanup=resolved", failure.Message);
            Assert.False(Directory.Exists(owner.Root));
        }
        finally { identityLock?.Dispose(); }
    }

    [Fact(Skip = "Requires Windows contained launch.", SkipUnless = nameof(IsWindows))]
    public async Task Cleanup_ExitedHarness_ReleasesOrphanedDescendant()
    {
        await using var owner = StartControlled("orphan");
        using var descendant = HoldReadyDescendant(owner);
        File.WriteAllText(Path.Combine(owner.Root, "proceed"), "go");
        Assert.True(owner.Harness.WaitForExit(10000), owner.Diagnostics());
        Assert.False(descendant.HasExited, owner.Diagnostics());
        var releaseObserved = false;
        var failure = await FailIdentity(owner, observeCleanup: exited =>
        {
            // Observe the descendant's signal before successful cleanup removes the fixture root.
            releaseObserved = File.Exists(Path.Combine(owner.Root, "descendant-release-observed"));
            return exited;
        });
        Assert.StartsWith(Fixture.IdentityFailure, failure.Message);
        Assert.True(descendant.WaitForExit(5000), failure.Message);
        Assert.True(owner.CustodyResolved, failure.Message);
        Assert.True(releaseObserved, "Orphaned descendant did not observe cooperative release. " + failure.Message);
        Assert.False(Directory.Exists(owner.Root));
    }

    [Fact(Skip = "Requires Windows contained launch.", SkipUnless = nameof(IsWindows))]
    public async Task Cleanup_LatePublication_ReapsOwnedTree()
    {
        await using var owner = StartControlled("late-publication");
        using var descendant = HoldReadyDescendant(owner);
        Assert.Equal("absent", Fixture.ObserveIdentity(owner.IdentityPath));
        var failure = await FailIdentity(owner);
        Assert.StartsWith(Fixture.IdentityFailure, failure.Message);
        Assert.True(descendant.WaitForExit(5000), failure.Message);
        Assert.Contains("late-publication-observed", owner.Stdout!.Snapshot().Tail);
        Assert.True(owner.CustodyResolved, failure.Message);
        Assert.False(Directory.Exists(owner.Root));
    }

    [Fact(Skip = "Requires Windows contained launch.", SkipUnless = nameof(IsWindows))]
    public async Task Cleanup_LateStart_ObservesPersistentRelease()
    {
        await using var owner = StartControlled("late-start");
        WaitForFile(owner, "root-ready");
        Assert.False(File.Exists(Path.Combine(owner.Root, "ready.json")), owner.Diagnostics());
        var failure = await FailIdentity(owner);
        Assert.StartsWith(Fixture.IdentityFailure, failure.Message);
        Assert.True(owner.Harness.HasExited, failure.Message);
        Assert.Contains("late-release-observed", owner.Stdout!.Snapshot().Tail);
        Assert.True(owner.CustodyResolved, failure.Message);
        Assert.False(Directory.Exists(owner.Root));
    }

    [Fact(Skip = "Requires Windows contained launch.", SkipUnless = nameof(IsWindows))]
    public async Task Cleanup_UncooperativeDescendant_UsesOwnedGroup()
    {
        await using var owner = StartControlled("ignore-release");
        using var descendant = HoldReadyDescendant(owner);
        var failure = await FailIdentity(owner);
        Assert.True(owner.ForcedTermination, failure.Message);
        Assert.True(descendant.WaitForExit(5000), failure.Message);
        Assert.True(owner.CustodyResolved, failure.Message);
        Assert.False(Directory.Exists(owner.Root));
    }

    [Fact(Skip = "Requires Windows contained launch.", SkipUnless = nameof(IsWindows))]
    public Task Cleanup_ForeignIdentity_PreservesUnrelatedProcess() => CheckForeignIdentity(stale: false);

    [Fact(Skip = "Requires Windows contained launch.", SkipUnless = nameof(IsWindows))]
    public Task Cleanup_StaleIdentity_PreservesUnrelatedProcess() => CheckForeignIdentity(stale: true);

    private static async Task CheckForeignIdentity(bool stale)
    {
        await using var unrelated = StartControlled();
        using var unrelatedDescendant = HoldReadyDescendant(unrelated);
        await using var owner = StartControlled();
        using var descendant = HoldReadyDescendant(owner);
        File.WriteAllText(owner.IdentityPath, JsonSerializer.Serialize(new
        {
            processId = unrelated.Harness.Id, parentProcessId = Environment.ProcessId,
            startedAt = stale ? DateTime.UnixEpoch : unrelated.Harness.StartTime.ToUniversalTime(),
            executable = unrelated.Harness.MainModule!.FileName, identityStatus = "confirmed"
        }));
        Assert.Equal("valid", Fixture.ObserveIdentity(owner.IdentityPath));
        Assert.Null(await owner.CleanupAsync());
        Assert.True(descendant.WaitForExit(5000), owner.Diagnostics());
        Assert.False(Directory.Exists(owner.Root));
        Assert.False(unrelated.Harness.HasExited, "Foreign published identity must not confer kill authority.");
        Assert.False(unrelatedDescendant.HasExited, "Unrelated tree must remain live.");
        Assert.Null(await unrelated.CleanupAsync());
        Assert.True(unrelatedDescendant.WaitForExit(5000), unrelated.Diagnostics());
    }

    [Fact(Skip = "Requires Windows contained launch.", SkipUnless = nameof(IsWindows))]
    public async Task Cleanup_UnconfirmedObservation_PreservesPrimaryFailure()
    {
        await using var owner = StartControlled();
        using var descendant = HoldReadyDescendant(owner);
        try
        {
            var failure = await Assert.ThrowsAsync<Xunit.Sdk.XunitException>(() => owner.RunAsync(() =>
            {
                using var identity = owner.RequireIdentity(waitForPublication: false);
                return Task.CompletedTask;
            }, observeCleanup: actual => false));
            Assert.StartsWith(Fixture.IdentityFailure, failure.Message);
            Assert.Contains("cleanup=owned-tree-exit-unconfirmed", failure.Message);
            Assert.DoesNotContain("cleanup=resolved", failure.Message);
            Assert.False(owner.CustodyResolved);
            Assert.True(File.Exists(owner.ReleasePath), failure.Message);
            // Only observation is injected. The retained job and live handles establish real exit.
            Assert.True(owner.Harness.HasExited, failure.Message);
            Assert.True(descendant.WaitForExit(5000), failure.Message);
        }
        finally
        {
            Assert.Null(await owner.CleanupAsync());
        }
        Assert.False(Directory.Exists(owner.Root));
    }

    private static Task<Xunit.Sdk.XunitException> FailIdentity(Fixture owner, Func<bool, bool>? observeCleanup = null) =>
        Assert.ThrowsAsync<Xunit.Sdk.XunitException>(() => owner.RunAsync(() =>
        {
            using var identity = owner.RequireIdentity(waitForPublication: false);
            return Task.CompletedTask;
        }, observeCleanup));

    private static JsonElement ParseDiagnostics(string failure)
    {
        var line = failure.Split(Environment.NewLine).Single(value => value.StartsWith("{\"phase\":", StringComparison.Ordinal));
        using var parsed = JsonDocument.Parse(line);
        return parsed.RootElement.Clone();
    }

    private static void AssertStream(JsonElement stream, string content)
    {
        Assert.Equal("open", stream.GetProperty("State").GetString());
        Assert.Equal(content, stream.GetProperty("Content").GetString());
        Assert.InRange(stream.GetProperty("Tail").GetString()!.Length, 0, 4096);
    }

    private static Fixture CreateOwner() => new(Path.Combine(Path.GetTempPath(), "mcg-focused-" + Guid.NewGuid().ToString("N")));

    private static Process HoldReadyDescendant(Fixture owner)
    {
        JsonDocument? ready = null;
        Assert.True(SpinWait.SpinUntil(() => DotnetBuildEnvironmentManagerTests.TryReadJsonDocument(
            Path.Combine(owner.Root, "ready.json"), out ready), TimeSpan.FromSeconds(15)),
            "Independent descendant readiness was not published. " + owner.Diagnostics());
        using (ready)
        {
            var pid = ready!.RootElement.GetProperty("processId").GetInt32();
            var process = Process.GetProcessById(pid);
            try
            {
                _ = process.SafeHandle; // Hold actual observation identity across PID reuse; never used for termination.
                Assert.False(process.HasExited, "Ready descendant already exited. " + owner.Diagnostics());
                Assert.Equal("running-same-identity", DotnetBuildEnvironmentManagerTests.GetFocusedChildIdentityState(ready.RootElement));
                Assert.True(SpinWait.SpinUntil(() => owner.Stdout!.Snapshot().Tail.Contains("fixture-ready", StringComparison.Ordinal),
                    TimeSpan.FromSeconds(10)), "Harness did not flush controlled readiness output. " + owner.Diagnostics());
                return process;
            }
            catch { process.Dispose(); throw; }
        }
    }

    private static void WaitForFile(Fixture owner, string file) => Assert.True(
        SpinWait.SpinUntil(() => File.Exists(Path.Combine(owner.Root, file)), TimeSpan.FromSeconds(15)),
        "Fixture control event missing: " + file + ". " + owner.Diagnostics());

    private static Fixture StartControlled(string mode = "normal")
    {
        var owner = CreateOwner();
        var script = Path.Combine(owner.Root, "control.ps1");
        var descendantScript = Path.Combine(owner.Root, "descendant.ps1");
        // FileSystemWatcher is a signal wait; file predicates handle publication before watcher setup.
        const string waitFunction = """
            function Wait-FixtureFile([string]$Path) {
                $watcher = [IO.FileSystemWatcher]::new($env:FOCUSED_TREE_ROOT)
                try {
                    while (-not [IO.File]::Exists($Path) -and -not [IO.File]::Exists($env:FOCUSED_TREE_RELEASE)) {
                        [void]$watcher.WaitForChanged([IO.WatcherChangeTypes]::All, 1000)
                    }
                } finally { $watcher.Dispose() }
            }
            """;
        File.WriteAllText(descendantScript, waitFunction + """

            [IO.File]::WriteAllText((Join-Path $env:FOCUSED_TREE_ROOT 'descendant-ready'), 'ready')
            if ($env:FOCUSED_CONTROL_MODE -eq 'ignore-release') {
                $never = [Threading.EventWaitHandle]::new($false, [Threading.EventResetMode]::ManualReset)
                [void]$never.WaitOne()
            } else {
                Wait-FixtureFile $env:FOCUSED_TREE_RELEASE
                [IO.File]::WriteAllText((Join-Path $env:FOCUSED_TREE_ROOT 'descendant-release-observed'), 'observed')
            }
            """);
        File.WriteAllText(script, waitFunction + """

            $ErrorActionPreference = 'Stop'
            [IO.File]::WriteAllText((Join-Path $env:FOCUSED_TREE_ROOT 'root-ready'), 'ready')
            if ($env:FOCUSED_CONTROL_MODE -eq 'late-start') {
                Wait-FixtureFile (Join-Path $env:FOCUSED_TREE_ROOT 'proceed')
                if ([IO.File]::Exists($env:FOCUSED_TREE_RELEASE)) { [Console]::Out.WriteLine('late-release-observed'); exit 0 }
            }
            if ([IO.File]::Exists($env:FOCUSED_TREE_RELEASE)) { exit 0 }
            $info = [Diagnostics.ProcessStartInfo]::new()
            $info.FileName = $env:FOCUSED_TREE_SHELL
            $info.UseShellExecute = $false
            $info.CreateNoWindow = $true
            foreach ($arg in @('-NoProfile', '-NonInteractive', '-File', (Join-Path $env:FOCUSED_TREE_ROOT 'descendant.ps1'))) {
                [void]$info.ArgumentList.Add($arg)
            }
            $child = [Diagnostics.Process]::Start($info)
            Wait-FixtureFile (Join-Path $env:FOCUSED_TREE_ROOT 'descendant-ready')
            $identity = [ordered]@{
                processId = $child.Id; parentProcessId = $PID
                startedAt = $child.StartTime.ToUniversalTime().ToString('o')
                executable = $child.MainModule.FileName; identityStatus = 'confirmed'
            }
            $identity | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $env:FOCUSED_TREE_ROOT 'ready.json')
            [Console]::Out.WriteLine('fixture-ready')
            [Console]::Out.Flush()
            if ($env:FOCUSED_CONTROL_MODE -eq 'orphan') {
                Wait-FixtureFile (Join-Path $env:FOCUSED_TREE_ROOT 'proceed')
                exit 0
            }
            Wait-FixtureFile $env:FOCUSED_TREE_RELEASE
            if ($env:FOCUSED_CONTROL_MODE -eq 'late-publication') {
                $identity | ConvertTo-Json | Set-Content -LiteralPath $env:FOCUSED_TREE_IDENTITY
                [Console]::Out.WriteLine('late-publication-observed')
            }
            [void]$child.WaitForExit()
            """);
        var start = new ProcessStartInfo(WorkerShell.Executable) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script })
            start.ArgumentList.Add(arg);
        start.Environment["FOCUSED_TREE_ROOT"] = owner.Root;
        start.Environment["FOCUSED_TREE_RELEASE"] = owner.ReleasePath;
        start.Environment["FOCUSED_TREE_IDENTITY"] = owner.IdentityPath;
        start.Environment["FOCUSED_TREE_SHELL"] = WorkerShell.Executable;
        start.Environment["FOCUSED_CONTROL_MODE"] = mode;
        try { owner.Start(start); return owner; }
        catch { owner.DisposeAsync().AsTask().GetAwaiter().GetResult(); throw; }
    }

    private sealed class GatedReader : TextReader
    {
        internal Channel<string> Chunks { get; } = Channel.CreateUnbounded<string>();
        internal TaskCompletionSource WaitingForNext { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private string pending = "";
        private bool receivedChunk;

        public override async Task<int> ReadAsync(char[] buffer, int index, int count)
        {
            if (pending.Length == 0)
            {
                // A subsequent read proves the capture loop consumed and appended the complete previous chunk.
                if (receivedChunk) WaitingForNext.TrySetResult();
                try { pending = await Chunks.Reader.ReadAsync(); receivedChunk = true; }
                catch (ChannelClosedException error) when (error.InnerException is IOException failure) { throw failure; }
                catch (ChannelClosedException) { return 0; }
            }
            var length = Math.Min(count, pending.Length);
            pending.CopyTo(0, buffer, index, length);
            pending = pending[length..];
            return length;
        }
    }
}
