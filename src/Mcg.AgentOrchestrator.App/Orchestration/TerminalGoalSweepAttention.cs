using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class TerminalGoalSweepAttention
{
    private const string CorrelationKeyPrefix = "terminal-sweep-blocker:";

    public static int Surface(
        AgentOrchestratorKernel kernel,
        TerminalGoalSweepResult result,
        string orchestratorDirectory,
        GoalId? onlyGoalId = null,
        CancellationToken cancellationToken = default)
    {
        return SurfaceAsync(
            kernel,
            result,
            CollaborationItemStore.ForDirectory(orchestratorDirectory),
            onlyGoalId,
            cancellationToken).GetAwaiter().GetResult();
    }

    internal static async Task<int> SurfaceAsync(
        AgentOrchestratorKernel kernel,
        TerminalGoalSweepResult result,
        ICollaborationItemStore store,
        GoalId? onlyGoalId = null,
        CancellationToken cancellationToken = default)
    {
        var scopedGoalIds = ResolveScopedGoalIds(kernel, onlyGoalId);
        if (scopedGoalIds.Count == 0)
        {
            return 0;
        }

        var activeKeys = new HashSet<string>(StringComparer.Ordinal);
        var sweptGoalIds = result.ExplicitlySweptGoalIds
            .Select(goalId => goalId.Value)
            .Where(scopedGoalIds.Contains)
            .ToHashSet(StringComparer.Ordinal);
        var changes = 0;

        foreach (var goalResult in result.Goals)
        {
            if (!scopedGoalIds.Contains(goalResult.GoalId.Value))
            {
                continue;
            }

            foreach (var blocker in goalResult.Blockers.Where(HasConcreteRemediationCommand))
            {
                if (ReconcileSweepRemediationCoordinator.IsAwaitingConductorAcceptanceGate(blocker))
                {
                    continue;
                }

                var correlationKey = BuildCorrelationKey(goalResult.GoalId, blocker.Kind);
                activeKeys.Add(correlationKey);
                await store.RaiseAsync(
                    CollaborationItemType.Decision,
                    goalResult.GoalId.Value,
                    $"Sweep blocker: {goalResult.GoalPrefix} {blocker.Kind}",
                    BuildBody(goalResult, blocker),
                    correlationKey,
                    cancellationToken);
                changes++;
            }
        }

        var items = await store.ListForGoalIdsAsync(sweptGoalIds, cancellationToken);
        foreach (var item in items)
        {
            if (item.CorrelationKey is not { Length: > 0 } correlationKey ||
                !correlationKey.StartsWith(CorrelationKeyPrefix, StringComparison.Ordinal) ||
                activeKeys.Contains(correlationKey))
            {
                continue;
            }

            if (await store.TryResolveAsync(correlationKey, "terminal sweep blocker resolved", cancellationToken))
            {
                changes++;
            }
        }

        return changes;
    }

    private static HashSet<string> ResolveScopedGoalIds(AgentOrchestratorKernel kernel, GoalId? onlyGoalId)
    {
        return onlyGoalId is { } goalId
            ? new HashSet<string>([goalId.Value], StringComparer.Ordinal)
            : new HashSet<string>(kernel.Goals.Select(goal => goal.Id.Value), StringComparer.Ordinal);
    }

    private static bool HasConcreteRemediationCommand(TerminalGoalSweepBlocker blocker)
    {
        return !string.IsNullOrWhiteSpace(blocker.Command) &&
            !blocker.Command.Equals("excluded", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildCorrelationKey(GoalId goalId, string kind) =>
        $"{CorrelationKeyPrefix}{goalId.Value}:{kind}";

    private static string BuildBody(TerminalGoalSweepGoalResult goal, TerminalGoalSweepBlocker blocker)
    {
        var evidenceLine = FormatEvidenceLine(goal, blocker);
        return string.Join(Environment.NewLine, [
            $"Goal: {goal.GoalPrefix}",
            $"Blocker kind: {blocker.Kind}",
            $"Evidence line: {evidenceLine}",
            $"Command: {blocker.Command}"
        ]);
    }

    private static string FormatEvidenceLine(TerminalGoalSweepGoalResult goal, TerminalGoalSweepBlocker blocker) =>
        $"SWEEP_BLOCKER goal={goal.GoalPrefix} kind={blocker.Kind} evidence=\"{blocker.Evidence}\" command=\"{blocker.Command}\"";
}
