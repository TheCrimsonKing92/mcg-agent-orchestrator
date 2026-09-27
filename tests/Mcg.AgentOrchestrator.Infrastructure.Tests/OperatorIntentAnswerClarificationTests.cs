using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class OperatorIntentAnswerClarificationTests
{
    [Xunit.Theory]
    [Xunit.InlineData(OperatorActorKind.Human)]
    [Xunit.InlineData(OperatorActorKind.Agent)]
    public async Task Answer_intent_resolves_open_clarification(OperatorActorKind actorKind)
    {
        var root = Path.Combine(Path.GetTempPath(), $"answer-clarification-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            var collaboration = CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory);
            var refinement = new GoalRefinementService(new InMemoryModelProviderRegistry([]),
                ModelFunctionCatalog.Empty, collaboration,
                new SpecRefinerPrecedentStore(workspace.SpecRefinerPrecedentsPath));
            var kernel = new AgentOrchestratorKernel();
            var goal = kernel.CreateGoal("Clarify scope");
            var key = $"spec-clarification:{goal.Id.Value}:scope:runtime";
            var item = await collaboration.RaiseAsync(CollaborationItemType.Clarification,
                goal.Id.Value, "Runtime scope", "Which runtime?", key).WaitAsync(TimeSpan.FromSeconds(30));
            var initialSpec = new RefinedSpec("Clarify scope", ["Use selected runtime"],
                VerificationClass.TestVerifiable, [],
                [new RefinedSpecOpenQuestion(key, "Which runtime?", "runtime", "Open", null)]);
            kernel.SetGoalRefinedSpec(goal.Id, initialSpec);
            var baselineRoot = Path.Combine(root, "baseline");
            Directory.CreateDirectory(baselineRoot);
            var baselineWorkspace = OrchestratorWorkspace.ForDirectory(baselineRoot);
            var baselineCollaboration = CollaborationItemStore.ForDirectory(baselineWorkspace.OrchestratorDirectory);
            var baselinePrecedents = new SpecRefinerPrecedentStore(baselineWorkspace.SpecRefinerPrecedentsPath);
            var baselineRefinement = new GoalRefinementService(new InMemoryModelProviderRegistry([]),
                ModelFunctionCatalog.Empty, baselineCollaboration, baselinePrecedents);
            var baselineKernel = new AgentOrchestratorKernel();
            var baselineGoal = baselineKernel.CreateGoal(goal.Id, "Clarify scope");
            baselineKernel.SetGoalRefinedSpec(baselineGoal.Id, initialSpec);
            var baselineItem = await baselineCollaboration.RaiseAsync(CollaborationItemType.Clarification,
                baselineGoal.Id.Value, "Runtime scope", "Which runtime?", key).WaitAsync(TimeSpan.FromSeconds(30));
            Xunit.Assert.True(await baselineRefinement.TryResolveOpenClarificationAsync(
                key, "Windows", baselineGoal.AuthoritativeBrief.Version).WaitAsync(TimeSpan.FromSeconds(30)));
            await baselineRefinement.SyncAnsweredClarificationsAsync(baselineKernel, baselineGoal.Id)
                .WaitAsync(TimeSpan.FromSeconds(30));
            var intents = SqliteOperatorIntentStore.ForDirectories(workspace.OrchestratorDirectory, workspace.LogDirectory);
            var payload = new AnswerOperatorIntentPayload(OperatorAnswerTargetKind.Clarification,
                item.Id, goal.Id.Value, "Windows", actorKind, ["receipt:scope"], "scope-precedent");
            var intent = await intents.EnqueueAsync(new OperatorIntentRecord(Guid.NewGuid().ToString("N"),
                Guid.NewGuid().ToString("N"), OperatorIntentVerbs.Answer, goal.Id.Value, null,
                JsonSerializer.Serialize(payload, OperatorIntentJson.Options), [], "author", "cli",
                "local-process", DateTimeOffset.UtcNow, ActorKind: actorKind)).WaitAsync(TimeSpan.FromSeconds(30));
            var coordinator = new OperatorIntentCoordinator(intents, decisions: collaboration,
                goalStateVersionResolver: _ => 0,
                clarificationAnswers: (correlation, text, version) =>
                    refinement.TryResolveOpenClarificationAsync(correlation, text, version));

            Xunit.Assert.True(coordinator.ExecutePending(kernel, goal).MutatedGoalState);
            coordinator.CompletePersisted([goal.Id]);
            await refinement.SyncAnsweredClarificationsAsync(kernel, goal.Id).WaitAsync(TimeSpan.FromSeconds(30));

            Xunit.Assert.Equal(OperatorIntentStatus.Applied,
                (await intents.GetAsync(intent.Id).WaitAsync(TimeSpan.FromSeconds(30)))!.Status);
            var resolved = Xunit.Assert.Single(await collaboration.ListAsync(goal.Id.Value).WaitAsync(TimeSpan.FromSeconds(30)));
            Xunit.Assert.Equal("Windows", resolved.Resolution);
            Xunit.Assert.Equal(goal.AuthoritativeBrief.Version, resolved.AuthoritativeAnswer?.BriefVersion);
            Xunit.Assert.Contains(goal.RefinedSpec!.Decisions, decision => decision.Choice.Contains("Windows", StringComparison.Ordinal));
            var baselineResolved = (await baselineCollaboration.ListAsync(baselineGoal.Id.Value)
                .WaitAsync(TimeSpan.FromSeconds(30))).Single(candidate => candidate.Id == baselineItem.Id);
            Xunit.Assert.Equal(baselineResolved.Status, resolved.Status);
            Xunit.Assert.Equal(baselineResolved.Resolution, resolved.Resolution);
            Xunit.Assert.Equal(baselineGoal.RefinedSpec!.Decisions, goal.RefinedSpec.Decisions);
            Xunit.Assert.Equal(baselineGoal.RefinedSpec.OpenQuestions, goal.RefinedSpec.OpenQuestions);
            var precedent = await new SpecRefinerPrecedentStore(workspace.SpecRefinerPrecedentsPath)
                .TryGetPrecedentAsync("scope").WaitAsync(TimeSpan.FromSeconds(30));
            var baselinePrecedent = await baselinePrecedents.TryGetPrecedentAsync("scope")
                .WaitAsync(TimeSpan.FromSeconds(30));
            Xunit.Assert.Equal(baselinePrecedent?.Choice, precedent?.Choice);
            Xunit.Assert.Equal(baselinePrecedent?.OriginBriefVersion, precedent?.OriginBriefVersion);
            var decision = await collaboration.GetDecisionStateAsync($"answer-{intent.Id}").WaitAsync(TimeSpan.FromSeconds(30));
            Xunit.Assert.NotNull(decision?.Receipt);
            Xunit.Assert.StartsWith($"{actorKind.ToString().ToLowerInvariant()}:author", decision.Receipt.ActorId);
            Xunit.Assert.Equal(AuthorizationTier.AttestLand, decision.Receipt.AuthenticationAssurance);
            Xunit.Assert.Contains(item.Id, decision.Request.Subject);
            Xunit.Assert.Contains(decision.Receipt.EvidenceHashes, evidence => evidence.ReceiptId == "receipt:scope");
            Xunit.Assert.Contains(goal.Timeline, entry => entry.OperatorIntentApplied is { ActorKind: var kind } &&
                kind == actorKind && entry.OperatorIntentApplied.DecisionId == decision.Receipt.Id);
            var landedAt = DateTimeOffset.UtcNow.AddSeconds(1);
            var digest = OwnerDigestReport.Build(
                [new OwnerDigestGoalInput(goal.Id.Value, landedAt, "candidate", goal.Timeline)],
                [], new SystemClock(), landedAt.AddHours(-1), landedAt.AddHours(1));
            Xunit.Assert.Equal(actorKind == OperatorActorKind.Agent ? 1 : 0, digest.Totals.Interventions.Agent);
            Xunit.Assert.Equal(actorKind == OperatorActorKind.Human ? 1 : 0, digest.Totals.Interventions.Human);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task Claimed_answer_recovers_decision_and_precedent_after_resolution(bool briefRevised)
    {
        var root = Path.Combine(Path.GetTempPath(), $"answer-recovery-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            var decisions = CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory);
            var precedents = new SpecRefinerPrecedentStore(workspace.SpecRefinerPrecedentsPath);
            var refinement = new GoalRefinementService(new InMemoryModelProviderRegistry([]),
                ModelFunctionCatalog.Empty, decisions, precedents);
            var kernel = new AgentOrchestratorKernel();
            var goal = kernel.CreateGoal("Recover clarification answer");
            var key = $"spec-clarification:{goal.Id.Value}:scope:runtime";
            var item = await decisions.RaiseAsync(CollaborationItemType.Clarification,
                goal.Id.Value, "Runtime scope", "Which runtime?", key).WaitAsync(TimeSpan.FromSeconds(30));
            var intents = SqliteOperatorIntentStore.ForDirectories(workspace.OrchestratorDirectory, workspace.LogDirectory);
            var payload = new AnswerOperatorIntentPayload(OperatorAnswerTargetKind.Clarification,
                item.Id, goal.Id.Value, "Windows", OperatorActorKind.Human);
            var intent = await intents.EnqueueAsync(new OperatorIntentRecord(Guid.NewGuid().ToString("N"),
                Guid.NewGuid().ToString("N"), OperatorIntentVerbs.Answer, goal.Id.Value, null,
                JsonSerializer.Serialize(payload, OperatorIntentJson.Options), [], "operator", "cli",
                "local-process", DateTimeOffset.UtcNow)).WaitAsync(TimeSpan.FromSeconds(30));
            Xunit.Assert.NotNull(await intents.ClaimNextAsync(goal.Id.Value, OperatorIntentCoordinator.ClaimOwner)
                .WaitAsync(TimeSpan.FromSeconds(30)));
            Xunit.Assert.True(await decisions.TryResolveAsync(key, "Windows",
                briefVersion: goal.AuthoritativeBrief.Version).WaitAsync(TimeSpan.FromSeconds(30)));
            if (briefRevised)
                kernel.ReviseGoalBrief(goal.Id, "Recover clarification answer under revised brief");
            Xunit.Assert.Null(await precedents.TryGetPrecedentAsync("scope").WaitAsync(TimeSpan.FromSeconds(30)));
            var coordinator = new OperatorIntentCoordinator(intents, decisions: decisions,
                goalStateVersionResolver: _ => 0,
                clarificationAnswers: (correlation, text, version) =>
                    refinement.TryResolveOpenClarificationAsync(correlation, text, version),
                clarificationAnswerRecovery: (correlation, text, version) =>
                    refinement.TryRecoverResolvedClarificationPrecedentAsync(correlation, text, version));

            Xunit.Assert.True(coordinator.ExecutePending(kernel, goal).MutatedGoalState);
            coordinator.CompletePersisted([goal.Id]);

            Xunit.Assert.Equal(OperatorIntentStatus.Applied,
                (await intents.GetAsync(intent.Id).WaitAsync(TimeSpan.FromSeconds(30)))!.Status);
            Xunit.Assert.NotNull((await decisions.GetDecisionStateAsync($"answer-{intent.Id}")
                .WaitAsync(TimeSpan.FromSeconds(30)))?.Receipt);
            Xunit.Assert.Equal("Windows", (await precedents.TryGetPrecedentAsync("scope")
                .WaitAsync(TimeSpan.FromSeconds(30)))?.Choice);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
