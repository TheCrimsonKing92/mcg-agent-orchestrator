using Mcg.AgentOrchestrator.Infrastructure;

public sealed class WorkerGitContextTests
{
    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public void ReviewerMergeTreeFailurePreservesWhetherGitStarted(bool processStarted)
    {
        var workspace = Path.Combine(Path.GetTempPath(), $"mcg-worker-git-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(workspace, ".git"));
        try
        {
            var context = new WorkerGitContext((_, _, _) =>
                new GitCli.GitResult(
                    ExitCode: 2,
                    Output: string.Empty,
                    Error: processStarted ? "git returned an error" : "git could not start",
                    ProcessStarted: processStarted));

            var exception = Xunit.Assert.Throws<ReviewerMergeTreeStatusException>(() =>
                context.ReadReviewerMergeTreeStatus(workspace));

            Xunit.Assert.Equal(processStarted, exception.GitProcessStarted);
        }
        finally
        {
            try { Directory.Delete(workspace, recursive: true); } catch { }
        }
    }
}
