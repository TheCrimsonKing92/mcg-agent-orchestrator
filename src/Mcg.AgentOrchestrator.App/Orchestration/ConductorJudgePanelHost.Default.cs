using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorJudgePanelHost
{
    internal static ConductorJudgePanelHost CreateDefault(OrchestratorWorkspace workspace)
    {
        ModelFunctionCatalog catalog;
        try { catalog = ModelFunctionCatalogStore.Load(workspace.ModelFunctionCatalogPath); }
        // A deserialized null Bindings list fails in the shared loader before resolution.
        catch (NullReferenceException) { catalog = new(null!); }
        return new(new ConductorJudgePanelCaseStore(Path.Combine(workspace.OrchestratorDirectory, "judge-panel.db")),
            new CodexSolPanelJudgeRunner(catalog), new ClaudeSonnetPanelJudgeRunner(catalog),
            goal => ResolveCandidate(workspace, goal), new ConductEventLogWriter(workspace.ConductEventsLogPath));
    }

    private static string? ResolveCandidate(OrchestratorWorkspace workspace, Goal goal)
    {
        var directory = GoalWorktrees.TryResolve(workspace.ExecutionDirectory, goal.Id);
        if (directory is null) return null;
        try
        {
            var result = GitCli.Run(directory, "rev-parse", "HEAD");
            var sha = result.Output.Trim();
            return result.Succeeded && sha.Length == 40 && sha.All(Uri.IsHexDigit) ? sha : null;
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        { return null; }
    }
}
