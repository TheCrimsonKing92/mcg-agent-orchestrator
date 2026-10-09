using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Xunit;

// Parallel-safe: CaptureConsole routes output through AsyncLocal; fixtures have no shared state.
public sealed class BriefLintTextViewCharacterizationTests
{
    // Derived from ConsoleViews.BriefLint.cs:7-10 before the extraction.
    [Fact]
    public void Print_DifferentSeveritiesAndKinds_PreservesExactText()
    {
        BriefLintFinding[] findings =
        [
            new("missing-owner", BriefLintSeverity.BlocksDispatch, "Owner missing.", "Name the owner."),
            new("broad-scope", BriefLintSeverity.Advisory, "Scope is broad.", "Narrow the scope.")
        ];
        var actual = CaptureConsole(() => BriefLintTextView.PrintBriefLintFindings(findings));
        var expected = string.Join(Environment.NewLine,
            "BRIEF-LINT blocks-dispatch missing-owner: Owner missing. remedy: Name the owner.",
            "BRIEF-LINT advisory broad-scope: Scope is broad. remedy: Narrow the scope.",
            "");
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Print_EmptyFindings_EmitsNothing()
    {
        Assert.Equal("", CaptureConsole(() => BriefLintTextView.PrintBriefLintFindings([])));
    }
}
