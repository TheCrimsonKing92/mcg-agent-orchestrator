namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed partial record AcceptanceCheckResult
{
    public Mcg.AgentOrchestrator.Core.AcceptanceCohortCheckEvidence? CohortEvidence { get; init; }
    public string? CrosswalkExecutablePath { get; init; }
    public string? CrosswalkExecutableSha256 { get; init; }

    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<CrosswalkInvocationDiagnostic>? CrosswalkDiagnostics { get; init; }

    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? NormalizedTestResultPaths { get; init; }

    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public NormalizedTestResultWriteFailure? NormalizedTestResultWriteFailure { get; init; }
}
