using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;

public sealed class ConductorBatchLoopTestsPromptRolloutWatchLanding(ITestOutputHelper output) : ConductorBatchLoopTests(output)
{
    [Xunit.Fact]
    public void LandingSinkPersistsNovelPhrasesAndReceiptReplayDoesNotResetWatch()
    {
        using var repo = new PromptRolloutTestRepository();
        var now = PromptRolloutWatchEvaluationTests.LandedAt;
        var kernel = new AgentOrchestratorKernel(new PromptRolloutWatchEvaluationTests.FixedClock());
        var goal = kernel.CreateGoal("Prompt landing");
        var driver = MakeDriver();
        var receipt = new ConductorLandingReceipt(goal.Id.Value, [PromptRolloutPhraseStep.RoleRequirementsPath], repo.LandingSha);
        var chainedReceipts = 0;
        driver.SuccessfulLandingSink = _ => chainedReceipts++;
        new ConductorBatchLoop(workspace: OrchestratorWorkspace.ForDirectory(repo.Root), utcNow: () => now, measuredSweep: _ =>
        {
            driver.SuccessfulLandingSink!(receipt);
            now = now.AddHours(1);
            driver.SuccessfulLandingSink!(receipt);
            return null;
        }).Run(kernel, driver, ConductorAutonomyPolicy.Conservative, NoStopPath(), maxIterations: 1);
        var reloaded = Assert.Single(new PromptRolloutWatchStore(repo.StorePath).Load(_ => { }));
        Assert.Equal(repo.LandingSha, reloaded.LandingSha);
        Assert.Equal(goal.Id.Value, reloaded.LandingGoalId);
        Assert.Equal(PromptRolloutWatchEvaluationTests.LandedAt, reloaded.LandedAt);
        Assert.Contains("negative_control", reloaded.Phrases);
        Assert.Contains("revert-src", reloaded.Phrases);
        Assert.Equal("open", reloaded.State);
        Assert.Equal(2, chainedReceipts);
    }

    [Xunit.Fact]
    public void LandingSinkWithNoPromptChangeCreatesNoWatch()
    {
        using var repo = new PromptRolloutTestRepository();
        var coordinator = new PromptRolloutWatchCoordinator(repo.Root, new(repo.StorePath), _ => { });
        var kernel = new AgentOrchestratorKernel(new PromptRolloutWatchEvaluationTests.FixedClock());
        var goal = kernel.CreateGoal("Ordinary landing");
        var driver = MakeDriver();
        new ConductorBatchLoop(promptRolloutWatch: coordinator, measuredSweep: _ =>
        {
            driver.SuccessfulLandingSink!(new(goal.Id.Value, ["src/Other.cs"], repo.LandingSha));
            return null;
        }).Run(kernel, driver, ConductorAutonomyPolicy.Conservative, NoStopPath(), maxIterations: 1);
        Assert.Empty(new PromptRolloutWatchStore(repo.StorePath).Load(_ => { }));
        Assert.False(File.Exists(repo.StorePath));
    }

    [Xunit.Fact]
    public void LoopTickWritesExactlyOneTypedConductEventAgainstLandingGoal()
    {
        using var repo = new PromptRolloutTestRepository();
        var landedAt = PromptRolloutWatchEvaluationTests.LandedAt;
        var kernel = new AgentOrchestratorKernel(new PromptRolloutWatchEvaluationTests.FixedClock());
        var landing = kernel.CreateGoal("Prompt landing");
        var store = new PromptRolloutWatchStore(repo.StorePath);
        store.Append(new(repo.LandingSha, landing.Id.Value, landedAt, ["negative_control", "revert-src"], [], [], []));
        foreach (var id in new[] { "aaaaaaaa", "bbbbbbbb", "dddddddd" })
            PromptRolloutWatchEvaluationTests.AddEvent(kernel, id, AgentRole.Tester, ProgressKind.TaskRetried,
                "revert-src", landedAt.AddSeconds(1));
        var logPath = Path.Combine(repo.Root, "conduct-events.log");
        var writer = new ConductEventLogWriter(logPath, utcNow: () => landedAt);
        var coordinator = new PromptRolloutWatchCoordinator(repo.Root, store, line => ConductorBatchLoop.EmitProgress(line));
        new ConductorBatchLoop(promptRolloutWatch: coordinator, conductEventLogWriter: writer,
            utcNow: () => landedAt).Run(kernel, MakeDriver(), ConductorAutonomyPolicy.Conservative,
                NoStopPath(), maxIterations: 2);
        var records = File.ReadAllLines(logPath).Select(line => JsonSerializer.Deserialize<JsonElement>(line)).ToArray();
        var suspect = Assert.Single(records, record => record.GetProperty("eventKind").GetString() == "prompt-rollout-suspect");
        Assert.Equal(landing.Id.Value, suspect.GetProperty("goalId").GetString());
        Assert.Equal($"PROMPT_ROLLOUT_SUSPECT landing={repo.LandingSha} goal={landing.Id.Value} " +
            "phrases=revert-src goals=aaaaaaaa,bbbbbbbb,dddddddd", suspect.GetProperty("detail").GetString());
        Assert.Equal("fired", Assert.Single(new PromptRolloutWatchStore(repo.StorePath).Load(_ => { })).State);
    }
}
