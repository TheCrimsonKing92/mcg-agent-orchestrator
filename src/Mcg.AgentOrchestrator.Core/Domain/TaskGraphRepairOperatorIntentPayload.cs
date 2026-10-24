namespace Mcg.AgentOrchestrator.Core;

[System.Text.Json.Serialization.JsonUnmappedMemberHandling(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow)]
public sealed record TaskGraphInsertion(string Role, string Description, string? BeforeRole = null);

[System.Text.Json.Serialization.JsonUnmappedMemberHandling(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow)]
public sealed record TaskGraphRepairOperatorIntentPayload(
    IReadOnlyList<TaskGraphInsertion> Insertions,
    IReadOnlyList<string>? ExpectedFinalRoleOrder = null,
    int Version = 1);

public sealed class TaskGraphRepairRejectedException(string reasonCode, int? insertionIndex, string detail)
    : InvalidOperationException($"{reasonCode}: insertion={insertionIndex?.ToString() ?? "plan"}; {detail}")
{
    public string ReasonCode { get; } = reasonCode;
    public int? InsertionIndex { get; } = insertionIndex;
}
