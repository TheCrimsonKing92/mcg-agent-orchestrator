using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.App.Cli;

namespace Mcg.AgentOrchestrator.App.Dashboard.Rendering;

public static partial class GoalTranscriptRenderer
{
private static string BuildSuggestedCommand(
    Goal goal,
    NextActionItem item,
    IReadOnlyList<AgentDefinition>? agents = null) =>
    ConsoleViews.BuildSuggestedCommand(goal, item, agents);

private static string BuildStageSuggestedCommand(
    Goal goal,
    TaskStageReadiness stage,
    IReadOnlyList<AgentDefinition>? agents = null) =>
    ConsoleViews.BuildStageSuggestedCommand(goal, stage, agents);
}
