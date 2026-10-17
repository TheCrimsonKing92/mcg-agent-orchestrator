using Mcg.AgentOrchestrator.App.Orchestration;

// Parallel-safe: all filesystem changes and writer leases use a unique fixture root.
public sealed class RetentionSweepGuaranteeTests
{
    [Xunit.Theory]
    [Xunit.InlineData("content-and-time")]
    [Xunit.InlineData("length")]
    [Xunit.InlineData("time")]
    [Xunit.InlineData("missing")]
    public void Run_ThirdCandidateChanges_RefusesOnlyThatCandidate(string change)
    {
        using var fixture = new RetentionReclaimFixture();
        var paths = RetentionSweepPassCostTests.CreateCandidates(fixture, RetentionReclaimFixture.CompletedId, 5);
        var calls = 0;
        var result = RetentionSweepPassCostTests.Run(fixture, hook: path =>
        {
            if (++calls != 3) return;
            Assert.Equal(paths[2], path);
            var time = File.GetLastWriteTimeUtc(path);
            if (change is "content-and-time" or "length") File.WriteAllText(path, "changed candidate content");
            if (change == "length") File.SetLastWriteTimeUtc(path, time);
            if (change is "content-and-time" or "time") File.SetLastWriteTimeUtc(path, time.AddHours(1));
            if (change == "missing") File.Delete(path);
        });

        Assert.False(result.Failed);
        Assert.Equal(5, calls);
        Assert.Equal(4, result.AcceptanceArtifactsDeleted);
        Assert.Equal(change != "missing", File.Exists(paths[2]));
        Assert.All(paths.Where(path => path != paths[2]), path => Assert.False(File.Exists(path)));
        Assert.Contains(result.Decisions, decision => decision.Path == paths[2] &&
            decision.Action == EvidenceRetentionAction.RetainedUndecidable &&
            decision.Reason == "candidate-changed-before-effect");
    }

    [Xunit.Fact]
    public void Run_ThirdHookChangesAttempt_RefusesCurrentAndLaterCandidates()
    {
        using var fixture = new RetentionReclaimFixture();
        var paths = RetentionSweepPassCostTests.CreateCandidates(fixture, RetentionReclaimFixture.CompletedId, 5);
        var calls = 0;
        var result = RetentionSweepPassCostTests.Run(fixture, hook: path =>
        {
            if (++calls == 3) RetentionSweepPassCostTests.ChangeOrdinal(Path.GetDirectoryName(path)!);
        });

        Assert.False(result.Failed);
        Assert.Equal(3, calls);
        Assert.Equal(2, result.AcceptanceArtifactsDeleted);
        Assert.All(paths.Take(2), path => Assert.False(File.Exists(path)));
        Assert.All(paths.Skip(2), path =>
        {
            Assert.True(File.Exists(path));
            Assert.Contains(result.Decisions, decision => decision.Path == path &&
                decision.Action == EvidenceRetentionAction.RetainedUndecidable &&
                decision.Reason == "retention-facts-changed-before-effect");
        });
    }

    [Xunit.Fact]
    public void Run_ThirdHookAddsUnrelatedFile_ReportsDirectoryChangeAfterDeletion()
    {
        using var fixture = new RetentionReclaimFixture();
        var paths = RetentionSweepPassCostTests.CreateCandidates(fixture, RetentionReclaimFixture.CompletedId, 5);
        var directory = Path.GetDirectoryName(paths[0])!;
        var added = Path.Combine(directory, "unrelated.txt");
        var calls = 0;
        var result = RetentionSweepPassCostTests.Run(fixture, hook: _ =>
        {
            if (++calls == 3) File.WriteAllText(added, "unrelated evidence");
        });

        Assert.False(result.Failed);
        Assert.Equal(5, calls);
        Assert.Equal(5, result.AcceptanceArtifactsDeleted);
        Assert.All(paths, path => Assert.False(File.Exists(path)));
        Assert.Equal("unrelated evidence", File.ReadAllText(added));
        var decision = Assert.Single(result.Decisions.Where(item =>
            item.Reason == "retention-facts-changed-during-sweep"));
        Assert.Equal(directory, decision.Path);
        Assert.Equal(EvidenceRetentionAction.RetainedUndecidable, decision.Action);
        Assert.NotEmpty(decision.FactRevision!);
    }

    [Xunit.Fact]
    public void Run_EveryDeletionHook_HoldsWriterLease()
    {
        using var fixture = new RetentionReclaimFixture();
        var paths = RetentionSweepPassCostTests.CreateCandidates(fixture, RetentionReclaimFixture.CompletedId, 5);
        var directory = Path.GetDirectoryName(paths[0])!;
        var calls = 0;
        var result = RetentionSweepPassCostTests.Run(fixture, hook: path =>
        {
            calls++;
            AssertLeaseHeld(Path.GetDirectoryName(path)!);
        });

        Assert.False(result.Failed);
        Assert.Equal(5, calls);
        Assert.Equal(5, result.AcceptanceArtifactsDeleted);
        using var available = StorageRetentionMaintenance.TryAcquireAttemptWriterLease(directory);
        Assert.NotNull(available);
    }

    [Xunit.Fact]
    public void Run_PreMutationPathChange_TaintsPassWithoutRepeatedVerification()
    {
        using var fixture = new RetentionReclaimFixture();
        var paths = RetentionSweepPassCostTests.CreateCandidates(fixture, RetentionReclaimFixture.CompletedId, 5);
        var passes = new List<AttemptDirectoryPassStats>();
        var calls = 0;
        var result = RetentionSweepPassCostTests.Run(fixture, hook: _ => calls++, observer: passes.Add,
            beforePass: directory => File.WriteAllText(Path.Combine(directory, "added.txt"), "changed"));

        Assert.False(result.Failed);
        Assert.Equal(0, calls);
        Assert.Equal(0, result.AcceptanceArtifactsDeleted);
        var pass = Assert.Single(passes);
        Assert.Equal(2, pass.Enumerations);
        Assert.Equal(2, pass.FullPathHashes);
        Assert.All(paths, path =>
        {
            Assert.True(File.Exists(path));
            Assert.Contains(result.Decisions, decision => decision.Path == path &&
                decision.Reason == "retention-facts-changed-before-effect");
        });
    }

    internal static void AssertLeaseHeld(string directory)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var lease = StorageRetentionMaintenance.TryAcquireAttemptWriterLease(directory);
                Assert.Null(lease);
            }
            catch (Exception ex) { failure = ex; }
        }) { IsBackground = true };
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Writer-lease probe thread did not finish.");
        Assert.Null(failure);
    }
}
