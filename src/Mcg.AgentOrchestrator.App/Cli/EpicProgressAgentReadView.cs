using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class EpicProgressAgentReadView
{
    internal static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    internal static void Print(IReadOnlyList<EpicProgressRollup> rows) => Console.WriteLine(
        AgentReadEnvelope.Serialize(AgentReadEnvelope.EpicProgressSchema, 1, new { epics = rows.Select(Payload).ToArray() }, Options));

    private static object Payload(EpicProgressRollup row) => new
    {
        row.Epic, row.Project, row.Members, row.MemberGoals,
        row.GoalCount, row.BacklogItemCount, row.ActiveCount, row.VerifiedCount, row.ParkedCount,
        row.LandedCount, row.NewestUpdatedAt, row.VerifyingCount, row.FailedCount, row.ClosedCount, row.MissingCount,
        row.BacklogOpenCount, row.BacklogDoneCount, row.WindowCreatedCount, row.WindowTransitionedCount,
        row.WindowFailedCount, row.WindowLandedCount, row.UnknownLandingDateCount, row.Outcome,
        plan = row.Plan is null ? null : new
        {
            row.Plan.Plan.Bar, row.Plan.Items, row.Plan.Plan.Decisions, row.Plan.UnplannedMembers, row.Plan.UnplannedActiveMembers,
            scopeSummary = EpicPlanNextStep.Summary(row.Plan.Items), nextStep = EpicPlanNextStep.Compute(row.Plan)
        }
    };
}
