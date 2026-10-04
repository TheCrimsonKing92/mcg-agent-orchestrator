using System.Diagnostics;
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
            var start = new ProcessStartInfo("git")
            {
                WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            start.ArgumentList.Add("rev-parse");
            start.ArgumentList.Add("HEAD");
            using var process = Process.Start(start);
            if (process is null) return null;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(5000)) { process.Kill(entireProcessTree: true); return null; }
            var sha = stdout.GetAwaiter().GetResult().Trim();
            _ = stderr.GetAwaiter().GetResult();
            return process.ExitCode == 0 && sha.Length == 40 && sha.All(Uri.IsHexDigit) ? sha : null;
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        { return null; }
    }
}
