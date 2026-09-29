namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed record AcceptanceShardPolicySwitches(
    bool? FullShards = null,
    bool? ChangeScoped = null,
    Func<string, string?>? ReadVariable = null)
{
    private static readonly AsyncLocal<AcceptanceShardPolicySwitches?> Scoped = new();

    internal static IDisposable Use(AcceptanceShardPolicySwitches switches)
    {
        var previous = Scoped.Value;
        Scoped.Value = switches;
        return new Scope(() => Scoped.Value = previous);
    }

    internal static bool ResolveChangeScoped(AcceptanceShardPolicySwitches? switches)
    {
        switches ??= Scoped.Value;
        if (switches?.ChangeScoped is { } changeScoped)
            return changeScoped;

        var value = (switches?.ReadVariable ?? Environment.GetEnvironmentVariable)("MCG_ACCEPTANCE_CHANGE_SCOPED");
        return string.IsNullOrWhiteSpace(value) ||
            value.Equals("1", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("on", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool ResolveFullShards(AcceptanceShardPolicySwitches? switches)
    {
        switches ??= Scoped.Value;
        if (switches?.FullShards is { } fullShards)
            return fullShards;

        var value = (switches?.ReadVariable ?? Environment.GetEnvironmentVariable)("MCG_ACCEPTANCE_FULL_SHARDS");
        return value is not null &&
            (value.Equals("1", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("on", StringComparison.OrdinalIgnoreCase));
    }

    private sealed class Scope(Action restore) : IDisposable
    {
        public void Dispose() => restore();
    }
}
