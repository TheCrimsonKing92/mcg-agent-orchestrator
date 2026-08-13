namespace Mcg.AgentOrchestrator.Infrastructure;

/// <summary>
/// Operator configuration for the per-dispatch codex egress proxy, read once when a dispatch is prepared
/// and then carried as data on <see cref="DispatchProcessHost.DispatchRunParameters"/>. Kept separate from
/// <see cref="WorkerSandboxOptions"/> because the proxy is an egress-reliability concern, not a sandbox one.
/// Defaults to disabled, so it is inert until an operator opts in. Tests construct it explicitly rather than
/// through <see cref="FromEnvironment"/>, so the environment read never leaks across suites.
/// </summary>
public sealed record CodexEgressProxyOptions(
    bool Enabled = false,
    bool Enforce = false,
    int IdleTimeoutMs = 120_000,
    int ConnectTimeoutMs = 15_000)
{
    /// <summary>Set to 1/true to route codex workers through the egress proxy.</summary>
    public const string EnabledVariable = "MCG_CODEX_EGRESS_PROXY";

    /// <summary>Set to 1/true to cut idle tunnels (Enforce). Unset leaves the proxy in Observe (measure-only).</summary>
    public const string EnforceVariable = "MCG_CODEX_EGRESS_PROXY_ENFORCE";

    /// <summary>Idle window in milliseconds before Enforce cuts a silent tunnel.</summary>
    public const string IdleTimeoutVariable = "MCG_CODEX_EGRESS_PROXY_IDLE_MS";

    /// <summary>Upstream TCP connect timeout in milliseconds.</summary>
    public const string ConnectTimeoutVariable = "MCG_CODEX_EGRESS_PROXY_CONNECT_MS";

    public static CodexEgressProxyOptions FromEnvironment() => new(
        Enabled: IsTruthy(Environment.GetEnvironmentVariable(EnabledVariable)),
        Enforce: IsTruthy(Environment.GetEnvironmentVariable(EnforceVariable)),
        IdleTimeoutMs: PositiveIntOrDefault(Environment.GetEnvironmentVariable(IdleTimeoutVariable), 120_000),
        ConnectTimeoutMs: PositiveIntOrDefault(Environment.GetEnvironmentVariable(ConnectTimeoutVariable), 15_000));

    private static bool IsTruthy(string? value) =>
        string.Equals(value, "1", StringComparison.Ordinal) ||
        string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);

    private static int PositiveIntOrDefault(string? value, int fallback) =>
        int.TryParse(value, out var parsed) && parsed > 0 ? parsed : fallback;
}
