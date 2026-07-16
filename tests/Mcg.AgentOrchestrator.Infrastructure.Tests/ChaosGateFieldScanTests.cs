using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class ChaosGateFieldScanTests : ChaosGateTestBase
{
    // Leniency: field scan (no opener anywhere)
    [Xunit.Fact(DisplayName = "Leniency_FieldScanNoOpener_passes")]
    public void Leniency_FieldScanNoOpener_Passes()
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

        // Output has all required fields but NO 'WORKER_RESULT:' opener.
        var noOpenerOutput = $"""
            Implementation complete. All tests pass.

            files: {relPath}
            commands: dotnet build
            tests: Passed
            commit: {commit}
            blockers: none
            model_fit: adequate
            skills: dotnet-windows-build-hygiene
            confidence: high
            """;

        var logs = Path.Combine(root, "logs");
        File.WriteAllText(Path.Combine(logs, $"{AgentRole.Developer}.out.log"), noOpenerOutput);

        new BackgroundDispatchRunner(isStillRunning: _ => false)
            .RefreshLatestProcess(kernel, goal.Id, task.Id);

        Assert.Equal(WorkTaskStatus.Completed, task.Status);
        Assert.Equal(0, task.LastVerification!.ExitCode);
    }
}
