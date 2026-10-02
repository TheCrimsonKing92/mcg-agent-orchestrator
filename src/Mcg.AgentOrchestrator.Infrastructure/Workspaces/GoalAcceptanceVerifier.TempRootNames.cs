namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed partial class GoalAcceptanceVerifier
{
    internal const string HermeticProfileRootDirectoryName = "mcg-hvp";
    internal const string FocusedEvidenceBaselinesRootDirectoryName = "mcg-focused-evidence-baselines";
    internal const string OwnerResultsRootDirectoryName = "mcg-acceptance-owner-results";
    internal const string NuGetPackagesVariable = "NUGET_PACKAGES";

    internal static string DefaultNuGetGlobalPackagesFolder(string userProfile) =>
        Path.Combine(userProfile, ".nuget", "packages");
}
