namespace Mcg.AgentOrchestrator.Core;

// An unresolved refinement's remapping demand is answered by explicit ownership
// at a later version of the same criterion, independently of evidence satisfaction.
internal static class CriterionObligationRetirementRule
{
    internal static CriterionEvidenceObligation? FindSupersedingObligation(
        CriterionEvidenceObligation obligation,
        IReadOnlyList<CriterionEvidenceObligation> obligations) =>
        obligation.Owner == CriterionEvidenceOwner.Unknown &&
        obligation.State != CriterionEvidenceState.Repaired &&
        obligation.Provenance.Contains("unresolved during refinement v", StringComparison.Ordinal)
            ? obligations.Where(item =>
                    item.CriterionIndex == obligation.CriterionIndex &&
                    item.CriterionVersion > obligation.CriterionVersion &&
                    item.Owner is CriterionEvidenceOwner.Acceptance or CriterionEvidenceOwner.Operator &&
                    item.State != CriterionEvidenceState.Repaired)
                .MinBy(item => item.CriterionVersion)
            : null;

    internal static CriterionEvidenceObligation Annotate(
        CriterionEvidenceObligation obligation,
        IReadOnlyList<CriterionEvidenceObligation> obligations) =>
        FindSupersedingObligation(obligation, obligations) is { } superseding
            // Keep the annotation out of storage: Pending rows require null Detail
            // on restore. Status receives a canonical, repeatable projection.
            ? obligation with { Detail = $"retired: superseded by explicitly owned {superseding.Id}" }
            : obligation;
}
