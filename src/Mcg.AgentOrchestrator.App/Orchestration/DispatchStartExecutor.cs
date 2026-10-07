using System.Diagnostics;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record DispatchStartExecution(DispatchStartOutcome Outcome, string? RecoveryFailureReason);

internal sealed record DispatchStartTiming(
    string Phase, Goal Goal, DispatchStartOutcome? Outcome, TimeSpan Elapsed, string Detail);

internal static class DispatchStartExecutor
{
    internal const string PrepPhase = "dispatch-prep";
    internal const string RemediationPhase = "dispatch-remediation";

    internal static DispatchStartExecution Execute(
        Goal goal,
        string goalPrefix,
        ConductorAutonomyPolicy policy,
        GoalLifecycleState fromState,
        Func<Goal, ConductorAutonomyPolicy, DispatchStartOutcome> dispatchAndStart,
        Func<Goal, ConductorAutonomyPolicy, DispatchStartOutcome> startRecordedDispatches,
        Func<WorkerSandboxPrepRecoverableAction, bool> recoverSandboxPrep,
        Func<string> runRemediation,
        Func<Goal> readCurrentGoal,
        Action<DispatchStartTiming> reportTiming)
    {
        var start = fromState == GoalLifecycleState.Dispatched ? startRecordedDispatches : dispatchAndStart;
        var dispatchTimingGoal = goal;
        var startClock = ConductorBatchLoop.StartDiagnosticTimer();
        var outcome = start(goal, policy);
        startClock.Stop();
        reportTiming(new(PrepPhase, dispatchTimingGoal, outcome, startClock.Elapsed, $"result={outcome.Category}"));
        goal = readCurrentGoal();
        if (outcome.Category == DispatchStartOutcomeCategory.RecoverableSandboxPrep)
        {
            if (!TryRecoverSandboxPrep(outcome, goalPrefix, recoverSandboxPrep, out var recoveryFailure))
            {
                return new(outcome, recoveryFailure);
            }

            var retryStart = fromState == GoalLifecycleState.WorkspaceReady
                ? startRecordedDispatches
                : start;
            startClock.Restart();
            outcome = retryStart(goal, policy);
            startClock.Stop();
            reportTiming(new(PrepPhase, dispatchTimingGoal, outcome, startClock.Elapsed, $"result={outcome.Category} retry=sandbox-prep"));
            goal = readCurrentGoal();
        }

        if (outcome.Category == DispatchStartOutcomeCategory.SpawnFailed)
        {
            var firstFailure = outcome;
            var remediationClock = Stopwatch.StartNew();
            var remediationResult = runRemediation();
            remediationClock.Stop();
            reportTiming(new(RemediationPhase, goal, null, remediationClock.Elapsed, $"result={remediationResult}"));
            var retryStart = fromState == GoalLifecycleState.WorkspaceReady
                ? startRecordedDispatches
                : start;
            startClock.Restart();
            outcome = retryStart(goal, policy);
            startClock.Stop();
            reportTiming(new(PrepPhase, dispatchTimingGoal, outcome, startClock.Elapsed, $"result={outcome.Category} retry=spawn-failed"));
            goal = readCurrentGoal();
            if (outcome.Category == DispatchStartOutcomeCategory.EmptyBatch)
            {
                outcome = firstFailure;
            }
        }

        return new(outcome, null);
    }

    private static bool TryRecoverSandboxPrep(
        DispatchStartOutcome outcome,
        string goalPrefix,
        Func<WorkerSandboxPrepRecoverableAction, bool> recoverSandboxPrep,
        out string failureReason)
    {
        if (outcome.SandboxPrepRecoveryAction is not { } action)
        {
            failureReason = outcome.Reason ?? "Low-IL sandbox prep recovery action was missing.";
            return false;
        }

        try
        {
            if (recoverSandboxPrep(action))
            {
                failureReason = string.Empty;
                return true;
            }
        }
        catch (Exception ex)
        {
            failureReason = $"Low-IL sandbox prep recovery failed for goal {goalPrefix}: {ex.Message}";
            return false;
        }

        failureReason = $"Low-IL sandbox prep recovery failed for goal {goalPrefix}: {action.Reason}";
        return false;
    }
}
