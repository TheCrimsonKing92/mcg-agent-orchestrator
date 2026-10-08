namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal interface IOwnerGoalEpicLookup
{
    Task<string> GetTitleAsync(string goalId, CancellationToken cancellationToken);
}
