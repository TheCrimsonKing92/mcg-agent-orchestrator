using Mcg.AgentOrchestrator.Core;

public sealed class DispatchContractDiagnosticTests
{
    private const string RejectionRule = DispatchFailureDiagnosticMarker.ResearcherOutputContractRejected;
    private const string Diagnostic = "Researcher output contract failed: missing required section 'likely seams and risks'. Retry Planner for contract repair.";

    [Xunit.Theory]
    [Xunit.InlineData("\n")]
    [Xunit.InlineData("\r\n")]
    public void ExtractsContractDiagnosticWithEitherLineEnding(string newline)
    {
        var stderr = string.Join(newline,
            "ordinary worker noise", Diagnostic, DispatchFailureDiagnosticMarker.Format(RejectionRule),
            "later resource accounting");

        Xunit.Assert.True(DispatchContractDiagnostic.TryExtract(stderr, RejectionRule, out var diagnostic));
        Xunit.Assert.Equal(Diagnostic, diagnostic);
    }

    [Xunit.Theory]
    [Xunit.InlineData(DispatchFailureDiagnosticMarker.ResearcherArtifactPersistenceFailed,
        "Scout research output contract could not persist the accepted artifact: write failed.")]
    [Xunit.InlineData(RejectionRule,
        "Researcher durable receipt failed revalidation: missing required section 'likely seams and risks'. Retry Researcher for contract repair.")]
    public void ExtractsOtherOrchestratorContractPrefixes(string rule, string expected)
    {
        var stderr = $"worker noise\n{expected}\n{DispatchFailureDiagnosticMarker.Format(rule)}";

        Xunit.Assert.True(DispatchContractDiagnostic.TryExtract(stderr, rule, out var diagnostic));
        Xunit.Assert.Equal(expected, diagnostic);
    }

    [Xunit.Fact]
    public void UsesLastMatchingMarkerAndNearestNonBlankLineTrimmed()
    {
        const string latest = "Researcher output contract failed: required section 'likely seams and risks' is not substantive.";
        var marker = DispatchFailureDiagnosticMarker.Format(RejectionRule);
        var stderr = $"{Diagnostic}\n{marker}\nworker noise\n \t{latest}\t \n \t\n \t{marker}\t \nresource receipt";

        Xunit.Assert.True(DispatchContractDiagnostic.TryExtract(stderr, RejectionRule, out var diagnostic));
        Xunit.Assert.Equal(latest, diagnostic);
    }

    [Xunit.Theory]
    [Xunit.InlineData(null)]
    [Xunit.InlineData("")]
    [Xunit.InlineData(" \t\r\n")]
    [Xunit.InlineData(Diagnostic)]
    public void RejectsMissingMarker(string? stderr)
    {
        Xunit.Assert.False(DispatchContractDiagnostic.TryExtract(stderr, RejectionRule, out var diagnostic));
        Xunit.Assert.Equal(string.Empty, diagnostic);
    }

    [Xunit.Fact]
    public void RejectsMarkerForDifferentRule()
    {
        var stderr = $"{Diagnostic}\n{DispatchFailureDiagnosticMarker.Format(DispatchFailureDiagnosticMarker.ResearcherArtifactPersistenceFailed)}";

        Xunit.Assert.False(DispatchContractDiagnostic.TryExtract(stderr, RejectionRule, out var diagnostic));
        Xunit.Assert.Equal(string.Empty, diagnostic);
    }

    [Xunit.Theory]
    [Xunit.InlineData("ordinary worker output")]
    [Xunit.InlineData("researcher output contract failed: worker-authored lowercase line")]
    [Xunit.InlineData("")]
    public void RejectsNonContractLineBeforeLastMarkerWithoutFallingBack(string line)
    {
        var marker = DispatchFailureDiagnosticMarker.Format(RejectionRule);
        var stderr = line.Length == 0
            ? $" \t\n{marker}"
            : $"{Diagnostic}\n{marker}\n{line}\n{marker}";

        Xunit.Assert.False(DispatchContractDiagnostic.TryExtract(stderr, RejectionRule, out var diagnostic));
        Xunit.Assert.Equal(string.Empty, diagnostic);
    }

    [Xunit.Fact]
    public void RejectsPlannerRuleEvenWithMatchingMarkerAndContractPrefix()
    {
        const string rule = DispatchFailureDiagnosticMarker.PlannerOutputContractRejected;
        var stderr = $"{Diagnostic}\n{DispatchFailureDiagnosticMarker.Format(rule)}";

        Xunit.Assert.False(DispatchContractDiagnostic.TryExtract(stderr, rule, out var diagnostic));
        Xunit.Assert.Equal(string.Empty, diagnostic);
    }
}
