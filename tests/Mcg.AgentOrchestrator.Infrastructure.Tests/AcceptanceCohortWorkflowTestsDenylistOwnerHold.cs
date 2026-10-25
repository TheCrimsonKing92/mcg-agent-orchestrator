using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using static Mcg.AgentOrchestrator.Infrastructure.Tests.LandingExecutorTestsDenylistOwnerHold;
using static Mcg.AgentOrchestrator.Infrastructure.Tests.LandingExecutorTestsLandingDenylist;

[Collection(TestCollections.LandingGitRunner)]
public sealed class AcceptanceCohortWorkflowTestsDenylistOwnerHold : AcceptanceCohortWorkflowTests
{
    internal const string Watched = "src/Mcg.AgentOrchestrator.App/Orchestration/RemoteGitMirror.cs";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Execute_ProtectedMember_HoldsWithOrWithoutWriterUntilApproved(bool train)
    {
        var repo = CreateAcceptanceCohortRepository();
        try
        {
            using var scenario = CreateScenario(repo, train, Watched);
            var writer = new GoalLifecycleEventWriter(scenario.Workspace.GoalLifecycleEventsDirectory);
            var decisions = CollaborationItemStore.ForDirectory(scenario.Workspace.OrchestratorDirectory);
            foreach (var eventWriter in new GoalLifecycleEventWriter?[] { null, writer })
            {
                var held = scenario.Land(eventWriter);
                Assert.Equal(AcceptanceCohortLandingOutcome.RetryableHold, held.Outcome);
                Assert.Contains(scenario.Goals[0].Id.Value[..8], held.Message, StringComparison.Ordinal);
                Assert.Contains($"rule=git-mutation path={Watched}", held.Message, StringComparison.Ordinal);
                Assert.Equal(scenario.Main, RunGitOutput(repo, "rev-parse", "main").Trim());
                Assert.Equal(1, GitCli.Run(repo, "rev-parse", "--verify", "--quiet",
                    $"refs/heads/{LandingExecutor.IntegrationBranchName}").ExitCode);
                Assert.All(scenario.Goals, goal => AssertNoLanded(scenario.Workspace, goal.Id));
                var item = Assert.Single(decisions.GetAttentionQueueAsync().GetAwaiter().GetResult());
                Assert.Equal(scenario.Goals[0].Id.Value, item.GoalId);
                Assert.Equal(CollaborationItemType.Decision, item.Type);
            }
            Approve(scenario.Workspace, scenario.Goals[0], scenario.CandidateRevisions[0]);
            var landed = scenario.Land(writer);
            Assert.Equal(AcceptanceCohortLandingOutcome.Advanced, landed.Outcome);
            Assert.Equal(scenario.Commit, RunGitOutput(repo, "rev-parse", "main").Trim());
            for (var index = 0; index < scenario.Goals.Length; index++)
            {
                using var line = ReadLanded(scenario.Workspace, scenario.Goals[index].Id);
                Assert.Equal(index == 0 ? "denylist-match-owner-approved" : "green-gate-auto",
                    line.RootElement.GetProperty("admissionRule").GetString());
                if (index == 0) Assert.Equal([$"git-mutation={Watched}"], ReadMatches(line));
                else Assert.Empty(ReadMatches(line));
            }
        }
        finally { DeleteDirectory(repo); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Execute_OrdinaryMembers_LandsWithoutAttention(bool train)
    {
        var repo = CreateAcceptanceCohortRepository();
        try
        {
            using var scenario = CreateScenario(repo, train, "src/First.cs");
            var decisions = CollaborationItemStore.ForDirectory(scenario.Workspace.OrchestratorDirectory);
            Assert.Empty(decisions.GetAttentionQueueAsync().GetAwaiter().GetResult());
            var result = scenario.Land(new GoalLifecycleEventWriter(scenario.Workspace.GoalLifecycleEventsDirectory));
            Assert.Equal(AcceptanceCohortLandingOutcome.Advanced, result.Outcome);
            Assert.Equal(scenario.Commit, RunGitOutput(repo, "rev-parse", "main").Trim());
            foreach (var goal in scenario.Goals)
            {
                using var line = ReadLanded(scenario.Workspace, goal.Id);
                Assert.Equal("green-gate-auto", line.RootElement.GetProperty("admissionRule").GetString());
                Assert.Empty(ReadMatches(line));
            }
            Assert.Empty(decisions.GetAttentionQueueAsync().GetAwaiter().GetResult());
        }
        finally { DeleteDirectory(repo); }
    }

    internal static Scenario CreateScenario(string repo, bool train, string firstPath, bool seedPolicy = false)
    {
        AddAcceptanceManifest(repo);
        if (seedPolicy)
        {
            var path = Path.Combine(repo, firstPath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "public sealed class ConductorAutonomyPolicy { }\n");
            RunGit(repo, "add", firstPath);
            RunGit(repo, "commit", "-m", "Seed policy before incident");
        }
        var kernel = new AgentOrchestratorKernel();
        var goals = new[] { CreateCompletedGoal(kernel, "First member", repo), CreateCompletedGoal(kernel, "Second member", repo) };
        var main = RunGitOutput(repo, "rev-parse", "main").Trim();
        const string ordinary = "src/Second.cs";
        var firstContent = seedPolicy
            ? File.ReadAllText(Path.Combine(repo, firstPath)) + "// processExecutionThrottlingOptOut enabled\n"
            : "first member";
        var candidates = new[]
        {
            CreateCandidate(repo, goals[0].Id.Value, firstPath, firstContent),
            CreateCandidate(repo, goals[1].Id.Value, ordinary, "second member")
        };
        foreach (var (goal, candidate) in goals.Zip(candidates))
        {
            kernel.RecordGoalRefinement(goal.Id, new RefinedSpec(goal.Objective,
                ["Full gate passes"], VerificationClass.TestVerifiable, [], []));
            kernel.MapCriterionEvidenceOwner(goal.Id, 0, 1, CriterionEvidenceOwner.Acceptance,
                "test", CriterionEvidenceScopes.FullAcceptanceGate, expectedCandidateSha: candidate.Revision);
        }
        var workspace = OrchestratorWorkspace.ForDirectory(repo);
        if (train)
        {
            var bindings = new[]
            {
                TrainBind(candidates[0].GoalId, candidates[0].Revision, firstPath, "resource:first"),
                TrainBind(candidates[1].GoalId, candidates[1].Revision, ordinary, "resource:second")
            };
            var integration = GoalWorktrees.CreateMergeTrainWorkspace(repo, main, bindings, TrunkBranchName.Default);
            try
            {
                var identity = MergeTrainIdentity.Create(integration.Members, main, integration.TreeRevision, "manifest-v1");
                var store = new MergeTrainAcceptanceStore(Path.Combine(workspace.OrchestratorDirectory, "merge-train-acceptance.db"));
                var receipt = store.SaveGateReceipt(new MergeTrainReceipt("owner-hold-train", identity,
                    MergeTrainGateOutcome.Passed, DateTimeOffset.UtcNow, 100, [], GateExitCode: 0,
                    GateTestResultPaths: [WritePassingTrx(repo, "owner-hold-train.trx")], ValidForLanding: true));
                return new Scenario(workspace, goals, candidates.Select(candidate => candidate.Revision).ToArray(),
                    main, integration.CommitRevision, writer => LandingExecutor.ExecuteMergeTrain(kernel, goals, workspace,
                        receipt, integration.CommitRevision, store, ConductorAutonomyPolicy.Permissive, eventWriter: writer), integration);
            }
            catch { integration.Dispose(); throw; }
        }
        else
        {
            var bindings = new[]
            {
                Bind(candidates[0].GoalId, candidates[0].Revision, firstPath, "resource:first"),
                Bind(candidates[1].GoalId, candidates[1].Revision, ordinary, "resource:second")
            };
            var integration = GoalWorktrees.CreateAcceptanceCohortWorkspace(repo, main, bindings, TrunkBranchName.Default);
            try
            {
                var identity = AcceptanceCohortIdentity.Create(bindings, main, integration.TreeRevision,
                    GoalAcceptanceVerifier.ComputeEffectiveAcceptancePlanIdentity(integration.Path, [firstPath, ordinary]));
                var store = new CohortAcceptanceStore(Path.Combine(workspace.OrchestratorDirectory, "cohort-acceptance.db"));
                var receipt = store.SaveGateReceipt(new AcceptanceCohortReceipt("owner-hold-cohort", identity,
                    AcceptanceCohortGateOutcome.Passed, DateTimeOffset.UtcNow, 100, [], GateExitCode: 0,
                    GateTestResultPaths: [WritePassingTrx(repo, "owner-hold-cohort.trx")], ValidForLanding: true));
                return new Scenario(workspace, goals, candidates.Select(candidate => candidate.Revision).ToArray(),
                    main, integration.CommitRevision, writer => LandingExecutor.ExecuteCohort(kernel, goals, workspace,
                        receipt, integration.CommitRevision, store, ConductorAutonomyPolicy.Permissive, eventWriter: writer), integration);
            }
            catch { integration.Dispose(); throw; }
        }
    }

    internal sealed record Scenario(
        OrchestratorWorkspace Workspace, Goal[] Goals, string[] CandidateRevisions, string Main, string Commit,
        Func<GoalLifecycleEventWriter?, AcceptanceCohortLandingResult> Land, IDisposable Integration) : IDisposable
    {
        public void Dispose() => Integration.Dispose();
    }
}
