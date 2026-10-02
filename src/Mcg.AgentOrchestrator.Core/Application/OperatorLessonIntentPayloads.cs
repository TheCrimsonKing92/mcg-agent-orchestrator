using System.Text.Json.Serialization;

namespace Mcg.AgentOrchestrator.Core;

public sealed record LessonRecordOperatorIntentPayload(
    string Situation,
    string Rule,
    IReadOnlyList<string> EvidenceReferences,
    IReadOnlyList<string> AppliesTo,
    string? GoalId,
    string WorkingDirectory,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? UntilGoal = null);

public sealed record LessonRetireOperatorIntentPayload(
    string LessonId,
    string Reason,
    IReadOnlyList<string> EvidenceReferences,
    string WorkingDirectory);
