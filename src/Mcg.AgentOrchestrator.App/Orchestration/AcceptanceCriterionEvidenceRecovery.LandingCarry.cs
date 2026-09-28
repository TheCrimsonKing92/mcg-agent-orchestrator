using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static partial class AcceptanceCriterionEvidenceRecovery
{
    private const string LandingCarryOperation = "conductor:criterion-evidence-landing-carry";

    private static bool TryApplyRecordedLandingCarry(
        AgentOrchestratorKernel kernel,
        Goal goal,
        string candidateSha,
        string mainSha,
        string executionDirectory,
        Func<string, IReadOnlyList<string>, GitCli.GitResult> gitRunner)
    {
        var pending = goal.OutstandingCriterionEvidenceObligations
            .Where(IsPendingAcceptanceObligation)
            .ToArray();
        var boundElsewhere = pending.Where(item =>
            !string.IsNullOrWhiteSpace(item.ExpectedCandidateSha) &&
            !string.Equals(item.ExpectedCandidateSha, candidateSha, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (boundElsewhere.Length == 0)
            return false;

        foreach (var entry in GoalOperationJournal.Read(executionDirectory, goal.Id).Entries.Reverse())
        {
            if (entry.Status != GoalOperationStatus.Completed ||
                !string.Equals(entry.Operation, LandingCarryOperation, StringComparison.Ordinal) ||
                !TryReadCarryField(entry.Detail, "newHead", out var newHead) ||
                !string.Equals(newHead, candidateSha, StringComparison.OrdinalIgnoreCase) ||
                !IsAncestor(executionDirectory, newHead, mainSha, gitRunner) ||
                !TryReadCarryField(entry.Detail, "oldHeads", out var oldHeads) ||
                !TryReadCarryField(entry.Detail, "obligations", out var obligationIds) ||
                !TryReadCarryField(entry.Detail, "equivalence", out var equivalence))
                continue;

            var heads = oldHeads.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var ids = obligationIds.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .ToHashSet(StringComparer.Ordinal);
            if (boundElsewhere.Any(item =>
                    !heads.Contains(item.ExpectedCandidateSha!) ||
                    !ids.Contains(item.Id) ||
                    !HasPassedEquivalence(equivalence, item.ExpectedCandidateSha!)))
                continue;

            // Validate every binding before changing any obligation. A completed journal record
            // is the durable proof from the landing tick; post-landing main is not a valid input
            // for recomputing that same patch equivalence.
            foreach (var obligation in boundElsewhere)
            {
                kernel.MapCriterionEvidenceOwner(
                    goal.Id,
                    obligation.CriterionIndex,
                    obligation.CriterionVersion,
                    CriterionEvidenceOwner.Acceptance,
                    "conductor recorded landing carry recovery",
                    obligation.RequiredScope,
                    obligation.FindingStableId,
                    candidateSha);
            }
            return true;
        }
        return false;
    }

    private static bool TryReadCarryField(string? detail, string name, out string value)
    {
        value = string.Empty;
        if (string.IsNullOrWhiteSpace(detail)) return false;
        foreach (var field in detail.Split(';', StringSplitOptions.TrimEntries))
        {
            if (!field.StartsWith(name + "=", StringComparison.Ordinal)) continue;
            value = field[(name.Length + 1)..].Trim();
            return value.Length > 0;
        }
        return false;
    }

    private static bool HasPassedEquivalence(string evidence, string oldHead) =>
        evidence.Split(" | ", StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Any(item => item.StartsWith(oldHead + ": goal-owned lines ", StringComparison.OrdinalIgnoreCase) ||
                         item.StartsWith(oldHead + ": range-diff ", StringComparison.OrdinalIgnoreCase));
}
