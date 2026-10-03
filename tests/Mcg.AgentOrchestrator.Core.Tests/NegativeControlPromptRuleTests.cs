using Mcg.AgentOrchestrator.Core;

public sealed class NegativeControlPromptRuleTests
{
    [Xunit.Theory]
    [Xunit.InlineData(AgentRole.Tester, TaskComplexity.Simple)]
    [Xunit.InlineData(AgentRole.Tester, TaskComplexity.Complex)]
    [Xunit.InlineData(AgentRole.Reviewer, TaskComplexity.Simple)]
    [Xunit.InlineData(AgentRole.Reviewer, TaskComplexity.Complex)]
    public void Requirements_ControlInstructions_AppearExactlyOnceInOrder(AgentRole role, TaskComplexity complexity)
    {
        const string applicability = "- Request a negative control only when a criterion says a test must fail against main or today's code; never for refactor, documentation or move-only criteria whose tests must pass unmodified.";
        const string proof = "- RED proof: evidence_request negative_control:\"revert-src\"; if tests use members the goal adds, add revert_paths naming only the src files implementing the behavior, or a repository policy file the conductor accepts (.gitattributes). For one hunk inside a file whose other changes the tests need, use mutation:{path,old_text,new_text} instead. After negative-control-compile-red, narrow revert_paths or ask for an operator record; never repeat the same request.";
        var lines = SdlcRolePromptRequirements.BuildPlainText(role, complexity).Split(Environment.NewLine);
        Assert.Single(lines, line => line == applicability);
        Assert.Single(lines, line => line == proof);
        Assert.True(Array.IndexOf(lines, applicability) < Array.IndexOf(lines, proof));
        Assert.DoesNotContain("- Prove RED with evidence_request negative_control:\"revert-src\".", lines);
    }
}
