namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class AcceptanceGateProgressProvenance
{
    internal static AcceptanceGateProgress Attach(AcceptanceGateProgress progress, IAcceptanceRunExecutionContext? context)
    {
        if (context is null) return progress;
        var owner = context;
        while (owner is AcceptanceRunExecutionContextView view) owner = view.Owner;
        return progress with
        {
            TreeRevision = progress.TreeRevision ?? context.TreeRevision,
            EvidenceKind = progress.EvidenceKind ?? context.EvidenceKind ?? (context.FocusedRunBinding is not null ? "member-reproduction" :
                owner is AcceptanceFocusedVerificationOwner ? "candidate-rerun" : "candidate"),
            ReproductionMemberId = progress.ReproductionMemberId ?? context.FocusedRunBinding?.MemberId
        };
    }
}
