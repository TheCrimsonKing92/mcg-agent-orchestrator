using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: each case owns its durable stores and policy file.
public sealed class ConductorExperimentFlagRevertControllerTests
{
    [Theory]
    [InlineData("revert", false)]
    [InlineData("inconclusive", true)]
    public void RevertOrGuardrail_SubmitsOnceRestoresPriorAndSurvivesRestart(string verdict, bool breached)
    {
        using var fixture = new ExperimentFlagTestFixture();
        var record = fixture.Add();
        fixture.Submit(record.Id);
        fixture.Tick();
        record = fixture.Experiments.ResolveAsync(record.Id).GetAwaiter().GetResult()!;
        var controller = new ConductorExperimentFlagRevertController(fixture.Experiments, () => fixture.Intents, fixture.PolicyPath);
        var reading = Reading(verdict, breached);
        Assert.True(controller.TryRevert(record, reading, ExperimentFlagTestFixture.Now));
        var revert = Assert.Single(fixture.Intents.ListForGoalAsync(OperatorIntentScopes.Workspace).GetAwaiter().GetResult()
            .Where(intent => intent.Verb == OperatorIntentVerbs.ExperimentRevertFlag));
        Assert.Equal(OperatorActorKind.Agent, revert.ActorKind);
        Assert.Equal("conductor", revert.Actor);
        Assert.Equal(OperatorIntentAdjudication.StewardAssurance, revert.AuthenticationAssurance);
        var decided = fixture.Experiments.ResolveAsync(record.Id).GetAwaiter().GetResult()!;
        Assert.Equal(ExperimentOutcomeState.Refuted, decided.Outcome);
        Assert.Equal($"operator-intent:{revert.Id}", decided.Decision!.Evidence);
        fixture.Tick();
        Assert.Equal(OperatorIntentStatus.Applied, fixture.Result(revert).Status);
        Assert.False(ConductorAutonomyPolicy.ParseJson(File.ReadAllText(fixture.PolicyPath)).FollowerGatesEnabled);
        Assert.False(controller.TryRevert(record, reading, ExperimentFlagTestFixture.Now));
        var restarted = new ConductorExperimentFlagRevertController(new ExperimentStore(fixture.Workspace.ExperimentStorePath),
            () => SqliteOperatorIntentStore.ForDirectories(fixture.Workspace.OrchestratorDirectory, fixture.Workspace.LogDirectory), fixture.PolicyPath);
        Assert.False(restarted.TryRevert(decided, reading, ExperimentFlagTestFixture.Now));
        Assert.Single(fixture.Intents.ListForGoalAsync(OperatorIntentScopes.Workspace).GetAwaiter().GetResult()
            .Where(intent => intent.Verb == OperatorIntentVerbs.ExperimentRevertFlag));
    }

    [Fact]
    public void RestartAfterEnqueueBeforeDecision_ReusesPersistedIntent()
    {
        using var fixture = new ExperimentFlagTestFixture();
        var record = fixture.Add();
        fixture.Submit(record.Id);
        fixture.Tick();
        record = fixture.Experiments.ResolveAsync(record.Id).GetAwaiter().GetResult()!;
        var existing = fixture.Intents.EnqueueAsync(RevertIntent(record.Id)).GetAwaiter().GetResult();
        var controller = new ConductorExperimentFlagRevertController(new ExperimentStore(fixture.Workspace.ExperimentStorePath), () => fixture.Intents, fixture.PolicyPath);
        Assert.True(controller.TryRevert(record, Reading("revert"), ExperimentFlagTestFixture.Now.AddMinutes(1)));
        var decided = fixture.Experiments.ResolveAsync(record.Id).GetAwaiter().GetResult()!;
        Assert.Equal($"operator-intent:{existing.Id}", decided.Decision!.Evidence);
        Assert.Single(fixture.Intents.ListForGoalAsync(OperatorIntentScopes.Workspace).GetAwaiter().GetResult()
            .Where(intent => intent.Verb == OperatorIntentVerbs.ExperimentRevertFlag));
        fixture.Tick();
        Assert.False(ConductorAutonomyPolicy.ParseJson(File.ReadAllText(fixture.PolicyPath)).FollowerGatesEnabled);
    }

    [Fact]
    public void FailedSubmission_LeavesExperimentOpenAndFileUntouched()
    {
        using var fixture = new ExperimentFlagTestFixture();
        var record = fixture.Add();
        fixture.Submit(record.Id);
        fixture.Tick();
        record = fixture.Experiments.ResolveAsync(record.Id).GetAwaiter().GetResult()!;
        fixture.Intents.EnqueueAsync(RevertIntent(record.Id) with { Actor = "different" }).GetAwaiter().GetResult();
        var before = File.ReadAllBytes(fixture.PolicyPath);
        var controller = new ConductorExperimentFlagRevertController(fixture.Experiments, () => fixture.Intents, fixture.PolicyPath);
        Assert.Throws<InvalidOperationException>(() => controller.TryRevert(record, Reading("revert"), ExperimentFlagTestFixture.Now));
        Assert.Equal(ExperimentOutcomeState.Open, fixture.Experiments.ResolveAsync(record.Id).GetAwaiter().GetResult()!.Outcome);
        Assert.Equal(before, File.ReadAllBytes(fixture.PolicyPath));
    }

    [Theory]
    [InlineData("keep", true)]
    [InlineData("revert", false)]
    public void KeepOrUnapplied_DoesNotEnqueueOrDecide(string verdict, bool applied)
    {
        using var fixture = new ExperimentFlagTestFixture();
        var record = fixture.Add();
        if (applied)
        {
            fixture.Submit(record.Id);
            fixture.Tick();
            record = fixture.Experiments.ResolveAsync(record.Id).GetAwaiter().GetResult()!;
        }
        var before = File.ReadAllBytes(fixture.PolicyPath);
        var controller = new ConductorExperimentFlagRevertController(fixture.Experiments, () => fixture.Intents, fixture.PolicyPath);
        Assert.False(controller.TryRevert(record, Reading(verdict), ExperimentFlagTestFixture.Now));
        Assert.DoesNotContain(fixture.Intents.ListForGoalAsync(OperatorIntentScopes.Workspace).GetAwaiter().GetResult(),
            intent => intent.Verb == OperatorIntentVerbs.ExperimentRevertFlag);
        Assert.Equal(ExperimentOutcomeState.Open, fixture.Experiments.ResolveAsync(record.Id).GetAwaiter().GetResult()!.Outcome);
        Assert.Equal(before, File.ReadAllBytes(fixture.PolicyPath));
    }

    private static ExperimentReadingResult Reading(string verdict, bool breached = false) =>
        new(0, new(99, ExperimentStopUnit.Goals), false, [], verdict, "test reading", breached);

    private static OperatorIntentRecord RevertIntent(string experimentId)
    {
        var id = Guid.NewGuid().ToString("n");
        return new(id, $"experiment-revert-flag:{experimentId}", OperatorIntentVerbs.ExperimentRevertFlag,
            OperatorIntentScopes.Workspace, null,
            JsonSerializer.Serialize(new ExperimentRevertFlagOperatorIntentPayload(experimentId), OperatorIntentJson.Options),
            [], "conductor", "conductor-experiment-revert", OperatorIntentAdjudication.StewardAssurance,
            ExperimentFlagTestFixture.Now, ActorKind: OperatorActorKind.Agent);
    }
}
