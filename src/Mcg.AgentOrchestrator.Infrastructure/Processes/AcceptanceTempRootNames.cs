namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class AcceptanceTempRootNames
{
    internal const string HermeticProfileRootDirectoryName = "mcg-hvp";
    internal const string FocusedEvidenceBaselinesRootDirectoryName = "mcg-focused-evidence-baselines";
    internal const string OwnerResultsRootDirectoryName = "mcg-acceptance-owner-results";
    internal const string NuGetPackagesVariable = "NUGET_PACKAGES";

    internal static string DefaultNuGetGlobalPackagesFolder(string userProfile) =>
        Path.Combine(userProfile, ".nuget", "packages");
}
