using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class WorkerSubscriptionPreflightFindings
{
    /// <summary>
    /// Adds the Claude auth preflight finding and returns the credential source that finding reports, so
    /// the caller can record it on the dispatch and the dispatch start boundary can transport THAT
    /// selection into the worker sandbox. Returns null when no Claude login was inspected, which is the
    /// only case where dispatch start may resolve a source for itself.
    /// </summary>
    internal static ClaudeCredentialSourceSelection? AddClaudeLowIntegrityAuthFinding(
        List<string> findings,
        AgentRole role,
        IWorkerProvider provider,
        WorkerSandboxOptions sandbox,
        Func<ClaudeCliAuthState>? claudeAuthProbe)
    {
        if (provider.Identity.Kind != ProviderKind.AnthropicClaudeCli)
        {
            findings.Add("auth: Claude CLI Low-IL auth preflight not applicable for this worker profile");
            return null;
        }

        if (!sandbox.Enabled)
        {
            findings.Add("auth: Claude CLI Low-IL auth preflight not required because worker sandbox is disabled");
            return null;
        }

        var authState = (claudeAuthProbe ?? ClaudeCliAuthProbe.FromEnvironment)();

        // Derived from the reported state, never computed beside it - including in API-key mode and for a
        // rejected source, so the pre-launch failure a worker hits names the source reported right here.
        var selection = authState.ToTransportedSelection();
        if (authState.HasAnthropicApiKey)
        {
            findings.Add("ok: Claude CLI Low-IL auth preflight found ANTHROPIC_API_KEY");
            return selection;
        }

        if (!authState.HasCliCredentialArtifact)
        {
            // Stays an `auth:` observation on purpose: admission policy is unchanged, and the hard
            // stop for an unusable login is the pre-launch seeding failure in ClaudeCredentialSource.
            findings.Add(
                "auth: Claude CLI Low-IL auth preflight found no API key and no CLI credential artifact; " +
                (authState.UnavailableReason ?? "no credential source was inspected"));
            return selection;
        }

        var artifact = string.IsNullOrWhiteSpace(authState.CredentialArtifactPath)
            ? "canonical Claude CLI credential path"
            : authState.CredentialArtifactPath;
        findings.Add(
            $"ok: Claude CLI Low-IL auth preflight will seed CLI credentials from {artifact} into the sandbox CLAUDE_CONFIG_DIR (subscription auth, proven at Low IL by live probe 2026-07-24)");
        return selection;
    }

    internal static void AddProviderBudgetExhaustionFinding(
        List<string> findings,
        IEnumerable<Goal> providerHoldScope,
        string providerName,
        string? credentialBinding)
    {
        if (!DispatchFailureClassifier.TryGetProviderBudgetExhaustionHold(
            providerHoldScope,
            providerName,
            credentialBinding,
            out var providerHold))
        {
            return;
        }

        findings.Add(
            $"blocked: provider budget exhausted for binding {providerHold.BindingKey} ({providerHold.BindingScope}); " +
            $"source goal {providerHold.SourceGoalId.Value[..8]} task {providerHold.SourceTaskId.Value[..8]} receipt {providerHold.EvidenceReceipt}");
    }
}
