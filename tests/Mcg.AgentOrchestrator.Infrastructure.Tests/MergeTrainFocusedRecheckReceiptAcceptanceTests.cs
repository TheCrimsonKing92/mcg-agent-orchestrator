using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

// Parallel-safe: receipt tampering and landing operate only on a scenario-owned repository.
public sealed class MergeTrainFocusedRecheckReceiptAcceptanceTests : AcceptanceCohortWorkflowTests
{
    [Fact]
    public void StoredComposite_CompleteProof_IsAcceptedByLanding()
    {
        MergeTrainFocusedRecheckTests.WithTrain("green", true, train =>
        {
            var receipt = train.Result.Receipt!;
            var readOnly = MergeTrainAcceptanceStore.OpenReadOnly(DatabasePath(train.Repo));
            var reloaded = Assert.IsType<MergeTrainReceipt>(readOnly.TryReadReceipt(receipt.Identity.Value));
            Assert.Equal(receipt.ReceiptId, reloaded.ReceiptId);
            Assert.Equal(receipt.GateAttemptId, reloaded.GateAttemptId);
            Assert.Equal(MergeTrainFocusedRecheckTests.ExpectedSelection, reloaded.Recheck!.Selection);
            Assert.True(MergeTrainFocusedRecheckReceiptAcceptance.Accepts(reloaded, readOnly));
            var result = LandingExecutor.ExecuteMergeTrain(train.Kernel, train.Goals.Take(2).ToArray(),
                OrchestratorWorkspace.ForDirectory(train.Repo), reloaded, train.TrainCommit!, train.Store,
                ConductorAutonomyPolicy.Permissive);
            Assert.Equal(AcceptanceCohortLandingOutcome.Advanced, result.Outcome);
            Assert.All(train.Goals.Take(2), goal => Assert.Equal(GoalStatus.Completed, goal.Status));
            Assert.Equal(reloaded.Identity.TrainTreeRevision, RunGitOutput(train.Repo, "rev-parse", "main^{tree}").Trim());
            Assert.Empty(train.Store.ReadFinalizedLandingTrainIds());
            // The driver acknowledges landing effects after the executor advances main.
            train.Store.CompleteLandingEffects(reloaded.Identity.Value, reloaded.ReceiptId);
            Assert.Equal(reloaded.Identity.Value, Assert.Single(train.Store.ReadFinalizedLandingTrainIds()));
            Assert.All(train.Goals.Take(2), goal =>
                Assert.Equal(reloaded.ReceiptId, Assert.Single(train.Store.ReadFinalizedLandingsForGoal(goal.Id)).ReceiptId));
        });
    }

