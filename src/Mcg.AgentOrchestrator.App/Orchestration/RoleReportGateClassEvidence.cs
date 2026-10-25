using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class RoleReportGateClassEvidence
{
    internal static Func<string, string?> For(string orchestratorDirectory, GoalId goalId, string? candidateSha)
    {
        var attempts = ProspectiveEvidenceGateResolver.ReadAttempts(orchestratorDirectory, goalId, candidateSha);
        IReadOnlyList<GroupedGateReceiptTrxEvidence.Receipt>? grouped = null;
        return name =>
        {
            foreach (var attempt in attempts)
            {
                var matches = ProspectiveEvidenceTrxRows.Match(name, attempt.Results);
                if (matches.Length > 0 && matches.All(row => row.Passed) &&
                    ProspectiveEvidenceTrxRows.AmbiguousClassNames(matches) is null)
                    return $"attempt {attempt.Id}";
            }
            grouped ??= GroupedGateReceiptTrxEvidence.ReadPassedReceipts(orchestratorDirectory, goalId, candidateSha);
            foreach (var receipt in grouped)
                if (GroupedGateReceiptTrxEvidence.Evaluate([name], receipt.TrxPaths, orchestratorDirectory)
                    .All(item => item.Satisfied))
                    return $"{receipt.Kind} receipt {receipt.ReceiptId}";
            return null;
        };
    }
}
