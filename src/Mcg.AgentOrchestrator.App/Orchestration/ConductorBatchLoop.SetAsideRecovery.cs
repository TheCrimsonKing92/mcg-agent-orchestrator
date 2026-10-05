using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorBatchLoop
{
    private const int AdvanceFaultReadmitPassThreshold = 3;

    private static void SetAsideDependencyEscalated(
        AgentOrchestratorKernel kernel,
        ConductorDriver driver,
        Goal goal,
        string holdReason,
        Dictionary<string, BatchSetAsideEntry> setAsideGoals,
        Dictionary<string, BatchSetAsideEntry> selfClearedSetAsideEntries,
        HashSet<string> dependencyEscalatedGoals)
    {
        SetAside(kernel, driver, goal, BatchSetAsideCondition.DependencyEscalated,
            setAsideGoals, selfClearedSetAsideEntries);
        dependencyEscalatedGoals.Add(goal.Id.Value);
        var dependency = holdReason[(holdReason.IndexOf(": ", StringComparison.Ordinal) + 2)..].Split(' ')[0];
        setAsideGoals[goal.Id.Value] = setAsideGoals[goal.Id.Value] with { EscalatedDependency = dependency };
    }

    private static void SetAsideAdvanceFault(
        AgentOrchestratorKernel kernel,
        ConductorDriver driver,
        Goal goal,
        string fingerprint,
        Dictionary<string, BatchSetAsideEntry> setAsideGoals,
        Dictionary<string, BatchSetAsideEntry> selfClearedSetAsideEntries)
    {
        SetAside(kernel, driver, goal, BatchSetAsideCondition.AdvanceFault,
            setAsideGoals, selfClearedSetAsideEntries);
        setAsideGoals[goal.Id.Value] = setAsideGoals[goal.Id.Value] with { FaultFingerprint = fingerprint };
    }

    private static void ReadmitRecoveredSetAsideGoals(
        AgentOrchestratorKernel kernel,
        string? onlyGoalId,
        Dictionary<string, BatchSetAsideEntry> setAsideGoals,
        HashSet<string> completedGoals,
        HashSet<string> escalatedGoals,
        HashSet<string> dependencyEscalatedGoals,
        HashSet<string> reapedGoals,
        HashSet<(string GoalId, string Fingerprint)> advanceFaultRetries,
        GoalProjectionCache goalProjectionCache)
    {
        dependencyEscalatedGoals.RemoveWhere(id =>
            !setAsideGoals.TryGetValue(id, out var entry) || entry.Condition != BatchSetAsideCondition.DependencyEscalated);
        foreach (var entry in setAsideGoals.Values.ToArray())
        {
            if (onlyGoalId is not null && entry.GoalId != onlyGoalId)
                continue;
            var goal = kernel.Goals.FirstOrDefault(g => g.Id.Value == entry.GoalId);
            if (goal is null || IsTerminalGoal(goal))
                continue;

            string decision;
            string progress;
            if (entry.Condition == BatchSetAsideCondition.DependencyEscalated)
            {
                var holdReason = GetDependencyHoldReason(goal, completedGoals, escalatedGoals, kernel,
                    out _, dependencyEscalatedGoals);
                if (holdReason is not null &&
                    (holdReason.StartsWith("dependency escalated", StringComparison.Ordinal) ||
                     holdReason.StartsWith("dependency-terminal-without-landing", StringComparison.Ordinal)))
                    continue;
                decision = $"condition=dependency-escalated; dependency={entry.EscalatedDependency}; hold={holdReason ?? "none"}.";
                progress = $"condition=dependency-escalated goal={entry.GoalId[..8]} dependency={entry.EscalatedDependency} reason={SanitizeReason(holdReason ?? "none")}";
            }
            else if (entry.Condition == BatchSetAsideCondition.AdvanceFault && entry.FaultFingerprint is not null)
            {
                // Count every loop pass, including blocked rechecks where totalTicks does not advance.
                var passes = Math.Min(entry.ReadmitPasses + 1, AdvanceFaultReadmitPassThreshold);
                setAsideGoals[entry.GoalId] = entry with { ReadmitPasses = passes };
                if (passes < AdvanceFaultReadmitPassThreshold ||
                    !advanceFaultRetries.Add((entry.GoalId, entry.FaultFingerprint)))
                    continue;
                decision = $"condition=advance-fault; fingerprint={entry.FaultFingerprint}; passes={passes}.";
                progress = $"condition=advance-fault goal={entry.GoalId[..8]} fingerprint={entry.FaultFingerprint} ticks={passes}";
            }
            else
            {
                continue;
            }

            setAsideGoals.Remove(entry.GoalId);
            escalatedGoals.Remove(entry.GoalId);
            dependencyEscalatedGoals.Remove(entry.GoalId);
            reapedGoals.Remove(entry.GoalId);
            goalProjectionCache.Invalidate(goal.Id);
            kernel.RecordGoalPolicyDecision(goal.Id, $"Set-aside re-admitted: {decision}");
            EmitProgress($"SET_ASIDE_READMITTED {progress}");
        }
    }
}

internal enum BatchSetAsideCondition
{
    AwaitingClarification,
    DependencyEscalated,
    AdvanceFault,
    LifecycleEscalation,
    PreLandingRebaseConflict
}

internal sealed record BatchSetAsideEntry(
    string GoalId,
    BatchSetAsideCondition Condition,
    string StateFingerprint,
    string? LastSelfClearEvidenceFingerprint = null,
    string? SweepBlockerKind = null,
    string? SweepBlockerFingerprint = null,
    string? FaultFingerprint = null,
    int ReadmitPasses = 0,
    string? EscalatedDependency = null);
