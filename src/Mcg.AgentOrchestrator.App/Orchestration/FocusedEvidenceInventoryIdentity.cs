using System.Security.Cryptography;
using System.Text;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// An inventory token qualifies only the inventory-derived UnsupportedProject refusal.
internal static class FocusedEvidenceInventoryIdentity
{
    internal const string Prefix = "focused-inventory-v1:";

    internal static string? Compute(AcceptanceGateEngineSettings? settings)
    {
        if (settings is null) return null;
        var forms = GoalAcceptanceVerifier.FocusedEvidenceSupportedProjectForms(settings);
        if (string.IsNullOrEmpty(forms)) return null;
        return Prefix + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(forms)))[..16];
    }

    internal static FindingEvidenceOutcome NotHonoured(
        FindingEvidenceNotHonouredReason reason, string detail, AcceptanceGateEngineSettings? settings) =>
        new(Honoured: false, Reason: reason, Detail: detail,
            DecisionReason: reason == FindingEvidenceNotHonouredReason.UnsupportedProject ? Compute(settings) : null);

    // The caller retains the Core permanence classification; legacy refusals remain binding.
    internal static bool StillBinds(FindingEvidenceOutcome prior, AcceptanceGateEngineSettings? settings)
    {
        if (prior.Reason != FindingEvidenceNotHonouredReason.UnsupportedProject ||
            prior.DecisionReason is not { } recorded || !recorded.StartsWith(Prefix, StringComparison.Ordinal))
            return true;
        var current = Compute(settings);
        return current is null || string.Equals(recorded, current, StringComparison.Ordinal);
    }
}
