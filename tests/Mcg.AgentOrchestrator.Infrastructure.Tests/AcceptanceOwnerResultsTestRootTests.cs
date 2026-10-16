using System.Collections.Concurrent;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class AcceptanceOwnerResultsTestRootTests : GoalAcceptanceVerifierTestBase
{
    private const string Manifest = """
        {
          "version": 1,
          "checks": [
            { "name": "phase seam", "type": "command", "command": "git", "arguments": ["diff", "--check"] }
          ],
          "forbiddenChangedPathGlobs": []
        }
        """;

    [Fact]
    public async Task ConcurrentRootsAreDistinctAndCleanedUp()
    {
        var roots = await Task.WhenAll(
            Task.Run(AcceptanceOwnerResultsTestRoot.Create),
            Task.Run(AcceptanceOwnerResultsTestRoot.Create));
        try
        {
            Assert.NotEqual(roots[0].Root, roots[1].Root);
            Assert.NotEqual(roots[0].ResultsPrefix, roots[1].ResultsPrefix);
            foreach (var root in roots)
            {
                Assert.Equal(root.Root, Path.GetDirectoryName(root.ResultsPrefix));
                Assert.True(Directory.Exists(root.Root));
            }
        }
        finally
        {
            foreach (var root in roots)
            {
                root.Dispose();
            }
        }

        foreach (var root in roots)
        {
            Assert.False(Directory.Exists(root.Root));
        }
    }

    [Fact]
    public async Task ConcurrentNullGoalGateRunsPassWithSeparateRoots()
    {
        var firstWorkspace = CreateManifestWorkspace(Manifest);
        var secondWorkspace = CreateManifestWorkspace(Manifest);
        using var firstRoot = AcceptanceOwnerResultsTestRoot.Create();
        using var secondRoot = AcceptanceOwnerResultsTestRoot.Create();
        var firstProgress = new ConcurrentQueue<AcceptanceGateProgress>();
        var secondProgress = new ConcurrentQueue<AcceptanceGateProgress>();
        var bothRunnersEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runnerCount = 0;

        async Task<GoalAcceptanceVerifier.CommandResult> RunCommandAsync()
        {
            if (Interlocked.Increment(ref runnerCount) == 2)
            {
                bothRunnersEntered.SetResult();
            }

            try
            {
                // Hang guard only; the event, rather than elapsed time, releases both runs.
                await bothRunnersEntered.Task.WaitAsync(TimeSpan.FromMinutes(1));
            }
            catch (TimeoutException ex)
            {
                throw new TimeoutException("Both gate command runners did not enter the concurrency barrier.", ex);
            }

            return new GoalAcceptanceVerifier.CommandResult(0, string.Empty);
        }

        try
        {
            var firstVerifier = new GoalAcceptanceVerifier((_, _, _) => RunCommandAsync());
            var secondVerifier = new GoalAcceptanceVerifier((_, _, _) => RunCommandAsync());
            var results = await Task.WhenAll(
                firstVerifier.RunOwnedAsync(
                    firstWorkspace, goalId: null, changedFiles: null, stableSlotIndex: null,
                    stableSlotLease: null, CancellationToken.None, firstRoot.CreateOptions(firstProgress.Enqueue)),
                secondVerifier.RunOwnedAsync(
                    secondWorkspace, goalId: null, changedFiles: null, stableSlotIndex: null,
                    stableSlotLease: null, CancellationToken.None, secondRoot.CreateOptions(secondProgress.Enqueue)));

            Assert.All(results, result => Assert.True(result.Passed));
            Assert.NotEqual(firstRoot.ResultsPrefix, secondRoot.ResultsPrefix);
            Assert.Single(firstProgress, item => item.Phase == "gate-phase-breakdown");
            Assert.Single(secondProgress, item => item.Phase == "gate-phase-breakdown");
        }
        finally
        {
            DeleteDirectoryWithRetry(firstWorkspace);
            DeleteDirectoryWithRetry(secondWorkspace);
        }
    }

    [Fact]
    public void ManifestWorkspacesWithoutGitShareTheFallbackOwnerResultsRoot()
    {
        var firstWorkspace = CreateManifestWorkspace(Manifest);
        var secondWorkspace = CreateManifestWorkspace(Manifest);
        try
        {
            Assert.NotEqual(firstWorkspace, secondWorkspace);
            Assert.False(Directory.Exists(Path.Combine(firstWorkspace, ".git")));
            Assert.False(Directory.Exists(Path.Combine(secondWorkspace, ".git")));
            var firstRoot = GoalAcceptanceVerifier.ResolveOwnerResultsRootForCandidate(firstWorkspace);
            var secondRoot = GoalAcceptanceVerifier.ResolveOwnerResultsRootForCandidate(secondWorkspace);

            Assert.Equal(firstRoot, secondRoot);
            Assert.Equal(Path.Combine(Path.GetTempPath(), GoalAcceptanceVerifier.OwnerResultsRootDirectoryName), firstRoot);
        }
        finally
        {
            DeleteDirectoryWithRetry(firstWorkspace);
            DeleteDirectoryWithRetry(secondWorkspace);
        }
    }
}
