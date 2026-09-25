using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static partial class CliPersistentStateRunner
{
    private static string ResolveGoalReplacementPreflightRoles(string pipeline) => pipeline switch
    {
        "five-role" => string.Join(',', new[]
        {
            AgentRole.Researcher,
            AgentRole.Planner,
            AgentRole.Developer,
            AgentRole.Tester,
            AgentRole.Reviewer
        }),
        "scout" => string.Join(',', new[]
        {
            AgentRole.Planner,
            AgentRole.Developer,
            AgentRole.Tester,
            AgentRole.Reviewer
        }),
        _ => $"unprepared:{pipeline}"
    };
}
