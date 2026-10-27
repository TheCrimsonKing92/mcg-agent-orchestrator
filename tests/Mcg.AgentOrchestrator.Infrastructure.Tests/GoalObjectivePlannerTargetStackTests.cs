using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: pure planner calls with explicit inputs and no shared mutable state.
public sealed class GoalObjectivePlannerTargetStackTests
{
    private const string CrosswalkObjective =
        "Make the vitest fixture src/view.test.ts::renders parse into the summary and add test coverage";
    private const string DocsObjective = "Inspect docs/usage.md and summarize current behavior";

    private static readonly string[] ToolObjectives =
    [
        "Implement src/App/Handler.cs with dotnet tests",
        "Fix the npm build in src/view.ts",
        "Fix the pytest suite in tests/test_parse.py",
        "Fix the golang build in cmd/server/main.go"
    ];

    [Fact]
    public void Build_CrosswalkFixture_UsesGoStackAndPreservesNoStackInference()
    {
        var targetPlan = GoalObjectivePlanner.Build(CrosswalkObjective, null, null, Toolchain.Go);
        var baseline = GoalObjectivePlanner.Build(CrosswalkObjective, null, null);

        Assert.Equal(new[] { "rg", "go build ./..., go vet ./..., go test ./..." }, targetPlan.RequiredTools);
        Assert.Equal(new[] { "rg", "run the project's build and test commands" }, baseline.RequiredTools);
    }

    public static IEnumerable<object[]> ToolOverrideCases() =>
        from objective in ToolObjectives
        from stack in Enum.GetValues<Toolchain>()
        select new object[] { objective, stack };

    [Theory]
    [MemberData(nameof(ToolOverrideCases))]
    public void Build_KnownStack_OverridesObjectiveToolSignals(string objective, Toolchain stack)
    {
        var plan = GoalObjectivePlanner.Build(objective, null, null, stack);
        var expected = stack == Toolchain.Go
            ? GoalIntakeTargetStack.GoBuildTestTool
            : GoalIntakeTargetStack.NeutralBuildTestTool;

        Assert.Equal(new[] { "rg", expected }, plan.RequiredTools);
        Assert.DoesNotContain(plan.RequiredTools, tool => tool.Contains("Invoke-TestSummary", StringComparison.Ordinal));
        Assert.DoesNotContain(plan.RequiredTools, tool => tool.Contains("npm/yarn", StringComparison.Ordinal));
        Assert.DoesNotContain(plan.RequiredTools, tool => tool.Contains("python / pytest", StringComparison.Ordinal));
    }

    public static IEnumerable<object[]> PlanPreservationCases() =>
        from objective in ToolObjectives.Prepend(CrosswalkObjective).Append(DocsObjective)
        from stack in Enum.GetValues<Toolchain>()
        select new object[] { objective, stack };

    [Theory]
    [MemberData(nameof(PlanPreservationCases))]
    public void Build_TargetStack_PreservesOtherFieldsAndToolInclusion(string objective, Toolchain stack)
    {
        var baseline = GoalObjectivePlanner.Build(objective, null, null);
        var plan = GoalObjectivePlanner.Build(objective, null, null, stack);

        Assert.Equal(baseline.FileScopes, plan.FileScopes);
        Assert.Equal(baseline.RiskLabels, plan.RiskLabels);
        Assert.Equal(baseline.RequiredVerification, plan.RequiredVerification);
        Assert.Equal(baseline.Workflow, plan.Workflow);
        Assert.Equal(baseline.Disposition, plan.Disposition);
        Assert.Equal(
            baseline.TaskBoundaries.Select(boundary => (boundary.Role, boundary.Verification)).ToArray(),
            plan.TaskBoundaries.Select(boundary => (boundary.Role, boundary.Verification)).ToArray());
        if (objective == DocsObjective)
            Assert.Equal(baseline.RequiredTools, plan.RequiredTools);
    }
}
