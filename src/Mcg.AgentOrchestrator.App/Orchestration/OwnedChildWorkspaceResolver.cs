using System.Text.Json;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Owned attempts execute in the target checkout but read the conductor's state store.
internal static class OwnedChildWorkspaceResolver
{
    internal static string? RecordedProjectName(OrchestratorWorkspace workspace) =>
        workspace.IsProjectScoped ? workspace.ProjectName : null;

    internal static OrchestratorWorkspace ForParallelAcceptanceAttempt(
        ConductorParallelAcceptanceAttempt attempt, OrchestratorProjectRegistry? registry = null)
    {
        var executionDirectory = !string.IsNullOrWhiteSpace(attempt.ExecutionDirectory)
            ? attempt.ExecutionDirectory!
            : OrchestratorWorkspace.ResolveRepoRoot(Environment.CurrentDirectory);
        var integrationBranch = attempt.IntegrationBranch ?? throw new InvalidOperationException(
            "acceptance attempt integration branch was not recorded");
        return Resolve(attempt.StateDirectory, attempt.ProjectName, executionDirectory,
            integrationBranch, registry);
    }

    internal static OrchestratorWorkspace ForGroupedGateAttempt(
        ConductorGroupedGateAttempt attempt, OrchestratorProjectRegistry? registry = null) =>
        Resolve(attempt.StateDirectory, attempt.ProjectName, attempt.ExecutionDirectory, null, registry);

    private static OrchestratorWorkspace Resolve(
        string? stateDirectory, string? projectName, string executionDirectory,
        string? integrationBranch, OrchestratorProjectRegistry? registry)
    {
        if (stateDirectory is null || projectName is null)
            registry ??= OrchestratorProjectRegistry.CreateDefault();
        if (stateDirectory is null && projectName is null)
        {
            var legacy = OrchestratorWorkspace.ForDirectory(executionDirectory, executionDirectory, null, registry!);
            return integrationBranch is null ? legacy : legacy with { IntegrationBranch = integrationBranch };
        }

        OrchestratorWorkspace? workspace = null;
        Exception? registryFailure = null;
        try
        {
            if (stateDirectory is not null &&
                (string.IsNullOrWhiteSpace(stateDirectory) || !Path.IsPathFullyQualified(stateDirectory)))
                throw new InvalidOperationException("Recorded state directory must be an absolute path.");
            if (projectName is not null)
            {
                // Invert the existing project layout only as a seed. ForProject must reproduce
                // the recorded directory before it can be used; it remains the path authority.
                var dataRoot = stateDirectory is null ? registry!.DataRootDirectory :
                    Directory.GetParent(Path.TrimEndingDirectorySeparator(stateDirectory))?.Parent?.FullName
                        ?? throw new InvalidOperationException("Recorded project state directory has no data root.");
                workspace = OrchestratorWorkspace.ForProject(projectName, executionDirectory, executionDirectory,
                    dataRootDirectory: dataRoot);
                var projectRegistry = stateDirectory is null ? registry! :
                    new OrchestratorProjectRegistry(
                        OrchestratorDataRoot.FromDirectory(dataRoot).ProjectsDirectory, dataRoot);
                try
                {
                    var project = projectRegistry.GetRequiredProject(projectName);
                    workspace = workspace with { IntegrationBranch = project.IntegrationBranch };
                }
                catch (Exception exception) when (stateDirectory is not null &&
                    !string.IsNullOrWhiteSpace(integrationBranch) &&
                    exception is ArgumentException or InvalidOperationException or IOException or
                        JsonException or UnauthorizedAccessException)
                {
                    registryFailure = exception;
                    workspace = workspace with { IntegrationBranch = integrationBranch };
                }
            }
            else
            {
                workspace = OrchestratorWorkspace.ForDirectory(executionDirectory, executionDirectory, null, registry!);
                if (integrationBranch is not null)
                    workspace = workspace with { IntegrationBranch = integrationBranch };
            }

            if (stateDirectory is not null &&
                !DefaultProjectStateLocation.SameRoot(workspace.OrchestratorDirectory, stateDirectory))
                throw new InvalidOperationException("Recorded state directory does not match the resolved workspace.");
            if (!File.Exists(workspace.SqliteStatePath))
                throw new InvalidOperationException("Recorded workspace state database does not exist.");
            return workspace;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or IOException ||
            (stateDirectory is not null && projectName is not null &&
                exception is JsonException or UnauthorizedAccessException))
        {
            throw new InvalidOperationException(
                $"Owned child workspace resolution failed: project='{projectName ?? "default"}', " +
                $"recorded state directory='{stateDirectory ?? "<absent>"}', " +
                $"resolved state database='{workspace?.SqliteStatePath ?? "<unresolved>"}'. {exception.Message}" +
                (registryFailure is null ? "" : $" Project registry lookup: {registryFailure.Message}"),
                exception);
        }
    }
}
