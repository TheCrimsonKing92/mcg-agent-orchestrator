using System.Text.Json;

namespace Mcg.AgentOrchestrator.Core;

// One authoritative execution of an explicitly declared check. Supplemental representations
// are custody artifacts, never additional executions.
public sealed record AcceptanceCohortCheckEvidence(
    string CheckName,
    string Format,
    string Project,
    string AttemptId,
    int RunOrdinal,
    int? ExitCode,
    string ArtifactRoot,
    IReadOnlyList<string> SelectedTestIds,
    int ExecutedTestCount,
    IReadOnlyList<AcceptanceCohortEvidenceArtifact> Artifacts)
{
    public string? CohortId { get; init; }
    public IReadOnlyList<AcceptanceCohortEvidenceArtifact> SupplementalArtifacts { get; init; } = [];
    [System.Text.Json.Serialization.JsonIgnore]
    public IEnumerable<AcceptanceCohortEvidenceArtifact> AllArtifacts => Artifacts.Concat(SupplementalArtifacts);

    public const string NormalizedFormat = "mcg-test-result/1";
    public const string TrxFormat = "trx";

    public AcceptanceCohortCheckEvidence BindToCohort(AcceptanceCohortIdentity identity) =>
        CohortId is null || CohortId == identity.Value ? this with { CohortId = identity.Value }
            : throw new InvalidDataException("Declared check evidence already belongs to another cohort.");
}

public sealed partial record AcceptanceCohortReceipt
{
    public IReadOnlyList<AcceptanceCohortCheckEvidence> GateChecks { get; init; } = [];
    [System.Text.Json.Serialization.JsonIgnore]
    public bool DeclaredEvidenceValidated { get; init; }

    private bool HasDeclaredContentBoundEvidence()
    {
        if (GateChecks.Count == 0)
            return AcceptanceCohortGateEvidence.HasContentBoundEvidence(GateTestResultPaths, GateEvidenceArtifacts);

        if (!DeclaredEvidenceValidated || !AcceptanceCohortGateEvidence.HasNormalizedTestResultPaths(GateTestResultPaths) ||
            GateChecks.Any(check => check.CohortId != Identity.Value || check.ExitCode != 0 || check.ExecutedTestCount <= 0 || check.Artifacts.Count == 0) ||
            GateChecks.Select(check => check.CheckName).Distinct(StringComparer.Ordinal).Count() != GateChecks.Count ||
            !GateEvidenceArtifacts.All(AcceptanceCohortGateEvidence.IsContentBoundArtifact)) return false;

        var declared = GateChecks.SelectMany(check => check.AllArtifacts).OrderBy(a => a.Path, StringComparer.Ordinal).ToArray();
        var retained = GateEvidenceArtifacts.Where(a => a.Kind != "gate-verdict").OrderBy(a => a.Path, StringComparer.Ordinal).ToArray();
        if (!declared.SequenceEqual(retained) || !declared.Select(a => a.Path).Order(StringComparer.OrdinalIgnoreCase)
                .SequenceEqual(GateTestResultPaths, StringComparer.OrdinalIgnoreCase)) return false;
        try
        {
            var verdict = GateEvidenceArtifacts.Single(a => a.Kind == "gate-verdict");
            using var document = JsonDocument.Parse(File.ReadAllBytes(verdict.Path));
            var root = document.RootElement;
            return root.GetProperty("CohortId").GetString() == Identity.Value &&
                root.GetProperty("ReceiptId").GetString() == ReceiptId &&
                root.GetProperty("Outcome").GetString() == Outcome.ToString() &&
                root.GetProperty("GateExitCode").GetInt32() == GateExitCode &&
                JsonSerializer.Serialize(root.GetProperty("GateChecks")) == JsonSerializer.Serialize(GateChecks);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or
            InvalidOperationException or KeyNotFoundException) { return false; }
    }
}
