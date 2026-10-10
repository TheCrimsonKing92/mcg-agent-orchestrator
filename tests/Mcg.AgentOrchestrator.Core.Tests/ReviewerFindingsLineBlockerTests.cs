using Mcg.AgentOrchestrator.Core;

public sealed class ReviewerFindingsLineBlockerTests
{
    [Xunit.Fact]
    public void TryFindBlocker_ResolvedBlockingAndOpenAdvisory_ReportsNone()
    {
        var output = BuildBlock(BuildFindings("resolved"));
        var verification = Review(output);

        Xunit.Assert.Contains("severity: blocker' checks", output);
        Xunit.Assert.False(WorkerResultBlockers.TryFindBlocker(output, out _));
        Xunit.Assert.False(WorkerResultBlockers.TryFindBlocker(verification, out _));
        Xunit.Assert.False(WorkerResultBlockers.TryFindHardFailureBlocker(verification, out _));
    }

    [Xunit.Theory]
    [Xunit.InlineData("open", true)]
    [Xunit.InlineData("resolved", false)]
    public void TryFindBlocker_BlockingFinding_UsesState(string state, bool expected)
    {
        var output = BuildBlock(BuildFindings(state));

        Xunit.Assert.Equal(expected, WorkerResultBlockers.TryFindBlocker(output, out var blocker));
        if (expected)
        {
            Xunit.Assert.Equal("markdown-emphasis-main-control", blocker);
            Xunit.Assert.DoesNotContain("markdown-emphasis-blocker-prefix-widening", blocker);
            Xunit.Assert.True(WorkerResultBlockers.TryFindHardFailureBlocker(Review(output), out _));
        }
    }

    [Xunit.Fact]
    public void TryFindBlocker_PlainBlockersField_PreservesFailure()
    {
        var output = BuildBlock("findings: []", "schema drift in the registry");

        Xunit.Assert.True(WorkerResultBlockers.TryFindBlocker(output, out var blocker));
        Xunit.Assert.Equal("schema drift in the registry", blocker);
    }

    [Xunit.Theory]
    [Xunit.InlineData("findings: not-json severity: blocker in Parser")]
    [Xunit.InlineData("findings: {\"description\":\"severity: blocker\"}")]
    [Xunit.InlineData("findings: [{\"description\":\"severity: blocker\"}, null]")]
    [Xunit.InlineData("blocker: schema drift in the registry")]
    public void TryFindBlocker_UnhandledLine_PreservesSubstringPath(string line)
    {
        Xunit.Assert.True(WorkerResultBlockers.TryFindBlocker(BuildBlock(line), out var blocker));
        Xunit.Assert.Equal(line, blocker);
    }

    [Xunit.Theory]
    [Xunit.InlineData("\"severity\":\"critical\",")]
    [Xunit.InlineData("\"severity\":\"\",")]
    [Xunit.InlineData("\"severity\":null,")]
    [Xunit.InlineData("\"severity\":1,")]
    [Xunit.InlineData("")]
    public void TryFindBlocker_UnknownSeverity_FailsClosed(string severity)
    {
        var line = $$"""
            findings: [{"stable_id":"unknown-severity-blocker",{{severity}}"state":"open"}]
            """;

        Xunit.Assert.True(WorkerResultBlockers.TryFindBlocker(BuildBlock(line), out var blocker));
        Xunit.Assert.Equal("unknown-severity-blocker", blocker);
    }

    [Xunit.Theory]
    [Xunit.InlineData("\"state\":\"unknown\",")]
    [Xunit.InlineData("\"state\":\"\",")]
    [Xunit.InlineData("\"state\":null,")]
    [Xunit.InlineData("\"state\":1,")]
    [Xunit.InlineData("")]
    public void TryFindBlocker_UnknownState_FailsClosed(string state)
    {
        var line = $$"""
            findings: [{"stable_id":"unknown-state-blocker",{{state}}"severity":"blocking"}]
            """;

        Xunit.Assert.True(WorkerResultBlockers.TryFindBlocker(BuildBlock(line), out var blocker));
        Xunit.Assert.Equal("unknown-state-blocker", blocker);
    }

    [Xunit.Fact]
    public void TryReadFindingsLine_NormalizedKeysAndValues_ExcludeNonBlockers()
    {
        const string line = """
            - **FiNdInGs**: [{"STABLE_ID":"resolved-blocker","STATE":" ReSoLvEd ","SEVERITY":"blocking"},{"stable_id":"advisory-blocker","state":"open","severity":" AdViSoRy "}]
            """;

        Xunit.Assert.True(ReviewerFindingsLineBlocker.TryReadFindingsLine(line, out var blocker));
        Xunit.Assert.Empty(blocker);
        Xunit.Assert.False(WorkerResultBlockers.TryFindBlocker(BuildBlock(line), out _));
    }

    [Xunit.Fact]
    public void TryReadFindingsLine_EmptyArray_IsHandledWithoutBlocker()
    {
        Xunit.Assert.True(ReviewerFindingsLineBlocker.TryReadFindingsLine("findings: []", out var blocker));
        Xunit.Assert.Empty(blocker);
    }

    [Xunit.Fact]
    public void TryReadFindingsLine_OpenItems_UsesIdentityFallbacksInOrder()
    {
        const string line = """
            findings: [{"stable_id":"first"},{"stable_id":" ","title":"fallback title"},{"description":"fallback description"},{}]
            """;

        Xunit.Assert.True(ReviewerFindingsLineBlocker.TryReadFindingsLine(line, out var blocker));
        Xunit.Assert.Equal("first; fallback title; fallback description; 3", blocker);
    }

    [Xunit.Theory]
    [Xunit.InlineData("blockers: []")]
    [Xunit.InlineData("findings")]
    [Xunit.InlineData("findings: not-json severity: blocker")]
    [Xunit.InlineData("findings: {}")]
    [Xunit.InlineData("findings: \"severity: blocker\"")]
    [Xunit.InlineData("findings: [{\"stable_id\":\"first-blocker\"}, 1]")]
    public void TryReadFindingsLine_UnusableValue_IsUnhandled(string line)
    {
        Xunit.Assert.False(ReviewerFindingsLineBlocker.TryReadFindingsLine(line, out var blocker));
        Xunit.Assert.Empty(blocker);
    }

    private static string BuildFindings(string state) => $$"""
        findings: [{"stable_id":"markdown-emphasis-main-control","state":"{{state}}","severity":"blocking","category":"correctness","location":{"file":"src/Parser.cs","region":"Normalize"},"description":"Main control."},{"stable_id":"markdown-emphasis-blocker-prefix-widening","state":"open","severity":"advisory","category":"correctness","location":{"file":"src/Parser.cs","region":"Classify"},"description":"Keep the severity: blocker' checks scoped."}]
        """;

    private static string BuildBlock(string line, string blockers = "none") => $$"""
        WORKER_RESULT:
        files: none
        commands: review
        tests: not-run - Reviewer is read-only
        commit: none
        blockers: {{blockers}}
        {{line}}
        touched_anchors: []
        verdict: pass
        END_WORKER_RESULT
        """;

    private static TaskVerificationRecord Review(string output) => new(
        "review", "C:\\repo", 0, output, string.Empty, DateTimeOffset.UtcNow,
        WorkerResultPresent: true);
}
