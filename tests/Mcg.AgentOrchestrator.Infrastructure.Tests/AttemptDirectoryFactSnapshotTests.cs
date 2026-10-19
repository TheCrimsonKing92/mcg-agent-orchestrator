using Mcg.AgentOrchestrator.App.Orchestration;

// Parallel-safe: metadata and paths belong to a unique fixture root.
public sealed class AttemptDirectoryFactSnapshotTests
{
    [Xunit.Fact]
    public void Capture_RecordedDeletion_AdvancesExpectedSetWithoutHashing()
    {
        using var fixture = new RetentionReclaimFixture();
        var paths = RetentionSweepPassCostTests.CreateCandidates(fixture, RetentionReclaimFixture.CompletedId, 40);
        var directory = Path.GetDirectoryName(paths[0])!;
        Assert.Equal(42, Directory.GetFiles(directory).Length);
        var calls = 0;
        var snapshot = AttemptDirectoryFactSnapshot.Capture(directory, RetentionReclaimFixture.CompletedId,
            root => { calls++; return Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories); });
        Assert.True(snapshot.AttemptsReadable);
        Assert.NotEmpty(snapshot.Revision);
        Assert.Equal(1, snapshot.Enumerations);
        Assert.Equal(1, snapshot.FullPathHashes);
        Assert.True(snapshot.VerifyUnchanged());
        Assert.Equal(2, snapshot.Enumerations);
        Assert.Equal(2, snapshot.FullPathHashes);

        File.Delete(paths[0]);
        snapshot.RecordDeletion(paths[0]);
        Assert.Equal(2, snapshot.Enumerations);
        Assert.Equal(2, snapshot.FullPathHashes);
        Assert.True(snapshot.VerifyUnchanged());
        Assert.Equal(3, snapshot.Enumerations);
        Assert.Equal(3, snapshot.FullPathHashes);
        Assert.Equal(3, calls);
    }

    [Xunit.Theory]
    [Xunit.InlineData("added")]
    [Xunit.InlineData("removed")]
    [Xunit.InlineData("ordinal")]
    public void VerifyUnchanged_ExternalChange_RefusesNewFacts(string change)
    {
        using var fixture = new RetentionReclaimFixture();
        var paths = RetentionSweepPassCostTests.CreateCandidates(fixture, RetentionReclaimFixture.CompletedId, 40);
        var directory = Path.GetDirectoryName(paths[0])!;
        var calls = 0;
        var snapshot = AttemptDirectoryFactSnapshot.Capture(directory, RetentionReclaimFixture.CompletedId,
            root => { calls++; return Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories); });
        var revision = snapshot.Revision;
        if (change == "added") File.WriteAllText(Path.Combine(directory, "added.txt"), "new");
        if (change == "removed") File.Delete(paths[0]);
        if (change == "ordinal") RetentionSweepPassCostTests.ChangeOrdinal(directory);

        Assert.True(StorageRetentionMaintenance.TryReadAttemptIdentities(directory,
            RetentionReclaimFixture.CompletedId, out var attempts, out _));
        Assert.Equal(change != "ordinal", snapshot.AttemptFactsUnchanged(attempts));
        Assert.False(snapshot.VerifyUnchanged());
        Assert.Equal(revision, snapshot.Revision);
        Assert.Equal(2, snapshot.Enumerations);
        Assert.Equal(2, snapshot.FullPathHashes);
        Assert.Equal(2, calls);
    }

    [Xunit.Fact]
    public void RecordDeletion_AttemptMetadata_AdvancesExpectedAttemptFacts()
    {
        using var fixture = new RetentionReclaimFixture();
        var paths = RetentionSweepPassCostTests.CreateCandidates(fixture, RetentionReclaimFixture.CompletedId, 40);
        var directory = Path.GetDirectoryName(paths[0])!;
        var snapshot = AttemptDirectoryFactSnapshot.Capture(directory, RetentionReclaimFixture.CompletedId);
        var metadata = Path.Combine(directory, "old.attempt.json");
        File.Delete(metadata);
        snapshot.RecordDeletion(metadata);
        Assert.Equal(1, snapshot.FullPathHashes);
        Assert.True(StorageRetentionMaintenance.TryReadAttemptIdentities(directory,
            RetentionReclaimFixture.CompletedId, out var attempts, out _));
        Assert.True(snapshot.AttemptFactsUnchanged(attempts));
        Assert.True(snapshot.VerifyUnchanged());
    }
}
