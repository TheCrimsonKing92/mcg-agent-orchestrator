using System.Text.Json;
using Mcg.AgentOrchestrator.App.Application;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class StatusAgentReadView
{
    internal static string Render(Goal goal, string? label, string status, TimeProvider? time = null) =>
        AgentReadEnvelope.Serialize(AgentReadEnvelope.StatusSchema, AgentReadEnvelope.StatusSchemaVersion,
            new
            {
                goal = goal.Id.Value,
                label,
                objective = OutputTextPreview.CreateSummary(goal.Objective).Text,
                status,
                tasks = goal.Tasks.Select((task, index) => new
                {
                    number = index + 1,
                    status = task.Status.ToString(),
                    role = task.RequiredRole.ToString(),
                    description = OutputTextPreview.CreateSummary(task.Description).Text,
                    agent = task.AssignedAgentId is null ? null : CliIdentifierText.ShortId(task.AssignedAgentId.Value)
                }).ToArray()
            }, new JsonSerializerOptions(JsonSerializerDefaults.Web), time);
}
