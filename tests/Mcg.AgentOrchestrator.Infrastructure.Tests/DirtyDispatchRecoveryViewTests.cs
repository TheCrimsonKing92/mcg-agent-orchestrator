using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.App.Cli;

public sealed class DirtyDispatchRecoveryViewTests
{
    private static readonly DirtyDispatchRecovery DirtyUsefulRecovery = new(
        "dirty-useful",
        ["M src/Foo.cs", "M tests/FooTests.cs"],
        ["Test run successful. Passed: 5, Failed: 0."],
        "C:\\worktrees\\goal-abc");

    private static readonly DirtyDispatchRecovery DirtyUnverifiedRecovery = new(
        "dirty-unverified",
        ["M src/Bar.cs"],
        [],
        "C:\\worktrees\\goal-abc");

    [Xunit.Fact(DisplayName = "FormatDirtyDispatchRecoverySummary_formats_dirty_useful_with_evidence")]
    public void FormatDirtyDispatchRecoverySummaryFormatsDirtyUsefulWithEvidence()
    {
        var summary = ConsoleViews.FormatDirtyDispatchRecoverySummary(DirtyUsefulRecovery);

        AssertEx.Contains(summary, text => text.StartsWith("dirty-useful:", StringComparison.Ordinal));
        AssertEx.Contains(summary, text => text.Contains("src/Foo.cs", StringComparison.Ordinal));
        AssertEx.Contains(summary, text => text.Contains("tests/FooTests.cs", StringComparison.Ordinal));
        AssertEx.Contains(summary, text => text.Contains("Passed: 5", StringComparison.Ordinal));
    }

    [Xunit.Fact(DisplayName = "FormatDirtyDispatchRecoverySummary_formats_dirty_unverified_with_no_evidence_note")]
    public void FormatDirtyDispatchRecoverySummaryFormatsDirtyUnverifiedWithNoEvidenceNote()
    {
        var summary = ConsoleViews.FormatDirtyDispatchRecoverySummary(DirtyUnverifiedRecovery);

        AssertEx.Contains(summary, text => text.StartsWith("dirty-unverified:", StringComparison.Ordinal));
        AssertEx.Contains(summary, text => text.Contains("src/Bar.cs", StringComparison.Ordinal));
        AssertEx.Contains(summary, text => text.Contains("none found", StringComparison.OrdinalIgnoreCase));
        AssertEx.Contains(summary, text => text.Contains("rerun focused verification", StringComparison.OrdinalIgnoreCase));
    }

    [Xunit.Fact(DisplayName = "BuildDirtyDispatchRecoverySuggestedCommand_points_to_task_details")]
    public void BuildDirtyDispatchRecoverySuggestedCommandPointsToTaskDetails()
    {
        const int taskNumber = 3;
        var command = ConsoleViews.BuildDirtyDispatchRecoverySuggestedCommand(taskNumber, DirtyUsefulRecovery);

        Assert.Equal($"task {taskNumber}", command);
    }

    [Xunit.Fact(DisplayName = "PrintDirtyDispatchRecovery_includes_git_inspect_stage_commit_and_verify_manual_workflow")]
    public void PrintDirtyDispatchRecoveryIncludesGitInspectStageCommitAndVerifyManualWorkflow()
    {
        const int taskNumber = 7;
        var output = CaptureConsole(() => ConsoleViews.PrintDirtyDispatchRecovery(taskNumber, DirtyUnverifiedRecovery));
        AssertEx.Contains(output, text => text.Contains("C:\\worktrees\\goal-abc", StringComparison.Ordinal));
        AssertEx.Contains(output, text => text.Contains("status --short", StringComparison.Ordinal));
        AssertEx.Contains(output, text => text.Contains("add -A", StringComparison.Ordinal));
        AssertEx.Contains(output, text => text.Contains("commit -m", StringComparison.Ordinal));
        AssertEx.Contains(output, text => text.Contains($"verify-manual {taskNumber} passed", StringComparison.Ordinal));
        AssertEx.Contains(output, text => text.Contains("dirty-unverified", StringComparison.Ordinal));
    }
}
