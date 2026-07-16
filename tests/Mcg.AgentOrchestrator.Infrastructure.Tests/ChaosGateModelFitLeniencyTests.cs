using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class ChaosGateModelFitLeniencyTests : ChaosGateTestBase
{
    // Leniency: model_fit is just the keyword 'adequate'
    [Xunit.Fact(DisplayName = "Leniency_ModelFitAdequateOnly_passes")]
    public void Leniency_ModelFitAdequateOnly_Passes()
    {
        var root = CreateSeededRepo();
        const string relPath = "src/Feature.cs";
        var (kernel, goal, task, _) = CreateChaosDispatch(
            root, AgentRole.Developer,
            WorkerResultBlock(relPath, "dotnet build", "Passed", modelFit: "adequate"),
            string.Empty,
            mutateWorktree: wt => CommitSourceFile(wt, relPath, "// feature"));

        new BackgroundDispatchRunner(isStillRunning: _ => false)
            .RefreshLatestProcess(kernel, goal.Id, task.Id);

        Assert.Equal(WorkTaskStatus.Completed, task.Status);
        Assert.Equal(0, task.LastVerification!.ExitCode);
    }
}
