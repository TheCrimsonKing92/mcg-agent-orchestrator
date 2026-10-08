using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: all stores are under the fixture's private root; driver and model are injected.
public sealed class ConductorBatchLoopTestsStewardCaseFRoute : ConductorBatchLoopTests
{
    public ConductorBatchLoopTestsStewardCaseFRoute(ITestOutputHelper output) : base(output) { }

    [Xunit.Theory]
    [Xunit.InlineData(AgentRole.Reviewer)]
    [Xunit.InlineData(AgentRole.Tester)]
    public async Task Answered_blocker_routes_once_then_repeat_requires_owner(AgentRole role)
    {
        using var harness = new StewardCaseFHarness(role);
        Assert.Single(harness.Detector.Detect(harness.Goal));
        Assert.Equal(WorkTaskStatus.Completed, harness.Developer.Status);
        new ConductorBatchLoop().WithSteward(harness.Host).Run(
            harness.Kernel, MakeDriver(utcNow: () => harness.Clock.UtcNow),
            ConductorAutonomyPolicy.Conservative, Path.Combine(harness.Root, "stop.signal"),
            maxIterations: 2, watchInterval: TimeSpan.FromMilliseconds(1), sleepFunc: _ => false,
            keepAliveWhenIdle: true, persistGoalTick: (_, _) => { });

        var intent = Assert.Single(await harness.Intents.ListForGoalAsync(harness.Goal.Id.Value));
        var payload = JsonSerializer.Deserialize<AdjudicateOperatorIntentPayload>(intent.PayloadJson,
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(OperatorIntentVerbs.Adjudicate, intent.Verb);
        Assert.Equal(harness.Developer.Id.Value, intent.TaskId);
        Assert.Equal("route", payload.Shape);
        Assert.Equal("ContractClarification", payload.Cause);
        Assert.Equal("reversible", payload.Reversibility);
        Assert.Contains(StewardCaseFHarness.Blocker, payload.Text);
        Assert.Contains(StewardCaseFHarness.Answer, payload.Text);
        Assert.Contains("Apply the answered clarification verbatim", payload.Text);
        Assert.DoesNotContain("rename the class", payload.Text);
        Assert.Contains("steward-case=F", payload.EvidenceReferences);
        Assert.Contains($"clarification-request={harness.Request.Id.Value}", payload.EvidenceReferences);
        Assert.Contains($"clarification-answer={StewardCaseFHarness.Answer}", payload.EvidenceReferences);
        Assert.Equal(0, harness.Model.Calls);

        var decisions = CollaborationItemStore.ForDirectory(harness.Root);
        var coordinator = new OperatorIntentCoordinator(harness.Intents,
            decisions: decisions, goalStateVersionResolver: _ => 7,
            evidenceResolver: new AdjudicationEvidenceResolver(harness.Root));
        coordinator.ExecutePending(harness.Kernel, harness.Goal);
        coordinator.CompletePersisted([harness.Goal.Id]);
        Assert.Equal(OperatorIntentStatus.Applied, (await harness.Intents.GetAsync(intent.Id))!.Status);
        Assert.Equal(WorkTaskStatus.Assigned, harness.Developer.Status);
        Assert.Equal(RetryCause.ContractClarification, harness.Developer.PendingRetryCause);
        Assert.Contains(StewardCaseFHarness.Answer, harness.Developer.AcceptedRetryFeedback!.Message);
        harness.Host.ServiceTick(harness.Kernel);

        harness.CompleteDeveloper(harness.Developer);
        harness.Kernel.RetryTask(harness.Goal.Id, harness.BlockerTask.Id,
            "Recheck the same candidate after the Developer correction.",
            RetryCause.ContractClarification, invalidateDownstream: false);
        Assert.Equal(WorkTaskStatus.Assigned, harness.BlockerTask.Status);
        harness.RecordBlocker();
        harness.Clock.Advance();
        harness.Kernel.SupersedeHumanInput(harness.Goal.Id, harness.Request.Id,
            StewardCaseFHarness.Answer, HumanInputAnswerOrigin.Operator);
        harness.Host.ServiceTick(harness.Kernel);
        Assert.Equal("steward-owner-question", harness.Goal.CurrentHold?.State);
        Assert.Contains("case=F", harness.Goal.CurrentHold!.Blocker);
        Assert.Single(await harness.Intents.ListForGoalAsync(harness.Goal.Id.Value));
        Assert.True(harness.Triggers.HasAppliedRoute(Assert.Single(harness.Detector.Detect(harness.Goal))));
        Assert.Equal(0, harness.Model.Calls);
    }

    [Xunit.Fact]
    public async Task Corrected_answer_discards_unharvested_old_route()
    {
        using var harness = new StewardCaseFHarness(AgentRole.Reviewer);
        harness.Host.ServiceTick(harness.Kernel);
        Assert.NotNull(harness.Host.CurrentRound);
        harness.Clock.Advance();
        harness.Kernel.SupersedeHumanInput(harness.Goal.Id, harness.Request.Id,
            "Revised answer: emit no payload.", HumanInputAnswerOrigin.Operator);
        harness.Host.ServiceTick(harness.Kernel);

        Assert.Empty(await harness.Intents.ListForGoalAsync(harness.Goal.Id.Value));
        Assert.Contains("reason=trigger-superseded", File.ReadAllText(Path.Combine(harness.Root, "conduct-events.log")));
        Assert.Equal(0, harness.Model.Calls);
    }

    [Xunit.Theory]
    [Xunit.InlineData("DeveloperGateReopenNoCommit")]
    [Xunit.InlineData("DeveloperReviewerFindingNoCommit")]
    public void Cases_D_and_E_still_require_model_round(string kind)
    {
        using var harness = new StewardCaseFHarness(AgentRole.Reviewer);
        var trigger = Assert.Single(harness.Detector.Detect(harness.Goal)) with
        { Kind = Enum.Parse<ConductorStewardTriggerKind>(kind) };
        Assert.Null(new ConductorStewardDeterministicRoute().TryBuild(trigger, harness.Root));
    }
}
