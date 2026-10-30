using static Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier;

namespace Mcg.AgentOrchestrator.Infrastructure;

/// <summary>Validates the manifest check contracts and infrastructure lanes required at startup.</summary>
internal static class AcceptanceStartupContractValidator
{
    internal static StartupContract Validate(
        string repositoryRoot,
        string? projectHomeDirectory,
        Func<AcceptanceManifestCheck, string[]> buildCommandArguments)
    {
        var manifest = AcceptanceManifest.Load(repositoryRoot, changedFiles: null, projectHomeDirectory);
        if (manifest.Checks.Count == 0)
        {
            throw new InvalidOperationException("Acceptance manifest loaded without any checks.");
        }
        foreach (var check in manifest.Checks)
        {
            var type = check.Type.Trim().ToLowerInvariant();
            if (type == "command")
            {
                _ = buildCommandArguments(check);
            }
            else if (type == "dotnet-test")
            {
                _ = AcceptanceCheckCommandBuilder.BuildDotnetTestArguments(check);
            }
            else if (type == "junit-command")
            {
                JunitCommandCheckDefinition.Validate(check);
            }
            else if (type is not ("crosswalk-test" or "no-op" or "grep-absent" or "grep-present" or "file-exists" or "command-exit"))
            {
                throw new InvalidOperationException(
                    $"Acceptance manifest check '{check.Name}' has unsupported type '{check.Type}'.");
            }
        }

        var laneNames = AcceptanceGateEngineSettings.Load(repositoryRoot, projectHomeDirectory)
            .InfrastructureTestLanes
            .Select(lane => lane.Name)
            .ToArray();
        if (laneNames.Length == 0 || laneNames.Any(string.IsNullOrWhiteSpace))
        {
            throw new InvalidOperationException("Acceptance infrastructure lanes could not be enumerated.");
        }

        return new StartupContract(manifest.Checks.Count, laneNames);
    }
}
