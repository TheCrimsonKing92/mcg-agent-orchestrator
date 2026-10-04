namespace Mcg.AgentOrchestrator.Core;

public enum VerifiedAdmissionAction { Proceed, Hold, Escalate }

/// <summary>Observed pre-gate inputs; nullable booleans mean the driver has not reached that check.</summary>
public sealed record VerifiedAdmissionFacts(GoalStatus GoalStatus)
{
    public bool? OwnerReviewHold { get; init; }
    public string? OwnerReviewSha { get; init; }
    public string? OwnerReviewReason { get; init; }
    public string? OwnerReviewFingerprint { get; init; }
    public bool? CohortAttribution { get; init; }
    public string? CohortAttributionEvidence { get; init; }
    public bool? ApparatusHold { get; init; }
    public string? ApparatusBranchSha { get; init; }
    public string? ApparatusMainSha { get; init; }
    public string? ApparatusCandidate { get; init; }
    public bool? EvidenceLease { get; init; }
    public string? EvidenceLeaseReason { get; init; }
    public bool? AllTasksPassed { get; init; }
    public string? GateStartDeferral { get; init; }
    public string? InfrastructureDeferredReasonCode { get; init; }
    public string? InfrastructureDeferredMessage { get; init; }
    public string? BuildSlotsBusyDetail { get; init; }
    public string? BuildLockBlockedDetail { get; init; }
    public string? CancellationProbeCause { get; init; }

    public IReadOnlyList<PolicyDecisionFact> ToRecordedFacts() => Array.AsReadOnly<PolicyDecisionFact>(
    [
        new("goalStatus", GoalStatus.ToString()),
        new("ownerReviewHold", Encode(OwnerReviewHold)),
        new("ownerReviewSha", OwnerReviewSha ?? ""),
        new("ownerReviewReason", OwnerReviewReason ?? ""),
        new("ownerReviewFingerprint", OwnerReviewFingerprint ?? ""),
        new("cohortAttribution", Encode(CohortAttribution)),
        new("cohortAttributionEvidence", CohortAttributionEvidence ?? ""),
        new("apparatusHold", Encode(ApparatusHold)),
        new("apparatusBranchSha", ApparatusBranchSha ?? ""),
        new("apparatusMainSha", ApparatusMainSha ?? ""),
        new("apparatusCandidate", ApparatusCandidate ?? ""),
        new("evidenceLease", Encode(EvidenceLease)),
        new("evidenceLeaseReason", EvidenceLeaseReason ?? ""),
        new("allTasksPassed", Encode(AllTasksPassed)),
        new("gateStartDeferral", GateStartDeferral ?? ""),
        new("infrastructureDeferredReasonCode", InfrastructureDeferredReasonCode ?? ""),
        new("infrastructureDeferredMessage", InfrastructureDeferredMessage ?? ""),
        new("buildSlotsBusyDetail", BuildSlotsBusyDetail ?? ""),
        new("buildLockBlockedDetail", BuildLockBlockedDetail ?? ""),
        new("cancellationProbeCause", CancellationProbeCause ?? "")
    ]);

    public static VerifiedAdmissionFacts FromRecordedFacts(IReadOnlyList<PolicyDecisionFact> facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        if (facts.Count != 20)
            throw new InvalidOperationException("Verified admission replay requires exactly twenty named facts.");
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var fact in facts)
            if (!values.TryAdd(fact.Name, fact.Value))
                throw new InvalidOperationException($"Duplicate verified admission fact '{fact.Name}'.");
        string Read(string name) => values.TryGetValue(name, out var value) ? value
            : throw new InvalidOperationException($"Missing verified admission fact '{name}'.");
        bool? ReadBool(string name) => Read(name) switch
        {
            "true" => true, "false" => false, "" => null,
            _ => throw new InvalidOperationException($"Invalid boolean verified admission fact '{name}'.")
        };
        if (!Enum.TryParse<GoalStatus>(Read("goalStatus"), out var status) ||
            !Enum.IsDefined(status) || status.ToString() != Read("goalStatus"))
            throw new InvalidOperationException("Invalid verified admission goal status fact.");
        var deferral = Read("gateStartDeferral");
        ValidateDeferral(deferral);
        return new(status)
        {
            OwnerReviewHold = ReadBool("ownerReviewHold"), OwnerReviewSha = Read("ownerReviewSha"),
            OwnerReviewReason = Read("ownerReviewReason"), OwnerReviewFingerprint = Read("ownerReviewFingerprint"),
            CohortAttribution = ReadBool("cohortAttribution"), CohortAttributionEvidence = Read("cohortAttributionEvidence"),
            ApparatusHold = ReadBool("apparatusHold"), ApparatusBranchSha = Read("apparatusBranchSha"),
            ApparatusMainSha = Read("apparatusMainSha"), ApparatusCandidate = Read("apparatusCandidate"),
            EvidenceLease = ReadBool("evidenceLease"), EvidenceLeaseReason = Read("evidenceLeaseReason"),
            AllTasksPassed = ReadBool("allTasksPassed"), GateStartDeferral = deferral,
            InfrastructureDeferredReasonCode = Read("infrastructureDeferredReasonCode"),
            InfrastructureDeferredMessage = Read("infrastructureDeferredMessage"),
            BuildSlotsBusyDetail = Read("buildSlotsBusyDetail"), BuildLockBlockedDetail = Read("buildLockBlockedDetail"),
            CancellationProbeCause = Read("cancellationProbeCause")
        };
    }

    internal static void ValidateDeferral(string? kind)
    {
        if (kind is not (null or "" or "infrastructure-deferred" or "build-slots-busy" or "build-lock-blocked" or "attempt-cancelled"))
            throw new InvalidOperationException("Invalid verified admission gate-start deferral fact.");
    }

    private static string Encode(bool? value) => value switch { true => "true", false => "false", null => "" };
}

