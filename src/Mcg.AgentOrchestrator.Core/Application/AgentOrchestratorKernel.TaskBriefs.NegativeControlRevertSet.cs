namespace Mcg.AgentOrchestrator.Core;

public sealed partial class AgentOrchestratorKernel
{
    private static IReadOnlyList<string> BuildNegativeControlRevertSetBriefBlock(Goal goal, TaskSpec task)
    {
        if (task.RequiredRole is not (AgentRole.Tester or AgentRole.Reviewer)) return [];
        var declaration = NegativeControlRevertDeclaration.Parse(goal.Objective);
        if (!declaration.Declared) return [];
        var lines = new List<string>
        {
            "## Negative-control revert set",
            declaration.Rejection is null ? $"declared: {string.Join(", ", declaration.Paths)}" :
                $"declaration rejected: {declaration.Rejection}",
            "revert_paths and mutation.path may name these paths in addition to src files; a file that declares a selected test class is refused."
        };
        var receipts = goal.Tasks.Where(candidate => candidate.RequiredRole is AgentRole.Tester or AgentRole.Reviewer)
            .SelectMany(candidate => candidate.VerificationHistory)
            .OrderByDescending(verification => verification.CompletedAt)
            .SelectMany(verification => verification.FindingEvidenceReceipts ?? [])
            .Where(receipt => receipt.Arms?.Any(arm => arm.Arm == FindingEvidenceArm.SourceReverted &&
                (arm.RestoredPaths?.Count > 0 || arm.DroppedPaths?.Count > 0)) == true)
            .DistinctBy(receipt => receipt.ReceiptId, StringComparer.Ordinal).Take(5);
        foreach (var receipt in receipts)
        {
            var arm = receipt.Arms!.First(arm => arm.Arm == FindingEvidenceArm.SourceReverted &&
                (arm.RestoredPaths?.Count > 0 || arm.DroppedPaths?.Count > 0));
            var selection = string.Join(',', receipt.Request.Selections.Select(item => $"{item.TestProject}:{item.TestClass}"));
            var outcome = receipt.NegativeControlOutcome is { } value ?
                FindingEvidenceNegativeControlOutcomeJsonConverter.ToWireValue(value) : "none";
            lines.Add($"revert_set: receipt={receipt.ReceiptId}; candidate_sha={receipt.CandidateSha}; selection={selection}; " +
                $"restored={DescribeRevertSetPaths(arm.RestoredPaths)}; dropped={DescribeRevertSetPaths(arm.DroppedPaths)}; " +
                $"mutation={receipt.MutationPath ?? "none"}; outcome={outcome}");
        }
        lines.Add(string.Empty);
        return lines;
    }

    private static string DescribeRevertSetPaths(IReadOnlyList<string>? paths) =>
        paths is { Count: > 0 } ? string.Join(',', paths) : "none";
}
