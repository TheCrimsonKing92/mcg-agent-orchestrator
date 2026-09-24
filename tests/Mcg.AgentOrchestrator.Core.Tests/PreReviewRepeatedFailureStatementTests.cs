using System.Collections.Immutable;
using Mcg.AgentOrchestrator.Core;

public sealed class PreReviewRepeatedFailureStatementTests
{
    [Fact]
    public void IncludesCurrentAssertionAndFirstRepositoryFrameWithinBounds()
    {
        var summary = new PreReviewRepeatedFailureSummary(2, ["Example.A"]);
        var statement = PreReviewRepeatedFailureStatement.Format(summary,
            new Dictionary<string, PreReviewRepeatedTestDetail>
            {
                ["Example.A"] = new("Expected: Completed\r\nActual: Verified",
                    "at Xunit.Framework()\n at Example.A() in C:\\repo\\tests\\Example.cs:line 42\n at Mcg.AgentOrchestrator.Example.B()")
            });

        Assert.Contains("2 consecutive", statement, StringComparison.Ordinal);
        Assert.Contains("Example.A", statement, StringComparison.Ordinal);
        Assert.Contains("Expected: Completed Actual: Verified", statement, StringComparison.Ordinal);
        Assert.Contains("C:\\repo\\tests\\Example.cs:line 42", statement, StringComparison.Ordinal);
        Assert.DoesNotContain("Xunit.Framework()", statement, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingResultKeepsStatementAndLongDetailsAreBounded()
    {
        var summary = new PreReviewRepeatedFailureSummary(2, ["Example.A", "Example.B"]);
        var statement = PreReviewRepeatedFailureStatement.Format(summary,
            new Dictionary<string, PreReviewRepeatedTestDetail>
            {
                ["Example.A"] = new(new string('x', 700), "at Mcg.AgentOrchestrator.Example.A()"),
                ["Example.B"] = new(null, null, "missing")
            });

        Assert.Contains("detail unavailable: missing", statement, StringComparison.Ordinal);
        Assert.Contains("…", statement, StringComparison.Ordinal);
        Assert.Equal("no repository frame", PreReviewRepeatedFailureStatement.SelectRepositoryFrame("at Xunit.Framework()"));
    }
}
