namespace Mcg.AgentOrchestrator.Core;

public static class HumanWaitPolicyDefaults
{
    /// <summary>
    /// PlannerPrerequisiteEvidence is a typed sub-kind of spec clarification: it differs only in who
    /// may read the answer afterwards, never in hold, park, sweep, or operator-record behaviour.
    /// Every policy that keys on SpecClarification must go through this predicate, or typing the
    /// kind silently changes when goals park and which answers the operator views show.
    /// </summary>
    public static bool IsSpecClarificationClass(HumanWaitKind kind) =>
        kind is HumanWaitKind.SpecClarification or HumanWaitKind.PlannerPrerequisiteEvidence;

    public static bool IsAutoDefaultable(HumanWaitKind kind) =>
        IsSpecClarificationClass(kind);

    public static bool IsDismissible(HumanWaitKind kind) =>
        IsSpecClarificationClass(kind) || kind is HumanWaitKind.RecoveryChoice or HumanWaitKind.Other;

    public static bool IsExternallyBlocked(HumanWaitKind kind) =>
        kind is HumanWaitKind.ExternalCredential or HumanWaitKind.ProviderAuth;

    public static bool BlocksActiveWork(HumanWaitKind kind) =>
        kind != HumanWaitKind.ProspectiveAcceptanceEvidence;
}
