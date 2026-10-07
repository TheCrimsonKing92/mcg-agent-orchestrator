using Mcg.AgentOrchestrator.Core;

public sealed class DispatchContractDiagnosticTestsPlannerRule
{
    private const string Rule = DispatchFailureDiagnosticMarker.PlannerOutputContractRejected;
    private const string Rejection = "Planner output contract failed. Stdout plan reason: target citation 'src/Missing.cs' does not exist and is not marked as a new file; source span [12..26).";
    private const string Citation = "Offending citation: 'src/Missing.cs'. Retry Planner for contract repair.";

    [Xunit.Theory]
    [Xunit.InlineData("\n", Rejection)]
    [Xunit.InlineData("\r\n", Rejection)]
    [Xunit.InlineData("\n", "Planner durable receipt failed revalidation: target citation 'src/Missing.cs' does not exist.")]
    [Xunit.InlineData("\r\n", "Planner durable receipt failed revalidation: target citation 'src/Missing.cs' does not exist.")]
    public void ExtractsRejectionAfterResourceReceipt(string newline, string rejection)
    {
        var stderr = string.Join(newline,
            "Planner recovery completed.", DispatchFailureDiagnosticMarker.Format(Rule),
            "RESOURCE goal=9797ec59 task=ae864b44 cpu_ms=100",
            rejection, Citation);

        Xunit.Assert.True(DispatchContractDiagnostic.TryExtract(stderr, Rule, out var diagnostic));
        Xunit.Assert.Equal(rejection + " " + Citation, diagnostic);
    }

    [Xunit.Theory]
    [Xunit.InlineData("\n")]
    [Xunit.InlineData("\r\n")]
    public void UsesLastFullMarkerAndStopsAtNextOrchestratorMarker(string newline)
    {
        var marker = DispatchFailureDiagnosticMarker.Format(Rule);
        var stderr = string.Join(newline,
            marker, "Planner output contract failed. Old rejection.", " \t" + marker + " \t",
            "RESOURCE accounting", " \t" + Rejection + "\t ", " \t", " " + Citation + " ",
            " @@MCG_OTHER_RECEIPT@@ ", "Planner output contract failed. Later unrelated text.");

        Xunit.Assert.True(DispatchContractDiagnostic.TryExtract(stderr, Rule, out var diagnostic));
        Xunit.Assert.Equal(Rejection + " " + Citation, diagnostic);
    }

    [Xunit.Theory]
    [Xunit.InlineData("")]
    [Xunit.InlineData("RESOURCE accounting")]
    public void RejectsTextOnlyBeforeLastMarker(string trailingText)
    {
        var marker = DispatchFailureDiagnosticMarker.Format(Rule);
        var stderr = $"{marker}\n{Rejection}\n{Citation}\n{marker}\n{trailingText}";

        Xunit.Assert.False(DispatchContractDiagnostic.TryExtract(stderr, Rule, out var diagnostic));
        Xunit.Assert.Equal(string.Empty, diagnostic);
    }

    [Xunit.Theory]
    [Xunit.InlineData("ordinary worker text")]
    [Xunit.InlineData("planner output contract failed. Wrong case.")]
    public void RejectsNonContractTextAfterMarker(string trailingText)
    {
        var stderr = DispatchFailureDiagnosticMarker.Format(Rule) + "\n" + trailingText;

        Xunit.Assert.False(DispatchContractDiagnostic.TryExtract(stderr, Rule, out var diagnostic));
        Xunit.Assert.Equal(string.Empty, diagnostic);
    }

    [Xunit.Theory]
    [Xunit.InlineData(null)]
    [Xunit.InlineData("")]
    [Xunit.InlineData(" \t\r\n")]
    [Xunit.InlineData(Rejection)]
    public void RejectsMissingMarker(string? stderr)
    {
        Xunit.Assert.False(DispatchContractDiagnostic.TryExtract(stderr, Rule, out var diagnostic));
        Xunit.Assert.Equal(string.Empty, diagnostic);
    }

    [Xunit.Fact]
    public void RejectsMarkerForAnotherRule()
    {
        var stderr = DispatchFailureDiagnosticMarker.Format(
            DispatchFailureDiagnosticMarker.ResearcherOutputContractRejected) + "\n" + Rejection;

        Xunit.Assert.False(DispatchContractDiagnostic.TryExtract(stderr, Rule, out var diagnostic));
        Xunit.Assert.Equal(string.Empty, diagnostic);
    }

    [Xunit.Fact]
    public void ScansToFirstPlannerPrefixBeforeJoiningContinuationLines()
    {
        var stderr = string.Join("\n", DispatchFailureDiagnosticMarker.Format(Rule),
            "@@MCG_OTHER_RECEIPT@@", "RESOURCE accounting", Rejection, Citation);

        Xunit.Assert.True(DispatchContractDiagnostic.TryExtract(stderr, Rule, out var diagnostic));
        Xunit.Assert.Equal(Rejection + " " + Citation, diagnostic);
    }

    [Xunit.Fact]
    public void CapsJoinedDiagnosticAtOneThousandCharacters()
    {
        const string prefix = "Planner output contract failed. ";
        var rejection = prefix + new string('x', 1500 - prefix.Length);
        var stderr = DispatchFailureDiagnosticMarker.Format(Rule) + "\n" + rejection;

        Xunit.Assert.True(DispatchContractDiagnostic.TryExtract(stderr, Rule, out var diagnostic));
        Xunit.Assert.Equal(rejection[..999] + "…", diagnostic);
        Xunit.Assert.Equal(1000, diagnostic.Length);
    }

    [Xunit.Fact]
    public void AppliesCapAfterJoiningMultipleLines()
    {
        const string prefix = "Planner output contract failed. ";
        var rejection = prefix + new string('x', 700 - prefix.Length);
        var continuation = new string('y', 700);
        var stderr = string.Join("\n", DispatchFailureDiagnosticMarker.Format(Rule), rejection, continuation);
        var joined = rejection + " " + continuation;

        Xunit.Assert.True(DispatchContractDiagnostic.TryExtract(stderr, Rule, out var diagnostic));
        Xunit.Assert.Equal(joined[..999] + "…", diagnostic);
    }
}
