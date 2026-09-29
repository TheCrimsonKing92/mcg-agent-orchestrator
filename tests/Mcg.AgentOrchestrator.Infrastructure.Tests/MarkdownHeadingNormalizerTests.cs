using Mcg.AgentOrchestrator.Infrastructure;

public sealed class MarkdownHeadingNormalizerTests : WorkerDispatchTestSupport
{
    private const string CSharpMapping =
        "1. maps to C# patterns in strings and comments versus code. disposition=planned; plan=Add a scanner test file.";

    [Xunit.Fact]
    public void PlannerMappingWithCSharpTokenRemainsIntact()
    {
        var plan = PlannerContractPlanFixture().Replace(
            PlannerContractAcceptanceMappingBody,
            CSharpMapping + "\n" +
            "2. maps to the remaining scanner behavior. disposition=planned; plan=Verify the other required behavior.",
            StringComparison.Ordinal);

        Xunit.Assert.Equal(plan.ReplaceLineEndings("\n"), MarkdownHeadingNormalizer.SeparateInlineAtxHeadings(plan));
        Xunit.Assert.True(
            PlannerOutputContract.TryValidate(
                plan,
                out var validatedPlan,
                out var diagnostic,
                acceptanceCriteria: ["Scan C# patterns.", "Verify the remaining behavior."]),
            diagnostic);
        Xunit.Assert.Contains(CSharpMapping, validatedPlan, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void GluedSectionHeadingsAreSeparated()
    {
        const string input = "worktree.## Premise validity\nintro### Target seams and symbols\nletter## Heading";
        const string expected = "worktree.\n## Premise validity\nintro\n### Target seams and symbols\nletter\n## Heading";

        Xunit.Assert.Equal(expected, MarkdownHeadingNormalizer.SeparateInlineAtxHeadings(input));
    }

    [Xunit.Theory]
    [Xunit.InlineData("F# code")]
    [Xunit.InlineData("issue# 12")]
    [Xunit.InlineData("C# strings")]
    [Xunit.InlineData("note.####### Seven hashes")]
    public void InlineTextWithoutGluedHeadingIsUnchanged(string inlineText)
    {
        var input = "Sentence with " + inlineText + " and more text.";

        Xunit.Assert.Equal(input, MarkdownHeadingNormalizer.SeparateInlineAtxHeadings(input));
    }

    [Xunit.Fact]
    public void HeadingsAlreadyAtLineStartAreUnchanged()
    {
        const string input = "# Single heading\n## Premise validity\n### Target seams and symbols";

        Xunit.Assert.Equal(input, MarkdownHeadingNormalizer.SeparateInlineAtxHeadings(input));
    }
}
