using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.App.Dashboard.Api;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class GoalRefinementTests
{
    // --- Binding selection and configuration failures ---

    [Xunit.Fact(DisplayName = "GoalRefinementService_throws_when_no_refiner_binding")]
    public async Task ThrowsWhenNoRefinerBinding()
    {
        var (service, kernel, goalId, _) = BuildScenario(
            ModelFunctionCatalog.Empty,
            responseJson: "{}");

        var ex = await Xunit.Assert.ThrowsAsync<InvalidOperationException>(
            () => service.RefineAsync(kernel, goalId));

        Xunit.Assert.Contains(ModelFunctionPurposes.SpecRefiner, ex.Message);
        Xunit.Assert.Contains("<none>", ex.Message);
    }

    [Xunit.Fact(DisplayName = "GoalLifecycleCommands_refines_with_fallback_before_activation_and_planner_brief")]
    public void GoalLifecycleCommandsRefinesWithFallbackBeforeActivationAndPlannerBrief()
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var providers = new InMemoryModelProviderRegistry([]);
        ModelFunctionCatalogStore.Save(workspace.ModelFunctionCatalogPath, new ModelFunctionCatalog([
            new ModelFunctionBinding(
                ModelFunctionPurposes.SpecRefiner,
                ModelLane.CheapApi,
                new ModelProfile("missing-provider", "fake-model", ModelCapability.Text, SubscriptionMode.ApiKey))
        ]));
        var kernel = new AgentOrchestratorKernel();

        var goal = GoalLifecycleCommands.CreateAndActivateGoal(
            kernel,
            AgentCatalog.Default().Agents,
            "Implement a rough feature objective",
            workspace,
            providers);

        Xunit.Assert.NotNull(goal.RefinedSpec);
        Xunit.Assert.Contains("Implement:", goal.RefinedSpec!.BehavioralContract);
        var planner = goal.Tasks.First(task => task.RequiredRole == AgentRole.Planner);
        var brief = kernel.BuildTaskBrief(goal.Id, planner.Id).Content;
        Xunit.Assert.Contains("Refined Spec", brief);
        Xunit.Assert.Contains(goal.RefinedSpec.BehavioralContract, brief);
    }

    [Xunit.Fact(DisplayName = "GoalLifecycleCommands_records_auto_pipeline_decision_and_creates_reviewer_lane")]
    public void GoalLifecycleCommandsRecordsAutoPipelineDecisionAndCreatesReviewerLane()
    {
        var kernel = new AgentOrchestratorKernel();

        var goal = GoalLifecycleCommands.CreateAndActivateGoal(
            kernel,
            AgentCatalog.Default().Agents,
            "Update security token rollback behavior in src/Mcg.AgentOrchestrator.App/AuthPolicy.cs and tests/Mcg.AgentOrchestrator.Infrastructure.Tests/AuthPolicyTests.cs");

        Xunit.Assert.Equal([AgentRole.Developer, AgentRole.Reviewer], goal.Tasks.Select(task => task.RequiredRole));
        Xunit.Assert.Contains(goal.Timeline, evt =>
            evt.Kind == ProgressKind.GoalPolicyDecision &&
            evt.Message.Contains("Intake pipeline decision (auto): developer-reviewer", StringComparison.Ordinal) &&
            evt.Message.Contains("security-risk", StringComparison.Ordinal));
    }

    [Xunit.Fact(DisplayName = "GoalLifecycleCommands_simple_goal_overrides_pipeline_to_developer_only")]
    public void GoalLifecycleCommandsSimpleGoalOverridesPipelineToDeveloperOnly()
    {
        var kernel = new AgentOrchestratorKernel();

        var goal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(
            kernel,
            AgentCatalog.Default().Agents,
            "Update security token rollback behavior in src/Mcg.AgentOrchestrator.App/AuthPolicy.cs and tests/Mcg.AgentOrchestrator.Infrastructure.Tests/AuthPolicyTests.cs");

        var task = Xunit.Assert.Single(goal.Tasks);
        Xunit.Assert.Equal(AgentRole.Developer, task.RequiredRole);
        Xunit.Assert.Contains(goal.Timeline, evt =>
            evt.Kind == ProgressKind.GoalPolicyDecision &&
            evt.Message.Contains("Intake pipeline decision (override): developer-only", StringComparison.Ordinal));
    }

    [Xunit.Fact(DisplayName = "GoalRefinementService_selects_named_refiner_binding_when_not_first")]
    public async Task SelectsNamedRefinerBindingWhenNotFirst()
    {
        var json = """
            ```json
            {
              "behavioralContract": "The intended refiner model was used.",
              "acceptanceCriteria": ["Selected by name"],
              "verificationClass": "TestVerifiable",
              "decisions": [],
              "forks": []
            }
            ```
            """;
        var provider = new FakeSmokeProvider(text: json, providerName: "fake-refiner");
        var registry = new InMemoryModelProviderRegistry([provider]);
        var catalog = new ModelFunctionCatalog([
            new ModelFunctionBinding(
                ModelFunctionPurposes.SpecRefiner,
                ModelLane.CheapApi,
                new ModelProfile("fake-refiner", "wrong-first-model", ModelCapability.Text, SubscriptionMode.ApiKey),
                Name: "wrong-refiner"),
            new ModelFunctionBinding(
                ModelFunctionPurposes.SpecRefiner,
                ModelLane.CheapApi,
                new ModelProfile("fake-refiner", "intended-model", ModelCapability.Text, SubscriptionMode.ApiKey),
                Name: ModelFunctionPurposes.SpecRefiner)
        ]);
        var service = new GoalRefinementService(
            registry,
            catalog,
            new FakeCollaborationItemStore(),
            new SpecRefinerPrecedentStore(Path.Combine(CreateTempDirectory(), "precedents.json")));
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Refine with intended model");

        var result = await service.RefineAsync(kernel, goal.Id);

        Xunit.Assert.Equal(RefinementOutcome.AutoRefined, result.Outcome);
        Xunit.Assert.Equal("intended-model", provider.LastRequest!.Options.ModelName);
        Xunit.Assert.Equal("The intended refiner model was used.", kernel.GetGoal(goal.Id).RefinedSpec!.BehavioralContract);
    }

    [Xunit.Fact(DisplayName = "GoalRefinementService_throws_when_refiner_binding_missing_among_other_bindings")]
    public async Task ThrowsWhenRefinerBindingMissingAmongOtherBindings()
    {
        var catalog = new ModelFunctionCatalog([
            new ModelFunctionBinding(
                ModelFunctionPurposes.SpecRefiner,
                ModelLane.CheapApi,
                new ModelProfile("fake-refiner", "wrong-model", ModelCapability.Text, SubscriptionMode.ApiKey),
                Name: "wrong-refiner")
        ]);
        var (service, kernel, goalId, _) = BuildScenario(catalog);

        var ex = await Xunit.Assert.ThrowsAsync<InvalidOperationException>(
            () => service.RefineAsync(kernel, goalId));

        Xunit.Assert.Contains(ModelFunctionPurposes.SpecRefiner, ex.Message);
        Xunit.Assert.Contains("wrong-refiner", ex.Message);
    }

    [Xunit.Fact(DisplayName = "GoalRefinementService_throws_when_refiner_binding_is_duplicated")]
    public async Task ThrowsWhenRefinerBindingIsDuplicated()
    {
        var catalog = new ModelFunctionCatalog([
            new ModelFunctionBinding(
                ModelFunctionPurposes.SpecRefiner,
                ModelLane.CheapApi,
                new ModelProfile("fake-refiner", "first-model", ModelCapability.Text, SubscriptionMode.ApiKey)),
            new ModelFunctionBinding(
                "other-purpose",
                ModelLane.CheapApi,
                new ModelProfile("fake-refiner", "second-model", ModelCapability.Text, SubscriptionMode.ApiKey),
                Name: ModelFunctionPurposes.SpecRefiner)
        ]);
        var (service, kernel, goalId, _) = BuildScenario(catalog);

        var ex = await Xunit.Assert.ThrowsAsync<InvalidOperationException>(
            () => service.RefineAsync(kernel, goalId));

        Xunit.Assert.Contains(ModelFunctionPurposes.SpecRefiner, ex.Message);
        Xunit.Assert.Contains("Multiple model-function bindings", ex.Message);
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

    [Xunit.Fact(DisplayName = "GoalRefinementService_kernel_resolve_falls_back_when_goal_missing")]
    public async Task KernelResolveFallsBackWhenGoalMissing()
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
        var resolved = false;
        var warningOutput = CaptureConsoleError(() =>
        {
            resolved = service.TryResolveOpenClarificationAsync(
                new AgentOrchestratorKernel(),
                correlationKey,
                "Stripe").GetAwaiter().GetResult();

            Xunit.Assert.True(resolved);
        });

        var resolvedItem = Xunit.Assert.Single(collab.Items);
        Xunit.Assert.Equal(CollaborationItemStatus.Resolved, resolvedItem.Status);
        Xunit.Assert.Equal("Stripe", resolvedItem.Resolution);
        var precedent = await precedentStore.TryGetPrecedentAsync("external-contract");
        Xunit.Assert.NotNull(precedent);
        Xunit.Assert.Equal("Stripe", precedent!.Choice);
        Xunit.Assert.Contains("Warning:", warningOutput, StringComparison.Ordinal);
        Xunit.Assert.Contains(correlationKey, warningOutput, StringComparison.Ordinal);
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

    [Xunit.Fact(DisplayName = "HasOpenClarification_true_when_clarification_is_delivered_but_unresolved")]
    public void HasOpenClarificationTrueWhenClarificationDeliveredButUnresolved()
    {
        var items = new[]
        {
            new CollaborationItem(
                "id1", CollaborationItemType.Clarification, "goal-1",
                CollaborationItemStatus.Delivered, "Q?", "body",
                "spec-clarification:abc:external-contract:xyz", DateTimeOffset.UtcNow, null, null)
        };

        Xunit.Assert.True(GoalRefinementService.HasOpenClarification(items));
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

    [Xunit.Fact(DisplayName = "SubscriptionCliCompleter_claude_completion_uses_direct_mode_and_stdout_can_parse")]
    public async Task SubscriptionCliCompleterClaudeCompletionUsesDirectModeAndStdoutCanParse()
    {
        string? capturedCommand = null;
        string? capturedPrompt = null;
        Task<string> FakeRunner(string command, string workingDirectory, CancellationToken cancellationToken)
        {
            capturedCommand = command;
            capturedPrompt = File.ReadAllText(Path.Combine(workingDirectory, "spec-refiner-prompt.md"));
            return Task.FromResult("""
                ```json
                {
                  "behavioralContract": "Use subscription CLI for spec refinement and ask about ambiguous auth identity.",
                  "acceptanceCriteria": ["CLI stdout is parsed", "Operator chooses the auth identity source"],
                  "verificationClass": "TestVerifiable",
                  "decisions": [],
                  "forks": [
                    {
                      "question": "Which auth identity source should operators use?",
                      "options": ["single shared key", "per-operator keys", "Discord identity"],
                      "recommended": "per-operator keys",
                      "disposition": "ASK"
                    }
                  ]
                }
                ```
                """);
        }

        var completer = new SubscriptionCliCompleter(
            "claude --model {subscriptionModelName} --permission-mode {permissionMode} --sandbox {sandboxMode} --cwd {workingDirectory} -p (Get-Content -Raw {promptPath}) -c reasoning={subscriptionReasoningEffort}",
            "claude-cli",
            "claude-sonnet-4-6",
            null,
            FakeRunner);

        var stdout = await completer.CompleteAsync(
            SpecRefinerPlanner.BuildPrompt("Refine this goal"),
            "spec-refiner-prompt.md",
            default);
        var output = SpecRefinerPlanner.Parse(stdout);

        Xunit.Assert.True(output.IsValid);
        Xunit.Assert.Equal("Use subscription CLI for spec refinement and ask about ambiguous auth identity.", output.BehavioralContract);
        Xunit.Assert.Single(output.Forks);
        Xunit.Assert.Equal("Which auth identity source should operators use?", output.Forks[0].Question);
        Xunit.Assert.Contains("Refine this goal", capturedPrompt!);
        Xunit.Assert.Contains("--model 'claude-sonnet-4-6'", capturedCommand!);
        Xunit.Assert.False(capturedCommand!.Contains("--permission-mode 'plan'", StringComparison.Ordinal));
        Xunit.Assert.True(capturedCommand.Contains("--permission-mode 'default'", StringComparison.Ordinal));
        Xunit.Assert.Contains("--sandbox 'read-only'", capturedCommand!);
        Xunit.Assert.Contains("reasoning='high'", capturedCommand!);
    }

    [Xunit.Fact(DisplayName = "SubscriptionCliCompleter_codex_completion_keeps_readonly_sandbox")]
    public async Task SubscriptionCliCompleterCodexCompletionKeepsReadonlySandbox()
    {
        string? capturedCommand = null;
        Task<string> FakeRunner(string command, string workingDirectory, CancellationToken cancellationToken)
        {
            capturedCommand = command;
            return Task.FromResult("{}");
        }

        var completer = new SubscriptionCliCompleter(
            "codex exec --model {subscriptionModelName} -c model_reasoning_effort={subscriptionReasoningEffort} --sandbox {sandboxMode} --cd {workingDirectory} (Get-Content -Raw {promptPath})",
            "codex-cli",
            "gpt-5.5",
            "medium",
            FakeRunner);

        await completer.CompleteAsync("Return JSON", "codex-prompt.md", default);

        Xunit.Assert.True(capturedCommand is not null);
        Xunit.Assert.True(capturedCommand!.Contains("--model 'gpt-5.5'", StringComparison.Ordinal));
        Xunit.Assert.True(capturedCommand.Contains("--sandbox 'read-only'", StringComparison.Ordinal));
        Xunit.Assert.True(capturedCommand.Contains("model_reasoning_effort='medium'", StringComparison.Ordinal));
    }

    [Xunit.Fact(DisplayName = "GoalRefinementService_uses_subscription_cli_when_binding_has_subscription")]
    public async Task GoalRefinementServiceUsesSubscriptionCliWhenBindingHasSubscription()
    {
        var catalog = new ModelFunctionCatalog([
            new ModelFunctionBinding(
                ModelFunctionPurposes.SpecRefiner,
                ModelLane.Capable,
                new ModelProfile("missing-api-provider", "unused-api-model", ModelCapability.Text, SubscriptionMode.ApiKey),
                Subscription: new SubscriptionLaunchProfile("claude-cli", "claude-sonnet-4-6"))
        ]);
        var profiles = new WorkerProfileCatalog([
            new WorkerProfile("claude-cli", "claude --model {subscriptionModelName} --permission-mode {permissionMode} -p (Get-Content -Raw {promptPath})")
        ]);
        var (service, kernel, goalId, _) = BuildScenario(
            catalog,
            workerProfiles: profiles,
            subscriptionCompleterFactory: sub => new SubscriptionCliCompleter(
                profiles.GetRequired(sub.WorkerProfileName).CommandTemplate,
                sub.WorkerProfileName,
                sub.ModelAlias ?? string.Empty,
                sub.ReasoningEffort,
                (_, _, _) => Task.FromResult("""
                    ```json
                    {
                      "behavioralContract": "Subscription CLI refined the goal.",
                      "acceptanceCriteria": ["No API provider is required"],
                      "verificationClass": "TestVerifiable",
                      "decisions": [],
                      "forks": []
                    }
                    ```
                    """)));

        var result = await service.RefineAsync(kernel, goalId);

        Xunit.Assert.Equal(RefinementOutcome.AutoRefined, result.Outcome);
        Xunit.Assert.Equal("Subscription CLI refined the goal.", kernel.GetGoal(goalId).RefinedSpec!.BehavioralContract);
    }

    [Xunit.Fact(DisplayName = "GoalRefinementService_selects_subscription_refiner_binding_by_identity_when_not_first")]
    public async Task GoalRefinementServiceSelectsSubscriptionRefinerBindingByIdentityWhenNotFirst()
    {
        var catalog = new ModelFunctionCatalog([
            new ModelFunctionBinding(
                ModelFunctionPurposes.SpecRefiner,
                ModelLane.Capable,
                new ModelProfile("missing-api-provider", "wrong-api-model", ModelCapability.Text, SubscriptionMode.ApiKey),
                Name: "wrong-refiner",
                Subscription: new SubscriptionLaunchProfile("claude-cli", "claude-sonnet-4-6")),
            new ModelFunctionBinding(
                ModelFunctionPurposes.SpecRefiner,
                ModelLane.Capable,
                new ModelProfile("missing-api-provider", "intended-api-model", ModelCapability.Text, SubscriptionMode.ApiKey),
                Name: ModelFunctionPurposes.SpecRefiner,
                Subscription: new SubscriptionLaunchProfile("codex-cli", "gpt-5.5", "medium"))
        ]);
        var profiles = new WorkerProfileCatalog([
            new WorkerProfile("claude-cli", "claude --model {subscriptionModelName} --permission-mode {permissionMode} -p (Get-Content -Raw {promptPath})"),
            new WorkerProfile("codex-cli", "codex exec --model {subscriptionModelName} -c model_reasoning_effort={subscriptionReasoningEffort} --sandbox {sandboxMode} --cd {workingDirectory} (Get-Content -Raw {promptPath})")
        ]);
        SubscriptionLaunchProfile? capturedSubscription = null;
        string? capturedCommand = null;
        var (service, kernel, goalId, _) = BuildScenario(
            catalog,
            workerProfiles: profiles,
            subscriptionCompleterFactory: sub =>
            {
                capturedSubscription = sub;
                return new SubscriptionCliCompleter(
                    profiles.GetRequired(sub.WorkerProfileName).CommandTemplate,
                    sub.WorkerProfileName,
                    sub.ModelAlias ?? string.Empty,
                    sub.ReasoningEffort,
                    (command, _, _) =>
                    {
                        capturedCommand = command;
                        return Task.FromResult("""
                            ```json
                            {
                              "behavioralContract": "The typed subscription refiner was used.",
                              "acceptanceCriteria": ["Selected by binding identity"],
                              "verificationClass": "TestVerifiable",
                              "decisions": [],
                              "forks": []
                            }
                            ```
                            """);
                    });
            });

        var result = await service.RefineAsync(kernel, goalId);

        Xunit.Assert.Equal(RefinementOutcome.AutoRefined, result.Outcome);
        Xunit.Assert.NotNull(capturedSubscription);
        var provider = WorkerProviderCatalog.Default().ResolveProfile(capturedSubscription!.WorkerProfileName);
        Xunit.Assert.Equal(ProviderKind.OpenAICodexCli, provider.Identity.Kind);
        Xunit.Assert.True(capturedCommand is not null);
        Xunit.Assert.Contains("--model 'gpt-5.5'", capturedCommand!);
        Xunit.Assert.Equal("The typed subscription refiner was used.", kernel.GetGoal(goalId).RefinedSpec!.BehavioralContract);
    }

    // --- SpecRefinerPlanner.ClassifyFork ---

    [Xunit.Fact(DisplayName = "ClassifyFork_low_confidence_high_blast_radius_returns_Ask")]
    public void ClassifyForkLowConfidenceHighBlastReturnsAsk()
    {
        var fork = new SpecRefinementFork("external-contract", "low", "high", "Which API?", "", "Unclear.");

        Xunit.Assert.Equal(SpecForkDisposition.Ask, SpecRefinerPlanner.ClassifyFork(fork));
    }

    [Xunit.Fact(DisplayName = "ClassifyFork_med_confidence_high_blast_radius_non_high_stakes_returns_Decide")]
    public void ClassifyForkMedConfidenceHighBlastNonHighStakesReturnsDecide()
    {
        var fork = new SpecRefinementFork("other", "med", "high", "Which API?", "REST", "Standard.");

        Xunit.Assert.Equal(SpecForkDisposition.Decide, SpecRefinerPlanner.ClassifyFork(fork));
    }

    [Xunit.Fact(DisplayName = "ClassifyFork_same_ambiguity_Conservative_asks_full_auto_decides")]
    public void ClassifyForkSameAmbiguityConservativeAsksFullAutoDecides()
    {
        var fork = new SpecRefinementFork("external-contract", "med", "high", "Which API?", "REST", "Standard.");
        var fullAuto = ConductorAutonomyPolicy.Permissive with { Name = "full-auto" };

        Xunit.Assert.Equal(SpecForkDisposition.Ask, SpecRefinerPlanner.ClassifyFork(fork, ConductorAutonomyPolicy.Conservative));
        Xunit.Assert.Equal(SpecForkDisposition.Decide, SpecRefinerPlanner.ClassifyFork(fork, fullAuto));
    }

    [Xunit.Fact(DisplayName = "GoalRefinementService_same_fork_asks_under_Conservative_but_auto_decides_under_full_auto")]
    public async Task GoalRefinementServiceSameForkAsksUnderConservativeButAutoDecidesUnderFullAuto()
    {
        var json = """
            ```json
            {
              "behavioralContract": "The system integrates with an external API.",
              "acceptanceCriteria": ["Endpoint responds correctly"],
              "verificationClass": "TestVerifiable",
              "decisions": [],
              "forks": [{"kind": "external-contract", "refinerConfidence": "med", "blastRadius": "high", "question": "Which API version to target?", "choice": "REST v2", "rationale": "Likely current default."}]
            }
            ```
            """;
        var conservative = BuildScenario(responseJson: json);
        var fullAuto = BuildScenario(responseJson: json);

        var conservativeResult = await conservative.Service.RefineAsync(
            conservative.Kernel,
            conservative.GoalId,
            ConductorAutonomyPolicy.Conservative);
        var fullAutoResult = await fullAuto.Service.RefineAsync(
            fullAuto.Kernel,
            fullAuto.GoalId,
            ConductorAutonomyPolicy.Permissive with { Name = "full-auto" });

        Xunit.Assert.Equal(RefinementOutcome.AwaitingClarification, conservativeResult.Outcome);
        Xunit.Assert.Single(conservative.Collaboration.Items);
        Xunit.Assert.Equal(RefinementOutcome.AutoRefined, fullAutoResult.Outcome);
        Xunit.Assert.Empty(fullAuto.Collaboration.Items);
        Xunit.Assert.Equal("REST v2", fullAuto.Kernel.GetGoal(fullAuto.GoalId).RefinedSpec!.Decisions.Single().Choice);
    }

    [Xunit.Fact(DisplayName = "ClassifyFork_low_confidence_low_blast_radius_returns_Decide")]
    public void ClassifyForkLowConfidenceLowBlastReturnsDecide()
    {
        var fork = new SpecRefinementFork("other", "low", "low", "Variable name?", "goalId", "Convention.");

        Xunit.Assert.Equal(SpecForkDisposition.Decide, SpecRefinerPlanner.ClassifyFork(fork));
    }

    [Xunit.Fact(DisplayName = "GoalRefinementGate_ask_fork_blocks_dispatch_until_collaboration_resolved")]
    public async Task GoalRefinementGateAskForkBlocksDispatchUntilResolved()
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var json = """
            ```json
            {
              "behavioralContract": "Integrates with an external API.",
              "acceptanceCriteria": ["Endpoint responds correctly"],
              "verificationClass": "TestVerifiable",
              "decisions": [],
              "forks": [{"kind": "external-contract", "refinerConfidence": "low", "blastRadius": "high", "question": "Which API version to target?", "choice": "", "rationale": "Unclear from objective."}]
            }
            ```
            """;
        var provider = new FakeSmokeProvider(text: json, providerName: "fake-refiner");
        var providers = new InMemoryModelProviderRegistry([provider]);
        ModelFunctionCatalogStore.Save(workspace.ModelFunctionCatalogPath, new ModelFunctionCatalog([
            new ModelFunctionBinding(
                ModelFunctionPurposes.SpecRefiner,
                ModelLane.CheapApi,
                new ModelProfile("fake-refiner", "fake-model", ModelCapability.Text, SubscriptionMode.ApiKey))
        ]));
        var kernel = new AgentOrchestratorKernel();
        var goal = GoalLifecycleCommands.CreateAndActivateGoal(
            kernel,
            AgentCatalog.Default().Agents,
            "Integrate API with unspecified version",
            workspace,
            providers);

        Xunit.Assert.True(GoalRefinementGate.HasOpenClarification(workspace, goal));
        var blocked = Xunit.Assert.ThrowsAny<InvalidOperationException>(
            () => GoalManagementCommandService.SubscriptionDispatchReadyTasks(
                kernel,
                workspace,
                goal,
                AgentCatalog.Default().Agents,
                WorkerProfileCatalog.Default(),
                providers));
        Xunit.Assert.Contains("Resolve spec clarification", blocked.Message);

        var item = (await CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory).ListAsync(goal.Id.Value))
            .Single();
        var resolved = await CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory)
            .TryResolveAsync(item.CorrelationKey!, "Use v2.");

        Xunit.Assert.True(resolved);
        Xunit.Assert.False(GoalRefinementGate.HasOpenClarification(workspace, goal));
        GoalRefinementGate.ThrowIfAwaitingClarification(workspace, goal);
    }

    [Xunit.Fact(DisplayName = "GoalRefinementGate_bulk_open_clarification_matches_per_goal_helper")]
    public async Task GoalRefinementGateBulkOpenClarificationMatchesPerGoalHelper()
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();
        var openGoal = kernel.CreateGoal("Needs operator decision");
        var resolvedGoal = kernel.CreateGoal("Resolved operator decision");
        var missingGoal = kernel.CreateGoal("No clarification");
        var unrelatedGoal = kernel.CreateGoal("Outside current run");
        var store = CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory);

        await store.RaiseAsync(
            CollaborationItemType.Clarification,
            openGoal.Id.Value,
            "open",
            "body",
            $"spec-clarification:{openGoal.Id.Value}:open");
        var resolvedItem = await store.RaiseAsync(
            CollaborationItemType.Clarification,
            resolvedGoal.Id.Value,
            "resolved",
            "body",
            $"spec-clarification:{resolvedGoal.Id.Value}:resolved");
        await store.TryResolveAsync(resolvedItem.CorrelationKey!, "done");
        await store.RaiseAsync(
            CollaborationItemType.Clarification,
            unrelatedGoal.Id.Value,
            "unrelated",
            "body",
            $"spec-clarification:{unrelatedGoal.Id.Value}:unrelated");

        var bulk = GoalRefinementGate.OpenClarificationGoalIds(
            workspace,
            [openGoal.Id, resolvedGoal.Id, missingGoal.Id]);

        Xunit.Assert.Equal(GoalRefinementGate.HasOpenClarification(workspace, openGoal), bulk.Contains(openGoal.Id));
        Xunit.Assert.Equal(GoalRefinementGate.HasOpenClarification(workspace, resolvedGoal), bulk.Contains(resolvedGoal.Id));
        Xunit.Assert.Equal(GoalRefinementGate.HasOpenClarification(workspace, missingGoal), bulk.Contains(missingGoal.Id));
        Xunit.Assert.DoesNotContain(unrelatedGoal.Id, bulk);
    }

    [Xunit.Fact(DisplayName = "GoalRefinementGate_is_idempotent_for_refined_goal")]
    public void GoalRefinementGateIsIdempotentForRefinedGoal()
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var provider = new FakeSmokeProvider(text: "{} ", providerName: "fake-refiner");
        var providers = new InMemoryModelProviderRegistry([provider]);
        ModelFunctionCatalogStore.Save(workspace.ModelFunctionCatalogPath, new ModelFunctionCatalog([
            new ModelFunctionBinding(
                ModelFunctionPurposes.SpecRefiner,
                ModelLane.CheapApi,
                new ModelProfile("fake-refiner", "fake-model", ModelCapability.Text, SubscriptionMode.ApiKey))
        ]));
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Do one thing");

        var first = GoalRefinementGate.EnsureRefined(kernel, workspace, providers, goal);
        var second = GoalRefinementGate.EnsureRefined(kernel, workspace, providers, goal);

        Xunit.Assert.True(first.RanRefinement);
        Xunit.Assert.False(second.RanRefinement);
        Xunit.Assert.Equal(1, kernel.GetTimeline(goal.Id).Count(evt =>
            evt.Kind == ProgressKind.GoalPolicyDecision &&
            evt.Message.Contains("Goal refinement attached", StringComparison.Ordinal)));
    }

    // --- Answer-back: a store-only resolution (the listener path) is synced into the spec by the gate ---

    [Xunit.Fact(DisplayName = "GoalRefinementGate_EnsureRefined_syncs_store_resolved_answer_into_spec_and_resumes")]
    public async Task EnsureRefinedSyncsStoreResolvedAnswerIntoSpecAndResumes()
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var provider = new FakeSmokeProvider(
            text: """
                ```json
                {"behavioralContract":"Integrate billing.","acceptanceCriteria":["works"],"verificationClass":"TestVerifiable","decisions":[],"forks":[{"kind":"external-contract","refinerConfidence":"low","blastRadius":"high","question":"Which API version?","choice":"","rationale":"unspecified"}]}
                ```
                """,
            providerName: "fake-refiner");
        var providers = new InMemoryModelProviderRegistry([provider]);
        ModelFunctionCatalogStore.Save(workspace.ModelFunctionCatalogPath, new ModelFunctionCatalog([
            new ModelFunctionBinding(
                ModelFunctionPurposes.SpecRefiner,
                ModelLane.CheapApi,
                new ModelProfile("fake-refiner", "fake-model", ModelCapability.Text, SubscriptionMode.ApiKey))
        ]));
        var kernel = new AgentOrchestratorKernel();
        var goal = GoalLifecycleCommands.CreateAndActivateGoal(
            kernel, AgentCatalog.Default().Agents, "Integrate API with unspecified version", workspace, providers);

        // Refinement raised a clarification: goal is awaiting and the spec has an open question.
        Xunit.Assert.True(GoalRefinementGate.HasOpenClarification(workspace, goal));
        Xunit.Assert.True(kernel.GetGoal(goal.Id).RefinedSpec!.HasOpenQuestions);

        // Listener path: resolve in the collaboration store ONLY (no kernel / no write lock).
        var item = (await CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory).ListAsync(goal.Id.Value)).Single();
        await CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory).TryResolveAsync(item.CorrelationKey!, "v2");
        Xunit.Assert.False(GoalRefinementGate.HasOpenClarification(workspace, goal));

        // Conductor/gate path: EnsureRefined syncs the stored answer into the spec and clears AwaitingClarification.
        var result = GoalRefinementGate.EnsureRefined(kernel, workspace, providers, kernel.GetGoal(goal.Id));

        Xunit.Assert.Equal(RefinementOutcome.AutoRefined, result.Outcome);
        Xunit.Assert.False(result.Spec.HasOpenQuestions);
        Xunit.Assert.Contains(result.Spec.Decisions, decision => decision.Choice == "v2");
        Xunit.Assert.False(kernel.GetGoal(goal.Id).RefinedSpec!.HasOpenQuestions);
    }

    [Xunit.Fact(DisplayName = "SubscriptionCliCompleter_BuildStartInfo_uses_utf8_stdio_encoding")]
    public void SubscriptionCliCompleterBuildStartInfoUsesUtf8StdioEncoding()
    {
        var startInfo = SubscriptionCliCompleter.BuildStartInfo("claude -p prompt", CreateTempDirectory());

        Xunit.Assert.Equal(System.Text.Encoding.UTF8, startInfo.StandardOutputEncoding);
        Xunit.Assert.Equal(System.Text.Encoding.UTF8, startInfo.StandardErrorEncoding);
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
        SpecRefinerPrecedentStore? precedentStore = null,
        WorkerProfileCatalog? workerProfiles = null,
        Func<SubscriptionLaunchProfile, SubscriptionCliCompleter>? subscriptionCompleterFactory = null)
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

        var service = new GoalRefinementService(
            registry,
            effectiveCatalog,
            collab,
            prec,
            workerProfiles,
            subscriptionCompleterFactory);
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Integrate the billing system");

        return (service, kernel, goal.Id, collab);
    }
}
