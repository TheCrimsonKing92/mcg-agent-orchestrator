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

        Assert.StartsWith("dirty-useful:", summary, StringComparison.Ordinal);
        Assert.Contains("src/Foo.cs", summary, StringComparison.Ordinal);
        Assert.Contains("tests/FooTests.cs", summary, StringComparison.Ordinal);
        Assert.Contains("Passed: 5", summary, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "FormatDirtyDispatchRecoverySummary_formats_dirty_unverified_with_no_evidence_note")]
    public void FormatDirtyDispatchRecoverySummaryFormatsDirtyUnverifiedWithNoEvidenceNote()
    {
        var summary = ConsoleViews.FormatDirtyDispatchRecoverySummary(DirtyUnverifiedRecovery);

        Assert.StartsWith("dirty-unverified:", summary, StringComparison.Ordinal);
        Assert.Contains("src/Bar.cs", summary, StringComparison.Ordinal);
        Assert.Contains("none found", summary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("rerun focused verification", summary, StringComparison.OrdinalIgnoreCase);
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
        Assert.Contains("C:\\worktrees\\goal-abc", output, StringComparison.Ordinal);
        Assert.Contains("status --short", output, StringComparison.Ordinal);
        Assert.Contains("add -A", output, StringComparison.Ordinal);
        Assert.Contains("commit -m", output, StringComparison.Ordinal);
        Assert.Contains($"verify-manual {taskNumber} passed", output, StringComparison.Ordinal);
        Assert.Contains("dirty-unverified", output, StringComparison.Ordinal);
    }
}
