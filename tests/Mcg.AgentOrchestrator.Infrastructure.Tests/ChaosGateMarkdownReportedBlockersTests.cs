using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class ChaosGateMarkdownReportedBlockersTests : ChaosGateTestBase
{
    // Leniency: markdown format with reported blockers passes (advisory)
    [Xunit.Fact(DisplayName = "Leniency_MarkdownDecoratedReportedBlockers_passes_advisory")]
    public void Leniency_MarkdownDecoratedReportedBlockers_PassesAdvisory()
    {
        var root = CreateSeededRepo();
        const string relPath = "src/Feature.cs";
        var (kernel, goal, task, _) = CreateChaosDispatch(
            root, AgentRole.Developer,
            string.Empty,
            string.Empty,
            mutateWorktree: null);

        var worktree = GoalWorktrees.Ensure(root, goal.Id);
        CommitSourceFile(worktree, relPath, "// feature");
        var commit = ReadGit(worktree, ["rev-parse", "--short", "HEAD"]);

        var block = MarkdownWorkerResultBlock(
            relPath, "dotnet build", "Passed",
            commit: commit,
            blockers: "API rate limit hit; retry after 1h");

        var logs = Path.Combine(root, "logs");
        File.WriteAllText(Path.Combine(logs, $"{AgentRole.Developer}.out.log"), block);

        new BackgroundDispatchRunner(isStillRunning: _ => false)
            .RefreshLatestProcess(kernel, goal.Id, task.Id);

        // Markdown-decorated block with a reported blocker: blockers is advisory, so the relevant
        // committed change on a clean worktree completes the dispatch.
        Assert.Equal(WorkTaskStatus.Completed, task.Status);
        Assert.Equal(0, task.LastVerification!.ExitCode);
    }
}
