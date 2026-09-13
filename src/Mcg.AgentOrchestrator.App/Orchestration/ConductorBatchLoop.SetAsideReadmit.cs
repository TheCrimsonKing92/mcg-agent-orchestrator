using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorBatchLoop
{
    private static BatchSetAsideCondition GetSetAsideCondition(ConductorAdvanceResult result) =>
        result.Outcome switch
        {
            ConductorAdvanceOutcome.Escalated { State: GoalLifecycleState.AwaitingClarification } =>
                BatchSetAsideCondition.AwaitingClarification,
            ConductorAdvanceOutcome.Escalated
            {
                State: GoalLifecycleState.Verified,
                Reason: var reason
            } when reason.StartsWith("pre-landing rebase conflict", StringComparison.OrdinalIgnoreCase) =>
                BatchSetAsideCondition.PreLandingRebaseConflict,
            _ => BatchSetAsideCondition.LifecycleEscalation
        };

    private static void SetAside(
        AgentOrchestratorKernel kernel,
        ConductorDriver driver,
        Goal goal,
        BatchSetAsideCondition condition,
        Dictionary<string, BatchSetAsideEntry> setAsideGoals,
        Dictionary<string, BatchSetAsideEntry>? selfClearedSetAsideEntries = null,
        TerminalGoalSweepResult? sweepResult = null)
    {
        goal = kernel.GetGoal(goal.Id);
        BatchSetAsideEntry? selfClearedEntry = null;
        if (selfClearedSetAsideEntries is not null)
        {
            selfClearedSetAsideEntries.TryGetValue(goal.Id.Value, out selfClearedEntry);
        }
        var lastSelfClearEvidenceFingerprint =
            condition is BatchSetAsideCondition.PreLandingRebaseConflict or BatchSetAsideCondition.LifecycleEscalation
                ? selfClearedEntry?.LastSelfClearEvidenceFingerprint
                : null;
        var sweepBlocker = condition == BatchSetAsideCondition.LifecycleEscalation
            ? SelectControllingSweepBlocker(sweepResult?.Goals
                .Where(result => result.GoalId == goal.Id)
                .SelectMany(result => result.Blockers) ?? [])
            : null;
        selfClearedSetAsideEntries?.Remove(goal.Id.Value);
        setAsideGoals[goal.Id.Value] = new BatchSetAsideEntry(
            goal.Id.Value,
            condition,
            BuildEscalatedGoalStateFingerprint(goal),
            lastSelfClearEvidenceFingerprint,
            sweepBlocker?.Kind,
            sweepBlocker is null
                ? null
                : BuildSweepBlockerFingerprint(sweepBlocker));
    }

    private static TerminalGoalSweepBlocker? SelectControllingSweepBlocker(
        IEnumerable<TerminalGoalSweepBlocker> blockers) =>
        blockers
            .OrderBy(blocker => blocker.Remedy.SafetyClass == TerminalGoalRemedySafetyClass.OperatorOnly ? 0 : 1)
            .ThenBy(blocker => blocker.Kind, StringComparer.Ordinal)
            .ThenBy(blocker => blocker.Evidence, StringComparer.Ordinal)
            .ThenBy(blocker => blocker.Command, StringComparer.Ordinal)
            .FirstOrDefault();

    private static string BuildSweepBlockerFingerprint(TerminalGoalSweepBlocker blocker) =>
        $"{blocker.Kind}\n{blocker.Evidence}\n{blocker.Command}";

    private static string BuildEscalatedGoalStateFingerprint(Goal goal)
    {
        var taskParts = goal.Tasks
            .OrderBy(task => task.Id.Value, StringComparer.Ordinal)
            .Select(task =>
                string.Join(
                    ":",
                    new[]
                    {
                    task.Id.Value,
                    $"candidate={task.LastDispatch?.ResultCommit?.Trim() ?? task.LastVerification?.ReviewedCommit?.Trim() ?? "none"}",
                    task.Status.ToString(),
                    task.LastDispatch is null ? "dispatch=none" : $"dispatch={task.LastDispatch.DispatchedAt.UtcTicks}:{task.LastDispatch.WorkerName}",
                    task.LatestRetryAt is null ? "retry=none" : $"retry={task.LatestRetryAt.Value.UtcTicks}"
                    }));

        return string.Join("|", taskParts);
    }

    private static string TryResolveLifecycleState(ConductorDriver driver, Goal goal)
    {
        try
        {
            return GoalLifecycle.ResolveState(goal, driver.GetFacts(goal)).ToString();
        }
        catch
        {
            return "LifecycleState=unknown";
        }
    }

    private static string TryResolveLifecycleState(
        GoalProjectionCache goalProjectionCache,
        ConductorDriver driver,
        Goal goal)
    {
        try
        {
            return goalProjectionCache.ResolveState(goal, driver).ToString();
        }
        catch
        {
            return "LifecycleState=unknown";
        }
    }
}
