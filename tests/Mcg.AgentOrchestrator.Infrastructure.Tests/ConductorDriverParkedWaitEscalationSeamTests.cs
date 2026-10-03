using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Xunit;

// Parallel-safe: lifecycle facts and all effects are local, injected delegates.
public sealed class ConductorDriverParkedWaitEscalationSeamTests
{
    [Fact(DisplayName = "Every advance resolves attention using the current clarification state")]
    public void AnsweredClarificationCallsResolverWithoutEscalatingAgain()
    {
        var (_, goal) = ConductorDriverTests.SimpleGoal();
        var open = true;
        var calls = new List<(string Kind, Goal Goal, GoalLifecycleState State)>();
        var driver = MakeDriver(
            _ => new GoalLifecycleFacts(WorkspaceExists: true, HasOpenClarification: open),
            (g, state) => calls.Add(("resolve", g, state)),
            (g, state, _) => calls.Add(("escalate", g, state)));

        var parked = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        Assert.IsType<ConductorAdvanceOutcome.Escalated>(parked.Outcome);
        Assert.Equal(new[]
        {
            ("resolve", goal, GoalLifecycleState.AwaitingClarification),
            ("escalate", goal, GoalLifecycleState.AwaitingClarification)
        }, calls);

        open = false; // The Author's answer closes the clarification fact used by ResolveState.
        var resumed = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        Assert.IsType<ConductorAdvanceOutcome.Executed>(resumed.Outcome);
        Assert.Equal(3, calls.Count);
        Assert.Equal(("resolve", goal, GoalLifecycleState.WorkspaceReady), calls[2]);
        Assert.Same(goal, calls[2].Goal);
    }

    [Fact(DisplayName = "Attention resolution failure leaves the goal advance available")]
    public void ResolverFailureDoesNotPreventAdvance()
    {
        var (_, goal) = ConductorDriverTests.SimpleGoal();
        var invoked = false;
        var driver = MakeDriver(_ => GoalLifecycleFacts.None, (_, _) =>
        {
            invoked = true;
            throw new InvalidOperationException("item store unavailable");
        });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        Assert.True(invoked);
        Assert.Equal(GoalLifecycleState.Created, Assert.IsType<ConductorAdvanceOutcome.Executed>(result.Outcome).FromState);
    }

    private static ConductorDriver MakeDriver(
        Func<Goal, GoalLifecycleFacts> getFacts,
        Action<Goal, GoalLifecycleState> resolve,
        Action<Goal, GoalLifecycleState, string>? escalate = null) => new(
            getFacts: getFacts,
            getRunningPaidWorkerCount: () => 0,
            createWorkspace: _ => "injected workspace",
            dispatchAndStart: _ => DispatchStartOutcome.Started(),
            startRecordedDispatches: null,
            buildServerShutdown: null,
            runAcceptanceVerification: _ => AcceptanceVerificationSummary.PassedWithNoUnmetCriteria,
            runAdvisorySemanticAcceptance: null,
            retryTask: null,
            recordTaskNote: null,
            recordCriterionRetryFeedback: null,
            clearCriterionRetryFeedback: null,
            rebaseOntoMain: _ => throw new InvalidOperationException("Unexpected rebase"),
            land: (_, _) => throw new InvalidOperationException("Unexpected landing"),
            afterSuccessfulLanding: null,
            record: _ => throw new InvalidOperationException("Unexpected record"),
            cleanup: _ => throw new InvalidOperationException("Unexpected cleanup"),
            writeEscalation: escalate ?? ((_, _, _) => throw new InvalidOperationException("Unexpected escalation")),
            classifyChangeRisk: _ => null,
            resolveParkedWaitEscalations: resolve);
}
