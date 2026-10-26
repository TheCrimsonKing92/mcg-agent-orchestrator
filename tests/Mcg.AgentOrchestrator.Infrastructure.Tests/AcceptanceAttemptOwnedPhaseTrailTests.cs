using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each fact owns its temp root; the lease failure precedes console redirection,
// and the priority request is injected so it cannot change the shared test host's priority.
public sealed class AcceptanceAttemptOwnedPhaseTrailTests
{
    [Theory]
    [InlineData("x.attempt.json", "x.phase.log")]
    [InlineData("x.json", "x.json.phase.log")]
    [InlineData("x.attempt.json.extra", "x.attempt.json.extra.phase.log")]
    [InlineData("x.ATTEMPT.JSON", "x.ATTEMPT.JSON.phase.log")]
    public void MetadataSuffix_MapsOnlyTerminalOrdinalSuffix(string metadata, string expected)
    {
        Assert.Equal(expected, AcceptanceAttemptOwnedPhaseTrail.PathFor(metadata));
    }

    [Fact]
    public void MissingDirectory_MarkDoesNotThrowOrCreateDirectory()
    {
        var root = SharedTestSupport.CreateTempDirectory();
        try
        {
            var missing = Path.Combine(root, "missing");
            var error = Record.Exception(() => AcceptanceAttemptOwnedPhaseTrail.Mark(
                Path.Combine(missing, "x.attempt.json"), "process-started"));
            Assert.Null(error);
            Assert.False(Directory.Exists(missing));
        }
        finally { SharedTestSupport.RemoveTempDirectory(root); }
    }

    [Fact]
    public void MarkedTrail_ReadsLastCompletePhaseAndMissingReturnsNull()
    {
        var root = SharedTestSupport.CreateTempDirectory();
        try
        {
            var metadataPath = Path.Combine(root, "x.attempt.json");
            Assert.Null(AcceptanceAttemptOwnedPhaseTrail.ReadLastPhase(metadataPath));
            AcceptanceAttemptOwnedPhaseTrail.Mark(metadataPath, "process-started");
            AcceptanceAttemptOwnedPhaseTrail.Mark(metadataPath, "failed", "exception=IOException");
            var lines = File.ReadAllLines(AcceptanceAttemptOwnedPhaseTrail.PathFor(metadataPath));
            Assert.Equal(2, lines.Length);
            Assert.Matches(@"^phase=process-started utc=\S+Z pid=\d+$", lines[0]);
            Assert.Matches(@"^phase=failed utc=\S+Z pid=\d+ exception=IOException$", lines[1]);
            Assert.Equal("failed", AcceptanceAttemptOwnedPhaseTrail.ReadLastPhase(metadataPath));

            File.AppendAllText(AcceptanceAttemptOwnedPhaseTrail.PathFor(metadataPath),
                "malformed\nphase=invalid\nphase=partial utc=");
            Assert.Equal("failed", AcceptanceAttemptOwnedPhaseTrail.ReadLastPhase(metadataPath));
        }
        finally { SharedTestSupport.RemoveTempDirectory(root); }
    }

    [Fact]
    public void HeldWriterLease_RecordsStartupPhasesThenFailure()
    {
        var root = SharedTestSupport.CreateTempDirectory();
        using var acquired = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        Exception? holderException = null;
        Thread? holder = null;
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var goal = kernel.CreateGoal("Record the owned child's phase before writer-lease timeout");
            var candidate = ConductorParallelAcceptanceCandidate.Create(goal, 0, [], "branch-timeout", "main-timeout");
            var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(root, TrunkBranchName.Default);
            var attempt = coordinator.CreateAttemptForTests(candidate);
            holder = new Thread(() =>
            {
                try
                {
                    using var lease = StorageRetentionMaintenance.AcquireAttemptWriterLease(
                        Path.GetDirectoryName(attempt.MetadataPath)!);
                    acquired.Set();
                    if (!release.Wait(TestHangGuard.HangSafetyBound))
                        throw new TimeoutException("Writer-lease holder release signal was not observed.");
                }
                catch (Exception ex) { holderException = ex; }
            }) { IsBackground = true };
            holder.Start();
            Assert.True(acquired.Wait(TestHangGuard.HangSafetyBound), "Writer-lease holder did not acquire its lease.");

            var exitCode = ConductorParallelAcceptanceAttemptCoordinator.RunOwnedProcess(
                attempt.MetadataPath, TimeSpan.FromMilliseconds(50), lowerCurrentProcessPriority: () => true);

            Assert.Equal(1, exitCode);
            var lines = File.ReadAllLines(AcceptanceAttemptOwnedPhaseTrail.PathFor(attempt.MetadataPath));
            Assert.Equal(4, lines.Length);
            Assert.StartsWith("phase=process-started ", lines[0]);
            Assert.StartsWith("phase=metadata-read ", lines[1]);
            Assert.StartsWith("phase=priority-applied ", lines[2]);
            Assert.Matches(@"^phase=failed utc=\S+ pid=\d+ exception=\w+$", lines[3]);
            Assert.DoesNotContain(lines, line => line.StartsWith("phase=writer-lease-acquired ", StringComparison.Ordinal));
        }
        finally
        {
            release.Set();
            if (holder is not null)
                Assert.True(holder.Join(TestHangGuard.HangSafetyBound), "Writer-lease holder did not return after release.");
            SharedTestSupport.RemoveTempDirectory(root);
        }
        Assert.Null(holderException);
    }
}
