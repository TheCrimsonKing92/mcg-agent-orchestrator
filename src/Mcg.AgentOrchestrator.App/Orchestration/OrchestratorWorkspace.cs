using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record OrchestratorWorkspace(
    string RootDirectory,
    string ExecutionDirectory,
    string OrchestratorDirectory,
    string TenantName,
    bool IsTenantScoped,
    string StatePath,
    string SqliteStatePath,
    string AgentCatalogPath,
    string WorkerProfilePath,
    string PromptDirectory,
    string LogDirectory,
    string ContinuationStorePath,
    string TranscriptPath)
{
    public const string DefaultTenantName = "default";
    public const string ContinuationStoreFileName = "continuation-watches.json";

    public static OrchestratorWorkspace ForDirectory(
        string rootDirectory,
        string? executionDirectory = null,
        string? tenantName = null)
    {
        var root = Path.GetFullPath(rootDirectory);
        var executionRoot = Path.GetFullPath(string.IsNullOrWhiteSpace(executionDirectory) ? rootDirectory : executionDirectory);
        var normalizedTenant = OrchestratorTenantSelection.NormalizeTenantName(tenantName);
        var isTenantScoped = !normalizedTenant.Equals(DefaultTenantName, StringComparison.OrdinalIgnoreCase);
        var orchestrator = isTenantScoped
            ? Path.Combine(root, ".orchestrator", "tenants", normalizedTenant)
            : Path.Combine(root, ".orchestrator");
        return new OrchestratorWorkspace(
            root,
            executionRoot,
            orchestrator,
            normalizedTenant,
            isTenantScoped,
            Path.Combine(orchestrator, "state.json"),
            Path.Combine(orchestrator, "state.db"),
            Path.Combine(orchestrator, "agents.json"),
            Path.Combine(orchestrator, "workers.json"),
            Path.Combine(orchestrator, "prompts"),
            Path.Combine(orchestrator, "logs"),
            isTenantScoped
                ? Path.Combine(orchestrator, ContinuationStoreFileName)
                : Path.Combine(root, ContinuationStoreFileName),
            Path.Combine(orchestrator, "transcript.md"));
    }

    public string BacklogStorePath => Path.Combine(OrchestratorDirectory, "backlog.db");

    public string DashboardUrlFilePath => Path.Combine(OrchestratorDirectory, ".dashboard-url");

    // Append-only advisory log of semantic-acceptance verdicts (one JSON object per line), kept in
    // the orchestrator state directory (NOT a goal worktree) so writing a receipt never dirties an
    // acceptance diff. Source for the local-vs-subscription judge agreement comparison.
    public string SemanticAcceptanceLogPath => Path.Combine(OrchestratorDirectory, "semantic-acceptance.jsonl");

    // Registry of orchestrator-internal model functions (acceptance-judge lanes, future samplers,
    // etc.) — separate from the worker agent catalog so internal model uses never touch task routing.
    public string ModelFunctionCatalogPath => Path.Combine(OrchestratorDirectory, "model-functions.json");

    // Goal work runs in the goal's worktree when one exists so concurrent
    // goals do not contend for the shared execution directory.
    public string ResolveExecutionDirectory(GoalId goalId)
    {
        return GoalWorktrees.TryResolve(ExecutionDirectory, goalId) ?? ExecutionDirectory;
    }
}
