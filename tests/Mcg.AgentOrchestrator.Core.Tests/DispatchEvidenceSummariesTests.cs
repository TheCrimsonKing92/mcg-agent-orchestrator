using Mcg.AgentOrchestrator.Core;
using Xunit;

// Parallel-safe: fixed in-memory records; no files, shared state or processes.
public sealed class DispatchEvidenceSummariesTests
{
    private static readonly DateTimeOffset CompletedAt = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void PreflightFailurePreservesBaselineSummary()
    {
        // Baseline 81ad7034a: DispatchOutcomeClassifyTests.ClassifyPreflightFailure;
        // DispatchFailureClassifier.BuildPreflightFailureEvidenceSummary supplies the exact prefix.
        var verification = Verification(stderr: "Low Integrity sandbox setup failed while applying label");

        Assert.True(DispatchEvidenceSummaries.IsPreflightFailure(verification));
        Assert.Equal(
            "sandbox-preflight-failure: Low Integrity sandbox setup failed while applying label",
            DispatchEvidenceSummaries.BuildPreflightFailureEvidenceSummary(verification));
    }

    [Fact]
    public void ScriptingStderrPreservesBaselineSummary()
    {
        // Baseline 81ad7034a: DispatchOutcomeClassifyTests.ClassifyIgnoresSandboxPreflightJsonWithGenericStatus429;
        // DispatchFailureClassifier.BuildRealFailureEvidenceSummary retains this substantive stderr line.
        var verification = Verification(stderr: "powershell.exe: ParserError: Unexpected token '}' in expression.");

        Assert.True(DispatchEvidenceSummaries.HasScriptingFailureEvidence(verification));
        Assert.Equal(
            "real-failure stderr-tail: powershell.exe: ParserError: Unexpected token '}' in expression.",
            DispatchEvidenceSummaries.BuildRealFailureEvidenceSummary(verification));
    }

    [Fact]
    public void MaxRuntimeReapPreservesBaselineSummary()
    {
        // Baseline 81ad7034a: DispatchOutcomeClassifyTests.ClassifyReapedMaxRuntimeIsEnvironmentalOperatorNeeded.
        var verification = Verification(
            stderr: "[dispatch-host] terminating worker tree: exceeded max runtime 60 min.");

        Assert.True(DispatchEvidenceSummaries.TryGetDispatchHostMaxRuntimeReapSummary(verification, out var summary));
        Assert.Equal(
            "Worker tree reaped by the dispatch host at its max runtime of 60 min; uncommitted tree contents are not evidence of a code failure.",
            summary);
    }

    [Fact]
    public void UnrecognisedEvidencePreservesEmptySummaryAndNegativeSignals()
    {
        // Baseline 81ad7034a: DispatchFailureClassifier.BuildEvidenceSummary returns string.Empty
        // when HasVerificationEvidence finds no verification term/count; all three signal readers miss.
        const string line = "Inspected the fixture and completed the requested work.";
        var verification = Verification(stdout: line, stderr: line);

        Assert.Equal(string.Empty, DispatchEvidenceSummaries.BuildEvidenceSummary(verification));
        Assert.False(DispatchEvidenceSummaries.IsPreflightFailure(verification));
        Assert.False(DispatchEvidenceSummaries.HasScriptingFailureEvidence(verification));
        Assert.False(DispatchEvidenceSummaries.TryGetDispatchHostMaxRuntimeReapSummary(verification, out var summary));
        Assert.Equal(string.Empty, summary);
    }

    private static TaskVerificationRecord Verification(string stdout = "", string stderr = "") =>
        new("opaque command", "C:\\repo", 1, stdout, stderr, CompletedAt);
}
