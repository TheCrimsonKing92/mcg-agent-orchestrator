using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

public enum AutonomyPolicyKind
{
    Observe,
    SafeAuto,
    SupervisedAuto
}

public enum AutonomyAction
{
    DispatchStart,
    ModelRun,
    Refresh,
    Retry,
    ProviderFailover,
    BuildTest,
    Acceptance,
    WorkspaceCleanup,
    BacklogLogEdit
}

public sealed record AutonomyPolicy(
    AutonomyPolicyKind Kind,
    string Name,
    string Description,
    bool AllowsDispatchStart,
    bool AllowsModelRun,
    bool AllowsRefresh,
    bool AllowsRetry,
    bool AllowsProviderFailover,
    bool AllowsBuildTest,
    bool AllowsAcceptance,
    bool AllowsWorkspaceCleanup,
    bool AllowsBacklogLogEdit)
{
    public static AutonomyPolicy Default => SupervisedAuto;

    public static AutonomyPolicy Observe { get; } = new(
        AutonomyPolicyKind.Observe,
        "observe",
        "Read and report state only; do not start workers, invoke models, verify, merge, clean up, or edit operator records.",
        AllowsDispatchStart: false,
        AllowsModelRun: false,
        AllowsRefresh: true,
        AllowsRetry: false,
        AllowsProviderFailover: false,
        AllowsBuildTest: false,
        AllowsAcceptance: false,
        AllowsWorkspaceCleanup: false,
        AllowsBacklogLogEdit: false);

    public static AutonomyPolicy SafeAuto { get; } = new(
        AutonomyPolicyKind.SafeAuto,
        "safe-auto",
        "Allow reversible goal progress such as starts, refreshes, retries, failover, and verification; block merge, cleanup, and backlog/log edits.",
        AllowsDispatchStart: true,
        AllowsModelRun: true,
        AllowsRefresh: true,
        AllowsRetry: true,
        AllowsProviderFailover: true,
        AllowsBuildTest: true,
        AllowsAcceptance: false,
        AllowsWorkspaceCleanup: false,
        AllowsBacklogLogEdit: false);

    public static AutonomyPolicy SupervisedAuto { get; } = new(
        AutonomyPolicyKind.SupervisedAuto,
        "supervised-auto",
        "Allow the full supervised lifecycle after explicit command/API confirmations, including verification, acceptance, cleanup, and operator record edits.",
        AllowsDispatchStart: true,
        AllowsModelRun: true,
        AllowsRefresh: true,
        AllowsRetry: true,
        AllowsProviderFailover: true,
        AllowsBuildTest: true,
        AllowsAcceptance: true,
        AllowsWorkspaceCleanup: true,
        AllowsBacklogLogEdit: true);

    public static IReadOnlyList<AutonomyPolicy> All { get; } = [Observe, SafeAuto, SupervisedAuto];

    public bool Allows(AutonomyAction action) => action switch
    {
        AutonomyAction.DispatchStart => AllowsDispatchStart,
        AutonomyAction.ModelRun => AllowsModelRun,
        AutonomyAction.Refresh => AllowsRefresh,
        AutonomyAction.Retry => AllowsRetry,
        AutonomyAction.ProviderFailover => AllowsProviderFailover,
        AutonomyAction.BuildTest => AllowsBuildTest,
        AutonomyAction.Acceptance => AllowsAcceptance,
        AutonomyAction.WorkspaceCleanup => AllowsWorkspaceCleanup,
        AutonomyAction.BacklogLogEdit => AllowsBacklogLogEdit,
        _ => false
    };

    public static AutonomyPolicy Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Default;
        }

        var normalized = value.Trim();
        return All.FirstOrDefault(policy =>
            policy.Name.Equals(normalized, StringComparison.OrdinalIgnoreCase) ||
            policy.Kind.ToString().Equals(normalized, StringComparison.OrdinalIgnoreCase)) ??
            throw new ArgumentException($"Unknown autonomy policy '{value}'. Use observe, safe-auto, or supervised-auto.");
    }

    public void ThrowIfDisallowed(AutonomyAction action, string operation)
    {
        if (Allows(action))
        {
            return;
        }

        throw new InvalidOperationException(
            $"Autonomy policy '{Name}' blocks {operation} ({Display(action)}). Use --autonomy {SupervisedAuto.Name} for supervised operator-approved lifecycle actions.");
    }

    public string DecisionMessage(AutonomyAction action, string operation, bool allowed) =>
        $"Autonomy policy {Name} {(allowed ? "allowed" : "blocked")} {operation} ({Display(action)}).";

    private static string Display(AutonomyAction action) => action switch
    {
        AutonomyAction.DispatchStart => "dispatch start",
        AutonomyAction.ModelRun => "model run",
        AutonomyAction.Refresh => "refresh",
        AutonomyAction.Retry => "retry",
        AutonomyAction.ProviderFailover => "provider failover",
        AutonomyAction.BuildTest => "build/test",
        AutonomyAction.Acceptance => "acceptance",
        AutonomyAction.WorkspaceCleanup => "workspace cleanup",
        AutonomyAction.BacklogLogEdit => "backlog/log edit",
        _ => action.ToString()
    };
}

public static class AutonomyPolicyEvidence
{
    public static void Record(
        AgentOrchestratorKernel kernel,
        Goal goal,
        AutonomyPolicy policy,
        AutonomyAction action,
        string operation,
        bool allowed)
    {
        kernel.RecordGoalPolicyDecision(goal.Id, policy.DecisionMessage(action, operation, allowed));
    }
}
