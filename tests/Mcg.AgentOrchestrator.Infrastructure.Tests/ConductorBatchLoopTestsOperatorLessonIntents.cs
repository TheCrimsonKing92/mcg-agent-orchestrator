using System.Security.Cryptography;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(TestCollections.DotnetBuildSlots)]
public sealed class ConductorBatchLoopTestsOperatorLessonIntents : ConductorBatchLoopTests
{
    public ConductorBatchLoopTestsOperatorLessonIntents(ITestOutputHelper output) : base(output) { }

    [Fact]
    public async Task WorkspaceLessonAppliesOnceWithNoLoadedGoals()
    {
        using var fixture = new OperatorLessonHarness();
        var proof = Path.Combine(fixture.Root, "proof.txt");
        var secondProof = Path.Combine(fixture.Root, "second-proof.txt");
        File.WriteAllText(proof, "verified proof\n");
        File.WriteAllText(secondProof, "second receipt\n");
        var intent = await fixture.Record(["operator-evidence:proof.txt", "operator-evidence:second-proof.txt"], ["conductor", "tests"],
            kind: OperatorActorKind.Agent);
        Assert.DoesNotContain(OperatorIntentScopes.Workspace,
            await fixture.IntentStore.ListActionableGoalIdsAsync());
        var reloadCalls = new List<string>();
        var progress = new List<string>();
        var loop = new ConductorBatchLoop(operatorIntents: fixture.Coordinator,
            utcNow: () => fixture.Now,
            goalReloadObservation: id =>
            {
                reloadCalls.Add(id);
                return new ConductorGoalReloadObservation.NotObserved();
            });
        var result = loop.Run(new AgentOrchestratorKernel(), MakeDriver(),
            ConductorAutonomyPolicy.Conservative, NoStopPath(), maxIterations: 2,
            onTick: tick =>
            {
                if (tick.ProgressLines is not null) progress.AddRange(tick.ProgressLines);
            });
        Assert.True(result.Ticks >= 1);

        Assert.Equal(OperatorIntentStatus.Applied, (await fixture.IntentStore.GetAsync(intent.Id))!.Status);
        var lesson = Assert.Single(fixture.LessonStore.List());
        Assert.Equal("When fixture fails", lesson.Situation);
        Assert.Equal("Inspect the exact receipt", lesson.Rule);
        Assert.Equal(["conductor", "tests"], lesson.AppliesTo);
        Assert.Equal(OperatorActorKind.Agent, lesson.ActorKind);
        Assert.Equal(fixture.Now, lesson.RecordedAt);
        Assert.Equal(2, lesson.Evidence.Count);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(proof))).ToLowerInvariant(),
            lesson.Evidence[0].ContentHash);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(secondProof))).ToLowerInvariant(),
            lesson.Evidence[1].ContentHash);
        Assert.DoesNotContain(OperatorIntentScopes.Workspace, reloadCalls);
        Assert.DoesNotContain(progress, line => line.Contains("awaiting-goal-reload", StringComparison.Ordinal));
    }
}
