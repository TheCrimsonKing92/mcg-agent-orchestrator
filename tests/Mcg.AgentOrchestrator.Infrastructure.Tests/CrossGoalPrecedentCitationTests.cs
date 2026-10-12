using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;

public sealed class CrossGoalPrecedentCitationTests
{
    private const string ForkKind = "ownership-lifecycle";
    private const string OperatorQuestion = "How are operator access credentials issued and rotated?";
    private const string WorkerQuestion = "How should worker failures be classified for retries?";

    [Fact]
    public async Task UnlinkedLegacyOriginWithDifferentQuestionRaisesClarification()
    {
        using var scenario = new Scenario();
        var originGoal = scenario.Kernel.CreateGoal("Configure operator access.");
        var origin = await scenario.InsertLegacyOriginAsync(originGoal.Id);
        await scenario.RecordUnlinkedPrecedentAsync();
        var goal = scenario.Kernel.CreateGoal("Implement worker failure classification.");
        Assert.NotEqual(origin.GoalId, goal.Id.Value);

        var result = await scenario.RefineAsync(goal.Id, WorkerQuestion);

        await AssertClarificationAsync(scenario, goal.Id, result);
        Assert.DoesNotContain("legacy:", JsonSerializer.Serialize(goal.RefinedSpec), StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnlinkedLegacyOriginWithMatchingQuestionIsCitedAsCarried()
    {
        using var scenario = new Scenario();
        var originGoal = scenario.Kernel.CreateGoal("Configure operator access.");
        var origin = await scenario.InsertLegacyOriginAsync(originGoal.Id);
        await scenario.RecordUnlinkedPrecedentAsync();
        var goal = scenario.Kernel.CreateGoal("Configure operator access for another goal.");
        var question = OperatorQuestion.ToUpperInvariant();
        Assert.Equal(
            GoalRefinementService.BuildNormalizedQuestionKey(ForkKind, OperatorQuestion),
            GoalRefinementService.BuildNormalizedQuestionKey(ForkKind, question));

        var result = await scenario.RefineAsync(goal.Id, question);

        Assert.Equal(RefinementOutcome.AutoRefined, result.Outcome);
        var decision = Assert.Single(result.Spec.Decisions);
        Assert.Equal("Database", decision.Choice);
        Assert.StartsWith($"Carried clarification precedent from goal {originGoal.Id.Value[..8]}", decision.Rationale);
        Assert.Contains(origin.Id, decision.Rationale, StringComparison.Ordinal);
        Assert.Contains($"topic: {ForkKind}", decision.Rationale, StringComparison.Ordinal);
        Assert.Contains("brief: unknown", decision.Rationale, StringComparison.Ordinal);
        Assert.DoesNotContain("legacy:", decision.Rationale, StringComparison.Ordinal);
        Assert.DoesNotContain("Authoritative clarification precedent", decision.Rationale, StringComparison.Ordinal);
        Assert.Empty(result.Spec.ClarificationAnswerHistory);
        Assert.Empty(await scenario.Store.ListAsync(goal.Id.Value));
    }

    [Fact]
    public async Task UnlinkedPrecedentWithoutAnyOriginRaisesClarification()
    {
        using var scenario = new Scenario();
        await scenario.RecordUnlinkedPrecedentAsync();
        Assert.Empty(await scenario.Store.ListAsync());
        var goal = scenario.Kernel.CreateGoal("Implement worker failure classification.");

        var result = await scenario.RefineAsync(goal.Id, WorkerQuestion);

        await AssertClarificationAsync(scenario, goal.Id, result);
    }

    [Fact]
    public async Task CompleteLinkedOriginWithDifferentQuestionRaisesClarification()
    {
        using var scenario = new Scenario();
        var originGoal = scenario.Kernel.CreateGoal("Configure operator access.");
        var origin = await scenario.CreateAnsweredOriginAsync(originGoal.Id);
        await scenario.Precedents.RecordPrecedentAsync(
            ForkKind, "Database", "Operator clarification answer.",
            originItemId: origin.Id,
            originGoalId: origin.GoalId,
            originAnswerId: origin.AuthoritativeAnswer!.Id,
            originBriefVersion: origin.AuthoritativeAnswer.BriefVersion);
        var precedent = await scenario.Precedents.TryGetPrecedentAsync(ForkKind);
        Assert.NotNull(precedent);
        Assert.True(precedent.HasCompleteOriginReference);
        Assert.Equal(1, precedent.OriginBriefVersion);
        var goal = scenario.Kernel.CreateGoal("Implement worker failure classification.");

        var result = await scenario.RefineAsync(goal.Id, WorkerQuestion);

        await AssertClarificationAsync(scenario, goal.Id, result);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SameGoalNullOriginRecoveryPreservesAuthoritativeCitation(bool realAnswer)
    {
        using var scenario = new Scenario();
        var goal = scenario.Kernel.CreateGoal("Implement worker failure classification.");
        var origin = realAnswer
            ? await scenario.CreateAnsweredOriginAsync(goal.Id)
            : await scenario.InsertLegacyOriginAsync(goal.Id);
        await scenario.RecordUnlinkedPrecedentAsync();
        const string topic = "worker-failure-classifier";
        // Both identities differ, so the earlier resolved-clarification branch cannot answer this fork.
        Assert.NotEqual(ForkKind, topic);
        Assert.NotEqual(
            GoalRefinementService.BuildNormalizedQuestionKey(ForkKind, OperatorQuestion),
            GoalRefinementService.BuildNormalizedQuestionKey(ForkKind, WorkerQuestion));

        var result = await scenario.RefineAsync(goal.Id, WorkerQuestion, topic);

        Assert.Equal(RefinementOutcome.AutoRefined, result.Outcome);
        var decision = Assert.Single(result.Spec.Decisions, candidate => candidate.Question == WorkerQuestion);
        Assert.Equal("Database", decision.Choice);
        Assert.StartsWith("Authoritative clarification precedent (topic:", decision.Rationale);
        Assert.Contains(origin.Id, decision.Rationale, StringComparison.Ordinal);
        Assert.DoesNotContain("legacy:", decision.Rationale, StringComparison.Ordinal);
        Assert.DoesNotContain("Carried clarification precedent", decision.Rationale, StringComparison.Ordinal);
        var answerSegment = realAnswer ? $"answer: {origin.AuthoritativeAnswer!.Id}, " : "";
        var brief = realAnswer ? "1" : "unknown";
        Assert.Equal(
            $"Authoritative clarification precedent (topic: {topic}, item: {origin.Id}, {answerSegment}brief: {brief}).",
            decision.Rationale);
        Assert.Equal(origin.Id, Assert.Single(await scenario.Store.ListAsync(goal.Id.Value)).Id);
    }

    private static async Task AssertClarificationAsync(Scenario scenario, GoalId goalId, RefinementResult result)
    {
        Assert.Equal(RefinementDisposition.Completed, result.Disposition);
        Assert.Equal(RefinementOutcome.AwaitingClarification, result.Outcome);
        Assert.Empty(result.Spec.Decisions);
        Assert.Single(result.Spec.OpenQuestions);
        var item = Assert.Single(await scenario.Store.ListAsync(goalId.Value));
        Assert.Equal(goalId.Value, item.GoalId);
        Assert.Equal(CollaborationItemType.Clarification, item.Type);
        Assert.Equal(CollaborationItemStatus.Raised, item.Status);
    }

    // Each scenario owns a unique SQLite database, precedent file and log directory; parallel-safe.
    private sealed class Scenario : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), nameof(CrossGoalPrecedentCitationTests), Guid.NewGuid().ToString("N"));
        private readonly string _databasePath;
        public AgentOrchestratorKernel Kernel { get; } = new();
        public CollaborationItemStore Store { get; }
        public SpecRefinerPrecedentStore Precedents { get; }

        public Scenario()
        {
            Directory.CreateDirectory(_root);
            _databasePath = Path.Combine(_root, "collaboration-items.db");
            Store = new CollaborationItemStore(_databasePath);
            Precedents = new SpecRefinerPrecedentStore(Path.Combine(_root, "precedents.json"));
        }

        public async Task<CollaborationItem> InsertLegacyOriginAsync(GoalId goalId)
        {
            var itemId = Guid.NewGuid().ToString("N");
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                   { DataSource = _databasePath, Pooling = false }.ToString()))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO collaboration_items
                        (id, type, goal_id, status, subject, body, correlation_key,
                         raised_at, resolved_at, resolution, answer_history_json)
                    VALUES
                        ($id, 'Clarification', $goal, 'Resolved', $subject, $body, $correlation,
                         $time, $time, 'Database', NULL)
                    """;
                command.Parameters.AddWithValue("$id", itemId);
                command.Parameters.AddWithValue("$goal", goalId.Value);
                command.Parameters.AddWithValue("$subject", $"Spec clarification needed: {OperatorQuestion}");
                // No Fork kind metadata: exercise the correlation-topic fallback used by the incident.
                command.Parameters.AddWithValue("$body", $"Question: {OperatorQuestion}");
                command.Parameters.AddWithValue("$correlation", $"spec-clarification:{goalId.Value}:{ForkKind}");
                command.Parameters.AddWithValue("$time", "2026-06-20T00:00:00.0000000+00:00");
                Assert.Equal(1, command.ExecuteNonQuery());
                command.CommandText = "SELECT answer_history_json FROM collaboration_items WHERE id = $id";
                Assert.Equal(DBNull.Value, command.ExecuteScalar());
            }

            var item = Assert.Single(await Store.ListAsync(goalId.Value));
            Assert.Equal(itemId, item.Id);
            Assert.Equal(CollaborationItemStatus.Resolved, item.Status);
            Assert.Equal("Database", item.Resolution);
            Assert.NotNull(item.AuthoritativeAnswer);
            Assert.StartsWith("legacy:", item.AuthoritativeAnswer.Id);
            return item;
        }

        public async Task<CollaborationItem> CreateAnsweredOriginAsync(GoalId goalId)
        {
            var correlation = $"spec-clarification:{goalId.Value}:{ForkKind}";
            await Store.RaiseAsync(CollaborationItemType.Clarification, goalId.Value,
                $"Spec clarification needed: {OperatorQuestion}",
                $"Fork kind: {ForkKind}\nQuestion: {OperatorQuestion}", correlation);
            Assert.True(await Store.TryResolveAsync(correlation, "Database", briefVersion: 1));
            var item = Assert.Single(await Store.ListAsync(goalId.Value));
            Assert.NotNull(item.AuthoritativeAnswer);
            Assert.DoesNotContain("legacy:", item.AuthoritativeAnswer.Id, StringComparison.Ordinal);
            return item;
        }

        public async Task RecordUnlinkedPrecedentAsync()
        {
            await Precedents.RecordPrecedentAsync(ForkKind, "Database", "Resolved previously.");
            var precedent = await Precedents.TryGetPrecedentAsync(ForkKind);
            Assert.NotNull(precedent);
            Assert.False(precedent.HasAnyOriginReference);
        }

        public async Task<RefinementResult> RefineAsync(GoalId goalId, string question, string? topicKey = null)
        {
            var json = JsonSerializer.Serialize(new
            {
                behavioralContract = "Return the configured result.",
                acceptanceCriteria = new[] { "Returns the configured result." },
                verificationClass = "TestVerifiable",
                decisions = Array.Empty<object>(),
                forks = new[] { new
                {
                    kind = ForkKind, topicKey, refinerConfidence = "low", blastRadius = "high",
                    question, choice = "", rationale = "Operator decision needed."
                } }
            });
            var provider = new FakeSmokeProvider(text: $"```json\n{json}\n```", providerName: "fake-refiner");
            var catalog = new ModelFunctionCatalog([
                new ModelFunctionBinding(ModelFunctionPurposes.SpecRefiner, ModelLane.CheapApi,
                    new ModelProfile("fake-refiner", "fake-model", ModelCapability.Text, SubscriptionMode.ApiKey))
            ]);
            var service = new GoalRefinementService(new InMemoryModelProviderRegistry([provider]),
                catalog, Store, Precedents, rawOutputDirectory: Path.Combine(_root, "logs"));

            var result = await service.RefineAsync(Kernel, goalId);

            Assert.NotNull(provider.LastRequest);
            Assert.Equal(RefinementDisposition.Completed, result.Disposition);
            return result;
        }

        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
