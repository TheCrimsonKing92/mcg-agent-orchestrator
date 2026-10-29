using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;
using static InfrastructureTestSupport;

// Parallel-safe: stores are workspace-local and LaunchOverride is scoped by AsyncLocal.
public sealed class GoalRefinementOwnershipAnswerLoopTests
{
    private const string Criterion = "The gate returns the declared readiness.";
    private const string Answer = "Drop the unmatched ownership claim and retain the declared criterion.";

    [Fact]
    public async Task AnsweredOwnershipQuestionAtBriefV1AwaitsClarificationAndNamesRevise()
    {
        var scenario = await CreateScenarioAsync();
        var goal = scenario.Kernel.GetGoal(scenario.GoalId);
        Assert.Equal(1, goal.AuthoritativeBrief.Version);
        var question = Assert.Single(goal.RefinedSpec!.OpenQuestions);
        Assert.Equal("Open", question.Status);
        Assert.Null(question.Answer);
        Assert.Null(question.Criterion);
        var item = Assert.Single(await scenario.Collaboration.ListAsync(goal.Id.Value));
        Assert.True(CollaborationItemLifecycle.IsTerminal(item.Status));
        Assert.Equal(Answer, item.Resolution);

        var readiness = GoalRefinementGate.Evaluate(goal, null, new([item]));

        Assert.Equal(GoalRefinementReadiness.AwaitingClarification, readiness.Readiness);
        Assert.Contains("ownership answer cannot take effect until the brief is revised", readiness.Detail,
            StringComparison.Ordinal);
        Assert.Contains($"revise {goal.Id.Value[..8]} --brief-file <path> --reason-file <path>",
            readiness.Detail, StringComparison.Ordinal);

        var timelineCount = goal.Timeline.Count;
        var launches = 0;
        var previousLaunchOverride = GoalRefinementWorkCoordinator.LaunchOverride;
        GoalRefinementWorkCoordinator.LaunchOverride = (_, _) =>
        {
            launches++;
            return new GoalRefinementWorkLaunchResult(false, null, "test-launch-suppressed");
        };
        try
        {
            var exception = Assert.Throws<InvalidOperationException>(() =>
                GoalDispatchOperations.EnsureRefinedForSpecConsumer(
                    scenario.Kernel, scenario.Workspace, scenario.Providers, goal));
            Assert.Equal(readiness.Detail, exception.Message);
        }
        finally
        {
            GoalRefinementWorkCoordinator.LaunchOverride = previousLaunchOverride;
        }

        Assert.Equal(0, launches);
        Assert.Null(await scenario.Repository.GetOutboxStateAsync(
            GoalRefinementWorkCoordinator.MessageId(goal.Id)));
        Assert.Equal(timelineCount, goal.Timeline.Count);
        Assert.DoesNotContain(goal.Timeline,
            entry => entry.Message == GoalRefinementWorkCoordinator.PendingPolicyReceipt);
        Assert.Equal(timelineCount, (await scenario.Repository.LoadAsync()).GetGoal(goal.Id).Timeline.Count);
    }

    [Fact]
    public async Task RevisedBriefV2SyncsOwnershipAnswerAndGateReturnsReady()
    {
        var scenario = await CreateScenarioAsync();
        scenario.Kernel.ReviseGoalBrief(scenario.GoalId,
            $"Revised ownership declaration.\n\n## Acceptance criteria\n\n1. {Criterion}");
        Assert.Equal(2, scenario.Kernel.GetGoal(scenario.GoalId).AuthoritativeBrief.Version);
        Assert.Equal("Open", Assert.Single(scenario.Kernel.GetGoal(scenario.GoalId)
            .RefinedSpec!.OpenQuestions).Status);
        await scenario.Repository.SaveAsync(scenario.Kernel);
        await scenario.Repository.EnsureOutboxMessageAsync(
            GoalRefinementWorkCoordinator.CreateMessage(scenario.GoalId));

        var result = await RunWorkerAsync(scenario);

        Assert.True(result.Claimed);
        Assert.True(result.Attached);
        var stored = (await scenario.Repository.LoadAsync()).GetGoal(scenario.GoalId);
        var question = Assert.Single(stored.RefinedSpec!.OpenQuestions);
        Assert.Equal("Answered", question.Status);
        Assert.Equal(Answer, question.Answer);
        var decision = Assert.Single(stored.RefinedSpec.Decisions);
        Assert.Equal(Answer, decision.Choice);
        Assert.Equal($"Answered by operator (key: {question.Id}).", decision.Rationale);
        var readiness = GoalRefinementGate.Evaluate(stored,
            await scenario.Repository.GetOutboxStateAsync(GoalRefinementWorkCoordinator.MessageId(stored.Id)),
            new(await scenario.Collaboration.ListAsync(stored.Id.Value)));
        Assert.Equal(GoalRefinementReadiness.Ready, readiness.Readiness);
    }

