using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Dashboard.Hosting;

internal sealed class GoalWorktreeOrphanSweepHostedService(
    OrchestratorWorkspace workspace,
    WorktreeCleanupContext cleanupContext) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        cleanupContext.Scheduler.SweepNow(workspace.ExecutionDirectory);

        using var timer = new PeriodicTimer(cleanupContext.Scheduler.Options.SweepInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            cleanupContext.Scheduler.SweepNow(workspace.ExecutionDirectory);
        }
    }
}
