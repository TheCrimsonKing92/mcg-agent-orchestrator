namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed partial class GoalAcceptanceVerifier
{
    internal const string HermeticProfileRootDirectoryName = AcceptanceTempRootNames.HermeticProfileRootDirectoryName;
    internal const string FocusedEvidenceBaselinesRootDirectoryName = AcceptanceTempRootNames.FocusedEvidenceBaselinesRootDirectoryName;
    internal const string OwnerResultsRootDirectoryName = AcceptanceTempRootNames.OwnerResultsRootDirectoryName;
    internal const string NuGetPackagesVariable = AcceptanceTempRootNames.NuGetPackagesVariable;

    internal static string DefaultNuGetGlobalPackagesFolder(string userProfile) => AcceptanceTempRootNames.DefaultNuGetGlobalPackagesFolder(userProfile);
}
