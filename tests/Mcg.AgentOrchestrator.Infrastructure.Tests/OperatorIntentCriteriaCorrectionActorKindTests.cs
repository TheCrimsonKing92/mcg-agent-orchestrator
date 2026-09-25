using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class OperatorIntentCriteriaCorrectionActorKindTests
{
    [Xunit.Theory]
    [Xunit.InlineData(OperatorActorKind.Agent, false)]
    [Xunit.InlineData(OperatorActorKind.Human, true)]
    public async Task ProgressIntentUsesActorKindForCriteriaAuthority(
        OperatorActorKind actorKind, bool applies)
    {
        var root = InfrastructureTestSupport.CreateTempDirectory();
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var task = new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer);
            var goal = kernel.CreateGoal("Protect criteria", [task]);
            kernel.SetGoalRefinedSpec(goal.Id, new RefinedSpec(
                "Protect criteria", ["ship it"], VerificationClass.TestVerifiable, [], []));
            var store = new SqliteOperatorIntentStore(
                Path.Combine(root, "operator-intents.db"), Path.Combine(root, "logs"));
            var correction = "CRITERIA CORRECTION: supersedes=\"ship it\"; correction=\"skip it\"";
            await store.EnqueueAsync(new OperatorIntentRecord(
                Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"),
                OperatorIntentVerbs.Progress, goal.Id.Value, task.Id.Value,
                JsonSerializer.Serialize(new ProgressOperatorIntentPayload(WorkTaskStatus.Running, correction)),
                [], "operator", "cli", "local-process", DateTimeOffset.UtcNow,
                ActorKind: actorKind));

            new OperatorIntentCoordinator(store).ExecutePending(kernel, goal);

            if (applies)
            {
                Assert.Equal("operator", Assert.Single(goal.EffectiveAcceptanceCriteriaCorrections).Actor);
                Assert.DoesNotContain(goal.Timeline, item => item.Message.Contains(
                    "CRITERIA_CORRECTION_IGNORED", StringComparison.Ordinal));
            }
            else
            {
                Assert.Empty(goal.EffectiveAcceptanceCriteriaCorrections);
                Assert.Contains(goal.Timeline, item => item.Message.Contains(
                    "CRITERIA_CORRECTION_IGNORED source=agent-intent", StringComparison.Ordinal));
            }
        }
        finally
        {
            SharedTestSupport.RemoveTempDirectory(root);
        }
    }
}
