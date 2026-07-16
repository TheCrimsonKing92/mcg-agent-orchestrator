using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class ChaosGateMarkdownBlockersNoneTests : ChaosGateTestBase
{
    // Leniency: markdown blockers: none with note passes
    [Xunit.Fact(DisplayName = "Leniency_MarkdownBlockersNoneWithNotes_passes")]
    public void Leniency_MarkdownBlockersNoneWithNotes_Passes()
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

        // Markdown-decorated key + 'none - ...' value
        var block = MarkdownWorkerResultBlock(
            relPath, "dotnet build", "Passed",
            commit: commit,
            blockers: "none - no edge cases outstanding");

        var logs = Path.Combine(root, "logs");
        File.WriteAllText(Path.Combine(logs, $"{AgentRole.Developer}.out.log"), block);

        new BackgroundDispatchRunner(isStillRunning: _ => false)
            .RefreshLatestProcess(kernel, goal.Id, task.Id);

        Assert.Equal(WorkTaskStatus.Completed, task.Status);
        Assert.Equal(0, task.LastVerification!.ExitCode);
    }
}
