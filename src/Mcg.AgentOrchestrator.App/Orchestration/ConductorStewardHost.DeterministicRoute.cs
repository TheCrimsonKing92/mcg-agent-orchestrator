namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorStewardHost
{
    private readonly ConductorStewardDeterministicRoute _deterministicRoute;

    private Task<string> StartRound(ConductorStewardTrigger trigger, string worktree)
    {
        var output = _deterministicRoute.TryBuild(trigger, worktree);
        return output is null
            ? Task.Run(() => _model.DispatchAsync(trigger, worktree, _shutdown.Token), _shutdown.Token)
            : Task.FromResult(output);
    }
}
