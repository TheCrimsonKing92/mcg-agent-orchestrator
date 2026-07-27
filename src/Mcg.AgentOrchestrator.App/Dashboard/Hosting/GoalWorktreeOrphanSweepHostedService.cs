using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Dashboard.Hosting;

internal sealed class GoalWorktreeOrphanSweepHostedService(OrchestratorWorkspace workspace) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        GoalWorktreeOrphanSweepScheduler.SweepNow(workspace.ExecutionDirectory);

        using var timer = new PeriodicTimer(GoalWorktreeOrphanSweepScheduler.Options.SweepInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            GoalWorktreeOrphanSweepScheduler.SweepNow(workspace.ExecutionDirectory);
        }
    }
}
