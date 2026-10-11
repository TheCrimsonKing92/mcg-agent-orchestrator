using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class GoalsAgentReadView
{
    internal static string Serialize(IReadOnlyList<GoalSummary> goals, TimeProvider? time = null) =>
        AgentReadEnvelope.Serialize(AgentReadEnvelope.GoalsSchema, AgentReadEnvelope.GoalsSchemaVersion,
            new
            {
                goals = goals.Select(goal => new
                {
                    id = goal.Id,
                    status = goal.Status,
                    condition = string.IsNullOrWhiteSpace(goal.Condition) ? null : goal.Condition,
                    objective = goal.Objective
                }).ToArray()
            }, new JsonSerializerOptions(JsonSerializerDefaults.Web), time);
}
