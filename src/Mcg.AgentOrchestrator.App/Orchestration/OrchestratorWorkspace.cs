namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record OrchestratorWorkspace(
    string RootDirectory,
    string ExecutionDirectory,
    string OrchestratorDirectory,
    string StatePath,
    string AgentCatalogPath,
    string WorkerProfilePath,
    string PromptDirectory,
    string LogDirectory,
    string TranscriptPath)
{
    public static OrchestratorWorkspace ForDirectory(string rootDirectory, string? executionDirectory = null)
    {
        var root = Path.GetFullPath(rootDirectory);
        var executionRoot = Path.GetFullPath(string.IsNullOrWhiteSpace(executionDirectory) ? rootDirectory : executionDirectory);
        var orchestrator = Path.Combine(root, ".orchestrator");
        return new OrchestratorWorkspace(
            root,
            executionRoot,
            orchestrator,
            Path.Combine(orchestrator, "state.json"),
            Path.Combine(orchestrator, "agents.json"),
            Path.Combine(orchestrator, "workers.json"),
            Path.Combine(orchestrator, "prompts"),
            Path.Combine(orchestrator, "logs"),
            Path.Combine(orchestrator, "transcript.md"));
    }
}
