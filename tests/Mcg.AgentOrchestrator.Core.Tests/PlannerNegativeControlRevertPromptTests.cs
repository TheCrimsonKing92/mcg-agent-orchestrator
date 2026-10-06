using Mcg.AgentOrchestrator.Core;

// Parallel-safe: only builds and compares immutable requirement text.
public sealed class PlannerNegativeControlRevertPromptTests
{
    private const string Rule = "- Only when a criterion needs a fail-on-main proof and the implementation lives under tests/, config/ or .agents/, add one plan line `negative-control-revert: <comma-separated repository-relative paths>` naming those implementation files; never name a file that declares a test class the criterion selects.";

    [Theory]
    [MemberData(nameof(TesterPromptNoWorkerTestExecutionTests.PromptVariants),
        MemberType = typeof(TesterPromptNoWorkerTestExecutionTests))]
    public void PlannerVariants_ContainRuleOnceImmediatelyBeforeAnchor(
        TaskComplexity? complexity, bool? includeHighRiskContract)
    {
        var lines = BuildVariant(AgentRole.Planner, complexity, includeHighRiskContract)
            .Split(Environment.NewLine);

        Assert.Single(lines, line => line == Rule);
        var anchorIndex = Array.FindIndex(lines, line =>
            line.StartsWith("- Do not modify repository files", StringComparison.Ordinal));
        Assert.True(anchorIndex > 0);
        Assert.Equal(Rule, lines[anchorIndex - 1]);
    }

    [Theory]
    [MemberData(nameof(TesterPromptNoWorkerTestExecutionTests.PromptVariants),
        MemberType = typeof(TesterPromptNoWorkerTestExecutionTests))]
    public void OtherRoleVariants_DoNotContainPlannerDeclarationInstruction(
        TaskComplexity? complexity, bool? includeHighRiskContract)
    {
        foreach (var role in new[] { AgentRole.Reviewer, AgentRole.Tester, AgentRole.Developer })
        {
            Assert.DoesNotContain("negative-control-revert: <comma-separated",
                BuildVariant(role, complexity, includeHighRiskContract), StringComparison.Ordinal);
        }
    }

    private static string BuildVariant(
        AgentRole role, TaskComplexity? complexity, bool? includeHighRiskContract)
    {
        if (complexity is null)
            return SdlcRolePromptRequirements.BuildPlainText(role);

        return includeHighRiskContract is null
            ? SdlcRolePromptRequirements.BuildPlainText(role, complexity.Value)
            : SdlcRolePromptRequirements.BuildPlainText(role, complexity.Value, includeHighRiskContract.Value);
    }
}
