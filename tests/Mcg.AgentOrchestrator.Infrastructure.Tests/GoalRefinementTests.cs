using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class GoalRefinementTests
{
    // --- Fallback when no model binding configured ---

    [Xunit.Fact(DisplayName = "GoalRefinementService_falls_back_to_minimal_spec_when_no_model_binding")]
    public async Task FallsBackToMinimalSpecWhenNoModelBinding()
    {
        var (service, kernel, goalId, _) = BuildScenario(
            ModelFunctionCatalog.Empty,
            responseJson: "{}");

        var result = await service.RefineAsync(kernel, goalId);

        Xunit.Assert.Equal(RefinementOutcome.AutoRefined, result.Outcome);
        var goal = kernel.GetGoal(goalId);
        Xunit.Assert.NotNull(goal.RefinedSpec);
        Xunit.Assert.Contains("Implement:", goal.RefinedSpec!.BehavioralContract);
    }

    // --- Clean auto-refine (no ask forks) ---

    [Xunit.Fact(DisplayName = "GoalRefinementService_auto_refines_when_all_forks_are_decide")]
    public async Task AutoRefinesWhenAllForksAreDecide()
    {
        var json = """
            ```json
            {
              "behavioralContract": "The service exposes goal status via REST.",
              "acceptanceCriteria": ["GET /goals/{id} returns 200"],
              "verificationClass": "TestVerifiable",
              "decisions": [{"question": "HTTP method?", "choice": "GET", "rationale": "Idempotent."}],
              "forks": [{"kind": "observable-behavior", "refinerConfidence": "high", "blastRadius": "low", "question": "HTTP method?", "choice": "GET", "rationale": "Idempotent."}]
            }
            ```
            """;

        var (service, kernel, goalId, collab) = BuildScenario(responseJson: json);

        var result = await service.RefineAsync(kernel, goalId);

        Xunit.Assert.Equal(RefinementOutcome.AutoRefined, result.Outcome);
        var goal = kernel.GetGoal(goalId);
        Xunit.Assert.NotNull(goal.RefinedSpec);
        Xunit.Assert.Equal("The service exposes goal status via REST.", goal.RefinedSpec!.BehavioralContract);
        Xunit.Assert.Empty(collab.Items); // no clarification items raised
    }

    // --- Ambiguous fork: low confidence + high blast radius ---

    [Xunit.Fact(DisplayName = "GoalRefinementService_raises_clarification_for_low_confidence_high_blast_fork")]
    public async Task RaisesClarificationForLowConfidenceHighBlastFork()
    {
        var json = """
            ```json
            {
              "behavioralContract": "The system integrates with an external API.",
              "acceptanceCriteria": ["Endpoint responds correctly"],
              "verificationClass": "TestVerifiable",
              "decisions": [],
              "forks": [{"kind": "external-contract", "refinerConfidence": "low", "blastRadius": "high", "question": "Which API version to target?", "choice": "", "rationale": "Unclear from objective."}]
            }
            ```
            """;

        var (service, kernel, goalId, collab) = BuildScenario(responseJson: json);

        var result = await service.RefineAsync(kernel, goalId);

        Xunit.Assert.Equal(RefinementOutcome.AwaitingClarification, result.Outcome);
        var goal = kernel.GetGoal(goalId);
        Xunit.Assert.NotNull(goal.RefinedSpec);
        Xunit.Assert.True(goal.RefinedSpec!.HasOpenQuestions);
        Xunit.Assert.Single(collab.Items);
        Xunit.Assert.Equal(CollaborationItemType.Clarification, collab.Items[0].Type);
        Xunit.Assert.Equal(CollaborationItemStatus.Raised, collab.Items[0].Status);
        Xunit.Assert.NotNull(collab.Items[0].CorrelationKey);
        Xunit.Assert.StartsWith(GoalRefinementService.CorrelationKeyPrefix, collab.Items[0].CorrelationKey);
    }

    // --- Resolve clarification and record precedent ---

    [Xunit.Fact(DisplayName = "GoalRefinementService_resolving_clarification_records_precedent")]
    public async Task ResolvingClarificationRecordsPrecedent()
    {
        var json = """
            ```json
            {
              "behavioralContract": "Integrates with billing.",
              "acceptanceCriteria": ["Charge applied"],
              "verificationClass": "TestVerifiable",
              "decisions": [],
              "forks": [{"kind": "external-contract", "refinerConfidence": "low", "blastRadius": "high", "question": "Stripe or Paddle?", "choice": "", "rationale": "No prior art."}]
            }
            ```
            """;

        var tempDir = CreateTempDirectory();
        var precedentStore = new SpecRefinerPrecedentStore(Path.Combine(tempDir, "precedents.json"));
        var (service, kernel, goalId, collab) = BuildScenario(
            responseJson: json,
            precedentStore: precedentStore);

        await service.RefineAsync(kernel, goalId);
        var correlationKey = collab.Items[0].CorrelationKey!;

        var resolved = await service.TryResolveOpenClarificationAsync(correlationKey, "Stripe");

        Xunit.Assert.True(resolved);
        var precedent = await precedentStore.TryGetPrecedentAsync("external-contract");
        Xunit.Assert.NotNull(precedent);
        Xunit.Assert.Equal("Stripe", precedent!.Choice);
    }

    // --- Precedent reuse: second goal with same forkKind does not re-ask ---

    [Xunit.Fact(DisplayName = "GoalRefinementService_reuses_precedent_for_same_forkKind")]
    public async Task ReusesPrecedentForSameForkKind()
    {
        var json = """
            ```json
            {
              "behavioralContract": "Integrates with billing again.",
              "acceptanceCriteria": ["Charge applied"],
              "verificationClass": "TestVerifiable",
              "decisions": [],
              "forks": [{"kind": "external-contract", "refinerConfidence": "low", "blastRadius": "high", "question": "Stripe or Paddle?", "choice": "", "rationale": "No prior art."}]
            }
            ```
            """;

        var tempDir = CreateTempDirectory();
        var precedentStore = new SpecRefinerPrecedentStore(Path.Combine(tempDir, "precedents.json"));

        // Pre-seed precedent for the same forkKind
        await precedentStore.RecordPrecedentAsync("external-contract", "Stripe", "Resolved previously.", default);

        var (service, kernel, goalId, collab) = BuildScenario(
            responseJson: json,
            precedentStore: precedentStore);

        var result = await service.RefineAsync(kernel, goalId);

        // Should auto-refine because the precedent answers the fork without asking
        Xunit.Assert.Equal(RefinementOutcome.AutoRefined, result.Outcome);
        Xunit.Assert.Empty(collab.Items); // no new clarification raised
        var goal = kernel.GetGoal(goalId);
        Xunit.Assert.False(goal.RefinedSpec!.HasOpenQuestions);
        // The decision should record the precedent answer
        Xunit.Assert.Single(goal.RefinedSpec!.Decisions);
        Xunit.Assert.Equal("Stripe", goal.RefinedSpec!.Decisions[0].Choice);
    }

    // --- HasOpenClarification static helper ---

    [Xunit.Fact(DisplayName = "HasOpenClarification_true_when_raised_clarification_item_exists")]
    public void HasOpenClarificationTrueWhenRaisedClarificationItemExists()
    {
        var items = new[]
        {
            new CollaborationItem(
                "id1", CollaborationItemType.Clarification, "goal-1",
                CollaborationItemStatus.Raised, "Q?", "body",
                "spec-clarification:abc:external-contract:xyz", DateTimeOffset.UtcNow, null, null)
        };

        Xunit.Assert.True(GoalRefinementService.HasOpenClarification(items));
    }

    [Xunit.Fact(DisplayName = "HasOpenClarification_false_when_clarification_is_resolved")]
    public void HasOpenClarificationFalseWhenClarificationResolved()
    {
        var items = new[]
        {
            new CollaborationItem(
                "id1", CollaborationItemType.Clarification, "goal-1",
                CollaborationItemStatus.Resolved, "Q?", "body",
                "spec-clarification:abc:external-contract:xyz", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "answer")
        };

        Xunit.Assert.False(GoalRefinementService.HasOpenClarification(items));
    }

    [Xunit.Fact(DisplayName = "HasOpenClarification_false_when_no_items")]
    public void HasOpenClarificationFalseWhenNoItems()
    {
        Xunit.Assert.False(GoalRefinementService.HasOpenClarification([]));
    }

    [Xunit.Fact(DisplayName = "HasOpenClarification_false_when_raised_item_has_non_spec_clarification_key")]
    public void HasOpenClarificationFalseWhenRaisedItemHasNonSpecClarificationKey()
    {
        var items = new[]
        {
            new CollaborationItem(
                "id1", CollaborationItemType.Clarification, "goal-1",
                CollaborationItemStatus.Raised, "Q?", "body",
                "inbox-landing-abc", DateTimeOffset.UtcNow, null, null)
        };

        Xunit.Assert.False(GoalRefinementService.HasOpenClarification(items));
    }

    // --- SpecRefinerPlanner.Parse ---

    [Xunit.Fact(DisplayName = "SpecRefinerPlanner_Parse_valid_fenced_json_returns_structured_output")]
    public void ParseValidFencedJsonReturnsStructuredOutput()
    {
        var json = """
            ```json
            {
              "behavioralContract": "The API returns goal status.",
              "acceptanceCriteria": ["GET /goals/{id} returns 200", "404 for unknown"],
              "verificationClass": "TestVerifiable",
              "decisions": [{"question": "HTTP method?", "choice": "GET", "rationale": "Idempotent."}],
              "forks": [{"kind": "observable-behavior", "refinerConfidence": "high", "blastRadius": "low", "question": "HTTP method?", "choice": "GET", "rationale": "Idempotent."}]
            }
            ```
            """;

        var output = SpecRefinerPlanner.Parse(json);

        Xunit.Assert.True(output.IsValid);
        Xunit.Assert.Equal("The API returns goal status.", output.BehavioralContract);
        Xunit.Assert.Equal(2, output.AcceptanceCriteria.Count);
        Xunit.Assert.Equal(VerificationClass.TestVerifiable, output.VerificationClass);
        Xunit.Assert.Single(output.Decisions);
        Xunit.Assert.Equal("GET", output.Decisions[0].Choice);
        Xunit.Assert.Single(output.Forks);
        Xunit.Assert.Equal("observable-behavior", output.Forks[0].Kind);
    }

    [Xunit.Fact(DisplayName = "SpecRefinerPlanner_Parse_RealWorldDependent_verificationClass_parsed")]
    public void ParseRealWorldDependentVerificationClassParsed()
    {
        var json = """
            ```json
            {
              "behavioralContract": "Deploys to production.",
              "acceptanceCriteria": [],
              "verificationClass": "RealWorldDependent",
              "decisions": [],
              "forks": []
            }
            ```
            """;

        var output = SpecRefinerPlanner.Parse(json);

        Xunit.Assert.True(output.IsValid);
        Xunit.Assert.Equal(VerificationClass.RealWorldDependent, output.VerificationClass);
    }

    [Xunit.Fact(DisplayName = "SpecRefinerPlanner_Parse_missing_behavioralContract_returns_invalid")]
    public void ParseMissingBehavioralContractReturnsInvalid()
    {
        var json = """
            ```json
            {"acceptanceCriteria": [], "verificationClass": "TestVerifiable", "decisions": [], "forks": []}
            ```
            """;

        var output = SpecRefinerPlanner.Parse(json);

        Xunit.Assert.False(output.IsValid);
        Xunit.Assert.NotEmpty(output.ValidationErrors);
    }

    [Xunit.Fact(DisplayName = "SpecRefinerPlanner_Parse_empty_string_returns_invalid")]
    public void ParseEmptyStringReturnsInvalid()
    {
        Xunit.Assert.False(SpecRefinerPlanner.Parse(string.Empty).IsValid);
    }

    // --- SpecRefinerPlanner.ClassifyFork ---

    [Xunit.Fact(DisplayName = "ClassifyFork_low_confidence_high_blast_radius_returns_Ask")]
    public void ClassifyForkLowConfidenceHighBlastReturnsAsk()
    {
        var fork = new SpecRefinementFork("external-contract", "low", "high", "Which API?", "", "Unclear.");

        Xunit.Assert.Equal(SpecForkDisposition.Ask, SpecRefinerPlanner.ClassifyFork(fork));
    }

    [Xunit.Fact(DisplayName = "ClassifyFork_med_confidence_high_blast_radius_returns_Decide")]
    public void ClassifyForkMedConfidenceHighBlastReturnsDecide()
    {
        var fork = new SpecRefinementFork("external-contract", "med", "high", "Which API?", "REST", "Standard.");

        Xunit.Assert.Equal(SpecForkDisposition.Decide, SpecRefinerPlanner.ClassifyFork(fork));
    }

    [Xunit.Fact(DisplayName = "ClassifyFork_low_confidence_low_blast_radius_returns_Decide")]
    public void ClassifyForkLowConfidenceLowBlastReturnsDecide()
    {
        var fork = new SpecRefinementFork("other", "low", "low", "Variable name?", "goalId", "Convention.");

        Xunit.Assert.Equal(SpecForkDisposition.Decide, SpecRefinerPlanner.ClassifyFork(fork));
    }

    // --- Helpers ---

    private static (
        GoalRefinementService Service,
        AgentOrchestratorKernel Kernel,
        GoalId GoalId,
        FakeCollaborationItemStore Collaboration)
    BuildScenario(
        ModelFunctionCatalog? catalog = null,
        string responseJson = "{}",
        SpecRefinerPrecedentStore? precedentStore = null)
    {
        var provider = new FakeSmokeProvider(text: responseJson, providerName: "fake-refiner");
        var registry = new InMemoryModelProviderRegistry([provider]);

        var effectiveCatalog = catalog ?? new ModelFunctionCatalog([
            new ModelFunctionBinding(
                ModelFunctionPurposes.SpecRefiner,
                ModelLane.CheapApi,
                new ModelProfile("fake-refiner", "fake-model", ModelCapability.Text, SubscriptionMode.ApiKey))
        ]);

        var collab = new FakeCollaborationItemStore();
        var tempDir = CreateTempDirectory();
        var prec = precedentStore ?? new SpecRefinerPrecedentStore(
            Path.Combine(tempDir, "precedents.json"));

        var service = new GoalRefinementService(registry, effectiveCatalog, collab, prec);
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Integrate the billing system");

        return (service, kernel, goal.Id, collab);
    }
}
