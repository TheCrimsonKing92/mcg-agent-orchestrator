using Mcg.AgentOrchestrator.Core;

// Parallel-safe: only reads requirement text; no shared resources or mutable state.
public sealed class DeveloperPromptNoWorkerTestExecutionTests
{
    private const string LineA =
        "- Before editing, name the test (existing or new) whose assertion fails without your change; do not run it: list it in tests: deferred, and the acceptance gate's source-reverted run proves it fails against main.";
    private const string LineB =
        "- Verify by building each changed project with the worker build check; name the focused test classes in tests: deferred instead of running them.";

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
