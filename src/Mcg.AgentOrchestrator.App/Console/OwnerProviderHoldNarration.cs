namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal static class OwnerProviderHoldNarration
{
    internal static bool TryNarrate(OwnerConductEvent evt, out string sentence, out string reason,
        out string nextStep, out string ownerAction)
    {
        sentence = reason = nextStep = ownerAction = "";
        var binding = Binding(evt);
        if (string.IsNullOrWhiteSpace(binding)) return false;
        var name = DisplayName(binding);
        var attempt = OwnerActivityNarrator.Field(evt, "attempt");
        ownerAction = "No. You can let the conductor continue.";
        switch (evt.EventKind)
        {
            case "provider-hold-placed":
                sentence = $"Provider paused: {name} has no usage balance left";
                var next = OwnerActivityNarrator.Field(evt, "next_probe");
                reason = next is not (null or "unavailable")
                    ? $"The provider reported that its usage balance is exhausted; next check {next} (attempt {attempt})."
                    : "The provider reported that its usage balance is exhausted; the next check time is unavailable.";
                nextStep = $"Work assigned to {name} waits; the conductor tries one task at the next check.";
                ownerAction = "No. If the provider states a reset time, record it with provider-reset.";
                return true;
            case "provider-hold-probe":
                sentence = $"Provider check: trying one task on {name}";
                reason = $"The scheduled check for {name} is due (attempt {attempt}).";
                nextStep = "If the task runs, the hold clears; otherwise the next check is scheduled.";
                return true;
            case "provider-hold-restored":
                sentence = $"Provider back: {name} is accepting work again";
                reason = $"A task on {name} ran after the hold, so the hold has cleared.";
                nextStep = $"Work waiting for {name} continues.";
                return true;
            default: return false;
        }
    }

    internal static string DisplayName(string bindingKey)
    {
        var separator = bindingKey.IndexOf("::", StringComparison.Ordinal);
        if (separator < 0) return bindingKey;
        var provider = bindingKey[..separator];
        var credential = bindingKey[(separator + 2)..].Trim();
        if (credential == "<provider-default>") return provider;
        var leaf = credential[(credential.LastIndexOfAny(['\\', '/']) + 1)..];
        return $"{provider} ({(leaf.Length == 0 ? credential : leaf)})";
    }

    internal static string? Binding(OwnerConductEvent evt)
    {
        if (evt.Detail.StartsWith("binding=", StringComparison.Ordinal)) return evt.Detail[8..].Trim();
        var start = evt.Detail.IndexOf(" binding=", StringComparison.Ordinal);
        return start < 0 ? null : evt.Detail[(start + 9)..].Trim();
    }
}
