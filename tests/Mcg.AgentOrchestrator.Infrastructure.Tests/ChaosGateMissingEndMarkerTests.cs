using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class ChaosGateMissingEndMarkerTests : ChaosGateTestBase
{
    // Leniency: missing END_WORKER_RESULT parses to EOF
    [Xunit.Fact(DisplayName = "Leniency_MissingEndMarker_treatsEofAsTerminator_and_passes")]
    public void Leniency_MissingEndMarker_TreatsEofAsTerminator()
    {
        var root = CreateSeededRepo();
        const string relPath = "src/Feature.cs";

        // Build a WORKER_RESULT block WITHOUT the END_WORKER_RESULT terminator
        var blockWithoutEnd = WorkerResultBlock(relPath, "dotnet build", "Passed")
            .Replace("\n            END_WORKER_RESULT\n", "\n", StringComparison.Ordinal)
            .Replace("\r\nEND_WORKER_RESULT\r\n", "\r\n", StringComparison.Ordinal)
            .Replace("END_WORKER_RESULT", "", StringComparison.Ordinal)
            .TrimEnd();

        var (kernel, goal, task, _) = CreateChaosDispatch(
            root, AgentRole.Developer, blockWithoutEnd, string.Empty,
            mutateWorktree: wt => CommitSourceFile(wt, relPath, "// feature"));

        new BackgroundDispatchRunner(isStillRunning: _ => false)
            .RefreshLatestProcess(kernel, goal.Id, task.Id);

        // Parser must tolerate missing END_WORKER_RESULT and treat EOF as terminator
        Assert.Equal(WorkTaskStatus.Completed, task.Status);
        Assert.Equal(0, task.LastVerification!.ExitCode);
    }
}
