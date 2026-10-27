namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed partial record AcceptanceCheckResult
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? NormalizedTestResultPaths { get; init; }

    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public NormalizedTestResultWriteFailure? NormalizedTestResultWriteFailure { get; init; }
}
