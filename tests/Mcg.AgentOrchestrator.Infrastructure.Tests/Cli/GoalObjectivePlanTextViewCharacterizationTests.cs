using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Xunit;

// Parallel-safe: AsyncLocal console capture and immutable, local fixtures.
// Expected JSON follows ConsoleViews.GoalObjectivePlan.cs at base 8711cbf2f;
// literals pin property order, casing, nulls and enum representation independently of serialization.
public sealed class GoalObjectivePlanTextViewCharacterizationTests
{
    [Fact]
    public void Print_ReadyPlanWithTaskBoundaries_PreservesExactText()
    {
        var plan = new GoalObjectivePlan("Update CLI", "scout", GoalObjectiveDisposition.Ready,
            TaskComplexity.Complex, "12 minutes", new(0.5, 0.25, 0.125, 0.125, 0),
            ["shared surface"], ["review required"],
            new(GoalIntakePipeline.Scout, false, ["complex objective", "review coverage"]),
            ["src/view.cs"], ["git"], ["focused tests"],
            [new(1, AgentRole.Developer, "Move view", "edit", "build"),
             new(2, AgentRole.Tester, "Pin output", "test", "exact text")],
            "Create goal");

        var actual = AsyncLocalConsoleRouter.Capture(() =>
            GoalObjectivePlanTextView.PrintGoalObjectivePlan(plan));

        Assert.Equal(string.Join(Environment.NewLine,
            "Goal objective plan:",
            """{"objective":"Update CLI","workflow":"scout","disposition":"Ready","estimatedComplexity":"Complex","historicalTimeEstimate":"12 minutes","historicalOutcomeRates":{"aggregateFailureRate":0.5,"realFailureRate":0.25,"environmentalFailureRate":0.125,"manufacturedFixedFailureRate":0.125,"unknownEraFailureRate":0},"riskLabels":["shared surface"],"capabilityWarnings":["review required"],"pipelineDecision":{"pipeline":"Scout","workflow":"scout","isOverride":false,"selectionSource":"automatic","orderedRoles":["Developer","Tester"],"reasons":["complex objective","review coverage"]},"fileScopes":["src/view.cs"],"requiredTools":["git"],"requiredVerification":["focused tests"],"taskBoundaries":[{"index":1,"role":"Developer","purpose":"Move view","capability":"edit","verification":"build"},{"index":2,"role":"Tester","purpose":"Pin output","capability":"test","verification":"exact text"}],"canCreateGoal":true,"recommendation":"Create goal"}""",
            ""), actual);
    }

    [Fact]
    public void Print_ClarificationWithoutHistoryOrTasks_PreservesExactText()
    {
        var plan = new GoalObjectivePlan("Clarify scope", "developer-only",
            GoalObjectiveDisposition.NeedsClarification, TaskComplexity.Simple, null, null, [], [],
            new(GoalIntakePipeline.DeveloperOnly, true, []), [], [], [], [], "Ask owner");

        var actual = AsyncLocalConsoleRouter.Capture(() =>
            GoalObjectivePlanTextView.PrintGoalObjectivePlan(plan));

        Assert.Equal(string.Join(Environment.NewLine,
            "Goal objective plan:",
            """{"objective":"Clarify scope","workflow":"developer-only","disposition":"NeedsClarification","estimatedComplexity":"Simple","historicalTimeEstimate":null,"historicalOutcomeRates":null,"riskLabels":[],"capabilityWarnings":[],"pipelineDecision":{"pipeline":"DeveloperOnly","workflow":"developer-only","isOverride":true,"selectionSource":"explicitly-required","orderedRoles":[],"reasons":[]},"fileScopes":[],"requiredTools":[],"requiredVerification":[],"taskBoundaries":[],"canCreateGoal":false,"recommendation":"Ask owner"}""",
            ""), actual);
    }
}
