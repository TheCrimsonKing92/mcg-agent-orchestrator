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
            Environment.GetEnvironmentVariable(DotnetBuildEnvironmentManager.IsolatedRootOverrideVariable),
            Environment.GetEnvironmentVariable(GoalAcceptanceVerifier.NuGetPackagesVariable));
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
            DotnetBuildEnvironmentManager.ResolveIsolatedRootBase(null,
                inputs.LocalApplicationData, null, inputs.TempPath, isWindows: true),
            DotnetBuildEnvironmentManager.ResolveIsolatedRootBase(null,
                null, null, inputs.TempPath, isWindows: true),
            Path.Combine(inputs.TempPath, GoalAcceptanceVerifier.HermeticProfileRootDirectoryName),
            Path.Combine(inputs.TempPath, GoalAcceptanceVerifier.FocusedEvidenceBaselinesRootDirectoryName),
            Path.Combine(inputs.TempPath, GoalAcceptanceVerifier.OwnerResultsRootDirectoryName),
            Path.Combine(inputs.TempPath, DotnetBuildEnvironmentManager.LandingTestsRootDirectoryName),
            string.IsNullOrWhiteSpace(inputs.NuGetPackagesOverride)
                ? GoalAcceptanceVerifier.DefaultNuGetGlobalPackagesFolder(inputs.UserProfile)
                : inputs.NuGetPackagesOverride
        };
        if (!string.IsNullOrWhiteSpace(inputs.LocalApplicationData))
            roots.Add(TempRootJanitor.ResolveLowIntegrityOwnedRootParent(inputs.LocalApplicationData));
        if (!string.IsNullOrWhiteSpace(inputs.IsolatedRootOverride))
            roots.Add(DotnetBuildEnvironmentManager.ResolveIsolatedRootBase(inputs.IsolatedRootOverride,
                inputs.LocalApplicationData, null, inputs.TempPath, isWindows: true));
        return roots.Select(NormalizePath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static string NormalizePath(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
}
