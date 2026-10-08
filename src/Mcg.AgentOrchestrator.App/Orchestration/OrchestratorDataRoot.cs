namespace Mcg.AgentOrchestrator.App.Orchestration;

// Per-user project data, independent of both the install root and target repositories.
internal sealed class OrchestratorDataRoot
{
    internal const string EnvironmentVariable = "MCG_ORCHESTRATOR_DATA";
    private OrchestratorDataRoot(string directory) => RootDirectory = Path.GetFullPath(directory);
    internal string RootDirectory { get; }

    internal static OrchestratorDataRoot Resolve(Func<string, string?>? readEnvironment = null)
    {
        var configured = (readEnvironment ?? Environment.GetEnvironmentVariable)(EnvironmentVariable)?.Trim();
        return FromDirectory(string.IsNullOrEmpty(configured) ? ResolveDefaultDirectory() : configured);
    }

    internal static OrchestratorDataRoot FromDirectory(string directory) => new(directory);

    internal static string ResolveDefaultDirectory()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return string.IsNullOrWhiteSpace(localAppData)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".mcg-agent-orchestrator")
            : Path.Combine(localAppData, "Mcg.AgentOrchestrator");
    }

    internal static void ExportForDescendants(
        Func<string, string?>? readEnvironment = null,
        Action<string, string?>? writeEnvironment = null)
    {
        var root = Resolve(readEnvironment);
        (writeEnvironment ?? Environment.SetEnvironmentVariable)(EnvironmentVariable, root.RootDirectory);
    }

    internal string ProjectsDirectory => Path.Combine(RootDirectory, "projects");

    internal string ProjectDirectory(string projectName) => Path.Combine(
        ProjectsDirectory, OrchestratorProjectSelection.NormalizeProjectName(projectName));
}
