namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed record HostScanExclusionInputs(
    string TempPath, string LocalApplicationData, string UserProfile, string RepositoryRoot,
    string? IsolatedRootOverride = null, string? NuGetPackagesOverride = null)
{
    public static HostScanExclusionInputs CaptureCurrent(string repositoryRoot)
    {
        var tempPath = Path.GetTempPath();
        var localAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        return new(tempPath,
            string.IsNullOrWhiteSpace(localAppData)
                ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) : localAppData,
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), repositoryRoot,
            Environment.GetEnvironmentVariable(DotnetBuildStorageLayout.IsolatedRootOverrideVariable),
            Environment.GetEnvironmentVariable(AcceptanceTempRootNames.NuGetPackagesVariable));
    }
}

/// <summary>Lists owned roots without creating directories or changing host preferences.</summary>
public static class HostScanExclusionRoots
{
    public static IReadOnlyList<string> Compute(HostScanExclusionInputs inputs)
    {
        var roots = new List<string>
        {
            inputs.RepositoryRoot,
            OrchestratorTempRoot.GetRoot(inputs.TempPath),
            DotnetBuildStorageLayout.ResolveIsolatedRootBase(null,
                inputs.LocalApplicationData, null, inputs.TempPath, isWindows: true),
            DotnetBuildStorageLayout.ResolveIsolatedRootBase(null,
                null, null, inputs.TempPath, isWindows: true),
            Path.Combine(inputs.TempPath, AcceptanceTempRootNames.HermeticProfileRootDirectoryName),
            Path.Combine(inputs.TempPath, AcceptanceTempRootNames.FocusedEvidenceBaselinesRootDirectoryName),
            Path.Combine(inputs.TempPath, AcceptanceTempRootNames.OwnerResultsRootDirectoryName),
            Path.Combine(inputs.TempPath, DotnetBuildStorageLayout.LandingTestsRootDirectoryName),
            string.IsNullOrWhiteSpace(inputs.NuGetPackagesOverride)
                ? AcceptanceTempRootNames.DefaultNuGetGlobalPackagesFolder(inputs.UserProfile)
                : inputs.NuGetPackagesOverride
        };
        if (!string.IsNullOrWhiteSpace(inputs.LocalApplicationData))
            roots.Add(TempRootJanitor.ResolveLowIntegrityOwnedRootParent(inputs.LocalApplicationData));
        if (!string.IsNullOrWhiteSpace(inputs.IsolatedRootOverride))
            roots.Add(DotnetBuildStorageLayout.ResolveIsolatedRootBase(inputs.IsolatedRootOverride,
                inputs.LocalApplicationData, null, inputs.TempPath, isWindows: true));
        return roots.Select(NormalizePath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static string NormalizePath(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
}
