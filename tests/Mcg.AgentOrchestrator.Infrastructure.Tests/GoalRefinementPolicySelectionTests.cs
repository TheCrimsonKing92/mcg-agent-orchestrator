using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class GoalRefinementPolicySelectionTests
{
    private const string Choice = "Keep the current visible ordering.";
    private const string Rationale = "It matches the stated user workflow.";
    private const string Question = "Which ordering should users see?";
    private const string BalancedPolicyName = "PermissiveCap5Rollback";

    [Fact]
    public async Task ConfiguredBalancedPolicyDecidesLowBlastMediumConfidenceFork()
    {
        var result = await RunCoordinatorAsync(
            (ConductorAutonomyPolicy.Permissive with { Name = BalancedPolicyName }).ToJson(),
            Response(Fork("observable-behavior", "ordering", "med", "low", Question)));

        Assert.True(result.Processed.Attached);
        Assert.False(result.Goal.RefinedSpec!.HasOpenQuestions);
        Assert.Equal(GoalRefinementReadiness.Ready, Readiness(result));
        var decision = Assert.Single(result.Goal.RefinedSpec.Decisions, item => item.Question == Question);
        Assert.Equal(Choice, decision.Choice);
        Assert.Equal(Rationale, decision.Rationale);
        Assert.Empty(result.CollaborationItems);
        Assert.Contains($"classification_policy={BalancedPolicyName}", result.Receipt);
        Assert.Empty(result.FallbackDecisions);
    }

    [Theory]
    [InlineData("missing", "Conservative", true)]
    [InlineData("conservative", "Conservative", false)]
    [InlineData("manual", "Manual", false)]
    [InlineData("invalid", "Conservative", true)]
    public async Task ConservativeAndManualPoliciesKeepForkOpen(
        string policyFile, string expectedPolicy, bool expectedFallback)
    {
        var policyJson = policyFile switch
        {
            "missing" => null,
            "conservative" => ConductorAutonomyPolicy.Conservative.ToJson(),
            "manual" => ConductorAutonomyPolicy.Manual.ToJson(),
            _ => "{ not json"
        };
        var result = await RunCoordinatorAsync(
            policyJson,
            Response(Fork("observable-behavior", "ordering", "med", "low", Question)));

        Assert.True(result.Processed.Attached);
        Assert.Equal("Open", Assert.Single(result.Goal.RefinedSpec!.OpenQuestions).Status);
        Assert.Equal(GoalRefinementReadiness.AwaitingClarification, Readiness(result));
        Assert.DoesNotContain(result.Goal.RefinedSpec.Decisions, item => item.Question == Question);
        Assert.Single(result.CollaborationItems);
        Assert.Contains($"classification_policy={expectedPolicy}", result.Receipt);
        if (expectedFallback)
        {
            var fallback = Assert.Single(result.FallbackDecisions);
            Assert.Contains("fallback=Conservative", fallback);
            Assert.Contains(policyFile == "missing" ? "reason=missing" : "reason=load-failed", fallback);
            Assert.Contains("conductor-policy.json", fallback);
        }
        else
        {
            Assert.Empty(result.FallbackDecisions);
        }
    }

    [Fact]
    public async Task BalancedPolicyStillAsksHighRiskAndFeasibilityForks()
    {
        var expectedQuestions = new[]
        {
            "Which risk should be accepted?",
            "Can this operation be reversed?",
            "Who can verify this criterion?"
        };
        var forks = new[]
        {
            Fork("observable-behavior", "risk", "low", "high", expectedQuestions[0]),
            Fork("reversibility", "reversal", "high", "high", expectedQuestions[1]),
            Fork("feasibility", "criterion", "high", "low", expectedQuestions[2])
        };
        var result = await RunCoordinatorAsync(
            (ConductorAutonomyPolicy.Permissive with { Name = BalancedPolicyName }).ToJson(),
            Response(forks));

        Assert.True(result.Processed.Attached);
        Assert.Equal(3, result.Goal.RefinedSpec!.OpenQuestions.Count);
        Assert.All(result.Goal.RefinedSpec.OpenQuestions, item => Assert.Equal("Open", item.Status));
        Assert.Equal(
            expectedQuestions.OrderBy(question => question, StringComparer.Ordinal),
            result.Goal.RefinedSpec.OpenQuestions
                .Select(item => item.Question)
                .OrderBy(question => question, StringComparer.Ordinal));
        Assert.Equal(GoalRefinementReadiness.AwaitingClarification, Readiness(result));
        Assert.Empty(result.Goal.RefinedSpec.Decisions);
        Assert.Equal(3, result.CollaborationItems.Count);
        Assert.Contains($"classification_policy={BalancedPolicyName}", result.Receipt);
    }

    private static string Fork(string kind, string topicKey, string confidence, string blastRadius, string question) =>
        System.Text.Json.JsonSerializer.Serialize(new
        {
            kind,
            topicKey,
            refinerConfidence = confidence,
            blastRadius,
            question,
            choice = Choice,
            rationale = Rationale
        });

    private static GoalRefinementReadiness Readiness(ScenarioResult result) =>
        GoalRefinementGate.Evaluate(
            result.Goal,
            null,
            new GoalRefinementClarificationFacts(result.CollaborationItems)).Readiness;

    private static string Response(params string[] forks) =>
        """
        ```json
        {"behavioralContract":"Show the selected ordering to users.","acceptanceCriteria":["Endpoint responds correctly"],"verificationClass":"TestVerifiable","decisions":[],"forks":[
        """ + string.Join(",", forks) + "]}\n```";

    private static async Task<ScenarioResult> RunCoordinatorAsync(string? policyJson, string response)
    {
        var root = CreateTempDirectory();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            if (policyJson is not null)
            {
                Directory.CreateDirectory(workspace.OrchestratorDirectory);
                await File.WriteAllTextAsync(
                    Path.Combine(workspace.OrchestratorDirectory, "conductor-policy.json"),
                    policyJson);
            }

            var provider = new ClarifyingGoalRefinerProvider(response);
            var providers = new InMemoryModelProviderRegistry([provider]);
            ModelFunctionCatalogStore.Save(workspace.ModelFunctionCatalogPath, new ModelFunctionCatalog([
                new ModelFunctionBinding(
                    ModelFunctionPurposes.SpecRefiner,
                    ModelLane.CheapApi,
                    new ModelProfile(provider.ProviderName, "fake-model", ModelCapability.Text, SubscriptionMode.ApiKey))
            ]));
            var kernel = new AgentOrchestratorKernel();
            var goal = kernel.CreateGoal("Show users a consistent ordering.");
            GoalRefinementWorkCoordinator.RecordPending(kernel, goal.Id);
            var repository = CreateMigratedStateRepository(workspace.SqliteStatePath);
            await repository.TransactWithOutboxAsync(
                (stored, _) =>
                {
                    stored.ReplaceGoalWithSnapshot(kernel.ExportGoalSnapshot(goal.Id));
                    return Task.FromResult((
                        true,
                        true,
                        (IReadOnlyList<OrchestratorStateOutboxMessage>)[GoalRefinementWorkCoordinator.CreateMessage(goal.Id)]));
                });

            var processed = await GoalRefinementWorkCoordinator.ProcessAsync(
                repository, workspace, providers, WorkerProfileCatalog.Default(), goal.Id);
            var persisted = (await repository.LoadAsync()).GetGoal(goal.Id);
            var decisions = persisted.Timeline
                .Where(item => item.Kind == ProgressKind.GoalPolicyDecision)
                .Select(item => item.Message)
                .ToArray();
            var receipt = Assert.Single(decisions.Where(item => item.StartsWith("spec_refinement outcome=completed", StringComparison.Ordinal)));
            var fallbackDecisions = decisions
                .Where(item => item.StartsWith(GoalRefinementWorkCoordinator.PolicyFallbackDecisionPrefix, StringComparison.Ordinal))
                .ToArray();
            var collaborationItems = await CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory)
                .ListAsync(goal.Id.Value);
            return new ScenarioResult(processed, persisted, receipt, fallbackDecisions, collaborationItems);
        }
        finally
        {
            SharedTestSupport.RemoveTempDirectory(root);
        }
    }

    private sealed record ScenarioResult(
        GoalRefinementWorkProcessResult Processed,
        Goal Goal,
        string Receipt,
        IReadOnlyList<string> FallbackDecisions,
        IReadOnlyList<CollaborationItem> CollaborationItems);
}
