using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

public enum WorkerSandboxProvider
{
    Unknown,
    Codex,
    Claude,
    Ollama
}

public interface IWorkerSandbox
{
    WorkerSandboxOptions Options { get; }

    bool ShouldConfine(AgentRole role, bool isLocalDispatch);
}

public sealed class EnvironmentWorkerSandbox : IWorkerSandbox
{
    public WorkerSandboxOptions Options { get; } = WorkerSandboxOptions.FromEnvironment();

    public bool ShouldConfine(AgentRole role, bool isLocalDispatch) =>
        Options.Enabled && !isLocalDispatch && role is AgentRole.Developer or AgentRole.Tester;
}

/// <summary>
/// Configuration for the OS-level worker sandbox: when enabled (and on Windows), write-capable
/// worker dispatches (Developer/Tester) run at Low integrity as the operator account. Mandatory
/// Integrity Control confines writes to the Low-labeled goal worktree and sandbox scratch; linked
/// worktree git metadata under the shared repository .git stays medium integrity and is committed
/// by the orchestrator after verified edits. Gated by environment so it is off by default and a
/// no-op on non-Windows hosts.
/// </summary>
public sealed record WorkerSandboxOptions(
    bool Enabled,
    string Account,
    string CredentialTarget)
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
