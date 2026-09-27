using Mcg.AgentOrchestrator.Core;

public sealed class DeveloperNoChangePromptContractTests
{
    [Xunit.Fact]
    public void DefaultDeveloperInstructionsCarryDeferredNoChangeContract()
    {
        var instructions = SdlcRolePromptRequirements.BuildPlainText(AgentRole.Developer);
        Xunit.Assert.Contains("NO_CHANGE:", instructions, StringComparison.Ordinal);
        Xunit.Assert.Contains("tests: deferred", instructions, StringComparison.Ordinal);
    }

    [Xunit.Theory]
    [Xunit.InlineData(TaskComplexity.Simple)]
    [Xunit.InlineData(TaskComplexity.Complex)]
    public void DeveloperInstructionsPairRationaleWithDeferredClassNames(TaskComplexity complexity)
    {
        var instructions = SdlcRolePromptRequirements.BuildPlainText(AgentRole.Developer, complexity);

        Xunit.Assert.Contains("NO_CHANGE:", instructions, StringComparison.Ordinal);
        Xunit.Assert.Contains("tests: deferred", instructions, StringComparison.Ordinal);
        Xunit.Assert.Contains("test class", instructions, StringComparison.Ordinal);
    }
}
