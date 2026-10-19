using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each case owns its in-memory kernel/store/provider and private temporary files.
public sealed class AcceptanceCriterionLocalGoalsRefinementTests
{
    private const string LiveCriterion =
        "Test fixture: drive two concurrent goals through dispatch. Developer owns; Acceptance executes. TEST-VERIFIABLE.";

    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task HistoricalCriterion_RefinesWithoutFeasibilityClarification(bool developerExecutes)
    {
        var criterion = AcceptanceCriterionFeasibilityLocalGoalsTests.HistoricalCriterion;
        if (developerExecutes)
            criterion = criterion.Replace("Developer owns; Acceptance executes", "Developer owns; Developer executes", StringComparison.Ordinal);
        var tempDirectory = CreateTempDirectory();
        try
        {
            var (service, kernel, goalId, collaboration) = BuildScenario(tempDirectory, criterion);

            var result = await service.RefineAsync(kernel, goalId);

            Xunit.Assert.Equal(RefinementOutcome.AutoRefined, result.Outcome);
            Xunit.Assert.Equal([criterion], result.Spec.AcceptanceCriteria);
            Xunit.Assert.Empty(result.Spec.OpenQuestions);
            Xunit.Assert.Empty(collaboration.Items);
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Xunit.Fact]
    public async Task LiveCoordination_RetainsClarificationTopicAndDispositions()
    {
        var tempDirectory = CreateTempDirectory();
        try
        {
            var (service, kernel, goalId, collaboration) = BuildScenario(tempDirectory, LiveCriterion);

            var result = await service.RefineAsync(kernel, goalId);

            Xunit.Assert.Equal(RefinementOutcome.AwaitingClarification, result.Outcome);
            Xunit.Assert.Equal([LiveCriterion], result.Spec.AcceptanceCriteria);
            var question = Xunit.Assert.Single(result.Spec.OpenQuestions);
            // Pin the persisted topic contract independently of the classifier's helper.
            var topic = "feasibility-" + Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(LiveCriterion))).ToLowerInvariant()[..16];
            Xunit.Assert.Equal(topic, question.TopicKey);
            Xunit.Assert.Equal("feasibility", question.ForkKind);
            Xunit.Assert.Equal("high", question.BlastRadius);
            Xunit.Assert.Equal("Open", question.Status);
            Xunit.Assert.Equal(LiveCriterion, question.Criterion);
            Xunit.Assert.StartsWith("Criterion infeasible for Acceptance:", question.Question, StringComparison.Ordinal);
            var dispositionLines = question.Question.Split('\n')
                .Select(line => line.Trim()).Where(line => line.Length > 1 && char.IsDigit(line[0]) && line[1] == '.').ToArray();
            Xunit.Assert.Equal(3, dispositionLines.Length);
            Xunit.Assert.StartsWith("1. Re-scope", dispositionLines[0], StringComparison.Ordinal);
            Xunit.Assert.StartsWith("2. Mark OPERATOR-OWNED and post-landing", dispositionLines[1], StringComparison.Ordinal);
            Xunit.Assert.StartsWith("3. Supply the reproducing scenario", dispositionLines[2], StringComparison.Ordinal);
            Xunit.Assert.Contains("re-scope: <replacement criterion>", question.Question, StringComparison.Ordinal);
            Xunit.Assert.Contains("supply-reproducing-scenario: <worker-accessible scenario>", question.Question, StringComparison.Ordinal);
            var item = Xunit.Assert.Single(collaboration.Items);
            Xunit.Assert.Equal(CollaborationItemType.Clarification, item.Type);
            Xunit.Assert.Equal(goalId.Value, item.GoalId);
            Xunit.Assert.Equal(question.Id, item.CorrelationKey);
            Xunit.Assert.Contains(topic, item.Subject, StringComparison.Ordinal);
            Xunit.Assert.Contains(question.Question, item.Body, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    private static (
        GoalRefinementService Service,
        AgentOrchestratorKernel Kernel,
        GoalId GoalId,
        FakeCollaborationItemStore Collaboration) BuildScenario(string tempDirectory, string criterion)
    {
        var json = $$"""
            {
              "behavioralContract": "Exercise the requested behavior.",
              "acceptanceCriteria": [{{JsonSerializer.Serialize(criterion)}}],
              "verificationClass": "TestVerifiable",
              "decisions": [],
              "forks": []
            }
            """;
        var provider = new FakeSmokeProvider(text: json, providerName: "fake-refiner");
        var registry = new InMemoryModelProviderRegistry([provider]);
        var catalog = new ModelFunctionCatalog([
            new ModelFunctionBinding(
                ModelFunctionPurposes.SpecRefiner,
                ModelLane.CheapApi,
                new ModelProfile("fake-refiner", "fake-model", ModelCapability.Text, SubscriptionMode.ApiKey))
        ]);
        var collaboration = new FakeCollaborationItemStore();
        var service = new GoalRefinementService(
            registry, catalog, collaboration,
            new SpecRefinerPrecedentStore(Path.Combine(tempDirectory, "precedents.json")),
            rawOutputDirectory: Path.Combine(tempDirectory, "logs"));
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Integrate the billing system");
        return (service, kernel, goal.Id, collaboration);
    }
}
