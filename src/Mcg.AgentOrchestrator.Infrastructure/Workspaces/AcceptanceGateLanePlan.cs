using System.Text.Json;

namespace Mcg.AgentOrchestrator.Infrastructure;

// Observation of a completed selection decision, never an input to scheduling.
internal sealed record AcceptanceGateLanePlan(string SchemaVersion, string AttemptId,
    IReadOnlyList<string> Scheduled, IReadOnlyList<AcceptanceGateLaneSkip> Skipped)
{
    internal const string CurrentSchemaVersion = "gate-lane-plan.v1";
    internal const string ArtifactSuffix = ".lane-plan.json";
    internal static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    internal string ToJson() => JsonSerializer.Serialize(this, JsonOptions);
    internal string FormatEventTarget() => "GATE_LANE_PLAN " + ToJson();
}
