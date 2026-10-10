namespace Mcg.AgentOrchestrator.Infrastructure;

/// <summary>The target repository has no acceptance manifest at any supported location.</summary>
public sealed class MissingAcceptanceManifestException : InvalidOperationException
{
    public IReadOnlyList<string> SearchedLocations { get; }

    public MissingAcceptanceManifestException(IReadOnlyList<string> searchedLocations)
        : base("Acceptance manifest is missing. Searched locations:" + Environment.NewLine +
            string.Join(Environment.NewLine, searchedLocations) + Environment.NewLine +
            "Place acceptance-manifest.json in the per-user project folder " +
            @"(%LOCALAPPDATA%\Mcg.AgentOrchestrator\projects\<project>\) or run project onboarding.")
    {
        SearchedLocations = Array.AsReadOnly(searchedLocations.ToArray());
    }
}
