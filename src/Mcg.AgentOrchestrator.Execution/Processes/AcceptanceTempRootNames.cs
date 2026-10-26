namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class AcceptanceTempRootNames
{
    internal const string HermeticProfileRootDirectoryName = "mcg-hvp";
    internal const string FocusedEvidenceBaselinesRootDirectoryName = "mcg-focused-evidence-baselines";
    internal const string OwnerResultsRootDirectoryName = "mcg-acceptance-owner-results";
    internal const string NuGetPackagesVariable = "NUGET_PACKAGES";

    internal static string DefaultNuGetGlobalPackagesFolder(string userProfile) =>
        Path.Combine(userProfile, ".nuget", "packages");

    // Worker builds and hermetic verification builds must pass the identical string, or NuGet redoes restore in the shared build root.
    internal static string ResolveNuGetPackagesFolder(string? inheritedValue)
    {
        if (!string.IsNullOrWhiteSpace(inheritedValue)) return inheritedValue;
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return DefaultNuGetGlobalPackagesFolder(string.IsNullOrWhiteSpace(profile)
            ? Path.Combine(Path.GetTempPath(), HermeticProfileRootDirectoryName) : profile);
    }
}
