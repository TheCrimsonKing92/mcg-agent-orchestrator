namespace Mcg.AgentOrchestrator.Core;

public static class ReviewFindingContractRepairEnvelopeBuilder
{
    private static readonly string[] OutputSchema =
    [
        "findings: {\"findings\":[{\"stable_id\":\"...\",\"state\":\"Open|Resolved\",\"location\":{\"file\":\"...\",\"region\":\"...\",\"hunk\":null},\"description\":\"...\",\"severity\":\"blocking|advisory\",\"category\":\"...\"}],\"touched_anchors\":[{\"file\":\"...\",\"region\":\"...\",\"hunk\":null}]}",
        "WORKER_RESULT: include files, commands, tests, commit, blockers, model_fit, skills, confidence, then END_WORKER_RESULT",
        "Preserve every stable finding identity and substantive verdict; correct only the stated contract violation."
    ];

    public static ReviewFindingContractRepairEnvelope Build(
        string candidateSha,
        string priorSubstantiveVerdict,
        IReadOnlyList<CanonicalReviewFindingEntry> findings,
        IReadOnlyList<ReviewFindingLocation> touchedAnchorProof,
        ReviewFindingContractViolation contractViolation,
        IReadOnlyList<ReviewFindingContentReference> immutableReferences) =>
        new(
            ContextContractVersion.V1.Value,
            candidateSha,
            priorSubstantiveVerdict,
            findings,
            touchedAnchorProof,
            contractViolation,
            OutputSchema,
            immutableReferences);
}
