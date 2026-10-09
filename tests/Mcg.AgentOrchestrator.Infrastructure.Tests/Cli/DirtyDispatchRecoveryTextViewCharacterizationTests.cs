using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.Core;
using Xunit;

// Parallel-safe: CaptureConsole routes output through AsyncLocal; fixtures have no shared state.
public sealed class DirtyDispatchRecoveryTextViewCharacterizationTests
{
    // Derived from ConsoleViews.DispatchRecovery.cs:7-44 before the extraction.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SuggestedCommand_WithOrWithoutEvidence_PreservesExactText(bool verified)
    {
        Assert.Equal("task 7", DirtyDispatchRecoveryTextView.BuildDirtyDispatchRecoverySuggestedCommand(7, Recovery(verified)));
    }

    [Theory]
    [InlineData(true, "dirty-useful: changed files [M src/Foo.cs, M tests/FooTests.cs]; verification evidence: Passed: 5, Failed: 0; Build: 0 errors")]
    [InlineData(false, "dirty-unverified: changed files [M src/Foo.cs, M tests/FooTests.cs]; verification evidence: none found; rerun focused verification before committing")]
    public void Summary_WithOrWithoutEvidence_PreservesExactText(bool verified, string expected)
    {
        Assert.Equal(expected, DirtyDispatchRecoveryTextView.FormatDirtyDispatchRecoverySummary(Recovery(verified)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Print_QuotedDirectoryAndMultipleFiles_PreservesExactText(bool verified)
    {
        var actual = CaptureConsole(() => DirtyDispatchRecoveryTextView.PrintDirtyDispatchRecovery(7, Recovery(verified)));
        var expected = string.Join(Environment.NewLine,
            verified ? "Dispatch recovery: dirty-useful" : "Dispatch recovery: dirty-unverified",
            "Changed files: M src/Foo.cs, M tests/FooTests.cs",
            verified
                ? "Verification evidence: Passed: 5, Failed: 0; Build: 0 errors"
                : "Verification evidence: none found; rerun focused verification before committing.",
            "Safe recovery workflow:",
            "  1. git -C 'C:\\o''brien\\wt' status --short",
            "  2. git -C 'C:\\o''brien\\wt' diff --stat",
            "  3. rerun the focused/full verification command in the worktree",
            "  4. git -C 'C:\\o''brien\\wt' add -A",
            verified
                ? "  5. git -C 'C:\\o''brien\\wt' commit -m 'Recover dirty-useful task 7'"
                : "  5. git -C 'C:\\o''brien\\wt' commit -m 'Recover dirty-unverified task 7'",
            verified
                ? "  6. verify-manual 7 passed 'Reviewed dirty-useful dispatch diff, reran verification, committed <sha>.'"
                : "  6. verify-manual 7 passed 'Reviewed dirty-unverified dispatch diff, reran verification, committed <sha>.'",
            "");
        Assert.Equal(expected, actual);
    }

    private static DirtyDispatchRecovery Recovery(bool verified) => new(
        verified ? "dirty-useful" : "dirty-unverified",
        ["M src/Foo.cs", "M tests/FooTests.cs"],
        verified ? ["Passed: 5, Failed: 0", "Build: 0 errors"] : [],
        "C:\\o'brien\\wt");
}
