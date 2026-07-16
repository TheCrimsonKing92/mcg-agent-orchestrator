using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class ChaosGateWorkerResultFallbackTests : ChaosGateTestBase
{
    // Leniency: stdout parse failure (not missing block) also tries committed file
    [Xunit.Fact(DisplayName = "Leniency_AnyStdoutFormatFailureFallsBackToCommittedFile_passes")]
    public void Leniency_AnyStdoutFormatFailureFallsBackToCommittedFile_Passes()
    {
        // Stdout has a block opener but is missing the 'skills' field (format failure).
        // Committed file has the complete canonical block.
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

        // Stdout: block with 'skills' field omitted -> parse fails with "missing field(s)"
        var incompleteBlock = $"""
            WORKER_RESULT:
            files: {relPath}
            commands: dotnet build
            tests: Passed
            commit: {commit}
            blockers: none
            model_fit: adequate
            confidence: high
            END_WORKER_RESULT
            """;

        var logs = Path.Combine(root, "logs");
        File.WriteAllText(Path.Combine(logs, $"{AgentRole.Developer}.out.log"), incompleteBlock);

        // Committed file: complete canonical block
        var completeBlock = WorkerResultBlock(relPath, "dotnet build", "Passed", commit: commit);
        File.WriteAllText(Path.Combine(worktree, "WORKER_RESULT.md"), completeBlock);
        RunGit(worktree, ["add", "-A"], CommittedAt.AddSeconds(5));
        RunGit(worktree, ["commit", "-m", "Add WORKER_RESULT.md"], CommittedAt.AddSeconds(5));

        new BackgroundDispatchRunner(isStillRunning: _ => false)
            .RefreshLatestProcess(kernel, goal.Id, task.Id);

        Assert.Equal(WorkTaskStatus.Completed, task.Status);
        Assert.Equal(0, task.LastVerification!.ExitCode);
    }
}
