namespace Mcg.AgentOrchestrator.Infrastructure;

/// <summary>
/// Configuration for the OS-level worker sandbox: when enabled (and on Windows), write-capable
/// worker dispatches (Developer/Tester) run AS a dedicated low-privilege local account whose NTFS
/// ACLs confine writes to the per-run worktree + git common dir, with codex's own sandbox set to
/// danger-full-access (so it uses ordinary CreateProcess and never hits the CreateProcessAsUserW
/// poisoning). Gated by environment so it is off by default and a no-op on non-Windows hosts.
/// </summary>
public sealed record WorkerSandboxOptions(bool Enabled, string Account, string CredentialTarget)
{
    public const string EnabledVariable = "MCG_WORKER_SANDBOX";
    public const string AccountVariable = "MCG_WORKER_ACCOUNT";
    public const string CredentialTargetVariable = "MCG_WORKER_CREDENTIAL_TARGET";

    public const string DefaultAccount = "mcg-worker";
    public const string DefaultCredentialTarget = "mcg-orchestrator-worker";

    public static WorkerSandboxOptions FromEnvironment()
    {
        var flag = Environment.GetEnvironmentVariable(EnabledVariable);
        var enabled = OperatingSystem.IsWindows() &&
            (string.Equals(flag, "1", StringComparison.Ordinal) ||
             string.Equals(flag, "true", StringComparison.OrdinalIgnoreCase));

        var account = NonEmptyOrDefault(Environment.GetEnvironmentVariable(AccountVariable), DefaultAccount);
        var target = NonEmptyOrDefault(Environment.GetEnvironmentVariable(CredentialTargetVariable), DefaultCredentialTarget);
        return new WorkerSandboxOptions(enabled, account, target);
    }

    private static string NonEmptyOrDefault(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value;
}
