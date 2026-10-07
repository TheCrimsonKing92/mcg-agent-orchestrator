using Mcg.AgentOrchestrator.Infrastructure;

public sealed class WorkerResultParserEvidenceTests
{
    [Xunit.Fact(DisplayName = "WorkerResultParser_parses_canonical_inconclusive_tests_status")]
    public void WorkerResultParserParsesCanonicalInconclusiveTestsStatus()
    {
        var text = WorkerResultBlock(
            "dotnet test --no-build --filter Focused",
            "inconclusive - process killed; no TRX");

        Assert.True(WorkerResultParser.TryParseResult(text, out var result, out var diagnostic), diagnostic);
        Assert.Equal(WorkerResultParser.TestsStatus.Inconclusive, result.TestsStatus);
        Assert.Equal("inconclusive - process killed; no TRX", result.Fields["tests"]);
        Assert.False(WorkerResultParser.TestsReportFailure(result, out _));
        Assert.False(WorkerResultParser.TryParseSuccessfulResult(text, out _, out _));
    }

    [Xunit.Theory(DisplayName = "WorkerResultParser_requires_current_round_evidence_for_inconclusive")]
    [Xunit.InlineData("inconclusive", false)]
    [Xunit.InlineData("inconclusive: process killed", false)]
    [Xunit.InlineData("inconclusive process killed", false)]
    [Xunit.InlineData("inconclusive - ", false)]
    [Xunit.InlineData("inconclusive - process killed; no TRX", true)]
    public void WorkerResultParserRequiresCurrentRoundEvidenceForInconclusive(
        string tests,
        bool expectedInconclusive)
    {
        var text = WorkerResultBlock("dotnet test --no-build --filter Focused", tests);

        var parsed = WorkerResultParser.TryParseResult(text, out var result, out var diagnostic);

        Assert.Equal(expectedInconclusive, parsed);
        Assert.Equal(
            expectedInconclusive ? WorkerResultParser.TestsStatus.Inconclusive : WorkerResultParser.TestsStatus.Unknown,
            result.TestsStatus);
        if (!expectedInconclusive)
        {
            Assert.Contains("requires", diagnostic, StringComparison.Ordinal);
        }
    }

    [Xunit.Theory(DisplayName = "WorkerResultParser_read_only_success_policy_accepts_only_known_non_failing_statuses")]
    [Xunit.InlineData("pass - source inspection complete", true)]
    [Xunit.InlineData("not-run - read-only task", true)]
    [Xunit.InlineData("deferred - implementation verification belongs to Developer", true)]
    [Xunit.InlineData("inconclusive - external artifact was unavailable", true)]
    [Xunit.InlineData("fail - source contract violated", false)]
    [Xunit.InlineData("inspection complete", false)]
    public void WorkerResultParserReadOnlySuccessPolicyAcceptsOnlyKnownNonFailingStatuses(
        string tests,
        bool expected)
    {
        var text = WorkerResultBlock("source inspection", tests);

        var parsed = WorkerResultParser.TryParseSuccessfulResult(
            text,
            out _,
            out _,
            allowNoChangedFiles: true,
            requireNoBlockers: true,
            allowReadOnlyTestStatuses: true);

        Assert.Equal(expected, parsed);
    }

    [Xunit.Theory(DisplayName = "WorkerResultParser_rejects_noncanonical_premise_invalid")]
    [Xunit.InlineData("premise-invalid")]
    [Xunit.InlineData("premise-invalid: required API does not exist")]
    [Xunit.InlineData("premise-invalid required API does not exist")]
    [Xunit.InlineData("premise-invalid - ")]
    public void WorkerResultParserRejectsNoncanonicalPremiseInvalid(string blockers)
    {
        var text = WorkerResultBlock(
            "inspected source",
            "not-run - read-only task",
            blockers);

        Assert.False(WorkerResultParser.TryParseResult(text, out _, out var diagnostic));
        Assert.Contains("premise-invalid requires", diagnostic, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "WorkerResultParser_uses_latest_WORKER_RESULT_block")]
    public void WorkerResultParserUsesLatestWorkerResultBlock()
    {
        var text =
            WorkerResultBlock("stale command", "pass - stale result") +
            Environment.NewLine +
            WorkerResultBlock("latest command", "inconclusive - latest command timed out; no TRX");

        Assert.True(WorkerResultParser.TryParseResult(text, out var result, out var diagnostic), diagnostic);
        Assert.Equal(WorkerResultParser.TestsStatus.Inconclusive, result.TestsStatus);
        Assert.Equal("latest command", result.Fields["commands"]);
        Assert.Equal("inconclusive - latest command timed out; no TRX", result.Fields["tests"]);
    }

    [Xunit.Fact(DisplayName = "WorkerResultParser_rejects_unstructured_failing_tests_with_status_guidance")]
    public void WorkerResultParserRejectsUnstructuredFailingTestsWithStatusGuidance()
    {
        var text = WorkerResultBlock("focused verification", "3 failed, see log");

        Assert.True(WorkerResultParser.TryParseResult(text, out var result, out var diagnostic), diagnostic);
        Assert.Equal(WorkerResultParser.TestsStatus.Unknown, result.TestsStatus);
        Assert.True(WorkerResultParser.TestsReportFailure(result, out _));

        Assert.False(WorkerResultParser.TryParseSuccessfulResult(text, out _, out diagnostic, allowNoChangedFiles: true));
        Assert.Contains("tests field is unstructured", diagnostic, StringComparison.Ordinal);
        Assert.Contains("Restate tests with a leading status word", diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain("tests reported failure", diagnostic, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "WorkerResultParser_accepts_leading_pass_with_failure_words_in_narrative")]
    public void WorkerResultParserAcceptsLeadingPassWithFailureWordsInNarrative()
    {
        var text = WorkerResultBlock("focused verification", "pass - verified retry path that previously failed");

        var parsed = WorkerResultParser.TryParseSuccessfulResult(text, out _, out var diagnostic, allowNoChangedFiles: true);

        Assert.True(parsed, diagnostic);
    }

    [Xunit.Fact(DisplayName = "WorkerResultParser_preserves_structured_fail_diagnostic")]
    public void WorkerResultParserPreservesStructuredFailDiagnostic()
    {
        var text = WorkerResultBlock("focused verification", "fail - 2 assertions failed");

        Assert.False(WorkerResultParser.TryParseSuccessfulResult(text, out _, out var diagnostic, allowNoChangedFiles: true));
        Assert.Equal("WORKER_RESULT tests reported failure: fail - 2 assertions failed.", diagnostic);
    }

    private static string WorkerResultBlock(
        string commands,
        string tests,
        string blockers = "none")
    {
        return $"""
            WORKER_RESULT:
            files: none
            commands: {commands}
            tests: {tests}
            commit: none
            blockers: {blockers}
            model_fit: OpenAI/test - adequate - parser verification - sufficient
            skills: none
            confidence: high
            END_WORKER_RESULT
            """;
    }
}
