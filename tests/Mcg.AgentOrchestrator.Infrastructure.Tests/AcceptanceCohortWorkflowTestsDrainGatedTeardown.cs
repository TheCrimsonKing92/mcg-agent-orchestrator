using Xunit;

public sealed class AcceptanceCohortWorkflowTestsDrainGatedTeardown
{
    [Fact]
    public void UndrainedCondition_KeepsTrxAndRepositoryAndNamesCondition()
    {
        using var resources = new TeardownResources();

        var result = AcceptanceCohortWorkflowTestsBackgroundAndCapacity.RemoveAfterObservedDrain(
            () => false, "ActiveRootCount == 0", TimeSpan.FromMilliseconds(50), resources.Removals);

        Assert.True(File.Exists(resources.Trx), "Undrained teardown removed the TRX file.");
        Assert.True(Directory.Exists(resources.Repository), "Undrained teardown removed the repository.");
        Assert.True(File.Exists(resources.RepositoryFile));
        Assert.Empty(resources.Removed);
        Assert.False(result.Drained);
        var failure = Record.Exception(result.AssertDrained);
        Assert.NotNull(failure);
        Assert.Contains("ActiveRootCount == 0", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ObservedDrain_RemovesTrxAndRepositoryAsToday()
    {
        using var resources = new TeardownResources();
        using var drained = new ManualResetEventSlim();
        drained.Set();

        var result = AcceptanceCohortWorkflowTestsBackgroundAndCapacity.RemoveAfterObservedDrain(
            () => drained.IsSet, "ActiveRootCount == 0", TimeSpan.FromSeconds(10), resources.Removals);

        Assert.False(File.Exists(resources.Trx));
        Assert.False(Directory.Exists(resources.Repository));
        Assert.Equal(new[] { "trx", "repo" }, resources.Removed);
        Assert.True(result.Drained);
        Assert.Null(Record.Exception(result.AssertDrained));
    }

    // These paths have no background producers, so this fixture can always release them.
    private sealed class TeardownResources : IDisposable
    {
        public string Repository { get; } = Path.Combine(Path.GetTempPath(), $"cohort-teardown-{Guid.NewGuid():N}");
        public string Trx => Path.Combine(Repository, "result.trx");
        public string RepositoryFile => Path.Combine(Repository, "candidate.txt");
        public List<string> Removed { get; } = [];
        public Action[] Removals =>
        [
            () => { File.Delete(Trx); Removed.Add("trx"); },
            () => { Directory.Delete(Repository, recursive: true); Removed.Add("repo"); }
        ];

        public TeardownResources()
        {
            Directory.CreateDirectory(Repository);
            File.WriteAllText(Trx, "owned TRX");
            File.WriteAllText(RepositoryFile, "owned candidate");
        }

        public void Dispose()
        {
            if (Directory.Exists(Repository))
                Directory.Delete(Repository, recursive: true);
        }
    }
}
