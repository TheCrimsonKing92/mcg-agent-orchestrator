using Mcg.AgentOrchestrator.Core;

// Parallel-safe: only reads requirement text; no shared resources or mutable state.
public sealed class DeveloperPromptNoWorkerTestExecutionTests
{
    private const string LineA =
        "- Name the failing test in `tests: deferred`; never run tests.";
    private const string LineB =
        "- Build changed projects with the worker build check.";

    [Theory]
    [InlineData(null)]
    [InlineData(TaskComplexity.Simple)]
    [InlineData(TaskComplexity.Complex)]
    public void BuildPlainText_DeveloperVariant_DefersTestExecution(TaskComplexity? complexity)
    {
        var instructions = complexity is null
            ? SdlcRolePromptRequirements.BuildPlainText(AgentRole.Developer)
            : SdlcRolePromptRequirements.BuildPlainText(AgentRole.Developer, complexity.Value);

        Assert.Equal(1, instructions.Split(LineA, StringSplitOptions.None).Length - 1);
        Assert.Equal(1, instructions.Split(LineB, StringSplitOptions.None).Length - 1);
        Assert.DoesNotContain("quote its assertion output", instructions, StringComparison.Ordinal);
        Assert.DoesNotContain("Run focused verification", instructions, StringComparison.Ordinal);
    }
}
