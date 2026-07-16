using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class ChaosGateMarkdownOpenerTests : ChaosGateTestBase
{
    // Leniency: markdown-decorated opener and field names pass
    [Xunit.Fact(DisplayName = "Leniency_MarkdownDecoratedOpenerAndFields_passes")]
    public void Leniency_MarkdownDecoratedOpenerAndFields_Passes()
    {
        var root = CreateSeededRepo();
        const string relPath = "src/Feature.cs";
        var (kernel, goal, task, _) = CreateChaosDispatch(
            root, AgentRole.Developer,
            string.Empty,   // stdout filled in below after commit
            string.Empty,
            mutateWorktree: null);

        var worktree = GoalWorktrees.Ensure(root, goal.Id);
        CommitSourceFile(worktree, relPath, "// feature");
        var commit = ReadGit(worktree, ["rev-parse", "--short", "HEAD"]);

        var logs = Path.Combine(root, "logs");
        File.WriteAllText(
            Path.Combine(logs, $"{AgentRole.Developer}.out.log"),
            MarkdownWorkerResultBlock(relPath, "dotnet build", "Passed", commit: commit));

        new BackgroundDispatchRunner(isStillRunning: _ => false)
            .RefreshLatestProcess(kernel, goal.Id, task.Id);

        Assert.Equal(WorkTaskStatus.Completed, task.Status);
        Assert.Equal(0, task.LastVerification!.ExitCode);
    }
}
