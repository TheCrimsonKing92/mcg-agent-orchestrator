using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class ChaosGateMarkdownCommittedFileTests : ChaosGateTestBase
{
    // Leniency: markdown-decorated committed file passes
    [Xunit.Fact(DisplayName = "Leniency_MarkdownDecoratedCommittedFile_passes")]
    public void Leniency_MarkdownDecoratedCommittedFile_Passes()
    {
        var root = CreateSeededRepo();
        const string relPath = "src/Feature.cs";
        var (kernel, goal, task, _) = CreateChaosDispatch(
            root, AgentRole.Developer,
            "Work complete. See WORKER_RESULT.md for result details.",
            string.Empty,
            mutateWorktree: wt =>
            {
                CommitSourceFile(wt, relPath, "// feature");
                var commit = ReadGit(wt, ["rev-parse", "--short", "HEAD"]);
                // Committed file has markdown-decorated WORKER_RESULT block.
                var block = MarkdownWorkerResultBlock(relPath, "dotnet build", "Passed", commit: commit);
                File.WriteAllText(Path.Combine(wt, "WORKER_RESULT.md"), block);
                RunGit(wt, ["add", "-A"], CommittedAt.AddSeconds(5));
                RunGit(wt, ["commit", "-m", "Add WORKER_RESULT.md"], CommittedAt.AddSeconds(5));
            });

        new BackgroundDispatchRunner(isStillRunning: _ => false)
            .RefreshLatestProcess(kernel, goal.Id, task.Id);

        Assert.Equal(WorkTaskStatus.Completed, task.Status);
        Assert.Equal(0, task.LastVerification!.ExitCode);
    }
}
