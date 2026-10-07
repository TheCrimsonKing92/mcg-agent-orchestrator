using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;

using static ConductorDriverTests;

[Xunit.Collection("IsolatedProcessSpawning")]
public sealed class DispatchStartDecisionCoverageTests
{
    [Xunit.Theory]
    [Xunit.InlineData(nameof(DispatchStartOutcomeCategory.Started))]
    [Xunit.InlineData(nameof(DispatchStartOutcomeCategory.Deferred))]
    [Xunit.InlineData(nameof(DispatchStartOutcomeCategory.EmptyBatch))]
    [Xunit.InlineData(nameof(DispatchStartOutcomeCategory.SpawnFailed))]
    public void AdvanceOnce_StartOutcome_RecordsAttributedCategory(string categoryName)
    {
        var category = Enum.Parse<DispatchStartOutcomeCategory>(categoryName);
        var (_, goal) = SimpleGoal();
        var startCalls = 0;
        var readinessCalls = 0;
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            dispatchAndStart: _ =>
            {
                startCalls++;
                return category switch
                {
                    DispatchStartOutcomeCategory.Started => DispatchStartOutcome.Started(),
                    DispatchStartOutcomeCategory.Deferred => DispatchStartOutcome.Deferred("Spec refinement pending"),
                    DispatchStartOutcomeCategory.EmptyBatch => DispatchStartOutcome.EmptyBatch("No tasks in ready batch"),
                    DispatchStartOutcomeCategory.SpawnFailed => DispatchStartOutcome.SpawnFailed("spawn failed"),
                    _ => throw new InvalidOperationException($"Unexpected test category {category}.")
                };
            },
            evaluateReadiness: _ =>
            {
                readinessCalls++;
                return new DispatchReadinessDeferred(
                    new DateTimeOffset(2026, 10, 4, 1, 0, 0, TimeSpan.Zero), "Provider retry pending");
            },
            writeEscalation: (_, _, _) => { });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        Assert.Equal(category == DispatchStartOutcomeCategory.SpawnFailed ? 2 : 1, startCalls);
        Assert.Equal(category == DispatchStartOutcomeCategory.EmptyBatch ? 1 : 0, readinessCalls);
        var payload = VerifiedAcceptanceEscalationDecision.BuildTickOutcomePayload(result.Outcome);
        var decision = Assert.IsType<PolicyDecisionRecord>(payload.Decision);
        Assert.Equal("dispatch-start", decision.Stage);
        Assert.Equal(category.ToString(), Assert.Single(decision.Facts, fact => fact.Name == "startOutcomeCategory").Value);
    }

    [Xunit.Fact]
    public void Driver_DeferredStart_DoesNotBypassPolicy()
    {
        var driverPath = Path.Combine(VerifiedRepositoryRoot.Find(),
            "src", "Mcg.AgentOrchestrator.App", "Orchestration", "ConductorDriver.cs");

        Assert.DoesNotContain("new ConductorAdvanceOutcome.Held(fromState, outcome.Reason",
            File.ReadAllText(driverPath), StringComparison.Ordinal);
    }
}
