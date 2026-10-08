using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

// Each case owns its repository, worktrees, receipt store and host artifacts; console redirection is stubbed.
public sealed class AcceptanceCohortWorkflowTestsMergeTrainVerdict : AcceptanceCohortWorkflowTests
{
    [Theory(DisplayName = "Failed train gates preserve the deciding recorded receipt in every mode")]
    [InlineData(true, false, true)]
    [InlineData(false, false, true)]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    public void NonPassedTrainPreservesRecordedReceipt(
        bool gateOnly, bool infrastructureFailure, bool publishChildResult)
    {
        var repo = CreateReducedAcceptanceCohortRepository();
        var cleanup = CreateIsolatedCleanupContext(repo);
        try
        {
            AddAcceptanceManifest(repo);
            var kernel = new AgentOrchestratorKernel();
            var goals = new[]
            {
                CreateCompletedGoal(kernel, "First verdict train member", repo),
                CreateCompletedGoal(kernel, "Second verdict train member", repo),
                CreateCompletedGoal(kernel, "Newest verdict train member", repo)
            };
            var paths = new[]
            {
                "tests/Mcg.AgentOrchestrator.Core.Tests/VerdictFirst.cs",
                "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/VerdictSecond.cs",
                "tests/Mcg.AgentOrchestrator.Dashboard.Tests/VerdictNewest.cs"
            };
            for (var index = 0; index < goals.Length; index++)
            {
                var goal = goals[index];
                kernel.RecordGoalRefinement(goal.Id, new RefinedSpec(goal.Objective,
                    ["The full acceptance gate passes"], VerificationClass.TestVerifiable, [], [])
                {
                    AcceptanceGateOwnedAcceptanceCriteria = ["The full acceptance gate passes"]
                });
                _ = CreateWorktreeCandidate(repo, goal.Id, paths[index], $"member {index}");
            }
            var verifier = new SequenceAcceptanceVerifier(infrastructureFailure
                ? [new AcceptanceVerificationResult(false, true, null, "gate unavailable",
                    Checks: [new AcceptanceCheckResult("combined", false, null, "gate unavailable")])]
                : [FailedVerification(repo, "three-red.trx", "three-member train"),
                    FailedVerification(repo, "two-red.trx", "two-member train")]);
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var driver = new ConductorDriver(kernel, workspace, verifier,
                AgentCatalog.Default().Agents, WorkerProfileCatalog.Default(), cleanupHooks: cleanup.Hooks);
            var selection = ProjectTrainSelection(driver, goals);
            var mainBefore = selection.Members[0].MainRevision;
            var originalIdentity = MaterializeIdentity(selection.BindMembers());
            // A binary red drops the newest member, then records a second red for the shorter train.
            var decidingIdentity = infrastructureFailure
                ? originalIdentity : MaterializeIdentity(selection.BindMembers().Take(2).ToArray());
            var expectedOutcome = infrastructureFailure
                ? MergeTrainGateOutcome.InfrastructureFailure : MergeTrainGateOutcome.Failed;
            var store = new MergeTrainAcceptanceStore(
                Path.Combine(workspace.OrchestratorDirectory, "merge-train-acceptance.db"));
            Assert.Null(store.TryReadReceipt(originalIdentity.Value));
            Assert.Null(store.TryReadReceipt(decidingIdentity.Value));

            if (gateOnly && publishChildResult)
            {
                var root = Path.Combine(workspace.OrchestratorDirectory, "verdict-attempt");
                var attempt = new ConductorGroupedGateAttempt("train-verdict", "train",
                    selection.Members.Select(ConductorGroupedGateMember.From).ToArray(),
                    mainBefore, originalIdentity.TrainTreeRevision, originalIdentity.ManifestIdentity,
                    originalIdentity.Value, DateTimeOffset.UnixEpoch, 0, 91001,
                    Path.Combine(root, "attempt.json"), Path.Combine(root, "result.json"),
                    Path.Combine(root, "exit"), Path.Combine(root, "out.log"), Path.Combine(root, "err.log"),
                    repo, ConductorAutonomyPolicy.Permissive.ToJson());
                ConductorGroupedGateAttemptCoordinator.Save(attempt);

                var exitCode = ConductorGroupedGateAttemptHost.Run(attempt.MetadataPath,
                    driver.RunGroupedGateAttemptBody, (_, _) => new NoopRestoration());

                Assert.Equal(0, exitCode);
                Assert.Equal("0", File.ReadAllText(attempt.ExitCodePath));
                var saved = Assert.IsType<MergeTrainReceipt>(store.TryReadReceipt(decidingIdentity.Value));
                Assert.Equal(expectedOutcome, saved.Outcome);
                using var result = JsonDocument.Parse(File.ReadAllText(attempt.ResultPath));
                Assert.Equal("completed", result.RootElement.GetProperty("Status").GetString());
                Assert.Equal(originalIdentity.Value, result.RootElement.GetProperty("IdentityValue").GetString());
                if (infrastructureFailure)
                {
                    Assert.False(result.RootElement.TryGetProperty("Verdict", out _));
                    Assert.False(result.RootElement.TryGetProperty("ReceiptId", out _));
                }
                else
                {
                    Assert.NotEqual(originalIdentity.Value, saved.Identity.Value);
                    Assert.Equal("failed", result.RootElement.GetProperty("Verdict").GetString());
                    Assert.Equal(saved.ReceiptId, result.RootElement.GetProperty("ReceiptId").GetString());
                }
            }
            else
            {
                var result = driver.RunMergeTrain(selection, goals, ConductorAutonomyPolicy.Permissive,
                    gateOnly: gateOnly);

                Assert.Null(result.Receipt);
                var recorded = Assert.IsType<MergeTrainReceipt>(result.RecordedReceipt);
                Assert.Equal(decidingIdentity.Value, recorded.Identity.Value);
                Assert.Equal(expectedOutcome, recorded.Outcome);
                Assert.Equal(store.TryReadReceipt(decidingIdentity.Value)!.ReceiptId, recorded.ReceiptId);
                Assert.Empty(result.MemberResults);
                Assert.Equal("outcome=Failed attempts=2 fallback=ordinary", result.Detail);
                Assert.Equal(goals[2].Id, Assert.Single(result.Ejections).GoalId);
                Assert.Equal(expectedOutcome,
                    Assert.IsType<MergeTrainReceipt>(store.TryReadReceipt(decidingIdentity.Value)).Outcome);
            }
            Assert.Equal(infrastructureFailure ? 1 : 2, verifier.RunCount);
            Assert.Equal(infrastructureFailure ? new[] { 3 } : new[] { 3, 2 },
                verifier.ChangedFiles.Select(files => files.Count).ToArray());
            Assert.Equal(expectedOutcome,
                Assert.IsType<MergeTrainReceipt>(store.TryReadReceipt(originalIdentity.Value)).Outcome);
            Assert.Equal(mainBefore, RunGitOutput(repo, "rev-parse", "main").Trim());
            Assert.All(paths, path => Assert.False(File.Exists(Path.Combine(repo, path))));
            AssertNoMergeTrainWorkspaces(repo);

            MergeTrainIdentity MaterializeIdentity(IReadOnlyList<MergeTrainMemberBinding> bindings)
            {
                using var integration = GoalWorktrees.CreateMergeTrainWorkspace(repo, mainBefore, bindings, cleanup.Hooks);
                var manifest = ((IGoalAcceptanceVerifier)verifier).ComputeEffectivePlanIdentity(integration.Path,
                    integration.Members.SelectMany(member => member.LandingPaths)
                        .Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
                return MergeTrainIdentity.Create(integration.Members, mainBefore, integration.TreeRevision, manifest);
            }
        }
        finally { DeleteDirectory(repo); }
    }

    private sealed class NoopRestoration : IDisposable
    {
        public void Dispose() { }
    }
}
