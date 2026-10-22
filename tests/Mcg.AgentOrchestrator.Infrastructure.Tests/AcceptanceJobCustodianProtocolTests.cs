using System.Text.Json;
using System.Diagnostics;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: protocol, bounded readers and operation-local clocks only.
public sealed class AcceptanceJobCustodianProtocolTests
{
    private static readonly SpawnProcessIdentity Holder = new(41,
        DateTimeOffset.Parse("2026-10-21T12:00:00Z"), Path.GetFullPath("holder.exe"));
    private static readonly SpawnProcessIdentity Custodian = Holder with { ProcessId = 42 };
    private static readonly AcceptanceJobCustodianProtocol Request = new("request", "exact-job", Holder, null, null);

    [Theory]
    [InlineData("matching")]
    [InlineData("missing")]
    [InlineData("malformed")]
    [InlineData("phase")]
    [InlineData("job")]
    [InlineData("holder-pid")]
    [InlineData("holder-start")]
    [InlineData("holder-image")]
    [InlineData("custodian")]
    [InlineData("settlement")]
    public void ReadinessRequiresExactProtocolAndIdentities(string field)
    {
        var response = Request with { Phase = "ready", Custodian = Custodian };
        response = field switch
        {
            "phase" => response with { Phase = "attached" },
            "job" => response with { JobName = "other" },
            "holder-pid" => response with { Holder = Holder with { ProcessId = 99 } },
            "holder-start" => response with { Holder = Holder with { StartedAt = Holder.StartedAt.AddSeconds(1) } },
            "holder-image" => response with { Holder = Holder with { ImagePath = Path.GetFullPath("other.exe") } },
            "custodian" => response with { Custodian = Custodian with { ProcessId = 99 } },
            "settlement" => response with { SettlementPath = Path.GetFullPath("other.json") },
            _ => response
        };
        var line = field == "missing" ? null : field == "malformed" ? "{broken" : JsonSerializer.Serialize(response);
        var error = Record.Exception(() => AcceptanceJobCustodianProtocol.Validate(line, "ready", Request, Custodian));
        if (field == "matching") Assert.Null(error);
        else Assert.NotNull(error);
    }

    [Fact]
    public void AttachmentCannotReuseReadiness()
    {
        var line = JsonSerializer.Serialize(Request with { Phase = "ready", Custodian = Custodian });
        Assert.Throws<InvalidOperationException>(() =>
            AcceptanceJobCustodianProtocol.Validate(line, "attached", Request, Custodian));
    }

    [Theory]
    [InlineData("incomplete")]
    [InlineData("oversized")]
    public async Task ProtocolReaderRejectsTruncationAndUnboundedLines(string fault)
    {
        using var reader = new StringReader(fault == "incomplete" ? "partial" : new string('x', 16385) + "\n");
        await Assert.ThrowsAsync<InvalidOperationException>(() => AcceptanceJobCustodianProtocol.ReadLineAsync(reader));
    }

    [Fact]
    public async Task DiagnosticsRetainOnlyBoundedTailWhileDrainingEntireStream()
    {
        var diagnostics = new AcceptanceJobCustodianDiagnostics();
        using var reader = new StringReader(new string('x', 100_000) + "last-diagnostic");
        await diagnostics.DrainAsync(reader);
        Assert.Equal(-1, reader.Peek());
        Assert.Equal(AcceptanceJobCustodianDiagnostics.Capacity, diagnostics.Snapshot().Length);
        Assert.EndsWith("last-diagnostic", diagnostics.Snapshot());
    }

    [Fact]
    public async Task StartupDeadlineIsInjectedWithoutWaitingForProductionTimeout()
    {
        var clock = new DeadlineClock();
        var bounds = new AcceptanceJobCustodianBounds { TimeProvider = clock };
        using var reader = new HeldReader();
        var pending = bounds.ReadLineAsync(reader);
        Assert.Equal(TimeSpan.FromSeconds(30), clock.DueTime);
        Assert.Equal(TimeSpan.FromSeconds(5), bounds.DiagnosticDrain);
        clock.Expire();
        await Assert.ThrowsAsync<TimeoutException>(() => pending);
        reader.End();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StartupFailureDrainsFloodedStderrAndRetainsFinalDiagnostic(bool malformed)
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = Directory.CreateTempSubdirectory("custody-diagnostic-").FullName;
        try
        {
            var executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell", "v1.0", "powershell.exe");
            // Read the request before producing failure, so registration is deterministic.
            var script = "[void][Console]::ReadLine();[Console]::Error.Write(('x'*100000));" +
                "[Console]::Error.WriteLine('final-startup-diagnostic');" +
                (malformed ? "[Console]::WriteLine('{broken');[void][Console]::ReadLine();" : "exit 7");
            var launch = new AcceptanceJobCustodianLaunchSpecification(executable,
                ["-NoLogo", "-NoProfile", "-NonInteractive", "-Command", script], root);
            using var self = Process.GetCurrentProcess();
            var identity = SpawnProcessIdentityReader.ReadForRegistration(self).Identity!;
            var error = Assert.Throws<InvalidOperationException>(() => AcceptanceJobCustodian.Start(
                launch, "diagnostic-only", identity, null));
            Assert.Contains("final-startup-diagnostic", error.Message);
            Assert.True(error.Message.Length < 10_000);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private sealed class HeldReader : TextReader
    {
        private readonly TaskCompletionSource<int> _end = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default) => new(_end.Task);
        internal void End() => _end.TrySetResult(0);
    }
    private sealed class DeadlineClock : TimeProvider
    {
        private TimerCallback? _callback;
        private object? _state;
        internal TimeSpan DueTime { get; private set; }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        { _callback = callback; _state = state; DueTime = dueTime; return new Timer(); }
        internal void Expire() => _callback!(_state);
        private sealed class Timer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
