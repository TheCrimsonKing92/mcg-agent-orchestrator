using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Uses only the isolated landing fixture and its SQLite intent/state stores.
[Collection(TestCollections.LandingGitRunner)]
public sealed class PostLandingEvidenceClosureTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CliMerge_InvalidIntegrationTarget_RejectsWithoutPublication(
        bool acceptance, bool detached)
    {
        using var fixture = await PostLandingObligationTests.Fixture.Create();
        var preLanding = Assert.Single(fixture.Goal.GetLandingPrerequisiteObligations(fixture.Candidate));
        fixture.Kernel.RecordCriterionEvidence(fixture.Goal.Id, preLanding.Id, preLanding.Owner,
            fixture.Candidate, "isolated-pre-landing-proof", preLanding.RequiredScope, true,
            "Isolated proof already recorded before the CLI merge");
        await fixture.Repository.SaveAsync(fixture.Kernel);
        var checkout = detached
            ? GitCli.Run(fixture.Root, "checkout", "--detach", fixture.MainBefore)
            : GitCli.Run(fixture.Root, "checkout", "-b", "non-integration", fixture.MainBefore);
        Assert.True(checkout.Succeeded, checkout.Error);
        Assert.Equal(detached ? "" : "non-integration",
            LandingExecutorTests.ReadGit(fixture.Root, "branch", "--show-current"));
        Assert.NotEqual(fixture.Candidate, fixture.MainBefore);
        Assert.Equal(fixture.MainBefore, LandingExecutorTests.ReadGit(fixture.Root, "rev-parse", "HEAD"));
        Assert.Null(await fixture.Repository.ReadPromotionFactAsync(fixture.Goal.Id));

        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = fixture.Goal;
        var output = AsyncLocalConsoleRouter.Capture(() =>
            AcceptanceStableSlotTestSupport.ExecuteWithIsolatedStableSlot(
                acceptance
                    ? ["acceptance", fixture.Goal.Id.Value, "--skip-verify", "--keep-workspace", "--no-record"]
                    : ["workspace", "merge", fixture.Goal.Id.Value],
                fixture.Repository, fixture.Workspace, ref agents, new InMemoryModelProviderRegistry([]),
                ref profiles, ref currentGoal));

        Assert.Contains($"HEAD must be attached to integration branch '{fixture.Workspace.IntegrationBranch}'", output);
        Assert.Equal(fixture.MainBefore, LandingExecutorTests.ReadGit(fixture.Root, "rev-parse", "HEAD"));
        Assert.Equal(fixture.MainBefore,
            LandingExecutorTests.ReadGit(fixture.Root, "rev-parse", fixture.Workspace.IntegrationBranch));
        Assert.Equal(fixture.Candidate,
            LandingExecutorTests.ReadGit(fixture.Root, "rev-parse", GoalWorktrees.BranchName(fixture.Goal.Id)));
        Assert.Null(await fixture.Repository.ReadPromotionFactAsync(fixture.Goal.Id));
        await fixture.Reload();
        Assert.NotEqual(GoalStatus.Completed, fixture.Goal.Status);
        var duty = Assert.Single(fixture.Goal.OutstandingPostLandingDuties);
        Assert.Equal(CriterionEvidenceState.Pending, duty.State);
        Assert.Null(duty.Publication);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CliMerge_LaterDuty_BindsPublicationAndClosesAfterReload(bool acceptance)
    {
        using var fixture = await PostLandingObligationTests.Fixture.Create();
        var preLanding = Assert.Single(fixture.Goal.GetLandingPrerequisiteObligations(fixture.Candidate));
        fixture.Kernel.RecordCriterionEvidence(fixture.Goal.Id, preLanding.Id, preLanding.Owner,
            fixture.Candidate, "isolated-pre-landing-proof", preLanding.RequiredScope, true,
            "Isolated proof already recorded before the CLI merge");
        await fixture.Repository.SaveAsync(fixture.Kernel);
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = fixture.Goal;
        var output = AsyncLocalConsoleRouter.Capture(() =>
            AcceptanceStableSlotTestSupport.ExecuteWithIsolatedStableSlot(
                acceptance
                    ? ["acceptance", fixture.Goal.Id.Value, "--skip-verify", "--keep-workspace", "--no-record"]
                    : ["workspace", "merge", fixture.Goal.Id.Value],
                fixture.Repository, fixture.Workspace, ref agents, new InMemoryModelProviderRegistry([]),
                ref profiles, ref currentGoal));
        Assert.Contains("Fast-forwarded", output);
        Assert.Equal(fixture.Candidate, LandingExecutorTests.ReadGit(fixture.Root, "rev-parse", "main"));
        await fixture.Reload();
        if (acceptance) Assert.Equal(GoalStatus.Completed, fixture.Goal.Status);
        var duty = Assert.Single(fixture.Goal.OutstandingPostLandingDuties);
        Assert.Equal(CriterionEvidenceState.Pending, duty.State);
        var publication = Assert.IsType<GoalPromotionFact>(duty.Publication);
        Assert.Equal(fixture.Candidate, publication.CandidateRevision);
        Assert.Equal(fixture.MainBefore, publication.PriorMainRevision);
        Assert.Equal(fixture.Candidate, publication.PromotedMainRevision);
        Assert.Equal(GoalPromotionFact.CliFastForwardProvenance, publication.Provenance);
        Assert.Equal(publication, await fixture.Repository.ReadPromotionFactAsync(fixture.Goal.Id));

        // Re-observing a successful merge must preserve the original publication.
        AsyncLocalConsoleRouter.Capture(() => AcceptanceStableSlotTestSupport.ExecuteWithIsolatedStableSlot(
            ["workspace", "merge", fixture.Goal.Id.Value], fixture.Repository, fixture.Workspace,
            ref agents, new InMemoryModelProviderRegistry([]), ref profiles, ref currentGoal));
        await fixture.Reload();
        Assert.Equal(publication, Assert.Single(fixture.Goal.OutstandingPostLandingDuties).Publication);
        Assert.Equal(publication, await fixture.Repository.ReadPromotionFactAsync(fixture.Goal.Id));

        var observation = Observation(duty) with
        {
            ActivatedAt = publication.PromotedAt,
            ObservedAt = publication.PromotedAt.AddTicks(1)
        };
        var store = SqliteOperatorIntentStore.ForDirectories(
            fixture.Workspace.OrchestratorDirectory, fixture.Workspace.LogDirectory);
        var intent = await Enqueue(store, fixture.Goal,
            Payload(fixture, duty, "cli-subsequent-observation", observation), OperatorActorKind.Human);
        var attempts = new ConductorParallelAcceptanceAttemptCoordinator(
            Path.Combine(fixture.Workspace.OrchestratorDirectory, "acceptance-gate-attempts"),
            TrunkBranchName.Default, fixture.Workspace.ExecutionDirectory);
        var outcome = await OperatorIntentGoalApplication.ApplyOffline(
            fixture.Workspace, fixture.Repository, new OperatorIntentCoordinator(store), attempts, fixture.Goal.Id);
        Assert.True(outcome is OperatorIntentOfflineOutcome.Applied,
            outcome is OperatorIntentOfflineOutcome.NotDurable failure ? failure.Reason : outcome.ToString());
        Assert.Equal(OperatorIntentStatus.Applied, (await store.GetAsync(intent.Id))!.Status);
        await fixture.Reload();
        Assert.Empty(fixture.Goal.OutstandingPostLandingDuties);
        var closed = fixture.Goal.CriterionEvidenceObligations.Single(item => item.Id == duty.Id);
        Assert.Equal(CriterionEvidenceState.Satisfied, closed.State);
        Assert.Equal(publication, closed.Publication);
        Assert.Equal("operator", closed.ObservationActor);
        Assert.Equal(observation, closed.Observation);
        if (acceptance) Assert.Equal(GoalStatus.Completed, fixture.Goal.Status);
    }

    [Theory]
    [InlineData("agent")]
    [InlineData("missing-activation")]
    [InlineData("pre-activation")]
    [InlineData("different-candidate")]
    [InlineData("different-scope")]
    [InlineData("different-version")]
    public async Task OperatorIntent_InvalidAuthorityOrObservation_LeavesPending(string scenario)
    {
        using var fixture = await PostLandingObligationTests.Fixture.Create();
        await fixture.Land();
        var duty = Assert.Single(fixture.Goal.OutstandingPostLandingDuties);
        var observation = Observation(duty);
        var payload = Payload(fixture, duty, "invalid", observation);
        payload = scenario switch
        {
            "missing-activation" => payload with { Observation = observation with { ActivationAttested = false } },
            "pre-activation" => payload with { Observation = observation with { ObservedAt = observation.ActivatedAt.AddSeconds(-1) } },
            "different-candidate" => payload with { CandidateSha = new string('e', 40) },
            "different-scope" => payload with { Observation = observation with { Scope = observation.Scope with { Target = "foreign-runtime" } } },
            "different-version" => payload with { ObligationId = "criterion-v99-1" },
            _ => payload
        };
        var store = SqliteOperatorIntentStore.ForDirectories(fixture.Workspace.OrchestratorDirectory, fixture.Workspace.LogDirectory);
        var intent = await Enqueue(store, fixture.Goal, payload,
            scenario == "agent" ? OperatorActorKind.Agent : OperatorActorKind.Human);
        var result = new OperatorIntentCoordinator(store).ExecutePending(fixture.Kernel, fixture.Goal);
        Assert.False(result.MutatedGoalState);
        Assert.Equal(OperatorIntentStatus.Rejected, (await store.GetAsync(intent.Id))!.Status);
        Assert.Equal(CriterionEvidenceState.Pending, Assert.Single(fixture.Goal.OutstandingPostLandingDuties).State);
        Assert.Equal(GoalStatus.Completed, fixture.Goal.Status);
        await fixture.Repository.SaveAsync(fixture.Kernel);
        await fixture.Reload();
        Assert.Equal(duty.Id, Assert.Single(fixture.Goal.OutstandingPostLandingDuties).Id);
    }

    [Fact]
    public async Task OperatorIntent_OriginalArtifactAfterMainMovement_ClosesDurably()
    {
        using var fixture = await PostLandingObligationTests.Fixture.Create();
        await fixture.Land();
        var duty = Assert.Single(fixture.Goal.OutstandingPostLandingDuties);
        LandingExecutorTests.AppendCommit(fixture.Root, "later-main.txt", "Unrelated later main change");
        Assert.NotEqual(duty.Publication!.PromotedMainRevision,
            LandingExecutorTests.ReadGit(fixture.Root, "rev-parse", "main"));
        fixture.Kernel.RecordGoalRefinement(fixture.Goal.Id,
            new RefinedSpec("Later criteria", ["Separate later implementation proof"], VerificationClass.TestVerifiable, [], []));
        await fixture.Repository.SaveAsync(fixture.Kernel);
        await fixture.Reload();
        Assert.Equal(duty.Id, Assert.Single(fixture.Goal.OutstandingPostLandingDuties).Id);
        var store = SqliteOperatorIntentStore.ForDirectories(fixture.Workspace.OrchestratorDirectory, fixture.Workspace.LogDirectory);
        var payload = Payload(fixture, duty, "valid-observation", Observation(duty));
        var intent = await Enqueue(store, fixture.Goal, payload, OperatorActorKind.Human);
        var coordinator = new OperatorIntentCoordinator(store);
        var attempts = new ConductorParallelAcceptanceAttemptCoordinator(
            Path.Combine(fixture.Workspace.OrchestratorDirectory, "acceptance-gate-attempts"),
            TrunkBranchName.Default, fixture.Workspace.ExecutionDirectory);
        var outcome = await OperatorIntentGoalApplication.ApplyOffline(fixture.Workspace, fixture.Repository,
            coordinator, attempts, fixture.Goal.Id);
        Assert.IsType<OperatorIntentOfflineOutcome.Applied>(outcome);
        Assert.Equal(OperatorIntentStatus.Applied, (await store.GetAsync(intent.Id))!.Status);
        await fixture.Reload();
        Assert.Equal(GoalStatus.Completed, fixture.Goal.Status);
        Assert.Empty(fixture.Goal.OutstandingPostLandingDuties);
        var accepted = fixture.Goal.CriterionEvidenceObligations.Single(item => item.Id == duty.Id);
        Assert.Equal(CriterionEvidenceState.Satisfied, accepted.State);
        Assert.Equal(duty.Publication, accepted.Publication);
        Assert.Equal("operator", accepted.ObservationActor);
        Assert.Equal(payload.Observation, accepted.Observation);
        Assert.Equal(1, accepted.CriterionVersion);
        Assert.Empty((await fixture.Repository.ReadAttentionProjectionAsync())!.PostLandingDuties!);

        await Enqueue(store, fixture.Goal, payload, OperatorActorKind.Human);
        Assert.IsType<OperatorIntentOfflineOutcome.Applied>(await OperatorIntentGoalApplication.ApplyOffline(
            fixture.Workspace, fixture.Repository, new OperatorIntentCoordinator(store), attempts, fixture.Goal.Id));
        var changed = await Enqueue(store, fixture.Goal,
            payload with { Observation = payload.Observation! with { ActivationIdentity = "another-deployment" } }, OperatorActorKind.Human);
        new OperatorIntentCoordinator(store).ExecutePending(fixture.Kernel, fixture.Goal);
        Assert.Equal(OperatorIntentStatus.Rejected, (await store.GetAsync(changed.Id))!.Status);
    }

    [Fact]
    public async Task CliSubmission_AgentAttributionAndMetadata_PersistWithoutPromotion()
    {
        using var fixture = await PostLandingObligationTests.Fixture.Create();
        await fixture.Land();
        var duty = Assert.Single(fixture.Goal.OutstandingPostLandingDuties);
        var observation = Observation(duty);
        var output = AsyncLocalConsoleRouter.Capture(() => CliCriterionEvidenceIntents.Submit(
            ["criterion-evidence-record", "--goal", fixture.Goal.Id.Value, duty.Id, "operator", fixture.Candidate,
                "cli-receipt", duty.RequiredScope, "passed", "Observed", "--observation-json",
                JsonSerializer.Serialize(observation, OperatorIntentJson.Options)], fixture.Workspace, fixture.Goal,
            new CliPersistentStateRunner.OperatorIntentAttribution("worker", "cli", "local-process", OperatorActorKind.Agent)));
        Assert.Contains("queued", output, StringComparison.OrdinalIgnoreCase);
        var store = SqliteOperatorIntentStore.ForDirectories(fixture.Workspace.OrchestratorDirectory, fixture.Workspace.LogDirectory);
        var intent = Assert.Single(await store.ListForGoalAsync(fixture.Goal.Id.Value));
        Assert.Equal(OperatorActorKind.Agent, intent.ActorKind);
        Assert.Equal(observation, JsonSerializer.Deserialize<CriterionEvidenceReceiptOperatorIntentPayload>(intent.PayloadJson,
            OperatorIntentJson.Options)!.Observation);
        new OperatorIntentCoordinator(store).ExecutePending(fixture.Kernel, fixture.Goal);
        Assert.Equal(OperatorIntentStatus.Rejected, (await store.GetAsync(intent.Id))!.Status);
        Assert.Single(fixture.Goal.OutstandingPostLandingDuties);
    }

    [Theory]
    [InlineData(OperatorIntentVerbs.Retry)]
    [InlineData(OperatorIntentVerbs.GoalPriority)]
    public async Task CompletedGoal_UnrelatedIntent_IsRejectedBeforeAnyMutation(string verb)
    {
        using var fixture = await PostLandingObligationTests.Fixture.Create();
        await fixture.Land();
        var before = JsonSerializer.Serialize(fixture.Kernel.ExportSnapshot());
        var store = SqliteOperatorIntentStore.ForDirectories(fixture.Workspace.OrchestratorDirectory, fixture.Workspace.LogDirectory);
        var intent = await store.EnqueueAsync(new OperatorIntentRecord(Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"),
            verb, fixture.Goal.Id.Value, fixture.Goal.Tasks.Single().Id.Value,
            verb == OperatorIntentVerbs.Retry
                ? JsonSerializer.Serialize(new RetryOperatorIntentPayload("retry", null))
                : JsonSerializer.Serialize(new GoalPriorityOperatorIntentPayload("high", "priority")),
            [], "operator", "cli", "local-process", DateTimeOffset.UtcNow));
        Assert.False(new OperatorIntentCoordinator(store).ExecutePending(fixture.Kernel, fixture.Goal).MutatedGoalState);
        Assert.Equal(OperatorIntentStatus.Rejected, (await store.GetAsync(intent.Id))!.Status);
        Assert.Equal(before, JsonSerializer.Serialize(fixture.Kernel.ExportSnapshot()));
    }

    private static CriterionPostLandingObservation Observation(CriterionEvidenceObligation duty) =>
        new(duty.Publication!.PromotedMainRevision, duty.ObservationScope!, "local-runtime-session-1",
            "Human verified the running artifact, runtime identity and activation record before observing",
            duty.Publication.PromotedAt.AddMinutes(1), duty.Publication.PromotedAt.AddMinutes(2), ActivationAttested: true,
            GoalId: duty.Publication.GoalId, ObligationId: duty.Id);

    private static CriterionEvidenceReceiptOperatorIntentPayload Payload(PostLandingObligationTests.Fixture fixture,
        CriterionEvidenceObligation duty, string receiptId, CriterionPostLandingObservation observation) =>
        new(duty.Id, CriterionEvidenceOwner.Operator, fixture.Candidate, receiptId, duty.RequiredScope, true,
            "Observed matching condition on the activated runtime", observation);

    private static Task<OperatorIntentRecord> Enqueue(SqliteOperatorIntentStore store, Goal goal,
        CriterionEvidenceReceiptOperatorIntentPayload payload, OperatorActorKind actorKind) =>
        store.EnqueueAsync(new OperatorIntentRecord(Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"),
            OperatorIntentVerbs.CriterionEvidenceRecord, goal.Id.Value, null,
            JsonSerializer.Serialize(payload, OperatorIntentJson.Options), [], "operator", "cli", "local-process",
            DateTimeOffset.UtcNow, ActorKind: actorKind));
}
