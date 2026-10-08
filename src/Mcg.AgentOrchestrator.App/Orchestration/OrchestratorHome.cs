using System.Security.Cryptography;
using System.Text;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// The orchestrator's source/install root, independent of the selected target project.
internal sealed class OrchestratorHome
{
    internal const string EnvironmentVariable = "MCG_ORCHESTRATOR_HOME";

    private OrchestratorHome(string rootDirectory) => RootDirectory = Path.GetFullPath(rootDirectory);

    internal string RootDirectory { get; }

    internal static OrchestratorHome Resolve(
        string defaultWorkspaceRoot, Func<string, string?>? readEnvironment = null)
    {
        var configured = (readEnvironment ?? Environment.GetEnvironmentVariable)(EnvironmentVariable)?.Trim();
        return new OrchestratorHome(string.IsNullOrEmpty(configured) ? defaultWorkspaceRoot : configured);
    }

    internal static OrchestratorHome ResolveForProcess() =>
        Resolve(OrchestratorWorkspace.ResolveRepoRoot(Environment.CurrentDirectory));

    internal static void ExportForDescendants(
        string defaultWorkspaceRoot,
        Func<string, string?>? readEnvironment = null,
        Action<string, string?>? writeEnvironment = null)
    {
        // A supervisor child starts in the selected project's directory. Preserve the
        // unprojected launch root rather than rediscovering home from that child's CWD.
        // Normalize configured relative paths too, before descendants change directory.
        var home = Resolve(defaultWorkspaceRoot, readEnvironment);
        (writeEnvironment ?? Environment.SetEnvironmentVariable)(EnvironmentVariable, home.RootDirectory);
    }

    internal bool IsHome(OrchestratorWorkspace workspace) => IsHome(workspace.ExecutionDirectory);

    internal bool IsHome(string directory) => string.Equals(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)),
        Path.TrimEndingDirectorySeparator(RootDirectory), StringComparison.OrdinalIgnoreCase);

    internal ConductorSuccessorStagingOptions? CreateSuccessorStagingOptions(OrchestratorWorkspace workspace)
    {
        if (!IsHome(workspace))
            return null;

        // Keep the workspace's existing spelling for the content-addressed build key.
        // It denotes home here; changing casing would change the self-hosted cache key.
        var repositoryRoot = workspace.ExecutionDirectory;
        var repositoryBuildKey = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(repositoryRoot))))[..16];
        var appOutputDirectory = Path.Combine(
            OrchestratorTempRoot.GetPurposeDirectory("self-relaunch-build"), repositoryBuildKey);
        var dotnetPath = Environment.GetEnvironmentVariable("MCG_ORCHESTRATOR_DOTNET_PATH");
        return new ConductorSuccessorStagingOptions(
            RepositoryRoot: repositoryRoot,
            AppProjectPath: Path.Combine(repositoryRoot, "src", "Mcg.AgentOrchestrator.App", "Mcg.AgentOrchestrator.App.csproj"),
            AppDllPath: Path.Combine(appOutputDirectory, "Mcg.AgentOrchestrator.App.dll"),
            UpdateHeadMarkerScriptPath: Path.Combine(repositoryRoot, "scripts", "Update-AppDllGitHeadMarker.ps1"),
            ResolveRunDirectoryScriptPath: Path.Combine(repositoryRoot, "scripts", "resolve-run-dir.ps1"),
            StateStorePath: workspace.SqliteStatePath,
            AgentCatalogPath: workspace.AgentCatalogPath,
            WorkerProfilePath: workspace.WorkerProfilePath,
            ModelFunctionCatalogPath: workspace.ModelFunctionCatalogPath,
            DotnetPath: dotnetPath ?? "dotnet",
            PowerShellPath: "powershell")
        {
            LandingAppBuildStore = LandingAppBuildStore.ForRepository(repositoryRoot, dotnetPath)
        };
    }
}
