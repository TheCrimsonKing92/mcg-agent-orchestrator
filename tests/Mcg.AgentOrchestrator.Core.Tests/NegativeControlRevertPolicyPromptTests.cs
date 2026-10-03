using Mcg.AgentOrchestrator.Core;

public sealed class NegativeControlRevertPolicyPromptTests
{
    [Xunit.Theory]
    [Xunit.InlineData(AgentRole.Tester, TaskComplexity.Simple)]
    [Xunit.InlineData(AgentRole.Tester, TaskComplexity.Complex)]
    [Xunit.InlineData(AgentRole.Reviewer, TaskComplexity.Simple)]
    [Xunit.InlineData(AgentRole.Reviewer, TaskComplexity.Complex)]
    public void Requirements_NameSharedPolicyFiles(AgentRole role, TaskComplexity complexity)
    {
        var lines = SdlcRolePromptRequirements.BuildPlainText(role, complexity).Split(Environment.NewLine);
        var proof = Assert.Single(lines, line => line.StartsWith("- RED proof:", StringComparison.Ordinal));
        const string marker = "a repository policy file the conductor accepts (";
        Assert.Contains(".gitattributes", proof);
        Assert.Contains(marker, proof);
        var start = proof.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
        var end = proof.IndexOf(')', start);
        Assert.True(end >= start, "The accepted policy-file list must have a closing parenthesis.");
        var policyFiles = proof[start..end].Split(", ", StringSplitOptions.None);
        Assert.Equal(NegativeControlRevertPolicy.RevertablePolicyFiles, policyFiles);
    }
}
