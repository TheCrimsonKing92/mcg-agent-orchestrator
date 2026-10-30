using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using static Mcg.AgentOrchestrator.Infrastructure.Tests.LandingExecutorTestsDenylistOwnerHold;

// Each fixture owns its git repository and stores; the collection isolates landing git hooks.
[Collection(TestCollections.LandingGitRunner)]
public sealed class PassedCohortApprovalHoldAttentionTests : AcceptanceCohortWorkflowTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [Trait("Category", "CrossTick")]
    public void HeldTicks_ApprovalApplied_RemovesSoloShapedWaitingItem(int waitingIndex)
    {
        using var scenario = new PassedCohortReceiptApprovalHoldTests.Scenario(waitingIndex);
        var decisions = CollaborationItemStore.ForDirectory(scenario.Workspace.OrchestratorDirectory);
        Assert.Empty(Attention());
        var waiting = scenario.Goals[waitingIndex];
        var candidate = scenario.CandidateRevisions[waitingIndex];
        string? itemId = null;
        for (var tick = 0; tick < 2; tick++)
        {
            scenario.Tick();
            var item = Assert.Single(decisions.GetAttentionQueueAsync().GetAwaiter().GetResult());
            Assert.Equal(item.Id, Assert.Single(Attention()).Id);
            Assert.Equal(CollaborationItemType.Decision, item.Type);
            Assert.Equal(waiting.Id.Value, item.GoalId);
            Assert.False(CollaborationItemLifecycle.IsTerminal(item.Status));
            // Pin the solo approval item's subject, actionable body and goal attribution.
            Assert.Equal($"Landing parked on {LandingExecutor.IntegrationBranchName}", item.Subject);
            Assert.Contains("landing-denylist owner approval required", item.Body, StringComparison.Ordinal);
            Assert.Contains($"goal={waiting.Id.Value[..8]} candidate={candidate}", item.Body, StringComparison.Ordinal);
            Assert.Contains("rule=landing-policy", item.Body, StringComparison.Ordinal);
            Assert.Contains($"path={Protected}", item.Body, StringComparison.Ordinal);
            Assert.Contains("fingerprint=v1:", item.Body, StringComparison.Ordinal);
            Assert.Contains($"approve-policy-change {waiting.Id.Value[..8]} {candidate} --text-file <reason-file>",
                item.Body, StringComparison.Ordinal);
            if (itemId is not null) Assert.Equal(itemId, item.Id);
            itemId = item.Id;
            Assert.All(scenario.Goals, goal => Assert.Equal(GoalStatus.Verified, goal.Status));
        }

        var intents = new SqliteOperatorIntentStore(Path.Combine(scenario.Workspace.OrchestratorDirectory,
            "operator-intents.db"), Path.Combine(scenario.Workspace.OrchestratorDirectory, "logs"));
        var intent = new OperatorIntentRecord(Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"),
            OperatorIntentVerbs.ApprovePolicyChange, waiting.Id.Value, null,
            JsonSerializer.Serialize(new ApprovePolicyChangeOperatorIntentPayload(candidate,
                "Owner reviewed the cohort member's protected content."),
                new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            [], "owner", "cli", "local-process", DateTimeOffset.UtcNow, ActorKind: OperatorActorKind.Human);
        intents.EnqueueAsync(intent).GetAwaiter().GetResult();
        var progress = scenario.Tick(new OperatorIntentCoordinator(intents, decisions: decisions,
            goalStateVersionResolver: _ => 0));

        Assert.Equal(OperatorIntentStatus.Applied, intents.GetAsync(intent.Id).GetAwaiter().GetResult()!.Status);
        Assert.True(AcceptancePolicyChangeDecision.IsApprovedAsync(decisions, waiting.Id.Value, candidate)
            .GetAwaiter().GetResult());
        Assert.Empty(Attention());
        Assert.All(scenario.Goals, goal => Assert.Equal(GoalStatus.Completed, goal.Status));
        Assert.Equal(1, scenario.Verifier.RunCount);
        Assert.Contains(progress, line => line.Contains(
            $"prelanded=true outcome=passed receipt={scenario.Receipt.ReceiptId}", StringComparison.Ordinal));

        IReadOnlyList<CollaborationItem> Attention() => CliCommandHandlers.CollaborationItemsForAttention(
            decisions.GetAttentionQueueAsync().GetAwaiter().GetResult(), scenario.Kernel, null, includeHistory: false);
    }
}
