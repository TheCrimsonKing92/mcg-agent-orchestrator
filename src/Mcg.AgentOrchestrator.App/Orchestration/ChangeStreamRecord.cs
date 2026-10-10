using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// A committed conductor change, ordered within one workspace's change stream.
internal sealed record ChangeStreamRecord(
    [property: JsonRequired] int Schema,
    [property: JsonRequired] long Sequence,
    [property: JsonRequired] DateTimeOffset Timestamp,
    [property: JsonRequired] string ChangeKind,
    [property: JsonRequired] string GoalId,
    [property: JsonRequired] string SourceEventKind,
    [property: JsonRequired] string Detail)
{
    internal const int CurrentSchemaVersion = 1;
    internal const string GoalTransition = "goal-transition";
    internal const string OwnerDecisionRaised = "owner-decision-raised";
    internal const string GoalStage = "goal-stage";
    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    internal static bool TryClassify(string eventKind, string? goalId, out string? changeKind)
    {
        changeKind = string.IsNullOrWhiteSpace(goalId) ? null : eventKind switch
        {
            "goal-escalation" => OwnerDecisionRaised,
            "goal" or "goal-landing" or "goal-left-working-set" or "watch-transition" => GoalTransition,
            "goal-lifecycle" or "acceptance" or "acceptance-cohort" => GoalStage,
            _ => null
        };
        return changeKind is not null;
    }

    internal static bool TryParse(string line, out ChangeStreamRecord? record)
    {
        record = null;
        try
        {
            var candidate = JsonSerializer.Deserialize<ChangeStreamRecord>(line, JsonOptions);
            if (candidate is null || candidate.Sequence <= 0 || string.IsNullOrWhiteSpace(candidate.GoalId) ||
                !TryClassify(candidate.SourceEventKind, candidate.GoalId, out var kind) ||
                candidate.ChangeKind != kind || candidate.Detail is null) return false;
            record = candidate;
            return true;
        }
        catch (JsonException) { return false; }
    }
}
