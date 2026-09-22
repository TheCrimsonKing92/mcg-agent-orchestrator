using Mcg.AgentOrchestrator.App.Dashboard.Api;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class CliCommandTestsHumanInputSupersede : CliCommandTestBase
{
    [Xunit.Fact]
    public void SupersedeCommand_ReportsAuthoritativeAnswer_AndMapperReturnsAuditHistory()
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Wait for a choice.", AgentRole.Planner);
        var goal = kernel.CreateGoal("Correct a clarification.", [task]);
        var request = kernel.RequestHumanInput(goal.Id, task.Id, "Which value?");
        kernel.SubmitHumanInput(request.Id, "Use one second.");

        var output = ExecuteCliAndCapture(
            ["supersede", goal.Id.Value[..8], request.Id.Value[..8], "Use five seconds."],
            kernel,
            workspace);
        var dto = DashboardResponseMapper.ToHumanInputDto(kernel, request);

        Assert.Contains("Authoritative answer: Use five seconds.", output, StringComparison.Ordinal);
        Assert.Equal("Use five seconds.", dto.Answer);
        Assert.Equal(2, dto.AnswerHistory.Count);
        Assert.True(dto.AnswerHistory[0].IsRetracted);
        Assert.False(dto.AnswerHistory[0].IsAuthoritative);
        Assert.Equal(dto.AnswerHistory[1].Id, dto.AnswerHistory[0].SupersededByAnswerId);
        Assert.True(dto.AnswerHistory[1].IsAuthoritative);
        Assert.Equal("Use one second.", dto.AnswerHistory[0].Text);
    }

    [Xunit.Fact]
    public void SupersedeCommand_ScopesRequestLookupToOwningGoal()
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();
        var owner = kernel.CreateGoal("Owner", [new TaskSpec(TaskId.New(), "Plan.", AgentRole.Planner)]);
        var other = kernel.CreateGoal("Other", [new TaskSpec(TaskId.New(), "Plan.", AgentRole.Planner)]);
        var request = kernel.RequestHumanInput(owner.Id, owner.Tasks.Single().Id, "Which value?");
        kernel.SubmitHumanInput(request.Id, "A");

        var error = Assert.ThrowsAny<KeyNotFoundException>(() => ExecuteCliAndCapture(
            ["supersede", other.Id.Value[..8], request.Id.Value[..8], "B"],
            kernel,
            workspace));

        Assert.Contains("was not found on goal", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("belongs to a different goal", error.Message, StringComparison.Ordinal);
        Assert.Equal("A", request.Answer);
    }

    [Xunit.Fact]
    public async Task SupersedeCommand_CorrectsClosedSpecClarification_AndPreservesAuditHistory()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var repository = CreateMigratedStateRepository(workspace.SqliteStatePath);
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Implement Use 5 seconds without changing Notice text.", AgentRole.Developer);
        var goal = kernel.CreateGoal("Prior artifact says Use 5 seconds. Notice text remains.", [task]);
        var correlationKey = $"spec-clarification:{goal.Id.Value}:minimum-backoff-values";
        kernel.SetGoalRefinedSpec(goal.Id, new RefinedSpec(
            "Use the selected backoff.",
            ["The selected backoff is applied."],
            VerificationClass.TestVerifiable,
            [new RefinedSpecDecision(
                "Which backoff?",
                "Use 5 seconds",
                $"Answered by operator (key: {correlationKey}).")],
            [new RefinedSpecOpenQuestion(
                correlationKey,
                "Which backoff?",
                "minimum-backoff-values",
                "Answered",
                "Use 5 seconds") ]));
        var store = CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory);
        var item = await store.RaiseAsync(
            CollaborationItemType.Clarification,
            goal.Id.Value,
            "Minimum backoff values",
            "Which backoff?",
            correlationKey);
        Assert.True(await store.TryResolveAsync(correlationKey, "Use 5 seconds"));

        var ordinaryAnswer = Assert.Throws<InvalidOperationException>(() => ExecuteCliAndCapture(
            ["attention", "answer", goal.Id.Value[..8], "minimum-backoff-values", "5 seconds"],
            kernel,
            workspace));
        var output = ExecuteCliAndCapture(
            ["supersede", goal.Id.Value[..8], "minimum-backoff-values", "5 seconds"],
            kernel,
            workspace);
        await repository.SaveAsync(kernel);
        var providers = new InMemoryModelProviderRegistry([]);
        var pending = Xunit.Assert.Throws<InvalidOperationException>(() =>
            GoalDispatchOperations.EnsureRefinedForSpecConsumer(
                kernel,
                workspace,
                providers,
                goal));
        Xunit.Assert.StartsWith("SPEC_REFINEMENT_PENDING", pending.Message, StringComparison.Ordinal);
        _ = await GoalRefinementWorkCoordinator.ProcessAsync(
            repository,
            workspace,
            providers,
            WorkerProfileCatalog.Default(),
            goal.Id);
        kernel = await repository.LoadAsync();
        var updated = Assert.Single((await store.ListAsync(goal.Id.Value)).Where(candidate => candidate.Id == item.Id));
        var historyOutput = ExecuteCliAndCapture(
            ["attention", "show", "--all", goal.Id.Value[..8]],
            kernel,
            workspace);
        var brief = kernel.BuildTaskBrief(goal.Id, task.Id).Content;

        Assert.Contains("already answered — use supersede", ordinaryAnswer.Message, StringComparison.Ordinal);
        Assert.Contains("Authoritative answer: 5 seconds", output, StringComparison.Ordinal);
        Assert.Equal("5 seconds", updated.Resolution);
        Assert.Equal(2, updated.AnswerHistory?.Count);
        Assert.True(updated.AnswerHistory![0].IsRetracted);
        Assert.Equal(updated.AnswerHistory[1].Id, updated.AnswerHistory[0].SupersededByAnswerId);
        Assert.False(updated.AnswerHistory[1].IsRetracted);
        Assert.Contains("retracted supersededBy=", historyOutput, StringComparison.Ordinal);
        Assert.Contains("authoritative: 5 seconds", historyOutput, StringComparison.Ordinal);
        Assert.Contains("5 seconds", brief, StringComparison.Ordinal);
        Assert.DoesNotContain("Use 5 seconds", brief, StringComparison.Ordinal);
        Assert.Contains("Notice text remains", brief, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public async Task SupersedeCommand_RefreshesLinkedPrecedentCompatibilitySnapshot()
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Choose a billing provider.");
        var correlationKey = $"spec-clarification:{goal.Id.Value}:billing-provider";
        var store = CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory);
        var raised = await store.RaiseAsync(
            CollaborationItemType.Clarification,
            goal.Id.Value,
            "Billing provider",
            "Stripe or Paddle?",
            correlationKey);
        Assert.True(await store.TryResolveAsync(
            correlationKey,
            "A",
            briefVersion: goal.AuthoritativeBrief.Version));
        var resolved = Assert.Single(await store.ListAsync(goal.Id.Value));
        Assert.NotNull(resolved.AuthoritativeAnswer);
        var firstAnswer = resolved.AuthoritativeAnswer!;
        var precedents = new SpecRefinerPrecedentStore(workspace.SpecRefinerPrecedentsPath);
        await precedents.RecordPrecedentAsync(
            "billing-provider",
            firstAnswer.Text,
            "Operator clarification answer.",
            originItemId: resolved.Id,
            originGoalId: resolved.GoalId,
            originAnswerId: firstAnswer.Id,
            originBriefVersion: firstAnswer.BriefVersion);

        ExecuteCliAndCapture(
            ["supersede", goal.Id.Value[..8], raised.Id[..8], "B"],
            kernel,
            workspace);

        var updated = Assert.Single(await store.ListAsync(goal.Id.Value));
        Assert.NotNull(updated.AuthoritativeAnswer);
        var current = updated.AuthoritativeAnswer!;
        var precedent = await precedents.TryGetPrecedentAsync("billing-provider");
        Assert.NotNull(precedent);
        Assert.Equal("B", current.Text);
        Assert.Equal("B", precedent!.Choice);
        Assert.Equal(updated.Id, precedent.OriginItemId);
        Assert.Equal(current.Id, precedent.OriginAnswerId);
        Assert.Equal(current.BriefVersion, precedent.OriginBriefVersion);
    }

    [Xunit.Fact]
    public async Task SupersedeCommand_WhenPrecedentSnapshotRefreshFails_LiveConsumptionUsesAuthoritativeAnswer()
    {
        const string matchingJson = """
            ```json
            {
              "behavioralContract": "Integrates with billing.",
              "acceptanceCriteria": ["Charge applied"],
              "verificationClass": "TestVerifiable",
              "decisions": [],
              "forks": [{"kind": "external-contract", "topicKey": "billing-provider", "refinerConfidence": "low", "blastRadius": "high", "question": "Stripe or Paddle?", "choice": "", "rationale": "No prior art."}]
            }
            ```
            """;
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Choose a billing provider.");
        var correlationKey = $"spec-clarification:{goal.Id.Value}:billing-provider";
        var store = CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory);
        var raised = await store.RaiseAsync(
            CollaborationItemType.Clarification,
            goal.Id.Value,
            "Billing provider",
            "Stripe or Paddle?",
            correlationKey);
        Assert.True(await store.TryResolveAsync(
            correlationKey,
            "A",
            briefVersion: goal.AuthoritativeBrief.Version));
        var resolved = Assert.Single(await store.ListAsync(goal.Id.Value));
        Assert.NotNull(resolved.AuthoritativeAnswer);
        var firstAnswer = resolved.AuthoritativeAnswer!;
        var precedents = new SpecRefinerPrecedentStore(workspace.SpecRefinerPrecedentsPath);
        await precedents.RecordPrecedentAsync(
            "billing-provider",
            firstAnswer.Text,
            "Operator clarification answer.",
            originItemId: resolved.Id,
            originGoalId: resolved.GoalId,
            originAnswerId: firstAnswer.Id,
            originBriefVersion: firstAnswer.BriefVersion);

        string warning;
        using (File.Open(
            workspace.SpecRefinerPrecedentsPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read))
        {
            warning = CaptureConsoleError(() => ExecuteCliAndCapture(
                ["supersede", goal.Id.Value[..8], raised.Id[..8], "B"],
                kernel,
                workspace));
        }

        var stalePrecedent = await precedents.TryGetPrecedentAsync("billing-provider");
        Assert.NotNull(stalePrecedent);
        Assert.Equal("A", stalePrecedent.Choice);
        Assert.Equal(firstAnswer.Id, stalePrecedent.OriginAnswerId);
        Assert.Contains("could not be refreshed", warning, StringComparison.Ordinal);
        ModelFunctionCatalogStore.Save(workspace.ModelFunctionCatalogPath, new ModelFunctionCatalog([
            new ModelFunctionBinding(
                ModelFunctionPurposes.SpecRefiner,
                ModelLane.CheapApi,
                new ModelProfile("fake-refiner", "fake-model", ModelCapability.Text, SubscriptionMode.ApiKey))
        ]));
        var providers = new InMemoryModelProviderRegistry([
            new FakeSmokeProvider(text: matchingJson, providerName: "fake-refiner")
        ]);
        var service = new GoalRefinementService(
            providers,
            ModelFunctionCatalogStore.Load(workspace.ModelFunctionCatalogPath),
            store,
            precedents,
            WorkerProfileCatalog.Default());
        var laterGoal = kernel.CreateGoal("Choose the same billing provider again.");

        var result = await service.RefineAsync(kernel, laterGoal.Id);

        var decision = Assert.Single(result.Spec.Decisions);
        var authoritativeAnswer = Assert.Single(await store.ListAsync(goal.Id.Value)).AuthoritativeAnswer;
        Assert.NotNull(authoritativeAnswer);
        Assert.Equal("B", authoritativeAnswer.Text);
        Assert.Equal("B", decision.Choice);
        Assert.Contains(authoritativeAnswer.Id, decision.Rationale, StringComparison.Ordinal);
    }
}