public sealed record VerifiedAdmissionDecision(
    VerifiedAdmissionAction Action, int DiscriminatingRung, string DiscriminatingEvidence,
    string Reason, string StableIdentity, VerifiedAdmissionFacts Facts)
{
    public PolicyDecisionRecord ToRecord() => new(VerifiedAdmissionPolicy.StageName, Action.ToString(),
        DiscriminatingRung, DiscriminatingEvidence, Reason, Facts.ToRecordedFacts());
}

/// <summary>Selects Verified-stage pre-gate outcomes without owning driver effects.</summary>
public static class VerifiedAdmissionPolicy
{
    public const string StageName = "verified-admission";

    public static VerifiedAdmissionDecision Evaluate(VerifiedAdmissionFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        VerifiedAdmissionFacts.ValidateDeferral(facts.GateStartDeferral);
        VerifiedAdmissionDecision Hold(int rung, string evidence, string reason, string identity = "") =>
            new(VerifiedAdmissionAction.Hold, rung, evidence, reason, identity, facts);
        static string Required(string? value, string name) => value ??
            throw new InvalidOperationException($"Verified admission requires {name}.");

        if (facts.OwnerReviewHold == true)
            return Hold(1, "owner-review-hold", Required(facts.OwnerReviewReason, "an owner-review reason"),
                $"owner-review-hold:{Required(facts.OwnerReviewSha, "an owner sha")}:{Required(facts.OwnerReviewFingerprint, "an owner fingerprint")}");
        if (facts.CohortAttribution == true)
            return new(VerifiedAdmissionAction.Escalate, 2, "cohort-attribution-failure",
                "Acceptance verification failed; review and fix before landing. " +
                Required(facts.CohortAttributionEvidence, "attribution evidence"), "", facts);
        if (facts.ApparatusHold == true)
            return Hold(3, "acceptance-apparatus-hold",
                $"Acceptance apparatus hold remains active for unchanged candidate {Required(facts.ApparatusCandidate, "a formatted candidate")}. Repair main or confirm acceptance-retry before another acceptance process starts.",
                $"acceptance-apparatus:{Required(facts.ApparatusBranchSha, "a branch sha")}:{Required(facts.ApparatusMainSha, "a main sha")}");
        if (facts.EvidenceLease == false)
            return Hold(4, "evidence-mutation-lease-hold", Required(facts.EvidenceLeaseReason, "a lease reason"));
        if (facts.AllTasksPassed == false)
            return Hold(5, "task-verification-precheck",
                "Goal is not ready for acceptance: complete every task with a passed verification before accepting this gate.");
        switch (facts.GateStartDeferral)
        {
            case "infrastructure-deferred":
                return Hold(6, "gate-start-infrastructure-deferred",
                    $"Acceptance infrastructure deferred ({Required(facts.InfrastructureDeferredReasonCode, "a deferral code")}); retry on next conduct tick. {Required(facts.InfrastructureDeferredMessage, "a deferral message")}");
            case "build-slots-busy":
                return Hold(7, "gate-start-build-slots-busy",
                    $"Stable dotnet build slots busy; retry on next conduct tick. {Required(facts.BuildSlotsBusyDetail, "busy slots")}");
            case "build-lock-blocked":
                return Hold(8, "gate-start-build-lock-blocked",
                    $"Build artifact lock blocked acceptance; retry on next conduct tick. {Required(facts.BuildLockBlockedDetail, "lock attribution")}");
            case "attempt-cancelled":
                return Hold(9, "gate-start-attempt-cancelled",
                    $"Acceptance attempt stopped by cancellation probe ({Required(facts.CancellationProbeCause, "a probe cause")}); retry when the goal is eligible.");
        }
        if (facts.EvidenceLease == true && facts.AllTasksPassed == true)
            return new(VerifiedAdmissionAction.Proceed, 0, "verified-admission-proceed",
                "Verified admission checks passed.", "", facts);
        throw new InvalidOperationException("Verified admission facts do not identify an outcome.");
    }
}
