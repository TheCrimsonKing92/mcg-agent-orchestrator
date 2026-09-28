using Mcg.AgentOrchestrator.Core;

public sealed class DeveloperDeferredTestsExactFormPromptTests
{
    private const string ExactForm =
        "- Format: `tests: deferred - ClassA, ClassB` or backticked names.";

    [Theory]
    [InlineData(null)]
    [InlineData(TaskComplexity.Simple)]
    [InlineData(TaskComplexity.Complex)]
    public void DeveloperKeepsExistingRequirementsAndAddsOneExactForm(TaskComplexity? complexity)
    {
        var instructions = complexity is null
            ? SdlcRolePromptRequirements.BuildPlainText(AgentRole.Developer)
            : SdlcRolePromptRequirements.BuildPlainText(AgentRole.Developer, complexity.Value);

        Assert.Equal(1, instructions.Split(ExactForm, StringSplitOptions.None).Length - 1);
        var existingRequirements = complexity == TaskComplexity.Simple
            ? new[]
            {
                "- No edits: start `NO_CHANGE:` line with reason; report `tests: deferred` naming test classes for conductor.",
                "- First honor an eligible typed early-convergence decision for the exact candidate by returning its passed focused receipts without replaying history or manufacturing edits.",
                "- Keep edits scoped and report changed files plus behavior enabled.",
                "- Before editing, name the failing test and quote its assertion output.",
                "- Run focused verification when practical and name exact commands.",
                "- Call out blockers or follow-up work explicitly."
            }
            : new[]
            {
                "- First honor eligible typed convergence for this candidate: return fresh passed focused receipts; clean worktree, no replay or invented edits.",
                "- No edits: start `NO_CHANGE:` line with reason; report `tests: deferred` naming test classes for conductor.",
                "- Implement only the requested behavior and keep edits scoped.",
                "- Before editing, name the failing test and quote its assertion output.",
                "- Report changed files and the behavior each change enables.",
                "- Run focused verification when practical and include exact command names.",
                "- Leave follow-up work explicit when the dashboard or orchestrator blocks the ideal path."
            };
        Assert.Contains("## Developer Requirements", instructions, StringComparison.Ordinal);
        foreach (var existing in existingRequirements)
            Assert.Contains(existing, instructions, StringComparison.Ordinal);
    }

    [Fact]
    public void TesterPromptDoesNotTeachDeveloperForm() =>
        Assert.DoesNotContain(ExactForm, SdlcRolePromptRequirements.BuildPlainText(AgentRole.Tester),
            StringComparison.Ordinal);
}
