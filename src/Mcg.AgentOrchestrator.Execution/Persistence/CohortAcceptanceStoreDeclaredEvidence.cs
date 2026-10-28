using System.Text.Json;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed partial class CohortAcceptanceStore
{
    private static bool DeclaredPathsMatch(AcceptanceCohortReceipt receipt) =>
        receipt.GateChecks.All(check => check.CohortId == receipt.Identity.Value) &&
        receipt.GateChecks.SelectMany(check => check.AllArtifacts).Select(a => a.Path).Order(StringComparer.OrdinalIgnoreCase)
            .SequenceEqual(receipt.GateTestResultPaths, StringComparer.OrdinalIgnoreCase);

    private static IReadOnlyList<AcceptanceCohortCheckEvidence> ReadDeclaredChecks(
        IReadOnlyList<AcceptanceCohortEvidenceArtifact> artifacts)
    {
        try
        {
            var verdicts = artifacts.Where(a => a.Kind == "gate-verdict").ToArray();
            if (verdicts.Length != 1 || !AcceptanceCohortGateEvidence.IsContentBoundArtifact(verdicts[0])) return [];
            using var document = JsonDocument.Parse(File.ReadAllBytes(verdicts[0].Path));
            if (!document.RootElement.TryGetProperty("GateChecks", out var checks)) return [];
            var declared = checks.Deserialize<AcceptanceCohortCheckEvidence[]>() ?? [];
            return declared;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or
            InvalidOperationException or ArgumentException) { return []; }
    }
}