    [Fact]
    public async Task AnsweredOrdinaryQuestionPendingSyncStillRequiresRepair()
    {
        var scenario = await CreateScenarioAsync(ownershipQuestion: false);
        var readiness = GoalRefinementGate.Evaluate(scenario.Kernel.GetGoal(scenario.GoalId), null,
            new(await scenario.Collaboration.ListAsync(scenario.GoalId.Value)));

        Assert.Equal(GoalRefinementReadiness.RepairRequired, readiness.Readiness);
        Assert.Equal("resolved-answers-pending-sync", readiness.Detail);
    }

    [Fact]
    public async Task SecondWorkerRunOnUnchangedOwnershipGoalAttachesNothing()
    {
        var scenario = await CreateScenarioAsync();
        await scenario.Repository.EnsureOutboxMessageAsync(
            GoalRefinementWorkCoordinator.CreateMessage(scenario.GoalId));
        var first = await RunWorkerAsync(scenario);
        Assert.True(first.Claimed);
        Assert.True(first.Attached); // The initial sync records the terminal item's answer history.
        var before = (await scenario.Repository.LoadAsync()).ExportGoalSnapshot(scenario.GoalId);
        Assert.Equal("Open", Assert.Single(before.RefinedSpec!.OpenQuestions).Status);
        Assert.Null(Assert.Single(before.RefinedSpec.OpenQuestions).Answer);
        var writer = new GoalLifecycleEventWriter(scenario.Workspace.GoalLifecycleEventsDirectory);
        var eventPath = writer.EventFilePath(scenario.GoalId);
        var eventsBefore = File.ReadAllLines(eventPath);
        Assert.Contains(eventsBefore, line => line.Contains("ClarificationNeeded", StringComparison.Ordinal));
        await scenario.Repository.EnsureOutboxMessageAsync(
            GoalRefinementWorkCoordinator.CreateMessage(scenario.GoalId));

        var second = await RunWorkerAsync(scenario);

        Assert.True(second.Claimed); // A missing outbox claim would make the no-attach assertion vacuous.
        Assert.False(second.Attached);
        var after = (await scenario.Repository.LoadAsync()).ExportGoalSnapshot(scenario.GoalId);
        Assert.Equal(before.Timeline.Count, after.Timeline.Count);
        Assert.Equal(before.RefinedSpecVersions.Count, after.RefinedSpecVersions.Count);
        Assert.Equal(JsonSerializer.Serialize(before), JsonSerializer.Serialize(after));
        Assert.Equal(eventsBefore, File.ReadAllLines(eventPath));
    }

    private static Task<GoalRefinementWorkProcessResult> RunWorkerAsync(Scenario scenario) =>
        GoalRefinementWorkCoordinator.ProcessAsync(scenario.Repository, scenario.Workspace,
            scenario.Providers, WorkerProfileCatalog.Default(), scenario.GoalId);

    private static async Task<Scenario> CreateScenarioAsync(bool ownershipQuestion = true)
    {
        var workspace = OrchestratorWorkspace.ForDirectory(CreateTempDirectory());
        var repository = CreateMigratedStateRepository(workspace.SqliteStatePath);
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal($"Resolve ownership.\n\n## Acceptance criteria\n\n1. {Criterion}");
        var topic = ownershipQuestion ? "ownership-v1-0123456789abcdef" : "api-version";
        var questionId = $"{GoalRefinementService.CorrelationKeyPrefix}{goal.Id.Value}:{topic}";
        kernel.SetGoalRefinedSpec(goal.Id, new RefinedSpec("Keep declared ownership.", [Criterion],
            VerificationClass.TestVerifiable, [],
            [new RefinedSpecOpenQuestion(questionId, "Which declared criterion owns this claim?",
                ownershipQuestion ? "ownership-lifecycle" : "external-contract", "Open", TopicKey: topic)]));
        await repository.SaveAsync(kernel);
        var collaboration = CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory);
        await collaboration.RaiseAsync(CollaborationItemType.Clarification, goal.Id.Value,
            "Ownership question", "Choose the declared evidence owner.", questionId);
        Assert.True(await collaboration.TryResolveAsync(questionId, Answer));
        return new(workspace, repository, kernel, goal.Id, collaboration,
            new InMemoryModelProviderRegistry([]));
    }

    private sealed record Scenario(
        OrchestratorWorkspace Workspace,
        SqliteOrchestratorStateRepository Repository,
        AgentOrchestratorKernel Kernel,
        GoalId GoalId,
        CollaborationItemStore Collaboration,
        IModelProviderRegistry Providers);
}
