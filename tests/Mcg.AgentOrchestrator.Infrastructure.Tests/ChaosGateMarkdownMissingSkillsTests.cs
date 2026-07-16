using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class ChaosGateMarkdownMissingSkillsTests : ChaosGateTestBase
{
    // Leniency: markdown format with missing skills passes (advisory)
    [Xunit.Fact(DisplayName = "Leniency_MarkdownDecoratedMissingSkills_passes_advisory")]
    public void Leniency_MarkdownDecoratedMissingSkills_PassesAdvisory()
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

        // Markdown block with all fields EXCEPT skills
        var block = $"""
            **WORKER_RESULT**:
            **files**: {relPath}
            **commands**: dotnet build
            **tests**: Passed
            **commit**: {commit}
            **blockers**: none
            **model_fit**: adequate
            **confidence**: high
            END_WORKER_RESULT
            """;

        var logs = Path.Combine(root, "logs");
        File.WriteAllText(Path.Combine(logs, $"{AgentRole.Developer}.out.log"), block);

        new BackgroundDispatchRunner(isStillRunning: _ => false)
            .RefreshLatestProcess(kernel, goal.Id, task.Id);

        // Skills is advisory; its absence no longer gates the dispatch when git shows a relevant
        // committed change on a clean worktree.
        Assert.Equal(WorkTaskStatus.Completed, task.Status);
        Assert.Equal(0, task.LastVerification!.ExitCode);
    }
}
