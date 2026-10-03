using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: AsyncLocal probe overrides and unique temporary ledgers.
public sealed class AcceptanceGateHostHealthRecordingTests : GoalAcceptanceVerifierTestBase
{
    [Fact]
    public void CompletionSamplesOnceAndAppendsOneRecord()
    {
        var root = CreateRoot();
        var calls = 0;
        using var probe = GateHostHealthProbe.PushProbe(() =>
        {
            calls++;
            return new(GateLoadSample.Available(250), GateLoadSample.Available(11800));
        });
        try
        {
            var time = new RecordingTimeProvider();
            var progress = new List<AcceptanceGateProgress>();
            var accountant = AcceptanceGatePhaseAccountant.Start(time, "goal", progress.Add);
            accountant.BindHostHealthLedger(root, "attempt");
            time.Advance(TimeSpan.FromSeconds(2));
            accountant.MarkCompleted(true);
            accountant.Dispose();
            accountant.Dispose();
            var emitted = Assert.Single(progress);
            Assert.Equal("gate-phase-breakdown", emitted.Phase);
            Assert.Contains("host_launch_ms=250", emitted.CurrentTarget);
            Assert.Contains("host_paged_pool_mb=11800", emitted.CurrentTarget);
            Assert.Equal(TimeSpan.FromSeconds(2), emitted.Elapsed);
            Assert.Equal(1, calls);
            var record = Assert.Single(GateHostHealthLedger.ReadRecent(GateHostHealthLedger.ResolveStorePath(root)));
            Assert.Equal(250d, record.LaunchMs);
            Assert.Equal(11800d, record.PagedPoolMb);
            Assert.Equal("attempt", record.GateAttemptId);
            Assert.Equal(time.GetUtcNow(), record.ObservedAt);
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnavailableOrThrowingProbePreservesGateOutcomeAndWritesNoRecord(bool throws)
    {
        var root = CreateRoot();
        using var probe = GateHostHealthProbe.PushProbe(() => throws
            ? throw new InvalidOperationException("injected probe failure")
            : new(GateLoadSample.Unavailable("launch missing; reason"), GateLoadSample.Unavailable("pool missing")));
        try
        {
            var progress = new List<AcceptanceGateProgress>();
            using (var accountant = AcceptanceGatePhaseAccountant.Start(new RecordingTimeProvider(), "goal", progress.Add))
            {
                accountant.BindHostHealthLedger(root, "attempt");
                accountant.MarkCompleted(true);
            }
            var emitted = Assert.Single(progress);
            Assert.Contains("host_launch_ms=unavailable:", emitted.CurrentTarget);
            Assert.Contains("host_paged_pool_mb=unavailable:", emitted.CurrentTarget);
            Assert.DoesNotContain(' ', emitted.CurrentTarget);
            foreach (var field in emitted.CurrentTarget.Split(';').Where(field => field.StartsWith("host_")))
                Assert.NotEmpty(field.Split("unavailable:")[1]);
            Assert.Equal("completed", emitted.PhaseBreakdown!.Outcome);
            Assert.Empty(GateHostHealthLedger.ReadRecent(GateHostHealthLedger.ResolveStorePath(root)));
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(true, "completed")]
    [InlineData(false, "failed")]
    public void LedgerWriteFailureDoesNotSuppressBreakdownOrReplaceVerdict(bool passed, string outcome)
    {
        var root = CreateRoot();
        using var probe = GateHostHealthProbe.PushProbe(() => new(
            GateLoadSample.Available(250), GateLoadSample.Unavailable("pool-unavailable")));
        try
        {
            var ledger = GateHostHealthLedger.ResolveStorePath(root);
            Directory.CreateDirectory(ledger); // A directory at the file path forces an append failure.
            var progress = new List<AcceptanceGateProgress>();
            using (var accountant = AcceptanceGatePhaseAccountant.Start(new RecordingTimeProvider(), "goal", progress.Add))
            {
                accountant.BindHostHealthLedger(root, "attempt");
                accountant.MarkCompleted(passed);
            }
            var emitted = Assert.Single(progress);
            Assert.Equal(outcome, emitted.PhaseBreakdown!.Outcome);
            Assert.Contains("host_launch_ms=250", emitted.CurrentTarget);
            Assert.Contains("host_paged_pool_mb=unavailable:pool-unavailable", emitted.CurrentTarget);
            Assert.True(Directory.Exists(ledger));
            Assert.False(File.Exists(ledger));
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task VerifierBindsLedgerAndPreservesVerdictForAvailableOrFailedProbe(bool available)
    {
        var root = CreateManifestWorkspace("""
            { "version": 1, "checks": [
                { "name": "host seam", "type": "command", "command": "git", "arguments": ["diff", "--check"] }
              ], "forbiddenChangedPathGlobs": [] }
            """);
        using var probe = GateHostHealthProbe.PushProbe(() => available
            ? new(GateLoadSample.Available(250), GateLoadSample.Available(11800))
            : throw new IOException("injected"));
        try
        {
            var progress = new List<AcceptanceGateProgress>();
            var verifier = new GoalAcceptanceVerifier((_, _, _) =>
                Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, string.Empty)), new RecordingTimeProvider());
            var result = await verifier.RunOwnedAsync(root, null, null, null, null, default,
                new AcceptanceRunExecutionOptions(ProgressSink: progress.Add));
            Assert.True(result.Passed);
            var emitted = Assert.Single(progress, item => item.Phase == "gate-phase-breakdown");
            Assert.Equal("completed", emitted.PhaseBreakdown!.Outcome);
            var records = GateHostHealthLedger.ReadRecent(GateHostHealthLedger.ResolveStorePath(root));
            if (available)
            {
                Assert.Contains("host_launch_ms=250", emitted.CurrentTarget);
                var record = Assert.Single(records);
                Assert.Equal(250d, record.LaunchMs);
                Assert.Equal(11800d, record.PagedPoolMb);
                Assert.False(string.IsNullOrWhiteSpace(record.GateAttemptId));
            }
            else
            {
                Assert.Contains("host_launch_ms=unavailable:probe-error:IOException", emitted.CurrentTarget);
                Assert.Empty(records);
            }
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void PartialAvailabilityWritesNullPoolAndRestoresNestedProbe()
    {
        using var outer = GateHostHealthProbe.PushProbe(() => new(
            GateLoadSample.Available(250), GateLoadSample.Unavailable("pool missing")));
        using (GateHostHealthProbe.PushProbe(() => new(
            GateLoadSample.Unavailable("launch missing"), GateLoadSample.Available(11800))))
        {
            var inner = GateHostHealthProbe.Capture();
            Assert.False(inner.LaunchMs.IsAvailable);
            Assert.Equal(11800d, inner.PagedPoolMb.Value);
        }
        var root = CreateRoot();
        try
        {
            var progress = new List<AcceptanceGateProgress>();
            using (var accountant = AcceptanceGatePhaseAccountant.Start(new RecordingTimeProvider(), "goal", progress.Add))
            {
                accountant.BindHostHealthLedger(root, "attempt");
                accountant.MarkCompleted(true);
            }
            var record = Assert.Single(GateHostHealthLedger.ReadRecent(GateHostHealthLedger.ResolveStorePath(root)));
            Assert.Equal(250d, record.LaunchMs);
            Assert.Null(record.PagedPoolMb);
            Assert.Contains("host_paged_pool_mb=unavailable:pool-missing", Assert.Single(progress).CurrentTarget);
        }
        finally { Directory.Delete(root, true); }
    }

    private static string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "host-health", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }
}
