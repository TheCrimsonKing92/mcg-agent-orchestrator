using System.Text.Json;
using System.Text.Json.Serialization;
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
                tasks = goal.Tasks.Select((task, index) => new TaskRow(
                    index + 1,
                    task.Status.ToString(),
                    task.RequiredRole.ToString(),
                    OutputTextPreview.CreateSummary(task.Description).Text,
                    task.AssignedAgentId is null ? null : CliIdentifierText.ShortId(task.AssignedAgentId.Value),
                    task.WorkerBuildInvocations.Count == 0 ? null : task.WorkerBuildInvocations.Select(i =>
                        new InvocationRow(i.Id, i.DispatchId, i.TreeDigest, i.Phase.ToString(),
                            i.Outcome?.ToString(), i.Consumed, i.CustodyReleased)).ToArray())).ToArray()
            }, new JsonSerializerOptions(JsonSerializerDefaults.Web), time);

    private sealed record TaskRow(int Number, string Status, string Role, string Description, string? Agent,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        InvocationRow[]? WorkerBuildInvocations);

    private sealed record InvocationRow(string Invocation, string Dispatch, string Candidate, string Phase,
        string? Outcome, bool Consumed, bool CustodyReleased);
}
