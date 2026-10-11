using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: unique roots and AsyncLocal overrides avoid process-wide writer mutation.
public sealed class GateLaneTimeLedgerWriterFailureTests
{
    [Theory]
    [InlineData(true, "completed")]
    [InlineData(false, "failed")]
    public void ThrowingWriterPreservesBreakdownTextAndOutcome(bool passed, string outcome)
    {
        var root = Directory.CreateTempSubdirectory("gate-lane-writer-failure-").FullName;
        using var probe = GateHostHealthProbe.PushProbe(() => new(
            GateLoadSample.Available(250), GateLoadSample.Unavailable("pool-unavailable")));
        try
        {
            var success = CompleteAttempt(Path.Combine(root, "success"), passed);
            Assert.Single(File.ReadAllLines(Path.Combine(root, "success", ".orchestrator", "gate-lane-time.jsonl")));
            var calls = 0;
            AcceptanceGateProgress failed;
            using (GateLaneTimeLedger.PushAppender((_, _) => { calls++; throw new IOException("injected writer failure"); }))
                failed = CompleteAttempt(Path.Combine(root, "failure"), passed);

            Assert.Equal(1, calls);
            Assert.Equal("gate-phase-breakdown", failed.Phase);
            Assert.Equal(outcome, success.PhaseBreakdown!.Outcome);
            Assert.Equal(outcome, failed.PhaseBreakdown!.Outcome);
            Assert.Equal(success.CurrentTarget, failed.CurrentTarget);
            Assert.Contains("host_launch_ms=250", failed.CurrentTarget);
            Assert.False(File.Exists(Path.Combine(root, "failure", ".orchestrator", "gate-lane-time.jsonl")));
            Assert.Single(GateHostHealthLedger.ReadRecent(GateHostHealthLedger.ResolveStorePath(Path.Combine(root, "failure"))));
        }
        finally { Directory.Delete(root, true); }
    }

    private static AcceptanceGateProgress CompleteAttempt(string root, bool passed)
    {
        Directory.CreateDirectory(root);
        var time = new RecordingTimeProvider();
        var progress = new List<AcceptanceGateProgress>();
        using (var accountant = AcceptanceGatePhaseAccountant.Start(time, "goal", progress.Add))
        {
            accountant.BindHostHealthLedger(root, "attempt");
            accountant.TransitionTo("lanes");
            accountant.RecordLaneSample(TimeSpan.FromMilliseconds(300));
            accountant.RecordLaneSample(TimeSpan.FromMilliseconds(900));
            accountant.RecordRemoteLaneSample(TimeSpan.FromMilliseconds(2000));
            accountant.RecordRemoteLaneSample(TimeSpan.FromMilliseconds(5000));
            time.Advance(TimeSpan.FromMilliseconds(1000));
            accountant.MarkCompleted(passed);
        }
        return Assert.Single(progress);
    }
}
