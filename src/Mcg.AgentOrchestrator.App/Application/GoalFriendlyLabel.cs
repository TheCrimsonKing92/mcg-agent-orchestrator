using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Application;

internal static class GoalFriendlyLabel
{
    public static string? Resolve(Goal goal, string backlogStorePath)
    {
        if (string.IsNullOrWhiteSpace(goal.SourceBacklogItemId) ||
            string.IsNullOrWhiteSpace(backlogStorePath) ||
            !File.Exists(backlogStorePath))
        {
            return null;
        }

        try
        {
            var item = BacklogStore.OpenReadOnly(backlogStorePath)
                .GetByExactIdAsync(goal.SourceBacklogItemId)
                .GetAwaiter()
                .GetResult();
            return string.IsNullOrWhiteSpace(item?.Title) ? null : item.Title;
        }
        catch
        {
            return null;
        }
    }
}