    [Theory]
    [InlineData("red-reference")]
    [InlineData("red-absent")]
    [InlineData("red-not-failed")]
    [InlineData("red-wrong-receipt")]
    [InlineData("red-wrong-attempt")]
    [InlineData("red-check-evidence-missing")]
    [InlineData("red-check-evidence-incomplete")]
    [InlineData("red-check-unexplained")]
    [InlineData("red-check-name")]
    [InlineData("red-check-identity")]
    [InlineData("recheck-reference")]
    [InlineData("recheck-wrong-attempt")]
    [InlineData("selection-missing-failure")]
    [InlineData("selection-missing-class")]
    [InlineData("execution-incomplete")]
    [InlineData("execution-not-run")]
    [InlineData("composition-order")]
    [InlineData("composition-member")]
    [InlineData("composition-candidate")]
    [InlineData("composition-main")]
    [InlineData("recheck-main")]
    [InlineData("recheck-tree")]
    [InlineData("dependency-abandoned")]
    [InlineData("disjoint-absent")]
    [InlineData("disjoint-failed")]
    [InlineData("paths-overlap")]
    [InlineData("unattributed")]
    public void IncompleteOrMismatchedProof_IsRefusedBeforePreparingLanding(string defect)
    {
        MergeTrainFocusedRecheckTests.WithTrain("green", true, train =>
        {
            var receipt = train.Result.Receipt!;
            var proof = receipt.Recheck!;
            var identity = receipt.Identity;
            var members = identity.Members.ToArray();
            var store = train.Store;
            var database = DatabasePath(train.Repo);
            switch (defect)
            {
                case "red-reference": proof = proof with { RedReceiptId = "" }; break;
                case "red-absent":
                case "red-not-failed":
                    database = Path.Combine(train.Repo, ".orchestrator", "refusal.db");
                    store = new MergeTrainAcceptanceStore(database);
                    if (defect == "red-not-failed")
                        store.SaveGateReceipt(train.Red with { Outcome = MergeTrainGateOutcome.InfrastructureFailure });
                    break;
                case "red-wrong-receipt": proof = proof with { RedReceiptId = "another-red-receipt" }; break;
                case "red-wrong-attempt": proof = proof with { TrainAttemptId = "another-red-attempt" }; break;
                case "red-check-evidence-missing":
                case "red-check-evidence-incomplete":
                case "red-check-unexplained":
                case "red-check-name":
                case "red-check-identity":
                    database = Path.Combine(train.Repo, ".orchestrator", "refusal.db");
                    store = new MergeTrainAcceptanceStore(database);
                    var recorded = Assert.Single(train.Red.FailedCheckEvidence!);
                    IReadOnlyList<MergeTrainFailedCheckEvidence>? coverage = defect switch
                    {
                        "red-check-evidence-missing" => null,
                        "red-check-evidence-incomplete" => [],
                        "red-check-unexplained" => [recorded with { ExplainedByFatalTrx = false }],
                        "red-check-name" => [recorded with { CheckName = "another-check" }],
                        _ => [recorded with { FatalTestIdentities = ["Ns.UnselectedTests.Fails"] }]
                    };
                    var storedRed = store.SaveGateReceipt(train.Red with { FailedCheckEvidence = coverage });
                    Assert.True(AcceptanceCohortGateEvidence.HasContentBoundEvidence(
                        storedRed.GateTestResultPaths, storedRed.GateEvidenceArtifacts));
                    break;
                case "recheck-reference": proof = proof with { RecheckAttemptId = "" }; break;
                case "recheck-wrong-attempt": proof = proof with { RecheckAttemptId = "another-recheck" }; break;
                case "selection-missing-failure":
                    proof = proof with { Selection = proof.Selection.Skip(1).ToArray(),
                        ExecutedSelectors = proof.Selection.Skip(1).ToArray() };
                    break;
                case "selection-missing-class":
                    proof = proof with { Selection = proof.Selection.Take(2).ToArray(),
                        ExecutedSelectors = proof.Selection.Take(2).ToArray() };
                    break;
                case "execution-incomplete": proof = proof with { ExecutedSelectors = proof.Selection.Take(2).ToArray() }; break;
                case "execution-not-run": proof = proof with { NotRunSelectionCount = 1 }; break;
                case "composition-order": Array.Reverse(members); break;
                case "composition-member": members[1] = train.Red.Identity.Members[2]; break;
                case "composition-candidate":
                    members[0] = members[0] with { CandidateRevision = train.Red.Identity.Members[2].CandidateRevision };
                    break;
                case "composition-main":
                    identity = MergeTrainIdentity.Create(members, train.Red.Identity.Members[2].CandidateRevision,
                        identity.TrainTreeRevision, identity.ManifestIdentity);
                    proof = proof with { RecheckMainRevision = identity.ObservedMainRevision };
                    break;
                case "recheck-main": proof = proof with { RecheckMainRevision = new string('f', 40) }; break;
                case "recheck-tree": proof = proof with { RecheckTreeRevision = new string('f', 40) }; break;
                case "dependency-abandoned": proof = proof with { DependencySelectionResolved = false }; break;
                case "disjoint-absent": proof = proof with { LandingPathsDisjoint = null }; break;
                case "disjoint-failed": proof = proof with { LandingPathsDisjoint = false }; break;
                case "paths-overlap":
                    members[0] = members[0] with { LandingPaths = train.Red.Identity.Members[2].LandingPaths };
                    break;
                case "unattributed": proof = proof with { AttributionSource = "no-attribution" }; break;
                default: throw new ArgumentOutOfRangeException(nameof(defect));
            }
            if (defect is "composition-order" or "composition-member" or "composition-candidate" or "paths-overlap")
                identity = MergeTrainIdentity.Create(members, identity.ObservedMainRevision,
                    identity.TrainTreeRevision, identity.ManifestIdentity);
            // Re-sign the changed proof so refusal cannot pass merely on a stale digest.
            var invalid = receipt with { Identity = identity, Recheck = proof,
                ReceiptId = MergeTrainFocusedRecheckReceipt.ReceiptId(identity, proof) };
            Assert.True(invalid.HasAuthoritativeLandingEvidence);
            Assert.False(MergeTrainFocusedRecheckReceiptAcceptance.Accepts(invalid, store));
            AssertNoPreparedLanding(database);
            var main = RunGitOutput(train.Repo, "rev-parse", "main").Trim();
            var error = Assert.Throws<InvalidOperationException>(() => LandingExecutor.ExecuteMergeTrain(
                train.Kernel, train.Goals.Take(2).ToArray(), OrchestratorWorkspace.ForDirectory(train.Repo),
                invalid, train.TrainCommit!, store, ConductorAutonomyPolicy.Permissive));
            Assert.Contains("invalid composite evidence", error.Message);
            Assert.Equal(main, RunGitOutput(train.Repo, "rev-parse", "main").Trim());
            Assert.All(train.Goals.Take(2), goal => Assert.Equal(GoalStatus.Verified, goal.Status));
            AssertNoPreparedLanding(database);
        });
    }

    [Fact]
    public void OrdinaryGateReceipt_KeepsAuthoritativeEvidenceContract()
    {
        MergeTrainFocusedRecheckTests.WithTrain("green", true, train =>
        {
            var ordinary = train.Result.Receipt! with { Recheck = null, ReceiptId = "ordinary-gate-receipt" };
            Assert.False(MergeTrainFocusedRecheckReceiptAcceptance.IsComposite(ordinary));
            Assert.True(ordinary.HasAuthoritativeLandingEvidence);
            Assert.False((ordinary with { GateExitCode = 1 }).HasAuthoritativeLandingEvidence);
            Assert.False((ordinary with { ValidForLanding = false }).HasAuthoritativeLandingEvidence);
            Assert.False((ordinary with { GateEvidenceArtifacts = [] }).HasAuthoritativeLandingEvidence);
        });
    }

    private static string DatabasePath(string repo) => Path.Combine(repo, ".orchestrator", "merge-train-acceptance.db");

    private static void AssertNoPreparedLanding(string database)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = database, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM merge_train_landings;";
        Assert.Equal(0L, (long)command.ExecuteScalar()!);
    }
}
