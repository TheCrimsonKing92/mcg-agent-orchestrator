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
