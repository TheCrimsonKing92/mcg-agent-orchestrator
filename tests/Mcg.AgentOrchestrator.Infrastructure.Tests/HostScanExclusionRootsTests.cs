using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: fixed inputs, no filesystem writes or environment mutation.
public sealed class HostScanExclusionRootsTests
{
    private static HostScanExclusionInputs FixedInputs() => new(
        Path.GetFullPath("/host-exclusion-tests/temp"),
        Path.GetFullPath("/host-exclusion-tests/user/AppData/Local"),
        Path.GetFullPath("/host-exclusion-tests/user"),
        Path.GetFullPath("/host-exclusion-tests/repo"));

    [Fact]
    public void Compute_FixedHostInputs_EqualsRootsFromCreatingMembers()
    {
        var inputs = FixedInputs();
        var expected = new[]
        {
            inputs.RepositoryRoot,
            OrchestratorTempRoot.GetRoot(inputs.TempPath),
            DotnetBuildEnvironmentManager.ResolveIsolatedRootBase(null,
                inputs.LocalApplicationData, null, inputs.TempPath, true),
            DotnetBuildEnvironmentManager.ResolveIsolatedRootBase(null, null, null, inputs.TempPath, true),
            TempRootJanitor.ResolveLowIntegrityOwnedRootParent(inputs.LocalApplicationData),
            Path.Combine(inputs.TempPath, GoalAcceptanceVerifier.HermeticProfileRootDirectoryName),
            Path.Combine(inputs.TempPath, GoalAcceptanceVerifier.FocusedEvidenceBaselinesRootDirectoryName),
            Path.Combine(inputs.TempPath, GoalAcceptanceVerifier.OwnerResultsRootDirectoryName),
            Path.Combine(inputs.TempPath, DotnetBuildEnvironmentManager.LandingTestsRootDirectoryName),
            GoalAcceptanceVerifier.DefaultNuGetGlobalPackagesFolder(inputs.UserProfile)
        }.Select(HostScanExclusionRoots.NormalizePath).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var actual = HostScanExclusionRoots.Compute(inputs);
        Assert.Equal(expected.Count, actual.Count);
        Assert.True(expected.SetEquals(actual), string.Join(Environment.NewLine, actual));
        Assert.Equal(actual, HostScanExclusionRoots.Compute(inputs));
    }

    [Fact]
    public void Compute_IsolatedOverride_IncludesOverrideAndBothDefaultLocations()
    {
        var inputs = FixedInputs() with { IsolatedRootOverride = Path.GetFullPath("/host-exclusion-tests/custom") };
        var roots = HostScanExclusionRoots.Compute(inputs);
        Assert.Contains(HostScanExclusionRoots.NormalizePath(
            DotnetBuildEnvironmentManager.ResolveIsolatedRootBase(inputs.IsolatedRootOverride,
                inputs.LocalApplicationData, null, inputs.TempPath, true)), roots);
        Assert.All(HostScanExclusionRoots.Compute(inputs with { IsolatedRootOverride = null }),
            root => Assert.Contains(root, roots));
    }

    [Fact]
    public void Compute_NuGetOverride_UsesConfiguredGlobalPackagesFolder()
    {
        var inputs = FixedInputs() with { NuGetPackagesOverride = Path.GetFullPath("/host-exclusion-tests/packages") };
        var roots = HostScanExclusionRoots.Compute(inputs);
        Assert.Contains(inputs.NuGetPackagesOverride, roots);
        Assert.DoesNotContain(GoalAcceptanceVerifier.DefaultNuGetGlobalPackagesFolder(inputs.UserProfile), roots);
    }

    [Fact]
    public void Compute_OverlappingRoots_DeduplicatesNormalizedPaths()
    {
        var inputs = FixedInputs();
        inputs = inputs with { IsolatedRootOverride = inputs.RepositoryRoot + Path.DirectorySeparatorChar };
        var roots = HostScanExclusionRoots.Compute(inputs);
        Assert.Equal(10, roots.Count);
        Assert.Equal(1, roots.Count(root => root == inputs.RepositoryRoot));
    }
}
