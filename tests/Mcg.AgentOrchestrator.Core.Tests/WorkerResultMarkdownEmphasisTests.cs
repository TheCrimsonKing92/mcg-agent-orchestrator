using Mcg.AgentOrchestrator.Core;

// Pure string parsing; cases share no mutable state or external resources.
public sealed class WorkerResultMarkdownEmphasisTests
{
    [Xunit.Fact]
    public void BoldLabels_ParseSuccessfulOutcomeWithoutBlocker()
    {
        var output = Block("**files:** none", "**tests:** pass - 12 facts",
            "**commit:** none", "**blockers:** none");

        Xunit.Assert.False(WorkerResultBlockers.TryFindBlocker(output, out _));
        Xunit.Assert.True(WorkerResultBlockers.TryGetBlockersStatus(output, out var blockers));
        Xunit.Assert.Equal(WorkerResultBlockers.BlockersStatus.None, blockers);
        Xunit.Assert.True(WorkerResultBlockers.TryGetTestsStatus(output, out var tests));
        Xunit.Assert.Equal(WorkerResultBlockers.TestsStatus.Pass, tests);
    }

    [Xunit.Theory]
    [Xunit.InlineData("**blockers:** none", "**tests:** pass - 3 facts")]
    [Xunit.InlineData("**blockers**: none", "**tests**: pass - 3 facts")]
    [Xunit.InlineData("- blockers: none", "- tests: pass - 3 facts")]
    [Xunit.InlineData("blockers: none", "tests: pass - 3 facts")]
    [Xunit.InlineData("**blockers:** **none**", "**tests:** **pass - 3 facts**")]
    [Xunit.InlineData("* blockers: none", "* tests: pass - 3 facts")]
    [Xunit.InlineData("__blockers:__ __none__", "__tests:__ __pass - 3 facts__")]
    [Xunit.InlineData("__blockers__: __none__", "__tests__: __pass - 3 facts__")]
    [Xunit.InlineData("*blockers*: *none*", "*tests*: *pass - 3 facts*")]
    [Xunit.InlineData("_blockers_: _none_", "_tests_: _pass - 3 facts_")]
    public void SupportedFieldFormats_ParseNoneAndPass(string blockersLine, string testsLine)
    {
        var output = Block(blockersLine, testsLine);

        Xunit.Assert.False(WorkerResultBlockers.TryFindBlocker(output, out _));
        Xunit.Assert.True(WorkerResultBlockers.TryGetBlockersStatus(output, out var blockers));
        Xunit.Assert.Equal(WorkerResultBlockers.BlockersStatus.None, blockers);
        Xunit.Assert.True(WorkerResultBlockers.TryGetTestsStatus(output, out var tests));
        Xunit.Assert.Equal(WorkerResultBlockers.TestsStatus.Pass, tests);
    }

    [Xunit.Fact]
    public void BoldLabel_ReportsSchemaDriftAsPresentBlocker()
    {
        var output = Block("**blockers:** schema drift in the registry");

        Xunit.Assert.True(WorkerResultBlockers.TryFindBlocker(output, out var blocker));
        Xunit.Assert.Equal("schema drift in the registry", blocker);
        Xunit.Assert.True(WorkerResultBlockers.TryGetBlockersStatus(output, out var status));
        Xunit.Assert.Equal(WorkerResultBlockers.BlockersStatus.Present, status);
    }

    [Xunit.Fact]
    public void BulletAndBoldValue_ReportsCannotBuildAsPresentBlocker()
    {
        var output = Block("- **blockers**: **cannot build**");

        Xunit.Assert.True(WorkerResultBlockers.TryFindBlocker(output, out var blocker));
        Xunit.Assert.Equal("cannot build", blocker);
        Xunit.Assert.True(WorkerResultBlockers.TryGetBlockersStatus(output, out var status));
        Xunit.Assert.Equal(WorkerResultBlockers.BlockersStatus.Present, status);
    }

    [Xunit.Fact]
    public void BoldTestsLabel_StillReportsFail()
    {
        var output = Block("**tests:** fail - 2 facts");

        Xunit.Assert.True(WorkerResultBlockers.TryGetTestsStatus(output, out var status));
        Xunit.Assert.Equal(WorkerResultBlockers.TestsStatus.Fail, status);
    }

    [Xunit.Fact]
    public void PlainFields_PreserveOutcomeAndAssignedScope()
    {
        var output = Block("blockers: none", "tests: pass - 4 facts",
            "assigned_scope_complete: true");

        Xunit.Assert.False(WorkerResultBlockers.TryFindBlocker(output, out _));
        Xunit.Assert.True(WorkerResultBlockers.TryGetBlockersStatus(output, out var blockers));
        Xunit.Assert.Equal(WorkerResultBlockers.BlockersStatus.None, blockers);
        Xunit.Assert.True(WorkerResultBlockers.TryGetTestsStatus(output, out var tests));
        Xunit.Assert.Equal(WorkerResultBlockers.TestsStatus.Pass, tests);
        Xunit.Assert.True(WorkerResultBlockers.TryFindTests(output, out var value));
        Xunit.Assert.Equal("pass - 4 facts", value);
        Xunit.Assert.True(WorkerResultBlockers.TryGetAssignedScopeComplete(output,
            out var complete, out var diagnostic), diagnostic);
        Xunit.Assert.True(complete);
        Xunit.Assert.Null(diagnostic);
    }

    [Xunit.Theory]
    [Xunit.InlineData("pass - see **note**", "pass - see **note**")]
    [Xunit.InlineData("[\"assigned_scope_complete\", \"**note**\"]",
        "[\"assigned_scope_complete\", \"**note**\"]")]
    [Xunit.InlineData("**_none_**", "_none_")]
    [Xunit.InlineData("**none__", "**none__")]
    [Xunit.InlineData("*none**", "*none**")]
    [Xunit.InlineData("**", "**")]
    public void ValueBoundaries_PreserveContentExceptOneWrappingPair(string input, string expected)
    {
        Xunit.Assert.True(WorkerResultBlockers.TryFindTests(Block("tests: " + input), out var value));
        Xunit.Assert.Equal(expected, value);
    }

    [Xunit.Fact]
    public void SingleMarkerAcrossColon_KeepsExistingBlockerValue()
    {
        var output = Block("*blockers:* none");

        Xunit.Assert.True(WorkerResultBlockers.TryFindBlocker(output, out var blocker));
        Xunit.Assert.Equal("* none", blocker);
        Xunit.Assert.False(WorkerResultBlockers.TryGetBlockersStatus(output, out var status));
        Xunit.Assert.Equal(WorkerResultBlockers.BlockersStatus.Unknown, status);
    }

    private static string Block(params string[] lines)
    {
        return "WORKER_RESULT:\n" + string.Join("\n", lines) + "\nEND_WORKER_RESULT";
    }
}
