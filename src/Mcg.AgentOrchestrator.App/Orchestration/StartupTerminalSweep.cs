using System.Diagnostics;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class StartupTerminalSweep
{
    public static TerminalGoalSweepResult Run(
        AgentOrchestratorKernel sweepKernel,
        OrchestratorWorkspace workspace,
        GoalId? watchGoalId,
        GoalWorktreeCleanupHooks cleanupHooks,
        Func<string, IReadOnlyList<string>, GitCli.GitResult>? gitRunner = null,
        Action<string>? writeLine = null)
    {
        var timing = Stopwatch.StartNew();
        var result = TerminalGoalSweep.Run(
            sweepKernel, workspace.ExecutionDirectory, workspace.IntegrationBranch, watchGoalId,
            cleanupHooks: cleanupHooks, orchestratorDirectory: workspace.OrchestratorDirectory,
            reclaimGoalRoots: true, gitRunner: gitRunner);
        timing.Stop();
        try
        {
            var line = StartupSweepStepsLine.Format(result, timing.ElapsedMilliseconds);
            if (writeLine is not null)
                writeLine(line);
            else
            {
                Console.WriteLine(line);
                Console.Out.Flush();
            }
        }
        catch (Exception)
        {
            // Diagnostic output must not prevent startup from persisting the sweep result.
        }

        return result;
    }
}
