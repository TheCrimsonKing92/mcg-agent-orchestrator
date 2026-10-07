using Mcg.AgentOrchestrator.Infrastructure;

public sealed class ResearcherOutputContractHeadingQualifierTests : WorkerDispatchTestSupport
{
    [Xunit.Theory]
    [Xunit.InlineData(false, "\n")]
    [Xunit.InlineData(false, "\r\n")]
    [Xunit.InlineData(true, "\n")]
    [Xunit.InlineData(true, "\r\n")]
    public void AcceptsPlainHeadingsAndOneQualifierOnAllFourHeadings(bool qualified, string newline)
    {
        var text = ResearcherContractFixture();
        if (qualified)
        {
            text = text.Replace("## Current source findings", "## Current source findings (facts and inferences)", StringComparison.Ordinal)
                .Replace("## Prior goal evidence", "## Prior goal evidence\t(facts)", StringComparison.Ordinal)
                .Replace("## Upstream capabilities", "## Upstream capabilities(inference)", StringComparison.Ordinal)
                .Replace("## Likely seams and risks", "## Likely seams and risks (inference)\t ", StringComparison.Ordinal);
        }
        text = text.ReplaceLineEndings(newline);

        Xunit.Assert.True(ResearcherOutputContract.TryValidate(text, out var research, out var diagnostic), diagnostic);
        Xunit.Assert.Equal(text.ReplaceLineEndings("\n").Trim(), research);
        Xunit.Assert.Equal(string.Empty, diagnostic);
    }

    [Xunit.Theory]
    [Xunit.InlineData("Likely seams and risks and more")]
    [Xunit.InlineData("Likely seams and risks: inference")]
    [Xunit.InlineData("Likely seams and risks (a) (b)")]
    [Xunit.InlineData("Likely seams and risks (outer (inner))")]
    [Xunit.InlineData("Likely seams and risks (inference) and more")]
    public void RejectsSuffixesBeyondOneNonNestedParenthetical(string heading)
    {
        var text = ResearcherContractFixture().Replace("## Likely seams and risks", $"## {heading}", StringComparison.Ordinal);

        Xunit.Assert.False(ResearcherOutputContract.TryValidate(text, out _, out var diagnostic));
        Xunit.Assert.Equal("missing required section 'likely seams and risks'", diagnostic);
    }

    [Xunit.Fact]
    public void QualifiedHeadingStillRequiresSubstantiveBody()
    {
        var fixture = ResearcherContractFixture().ReplaceLineEndings("\n");
        var sectionStart = fixture.IndexOf("## Likely seams and risks", StringComparison.Ordinal);
        var text = fixture[..sectionStart] + "## Likely seams and risks (inference)\nToo short.";

        Xunit.Assert.False(ResearcherOutputContract.TryValidate(text, out _, out var diagnostic));
        Xunit.Assert.Equal("required section 'likely seams and risks' is not substantive", diagnostic);
    }
}
