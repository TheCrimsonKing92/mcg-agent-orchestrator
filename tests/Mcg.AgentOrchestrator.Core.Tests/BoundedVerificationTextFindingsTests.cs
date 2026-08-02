using Mcg.AgentOrchestrator.Core;

public sealed class BoundedVerificationTextFindingsTests
{
    // Reproduction of the defect that silently destroyed twelve valid reviewer findings on goal 0b81147a
    // (2026-08-02). The stored verification excerpt keeps a head and a tail once the log exceeds
    // BoundThreshold and drops the middle. It used to slice RAW CHARACTERS, so whatever line straddled the
    // boundary was severed - and for the reviewer's single-line `findings:` JSON array that produced a
    // corrupt half-array that still looked like a findings field. The reader accepted it, the deserialize
    // threw, the exception was discarded, and the operator was told the reviewer had submitted no findings.
    private static string BuildReviewerLog(out string findingsLine)
    {
        findingsLine =
            "findings: [{\"stable_id\":\"straddling-finding\",\"state\":\"open\",\"severity\":\"blocking\"," +
            "\"category\":\"correctness\",\"location\":{\"file\":\"src/Some/File.cs\",\"region\":\"SomeMethod\"}," +
            "\"description\":\"" + new string('D', 6_500) + "\"}]";

        // Position the findings line so it straddles the head cut, exactly as the 6,772-char line did in the
        // real 16,724-char log against a 16,384 threshold.
        return
            "WORKER_RESULT\n" +
            new string('P', VerificationTextBounds.PreviewHeadChars - 200) + "\n" +
            findingsLine + "\n" +
            "touched_anchors: []\n" +
            new string('T', 2_000) + "\n" +
            "END_WORKER_RESULT\n";
    }

    [Xunit.Fact(DisplayName = "BoundText_never_severs_a_line_that_straddles_the_excerpt_boundary")]
    public void BoundTextNeverSeversALineThatStraddlesTheExcerptBoundary()
    {
        var log = BuildReviewerLog(out var findingsLine);
        Xunit.Assert.True(log.Length > VerificationTextBounds.BoundThreshold, "fixture must exceed the bound");

        var bounded = VerificationTextBounds.BoundText(log, @"C:\logs\reviewer.out.log");

        // The line must be kept WHOLE or dropped WHOLE. A severed copy is the specific corruption being
        // fixed: it is indistinguishable from a reviewer that emitted malformed JSON.
        if (bounded.Contains("findings:", StringComparison.Ordinal))
        {
            Xunit.Assert.Contains(findingsLine, bounded, StringComparison.Ordinal);
        }
    }

    [Xunit.Fact(DisplayName = "Reviewer_findings_survive_a_bounded_excerpt_via_the_disk_fallback")]
    public void ReviewerFindingsSurviveABoundedExcerptViaTheDiskFallback()
    {
        var log = BuildReviewerLog(out _);
        var root = Path.Combine(Path.GetTempPath(), "mcg-bounded-findings", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);
        var logPath = Path.Combine(root, "reviewer.out.log");
        File.WriteAllText(logPath, log);
        try
        {
            var bounded = VerificationTextBounds.BoundText(log, logPath);
            var verification = new TaskVerificationRecord(
                "codex exec",
                root,
                0,
                bounded,
                string.Empty,
                DateTimeOffset.UtcNow,
                StandardOutputPath: logPath);

            var parsed = WorkerResultBlockers.TryFindReviewFindingRound(verification, out var round, out var diagnostic);

            Xunit.Assert.True(parsed, $"findings should be recovered from disk, but: {diagnostic}");
            Xunit.Assert.Single(round.Findings);
            Xunit.Assert.Equal("straddling-finding", round.Findings[0].StableId);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact(DisplayName = "Reviewer_findings_absent_from_a_bounded_excerpt_report_an_actionable_reason")]
    public void ReviewerFindingsAbsentFromABoundedExcerptReportAnActionableReason()
    {
        // With no disk fallback available the findings genuinely cannot be recovered. What matters is that
        // the reason names the missing contract rather than surfacing a JSON parse error from a fragment the
        // reviewer never emitted - the latter reads as a worker fault and misdirects diagnosis.
        var log = BuildReviewerLog(out _);
        var bounded = VerificationTextBounds.BoundText(log, @"C:\logs\reviewer.out.log");
        var verification = new TaskVerificationRecord(
            "codex exec",
            @"C:\repo",
            0,
            bounded,
            string.Empty,
            DateTimeOffset.UtcNow,
            StandardOutputPath: null);

        var parsed = WorkerResultBlockers.TryFindReviewFindingRound(verification, out _, out var diagnostic);

        Xunit.Assert.False(parsed);
        Xunit.Assert.DoesNotContain("reached end of data", diagnostic, StringComparison.OrdinalIgnoreCase);
        Xunit.Assert.Contains("findings", diagnostic, StringComparison.OrdinalIgnoreCase);
    }
}
