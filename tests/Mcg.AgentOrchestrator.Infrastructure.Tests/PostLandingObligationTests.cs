using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using static ConductorDriverTests;

// Owns a unique real repository and stores; the collection protects the existing
// LandingExecutor git-runner seam. No production transitions or wall-clock verdicts.
[Collection(TestCollections.LandingGitRunner)]
public sealed class PostLandingObligationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CliMerge_PreLandingOperatorControl_PreventsPublication(bool acceptance)
    {
        using var fixture = await Fixture.Create(includePreLandingOperator: true);
        var gate = fixture.Goal.CriterionEvidenceObligations.Single(item => item.Owner == CriterionEvidenceOwner.Acceptance);
        fixture.Kernel.RecordCriterionEvidence(fixture.Goal.Id, gate.Id, gate.Owner, fixture.Candidate,
            "isolated-gate-proof", gate.RequiredScope, true, "Pre-landing gate satisfied; operator control pending");
        await fixture.Repository.SaveAsync(fixture.Kernel);
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = fixture.Goal;
        AsyncLocalConsoleRouter.Capture(() => AcceptanceStableSlotTestSupport.ExecuteWithIsolatedStableSlot(
            acceptance
                ? ["acceptance", fixture.Goal.Id.Value, "--skip-verify", "--keep-workspace", "--no-record"]
                : ["workspace", "merge", fixture.Goal.Id.Value],
            fixture.Repository, fixture.Workspace, ref agents, new InMemoryModelProviderRegistry([]),
            ref profiles, ref currentGoal));
        Assert.Equal(fixture.MainBefore, LandingExecutorTests.ReadGit(fixture.Root, "rev-parse", "main"));
        Assert.Null(await fixture.Repository.ReadPromotionFactAsync(fixture.Goal.Id));
        await fixture.Reload();
        Assert.Null(Assert.Single(fixture.Goal.OutstandingPostLandingDuties).Publication);
        Assert.Equal(CriterionEvidenceOwner.Operator,
            Assert.Single(fixture.Goal.GetLandingPrerequisiteObligations(fixture.Candidate)).Owner);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NormalLanding_LaterDuty_RemainsPendingAndVisibleAfterReload(bool archive)
    {
        using var fixture = await Fixture.Create();
        await fixture.Reload();
        Assert.Equal(2, fixture.Goal.CriterionEvidenceObligations.Count);
        var later = Assert.Single(fixture.Goal.OutstandingPostLandingDuties);
        Assert.Equal(CriterionEvidenceState.Pending, later.State);
        Assert.Single(fixture.Goal.GetLandingPrerequisiteObligations(fixture.Candidate));
        Assert.Empty(ConductorBatchLoop.TrainIneligibleCriterionEvidenceGoalIds([fixture.Goal]));

        await fixture.Land();
        if (archive)
            Assert.Equal(1, await fixture.Repository.ArchiveTerminalGoalSnapshotsAsync(
                DateTimeOffset.UtcNow.AddDays(1), compressSnapshots: true));
        await fixture.Reload();

        Assert.Equal(GoalStatus.Completed, fixture.Goal.Status);
        later = Assert.Single(fixture.Goal.OutstandingPostLandingDuties);
        Assert.Equal(CriterionEvidenceState.Pending, later.State);
        Assert.Equal(fixture.Candidate, later.Publication!.CandidateRevision);
        Assert.Equal(fixture.At, later.Publication.PromotedAt);
        var projection = Assert.IsType<AttentionReadProjection>(await fixture.Repository.ReadAttentionProjectionAsync());
        var projected = Assert.Single(projection.PostLandingDuties!);
        Assert.Equal(fixture.Goal.Id.Value, projected.GoalId);
        Assert.Equal(later.Id, projected.Obligation.Id);
        var selection = AttentionReadGoalSelection.Select(
            await fixture.Repository.ListGoalMetadataAsync(), [], [], [], [fixture.Goal.Id]);
        Assert.Equal(fixture.Goal.Id, Assert.Single(selection.GoalIds));
        var json = AttentionAgentReadView.Render([], [], goals: fixture.Kernel.Goals);
        Assert.Contains("postLandingDuties", json);
        Assert.Contains(later.Id, json);
        Assert.Contains(fixture.Candidate, json);
        var queryContext = await AttentionJsonReadContext.ReadAsync(fixture.Repository,
            fixture.Workspace, fixture.At.AddHours(1));
        Assert.Equal(later.Id, Assert.Single(queryContext.PostLandingDuties!).Obligation.Id);
        var show = AttentionShowAgentReadView.Render(fixture.Goal.Id.Value, [], [], _ => null,
            goals: fixture.Kernel.Goals);
        Assert.Contains(later.Id, show);
        var output = AsyncLocalConsoleRouter.Capture(() => PostLandingDutiesTextView.Print(fixture.Kernel.Goals));
        Assert.Contains("phase=PostLanding state=Pending", output);
        Assert.Contains(later.Id, output);
        Assert.Contains("criterion-evidence-record", output);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NormalLanding_ExplicitOrLegacyPreLandingControl_StillBlocks(bool legacy)
    {
        using var fixture = await Fixture.Create(includePreLandingOperator: true, legacyPreLanding: legacy);
        var result = LandingExecutor.Execute(fixture.Kernel, fixture.Goal, fixture.Workspace);
        Assert.False(result.MainAdvanced);
        Assert.IsType<LandingDecision.Escalate>(result.Decision);
        Assert.Equal(fixture.MainBefore, LandingExecutorTests.ReadGit(fixture.Root, "rev-parse", "main"));
        Assert.Throws<InvalidOperationException>(() => fixture.Kernel.CompleteGoal(fixture.Goal.Id, "must block"));
        var control = Assert.Single(fixture.Goal.GetLandingPrerequisiteObligations(fixture.Candidate));
        Assert.Equal(CriterionEvidenceOwner.Operator, control.Owner);
        Assert.Equal(CriterionEvidencePhase.PreLanding, control.Phase);
        fixture.Kernel.RecordCriterionEvidence(fixture.Goal.Id, control.Id, control.Owner,
            fixture.Candidate, "approval", control.RequiredScope, true, "Approved before publication");
        await fixture.Land();
        Assert.Equal(CriterionEvidenceState.Pending, Assert.Single(fixture.Goal.OutstandingPostLandingDuties).State);
    }

    [Theory]
    [InlineData("Unknown", "operator", true)]
    [InlineData("PostLanding", "acceptance-gate", true)]
    [InlineData("PostLanding", "operator", false)]
    [InlineData("PreLanding,PostLanding", "operator", true)]
    public void Intake_InvalidOrMixedDeclaration_ReportsAtomicDeclarationRequired(
        string phase, string owner, bool atomic)
    {
        var response = JsonSerializer.Serialize(new
        {
            behavioralContract = "Observe", acceptanceCriteria = new[]
            {
                new { text = "Observe", evidence_owner = owner, phase, atomic,
                    observation_scope = new { kind = "Runtime", condition = "operation", target = "runtime" } }
            }
        });
        var parsed = SpecRefinerPlanner.Parse(response);
        Assert.False(parsed.IsValid);
        Assert.Contains(parsed.ValidationErrors, error => error.Contains("Separate atomic obligations", StringComparison.Ordinal));
    }

    internal sealed class Fixture : IDisposable
    {
        internal string Root { get; private init; } = "";
        internal OrchestratorWorkspace Workspace { get; private init; } = null!;
        internal SqliteOrchestratorStateRepository Repository { get; private init; } = null!;
        internal AgentOrchestratorKernel Kernel { get; private set; } = null!;
        internal Goal Goal { get; private set; } = null!;
        internal string Candidate { get; private init; } = "";
        internal string MainBefore { get; private init; } = "";
        internal DateTimeOffset At { get; } = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

        internal static async Task<Fixture> Create(bool includePreLandingOperator = false, bool legacyPreLanding = false)
        {
            var root = LandingExecutorTests.CreateGitRepository();
            try
            {
                var (workspace, kernel, goal) = LandingExecutorTests.CreateAcceptedCandidate(root, "src/behavior.txt");
                var fixtureAt = new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);
                kernel = AgentOrchestratorKernel.FromSnapshot(kernel.ExportSnapshot(), new PublicationClock(fixtureAt.AddHours(-1)));
                goal = kernel.GetGoal(goal.Id);
                var criteria = new List<object>
                {
                    new { text = "The deterministic acceptance gate passes.", evidence_owner = "acceptance-gate", phase = "PreLanding" }
                };
                if (includePreLandingOperator)
                    criteria.Add(legacyPreLanding ?
                        new { text = "Operator approves publication.", evidence_owner = "operator" } :
                        new { text = "Operator approves publication.", evidence_owner = "operator", phase = "PreLanding" });
                criteria.Add(new
                {
                    text = "Operator observes the landed behavior after activation.", evidence_owner = "operator",
                    phase = "PostLanding", atomic = true,
                    observation_scope = new { kind = "Runtime", condition = "Organic operation uses the landed behavior", target = "local-runtime" }
                });
                var provider = new FakeSmokeProvider(JsonSerializer.Serialize(new
                {
                    behavioralContract = "Prove before publication and observe after activation.", acceptanceCriteria = criteria,
                    verificationClass = "TestVerifiable", decisions = Array.Empty<object>(), forks = Array.Empty<object>()
                }), providerName: "fake-refiner");
                var service = new GoalRefinementService(new InMemoryModelProviderRegistry([provider]),
                    new ModelFunctionCatalog([new ModelFunctionBinding(ModelFunctionPurposes.SpecRefiner, ModelLane.CheapApi,
                        new ModelProfile("fake-refiner", "fake-model", ModelCapability.Text, SubscriptionMode.ApiKey))]),
                    new FakeCollaborationItemStore(),
                    new SpecRefinerPrecedentStore(Path.Combine(workspace.OrchestratorDirectory, "precedents.json")),
                    WorkerProfileCatalog.Default(), rawOutputDirectory: workspace.LogDirectory);
                await service.RefineAsync(kernel, goal.Id);
                Assert.Single(goal.OutstandingPostLandingDuties);
                Assert.False(goal.RefinedSpec!.HasOpenQuestions);
                var candidate = LandingExecutorTests.ReadGit(root, "rev-parse", GoalWorktrees.BranchName(goal.Id));
                kernel.MapCriterionEvidenceOwner(goal.Id, 0, 1, CriterionEvidenceOwner.Acceptance,
                    "operator", CriterionEvidenceScopes.FullAcceptanceGate, expectedCandidateSha: candidate);
                var fixture = new Fixture
                {
                    Root = root, Workspace = workspace, Kernel = kernel, Goal = goal, Candidate = candidate,
                    MainBefore = LandingExecutorTests.ReadGit(root, "rev-parse", "main"),
                    Repository = new SqliteOrchestratorStateRepository(workspace.SqliteStatePath)
                };
                await fixture.Repository.SaveAsync(kernel);
                return fixture;
            }
            catch { LandingExecutorTests.TryDeleteDirectory(root); throw; }
        }

        internal async Task Land()
        {
            var result = LandingExecutor.Execute(Kernel, Goal, Workspace, promotionClock: new PublicationClock(At));
            Assert.True(result.MainAdvanced, result.Message);
            Assert.IsType<LandingDecision.Promote>(result.Decision);
            Assert.NotEqual(MainBefore, LandingExecutorTests.ReadGit(Root, "rev-parse", "main"));
            Assert.Equal(CriterionEvidenceState.Satisfied,
                Goal.CriterionEvidenceObligations.Single(item => item.Owner == CriterionEvidenceOwner.Acceptance).State);
            Kernel.CompleteGoal(Goal.Id, result.Message);
            Assert.Equal(GoalStatus.Completed, Goal.Status);
            await Repository.SaveAsync(Kernel);
            Kernel = AgentOrchestratorKernel.FromSnapshot(Kernel.ExportSnapshot(), new PublicationClock(At.AddHours(1)));
            Goal = Kernel.GetGoal(Goal.Id);
        }

        internal async Task Reload()
        {
            var id = Goal.Id;
            var loaded = await Repository.LoadGoalsAsync([id]);
            Kernel = AgentOrchestratorKernel.FromSnapshot(loaded.ExportSnapshot(),
                new PublicationClock(loaded.GetGoal(id).Status == GoalStatus.Completed ? At.AddHours(1) : At.AddHours(-1)));
            Goal = Kernel.GetGoal(id);
        }

        public void Dispose() => LandingExecutorTests.TryDeleteDirectory(Root);
        private sealed record PublicationClock(DateTimeOffset UtcNow) : IClock;
    }
}
