using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class ChaosGateMarkdownHeadingTests : ChaosGateTestBase
{
    // Leniency: ## WORKER_RESULT heading-style opener
    [Xunit.Fact(DisplayName = "Leniency_MarkdownHeadingOpener_passes")]
    public void Leniency_MarkdownHeadingOpener_Passes()
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

        // Use a markdown heading "## WORKER_RESULT:" as the opener
        var block = $"""
            ## WORKER_RESULT:
            files: {relPath}
            commands: dotnet build
            tests: Passed
            commit: {commit}
            blockers: none
            model_fit: adequate
            skills: dotnet-windows-build-hygiene
            confidence: high
            END_WORKER_RESULT
            """;

        var logs = Path.Combine(root, "logs");
        File.WriteAllText(Path.Combine(logs, $"{AgentRole.Developer}.out.log"), block);

        new BackgroundDispatchRunner(isStillRunning: _ => false)
            .RefreshLatestProcess(kernel, goal.Id, task.Id);

        Assert.Equal(WorkTaskStatus.Completed, task.Status);
        Assert.Equal(0, task.LastVerification!.ExitCode);
    }
}
