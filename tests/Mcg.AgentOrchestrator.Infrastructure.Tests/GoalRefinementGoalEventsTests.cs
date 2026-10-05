using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;
using static InfrastructureTestSupport;

// Parallel-safe: every store, log and provider belongs to this test's private workspace.
public sealed class GoalRefinementGoalEventsTests
{
    [Fact]
    public async Task OffTickRefinement_CommittedDecisions_AppearOnceWithOriginalIdentity()
    {
        var workspace = OrchestratorWorkspace.ForDirectory(CreateTempDirectory());
        var provider = new FakeSmokeProvider(
            text: """
                ```json
                {"behavioralContract":"Mirror committed refinement decisions.","acceptanceCriteria":["Each committed decision is logged once."],"verificationClass":"TestVerifiable","decisions":[],"forks":[]}
                ```
                """,
            providerName: "mirror-refiner");
        var providers = new InMemoryModelProviderRegistry([provider]);
        ModelFunctionCatalogStore.Save(workspace.ModelFunctionCatalogPath, new ModelFunctionCatalog([
            new ModelFunctionBinding(
                ModelFunctionPurposes.SpecRefiner, ModelLane.CheapApi,
                new ModelProfile("mirror-refiner", "fake-model", ModelCapability.Text, SubscriptionMode.ApiKey))
        ]));
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Research and plan a focused implementation with tests.", []);
        kernel.RecordGoalPolicyDecision(goal.Id, "Historical stored-only decision must not be backfilled.");
        var writer = new GoalLifecycleEventWriter(workspace.GoalLifecycleEventsDirectory, kernel: kernel);
        kernel.SetEventWriter(writer);
        GoalRefinementWorkCoordinator.RecordPending(kernel, goal.Id);
        var baseline = kernel.ExportGoalSnapshot(goal.Id);
        var repository = CreateMigratedStateRepository(workspace.SqliteStatePath);
        await repository.TransactWithOutboxAsync(
            (persistedKernel, _) =>
            {
                persistedKernel.ReplaceGoalWithSnapshot(baseline);
                return Task.FromResult((
                    ShouldSave: true,
                    Result: true,
                    OutboxMessages: (IReadOnlyList<OrchestratorStateOutboxMessage>)[
                        GoalRefinementWorkCoordinator.CreateMessage(goal.Id)
                    ]));
            });

        var processed = await GoalRefinementWorkCoordinator.ProcessAsync(
            repository, workspace, providers, WorkerProfileCatalog.Default(), goal.Id);

        Assert.True(processed.Claimed);
        Assert.True(processed.Attached);
        Assert.NotNull(provider.LastRequest);
        var stored = (await repository.LoadAsync()).GetGoal(goal.Id);
        Assert.NotNull(stored.RefinedSpec);
        var decisions = stored.Timeline.Skip(baseline.Timeline.Count)
            .Where(entry => entry.Kind == ProgressKind.GoalPolicyDecision).ToArray();
        Assert.NotEmpty(decisions);
        Assert.Contains(decisions, entry => entry.Message.StartsWith("Spec refiner raw output:", StringComparison.Ordinal));
        Assert.Contains(decisions, entry => entry.Message.StartsWith("spec_refinement outcome=completed", StringComparison.Ordinal));
        var logged = ReadTimeline(writer, goal.Id);
        foreach (var decision in decisions)
        {
            var expected = StateLogEntry.FromProgressEvent(decision);
            Assert.Single(stored.Timeline, entry => StateLogEntry.FromProgressEvent(entry) == expected);
            Assert.Single(logged, entry => entry == expected);
        }
        Assert.Single(logged, entry => entry.Message == GoalRefinementWorkCoordinator.PendingPolicyReceipt);
        Assert.DoesNotContain(logged, entry => entry.Message == "Historical stored-only decision must not be backfilled.");

        var lineCount = File.ReadAllLines(writer.EventFilePath(goal.Id)).Length;
        var replay = await GoalRefinementWorkCoordinator.ProcessAsync(
            repository, workspace, providers, WorkerProfileCatalog.Default(), goal.Id);
        Assert.False(replay.Attached);
        Assert.Equal(lineCount, File.ReadAllLines(writer.EventFilePath(goal.Id)).Length);
    }

    private static StateLogEntry[] ReadTimeline(GoalLifecycleEventWriter writer, GoalId goalId) =>
        File.ReadAllLines(writer.EventFilePath(goalId))
            .Select(StateLogDivergenceComparer.ParseLine).Where(line => line is not null)
            .Select(line => line!.Entry).ToArray();
}
