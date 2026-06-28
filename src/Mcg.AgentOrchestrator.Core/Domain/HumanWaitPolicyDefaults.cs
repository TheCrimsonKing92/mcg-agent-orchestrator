namespace Mcg.AgentOrchestrator.Core;

public static class HumanWaitPolicyDefaults
{
    public static bool IsAutoDefaultable(HumanWaitKind kind) =>
        kind is HumanWaitKind.SpecClarification;

    public static bool IsDismissible(HumanWaitKind kind) =>
        kind is HumanWaitKind.SpecClarification or HumanWaitKind.RecoveryChoice or HumanWaitKind.Other;

    public static bool IsExternallyBlocked(HumanWaitKind kind) =>
        kind is HumanWaitKind.ExternalCredential or HumanWaitKind.ProviderAuth;
}
