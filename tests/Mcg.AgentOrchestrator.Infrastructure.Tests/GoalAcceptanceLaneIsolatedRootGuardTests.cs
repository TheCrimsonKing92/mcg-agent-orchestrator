using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.CodeAnalysis.CSharp;

[Xunit.Collection("IsolatedProcessSpawning")]
public sealed class GoalAcceptanceLaneIsolatedRootGuardTests : GoalAcceptanceVerifierDotnetBuildSlotTests
{
    // Exceptions must name a lane class and explain its independent root ownership.
    private static readonly IReadOnlyDictionary<Type, string> CollectionExceptions = new Dictionary<Type, string>();
    private static readonly IReadOnlyDictionary<(Type Class, string Method), string> ChildClearExceptions =
        new Dictionary<(Type, string), string>();

    [Xunit.Theory]
    [Xunit.InlineData("Goal acceptance verifier")]
    [Xunit.InlineData("Goal acceptance build slots")]
    public void Lane_members_keep_isolated_roots_or_live_named_exceptions(string laneName)
    {
        var collections = LaneIsolatedRootScanner.IsolatedCollections(GetType().Assembly);
        Assert.Contains(TestCollections.GoalAcceptanceVerifier, collections);
        Assert.Contains(TestCollections.JobAccounting, collections);
        LaneIsolatedRootScanner.VerifyLane(VerifiedRepositoryRoot.Find(), laneName, GetType().Assembly,
            LaneIsolatedRootScanner.IsIsolatedCollection, CollectionExceptions, ChildClearExceptions,
            "a collection with IsolatedDotnetRootFixture");
    }

    [Xunit.Fact]
    public void Collection_guard_rejects_unisolated_samples_and_stale_exceptions()
    {
        var violation = Record.Exception(() => LaneIsolatedRootScanner.VerifyCollections(
            [typeof(UnisolatedSample)], LaneIsolatedRootScanner.IsIsolatedCollection,
            CollectionExceptions, "a collection with IsolatedDotnetRootFixture"));
        Assert.NotNull(violation);
        Assert.Contains("has no named isolation exception", violation.Message);

        var stale = Record.Exception(() => LaneIsolatedRootScanner.VerifyCollections(
            [typeof(UnisolatedSample)], LaneIsolatedRootScanner.IsIsolatedCollection,
            new Dictionary<Type, string> { [typeof(StaleSample)] = "Synthetic missing lane member." }, "isolated"));
        Assert.NotNull(stale);
        Assert.Contains("Stale collection exception", stale.Message);
    }

    [Xunit.Theory]
    [Xunit.InlineData(false, 1)]
    [Xunit.InlineData(true, 0)]
    public void Clear_guard_rejects_unrestored_synthetic_sample(bool restore, int expectedViolations)
    {
        // These private, fact-free samples are never enumerated as live lane members.
        var body = restore
            ? "var prior = Environment.GetEnvironmentVariable(\"MCG_DOTNET_ISOLATED_ROOT\"); " +
              "try { Environment.SetEnvironmentVariable(\"MCG_DOTNET_ISOLATED_ROOT\", null); } " +
              "finally { Environment.SetEnvironmentVariable(\"MCG_DOTNET_ISOLATED_ROOT\", prior); }"
            : "Environment.SetEnvironmentVariable(\"MCG_DOTNET_ISOLATED_ROOT\", null);";
        var source = CSharpSyntaxTree.ParseText("class ClearingSample { void Clear() { " + body + " } }").GetRoot();
        Assert.Equal(expectedViolations, LaneIsolatedRootScanner.FindUnsafeClears(source).Count());
    }

    [Xunit.Theory]
    [Xunit.InlineData("two-lane", false)]
    [Xunit.InlineData("two-lane", true)]
    [Xunit.InlineData("manifest", false)]
    [Xunit.InlineData("manifest", true)]
    [Xunit.InlineData("tracked-shape", false)]
    [Xunit.InlineData("tracked-shape", true)]
    public void Build_slot_scenario_resolves_its_temporary_host_root(string construction, bool nestedWorktree)
    {
        var scenarioRoot = construction switch
        {
            "two-lane" => CreateTwoLaneShardManifestWorkspace(2),
            "manifest" => CreateManifestWorkspace("""{ "version": 1, "checks": [] }"""),
            "tracked-shape" => CreateTrackedManifestShapeWorkspace(),
            _ => throw new ArgumentOutOfRangeException(nameof(construction))
        };
        try
        {
            var worktree = nestedWorktree
                ? Path.Combine(scenarioRoot, GoalWorktreeLayout.DirectoryName, "isolation")
                : scenarioRoot;
            Directory.CreateDirectory(worktree);
            var hostRoot = AcceptancePartitionVerdictCache.ResolveHostStateRoot(worktree);
            var repositoryRoot = VerifiedRepositoryRoot.Find();
            // Compare resolved paths only; never open the live state directory.
            var liveHostRoot = AcceptancePartitionVerdictCache.ResolveHostStateRoot(repositoryRoot);
            Assert.Equal(Normalize(scenarioRoot), Normalize(hostRoot), PathComparer);
            Assert.True(IsStrictlyBeneath(hostRoot, Path.Combine(Path.GetTempPath(), "mcg-acceptance-tests")),
                $"Scenario host '{hostRoot}' escaped its temporary parent for '{worktree}'.");
            Assert.False(IsSameOrBeneath(repositoryRoot, hostRoot),
                $"Scenario host '{hostRoot}' owns repository root '{repositoryRoot}'.");
            Assert.False(IsSameOrBeneath(liveHostRoot, hostRoot),
                $"Scenario host '{hostRoot}' owns live host root '{liveHostRoot}'.");
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

    private sealed class UnisolatedSample;
    private sealed class StaleSample;
}
