using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(TestCollections.DotnetBuildSlots)]
public sealed class ScenarioHostRootIsolationTests : GoalAcceptanceVerifierDotnetBuildSlotTests
{
    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public void Scenario_worktree_resolves_to_its_own_temporary_host(bool nestedWorktree)
    {
        // The private Scenario types use this same inherited construction path.
        var scenarioRoot = CreateTwoLaneShardManifestWorkspace(2);
        try
        {
            var worktree = nestedWorktree
                ? Path.Combine(scenarioRoot, GoalWorktreeLayout.DirectoryName, "isolation")
                : scenarioRoot;
            Directory.CreateDirectory(worktree);
            var hostRoot = AcceptancePartitionVerdictCache.ResolveHostStateRoot(worktree);
            var repositoryRoot = VerifiedRepositoryRoot.Find();
            var liveHostRoot = AcceptancePartitionVerdictCache.ResolveHostStateRoot(repositoryRoot);
            var liveStateDirectory = Path.GetDirectoryName(
                RemoteLaneExecutorConfiguration.ResolveStorePath(repositoryRoot))!;

            Assert.Equal(Normalize(scenarioRoot), Normalize(hostRoot), PathComparer);
            Assert.True(IsStrictlyBeneath(hostRoot, Path.Combine(Path.GetTempPath(), "mcg-acceptance-tests")),
                $"Scenario host '{hostRoot}' escaped its temporary parent for '{worktree}'.");
            Assert.False(IsSameOrBeneath(repositoryRoot, hostRoot),
                $"Scenario host '{hostRoot}' is the repository root or an ancestor of '{repositoryRoot}'.");
            Assert.False(IsSameOrBeneath(liveHostRoot, hostRoot),
                $"Scenario host '{hostRoot}' is the live host root or an ancestor of '{liveHostRoot}'.");
            Assert.False(IsSameOrBeneath(liveStateDirectory, hostRoot),
                $"Scenario host '{hostRoot}' owns the live state directory '{liveStateDirectory}'.");
            Assert.True(IsStrictlyBeneath(RemoteLaneExecutorConfiguration.ResolveStorePath(worktree), scenarioRoot));
            Assert.True(IsStrictlyBeneath(RemoteExecutorHealthLedger.ResolveStorePath(worktree), scenarioRoot));
        }
        finally
        {
            DeleteDirectoryWithRetry(scenarioRoot);
        }
    }

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static string Normalize(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static bool IsSameOrBeneath(string path, string root) =>
        PathComparer.Equals(Normalize(path), Normalize(root)) || IsStrictlyBeneath(path, root);

    private static bool IsStrictlyBeneath(string path, string root) =>
        Normalize(path).StartsWith(Normalize(root) + Path.DirectorySeparatorChar,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
