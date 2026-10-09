using Mcg.AgentOrchestrator.App.Orchestration;

// Parallel-safe: every board, registry, database and junction belongs to this fixture.
public sealed class DefaultStateMoverTests
{
    [Fact]
    public async Task Move_TerminalBoard_VerifiesTreeAndLeavesBackupAndJunction()
    {
        using var fixture = new DefaultStateMoverFixture();
        await fixture.Seed();
        var before = DefaultStateMoverFixture.Snapshot(fixture.Source);
        var result = fixture.Mover().Move();
        Assert.True(result.Succeeded, string.Join("; ", result.Refusals));
        Assert.Equal(before, DefaultStateMoverFixture.Snapshot(fixture.Destination));
        Assert.Equal(before, DefaultStateMoverFixture.Snapshot(fixture.Backup));
        Assert.False((File.GetAttributes(fixture.Backup) & FileAttributes.ReparsePoint) != 0);
        Assert.True((File.GetAttributes(fixture.Source) & FileAttributes.ReparsePoint) != 0);
        var target = new DirectoryInfo(fixture.Source).LinkTarget;
        Assert.NotNull(target);
        Assert.True(DefaultProjectStateLocation.SameRoot(target, fixture.Destination));
        var record = Assert.IsType<DefaultProjectStateLocation>(fixture.Registry.GetDefaultStateLocation(fixture.Repository));
        Assert.False(Path.IsPathRooted(record.RelativeStateDirectory));
        Assert.Equal(fixture.Destination, record.ResolveStateDirectory(fixture.DataRoot));
        Assert.Equal(fixture.Backup, Path.Combine(fixture.Repository, record.BackupDirectoryName));
        Assert.Equal(1, DefaultStateMoverFixture.GoalCount(fixture.Source));
        Assert.Equal(DefaultStateMoverFixture.GoalCount(fixture.Source), DefaultStateMoverFixture.GoalCount(fixture.Destination));
    }

    [Fact]
    public async Task Undo_UnchangedMovedBoard_RestoresRealDirectoryAndRetainsCopies()
    {
        using var fixture = new DefaultStateMoverFixture();
        await fixture.Seed();
        var before = DefaultStateMoverFixture.Snapshot(fixture.Source);
        Assert.True(fixture.Mover().Move().Succeeded);
        var result = fixture.Mover().Undo();
        Assert.True(result.Succeeded, string.Join("; ", result.Refusals));
        Assert.Null(new DirectoryInfo(fixture.Source).LinkTarget);
        Assert.False((File.GetAttributes(fixture.Source) & FileAttributes.ReparsePoint) != 0);
        Assert.Equal(before, DefaultStateMoverFixture.Snapshot(fixture.Source));
        Assert.Equal(before, DefaultStateMoverFixture.Snapshot(fixture.Backup));
        Assert.Equal(before, DefaultStateMoverFixture.Snapshot(fixture.Destination));
        Assert.Null(fixture.Registry.GetDefaultStateLocation(fixture.Repository));
    }

    [Fact]
    public async Task Undo_PostMoveWrites_RestoresCurrentTreeInsteadOfStaleBackup()
    {
        using var fixture = new DefaultStateMoverFixture();
        await fixture.Seed();
        Assert.True(fixture.Mover().Move().Succeeded);
        File.WriteAllText(Path.Combine(fixture.Destination, "logs", "after.jsonl"), "new event");
        var current = DefaultStateMoverFixture.Snapshot(fixture.Destination);
        Assert.True(fixture.Mover().Undo().Succeeded);
        Assert.Equal(current, DefaultStateMoverFixture.Snapshot(fixture.Source));
        Assert.False(File.Exists(Path.Combine(fixture.Backup, "logs", "after.jsonl")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Undo_UnusableMovedTree_RestoresBackup(bool missing)
    {
        using var fixture = new DefaultStateMoverFixture();
        await fixture.Seed();
        var before = DefaultStateMoverFixture.Snapshot(fixture.Source);
        Assert.True(fixture.Mover().Move().Succeeded);
        if (missing) Directory.Move(fixture.Destination, fixture.Destination + ".retained");
        else File.WriteAllText(Path.Combine(fixture.Destination, "state.db"), "corrupt database");
        var result = fixture.Mover().Undo();
        Assert.True(result.Succeeded, string.Join("; ", result.Refusals));
        Assert.Equal(before, DefaultStateMoverFixture.Snapshot(fixture.Source));
        Assert.Null(fixture.Registry.GetDefaultStateLocation(fixture.Repository));
    }

    [Fact]
    public async Task Move_JunctionCreationFails_RestoresOriginalAndClearsRecord()
    {
        using var fixture = new DefaultStateMoverFixture();
        await fixture.Seed();
        var before = DefaultStateMoverFixture.Snapshot(fixture.Source);
        var linkAttempted = false;
        var result = fixture.Mover(createLink: (_, _) =>
        {
            linkAttempted = true;
            Assert.NotNull(fixture.Registry.GetDefaultStateLocation(fixture.Repository));
            Assert.Equal(before, DefaultStateMoverFixture.Snapshot(fixture.Backup));
            throw new IOException("injected junction failure");
        }).Move();
        Assert.True(linkAttempted);
        Assert.False(result.Succeeded);
        Assert.Contains(result.Refusals, reason => reason.Contains("injected junction failure"));
        Assert.Equal(before, DefaultStateMoverFixture.Snapshot(fixture.Source));
        Assert.Equal(before, DefaultStateMoverFixture.Snapshot(fixture.Destination));
        Assert.Null(new DirectoryInfo(fixture.Source).LinkTarget);
        Assert.Null(fixture.Registry.GetDefaultStateLocation(fixture.Repository));
    }
}
