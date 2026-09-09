using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

public sealed class AcceptanceCohortWorkflowTestsLandingAndReceipts : AcceptanceCohortWorkflowTests
{

    [Fact]
    public void Store_PersistsReceiptAndTwoCoverageRowsAtomically_AndFairnessSurvivesRestart()
    {
        var root = Path.Combine(Path.GetTempPath(), $"cohort-store-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var databasePath = Path.Combine(root, "cohort.db");
            var first = Bind(new GoalId("11111111111111111111111111111111"), new string('b', 40), "src/A.cs", "resource:a");
            var second = Bind(new GoalId("22222222222222222222222222222222"), new string('c', 40), "tests/B.cs", "resource:b");
            var identity = AcceptanceCohortIdentity.Create(
                [first, second],
                new string('a', 40),
                new string('d', 40),
                "manifest-v1");
            var receipt = new AcceptanceCohortReceipt(
                "receipt-1",
                identity,
                AcceptanceCohortGateOutcome.Passed,
                DateTimeOffset.Parse("2026-08-11T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture),
                123,
                [],
                ValidForLanding: true,
                GateExitCode: 0,
                GateTestResultPaths: [WritePassingTrx(root, "cohort.trx")]);
            var store = new CohortAcceptanceStore(databasePath);

            var persistedReceipt = store.SaveGateReceipt(receipt);
            Assert.True(persistedReceipt.HasAuthoritativeLandingEvidence);
            Assert.NotEqual(receipt.GateTestResultPaths.Single(), persistedReceipt.GateTestResultPaths.Single());
            Assert.All(persistedReceipt.GateEvidenceArtifacts, artifact =>
            {
                Assert.True(File.Exists(artifact.Path));
                Assert.Equal(64, artifact.Sha256.Length);
            });
            File.WriteAllText(receipt.GateTestResultPaths.Single(), "later stable-slot output");
            Assert.True(persistedReceipt.HasAuthoritativeLandingEvidence);
            var reloadedReceipt = store.SaveGateReceipt(receipt);
            Assert.Equal(receipt.ReceiptId, reloadedReceipt.ReceiptId);
            Assert.Equal(receipt.Identity, reloadedReceipt.Identity);
            Assert.Equal(first, reloadedReceipt.Identity.Members[0]);
            Assert.Equal(second, reloadedReceipt.Identity.Members[1]);
            Assert.Equal(receipt.Outcome, reloadedReceipt.Outcome);
            Assert.Equal(receipt.FailedChecks, reloadedReceipt.FailedChecks);
            Assert.Equal(receipt.ValidForLanding, reloadedReceipt.ValidForLanding);
            Assert.Equal(0, reloadedReceipt.GateExitCode);
            Assert.Equal(persistedReceipt.GateTestResultPaths.ToArray(), reloadedReceipt.GateTestResultPaths.ToArray());
            Assert.Equal(persistedReceipt.GateEvidenceArtifacts.ToArray(), reloadedReceipt.GateEvidenceArtifacts.ToArray());
            store.PrepareLanding(persistedReceipt, new string('e', 40));
            var coverage = store.FinalizeLanding(identity.Value, receipt.ReceiptId);
            store.CompleteLandingEffects(identity.Value, receipt.ReceiptId);
            store.RecordOvertake(first.GoalId);
            store.SuppressPair("pair-1", identity.Value);

            Assert.Equal(2, coverage.Count);
            Assert.All(coverage, member =>
            {
                Assert.True(member.Landed);
                Assert.Equal(receipt.ReceiptId, member.ReceiptId);
            });
            using (var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT (SELECT COUNT(*) FROM cohort_receipts), (SELECT COUNT(*) FROM cohort_members);";
                using var reader = command.ExecuteReader();
                Assert.True(reader.Read());
                Assert.Equal(1, reader.GetInt32(0));
                Assert.Equal(2, reader.GetInt32(1));
            }

            var reopened = new CohortAcceptanceStore(databasePath);
            Assert.Equal(1, reopened.ReadOvertakeCount(first.GoalId));
            Assert.Contains("pair-1", reopened.ReadSuppressedPairs());
            Assert.Equal(identity.Value, reopened.TryReadReceipt(identity.Value)?.Identity.Value);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public void Store_InfrastructureCauseRoundTripsLegacyUnknown()
    {
        var root = Path.Combine(Path.GetTempPath(), $"cohort-store-cause-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var databasePath = Path.Combine(root, "cohort.db");
            var members = new[]
            {
                Bind(new GoalId("11111111111111111111111111111111"), new string('b', 40), "src/A.cs", "resource:a"),
                Bind(new GoalId("22222222222222222222222222222222"), new string('c', 40), "tests/B.cs", "resource:b")
            };
            var legacyIdentity = AcceptanceCohortIdentity.Create(
                members,
                new string('a', 40),
                new string('d', 40),
                "manifest-v1");
            using (var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False"))
            {
                connection.Open();
                using (var create = connection.CreateCommand())
                {
                    create.CommandText = """
                        CREATE TABLE cohort_receipts(
                            cohort_id TEXT PRIMARY KEY,
                            receipt_id TEXT NOT NULL UNIQUE,
                            main_revision TEXT NOT NULL,
                            combined_tree_revision TEXT NOT NULL,
                            manifest_identity TEXT NOT NULL,
                            outcome TEXT NOT NULL,
                            attribution TEXT NOT NULL,
                            valid_for_landing INTEGER NOT NULL,
                            completed_at TEXT NOT NULL,
                            gate_elapsed_ms INTEGER NOT NULL,
                            failed_checks_json TEXT NOT NULL,
                            gate_exit_code INTEGER NULL,
                            gate_test_result_paths_json TEXT NOT NULL DEFAULT '[]',
                            gate_evidence_artifacts_json TEXT NOT NULL DEFAULT '[]');
                        CREATE TABLE cohort_members(
                            cohort_id TEXT NOT NULL,
                            member_ordinal INTEGER NOT NULL,
                            goal_id TEXT NOT NULL,
                            branch_revision TEXT NOT NULL,
                            candidate_revision TEXT NOT NULL,
                            landing_paths_json TEXT NOT NULL,
                            resource_keys_json TEXT NOT NULL,
                            risk_tier TEXT NOT NULL,
                            promotion_disposition TEXT NOT NULL,
                            merge_status TEXT NOT NULL,
                            merge_reason TEXT NOT NULL,
                            landed INTEGER NOT NULL DEFAULT 0,
                            PRIMARY KEY(cohort_id, member_ordinal));
                        """;
                    create.ExecuteNonQuery();
                }
                using (var receipt = connection.CreateCommand())
                {
                    receipt.CommandText = """
                        INSERT INTO cohort_receipts(
                            cohort_id, receipt_id, main_revision, combined_tree_revision, manifest_identity,
                            outcome, attribution, valid_for_landing, completed_at, gate_elapsed_ms,
                            failed_checks_json, gate_exit_code, gate_test_result_paths_json, gate_evidence_artifacts_json)
                        VALUES ($cohort, 'legacy-receipt', $main, $tree, 'manifest-v1', 'InfrastructureFailure',
                            'NotApplicable', 0, $completed, 50, '["legacy infrastructure check"]', 2, '[]', '[]');
                        """;
                    receipt.Parameters.AddWithValue("$cohort", legacyIdentity.Value);
                    receipt.Parameters.AddWithValue("$main", legacyIdentity.ObservedMainRevision);
                    receipt.Parameters.AddWithValue("$tree", legacyIdentity.CombinedTreeRevision);
                    receipt.Parameters.AddWithValue("$completed", DateTimeOffset.UtcNow.ToString("O"));
                    receipt.ExecuteNonQuery();
                }
                for (var index = 0; index < members.Length; index++)
                {
                    var member = members[index];
                    using var insertMember = connection.CreateCommand();
                    insertMember.CommandText = """
                        INSERT INTO cohort_members(
                            cohort_id, member_ordinal, goal_id, branch_revision, candidate_revision,
                            landing_paths_json, resource_keys_json, risk_tier, promotion_disposition,
                            merge_status, merge_reason, landed)
                        VALUES ($cohort, $ordinal, $goal, $branch, $candidate, $paths, $resources,
                            $risk, $promotion, $mergeStatus, $mergeReason, 0);
                        """;
                    insertMember.Parameters.AddWithValue("$cohort", legacyIdentity.Value);
                    insertMember.Parameters.AddWithValue("$ordinal", index);
                    insertMember.Parameters.AddWithValue("$goal", member.GoalId.Value);
                    insertMember.Parameters.AddWithValue("$branch", member.BranchRevision);
                    insertMember.Parameters.AddWithValue("$candidate", member.CandidateRevision);
                    insertMember.Parameters.AddWithValue("$paths", JsonSerializer.Serialize(member.LandingPaths));
                    insertMember.Parameters.AddWithValue("$resources", JsonSerializer.Serialize(member.ResourceKeys));
                    insertMember.Parameters.AddWithValue("$risk", member.ChangeRiskTier.ToString());
                    insertMember.Parameters.AddWithValue("$promotion", member.AutoPromotionDisposition.ToString());
                    insertMember.Parameters.AddWithValue("$mergeStatus", member.MergeStatus);
                    insertMember.Parameters.AddWithValue("$mergeReason", member.MergeReason);
                    insertMember.ExecuteNonQuery();
                }
            }

            var store = new CohortAcceptanceStore(databasePath);
            var legacy = store.TryReadReceipt(legacyIdentity.Value);
            Assert.NotNull(legacy);
            Assert.Equal(AcceptanceCohortInfrastructureReasonCodes.LegacyUnknown, legacy.InfrastructureReasonCode);
            Assert.Null(legacy.InfrastructureDetail);

            var currentIdentity = AcceptanceCohortIdentity.Create(
                members,
                legacyIdentity.ObservedMainRevision,
                new string('e', 40),
                legacyIdentity.ManifestIdentity);
            _ = store.SaveGateReceipt(new AcceptanceCohortReceipt(
                "current-receipt",
                currentIdentity,
                AcceptanceCohortGateOutcome.InfrastructureFailure,
                DateTimeOffset.UtcNow,
                75,
                ["infrastructure tests"],
                GateExitCode: 2,
                GateTestResultPaths: [],
                InfrastructureReasonCode: AcceptanceCohortInfrastructureReasonCodes.TrxEvidenceIncoherent,
                InfrastructureDetail: "The TRX result set was incomplete."));

            var current = new CohortAcceptanceStore(databasePath).TryReadReceipt(currentIdentity.Value);
            Assert.NotNull(current);
            Assert.Equal(AcceptanceCohortInfrastructureReasonCodes.TrxEvidenceIncoherent, current.InfrastructureReasonCode);
            Assert.Equal("The TRX result set was incomplete.", current.InfrastructureDetail);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public void Store_RejectsPassingReceiptWithoutAuthoritativeEvidence()
    {
        var root = Path.Combine(Path.GetTempPath(), $"cohort-store-guard-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var identity = AcceptanceCohortIdentity.Create(
                [
                    Bind(new GoalId("11111111111111111111111111111111"), new string('b', 40), "src/A.cs", "resource:a"),
                    Bind(new GoalId("22222222222222222222222222222222"), new string('c', 40), "tests/B.cs", "resource:b")
                ],
                new string('a', 40),
                new string('d', 40),
                "manifest-v1");
            var store = new CohortAcceptanceStore(Path.Combine(root, "cohort.db"));
            var validTrx = WritePassingTrx(root, "valid.trx");
            var missingFile = Path.GetFullPath(Path.Combine(root, "missing.trx"));
            var malformedTrx = Path.GetFullPath(Path.Combine(root, "malformed.trx"));
            var incoherentTrx = Path.GetFullPath(Path.Combine(root, "incoherent.trx"));
            File.WriteAllText(malformedTrx, "not xml");
            File.WriteAllText(incoherentTrx, """
                <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
                  <Results><UnitTestResult testId="1" testName="Passes" outcome="Passed" /></Results>
                  <ResultSummary outcome="Completed"><Counters total="2" executed="2" passed="2" failed="0" /></ResultSummary>
                </TestRun>
                """);

            var missingExit = new AcceptanceCohortReceipt(
                "missing-exit", identity, AcceptanceCohortGateOutcome.Passed,
                DateTimeOffset.UtcNow, 1, [], null, [validTrx],
                ValidForLanding: true);
            var missingTrx = missingExit with
            {
                ReceiptId = "missing-trx",
                GateExitCode = 0,
                GateTestResultPaths = []
            };
            var unnormalizedTrx = missingTrx with
            {
                ReceiptId = "unnormalized-trx",
                GateTestResultPaths = ["relative.trx"]
            };
            var absentTrx = missingExit with
            {
                ReceiptId = "absent-trx",
                GateExitCode = 0,
                GateTestResultPaths = [missingFile]
            };
            var malformed = absentTrx with
            {
                ReceiptId = "malformed-trx",
                GateTestResultPaths = [malformedTrx]
            };
            var incoherent = absentTrx with
            {
                ReceiptId = "incoherent-trx",
                GateTestResultPaths = [incoherentTrx]
            };

            Assert.Throws<ArgumentException>(() => store.SaveGateReceipt(missingExit));
            Assert.Throws<ArgumentException>(() => store.SaveGateReceipt(missingTrx));
            Assert.Throws<ArgumentException>(() => store.SaveGateReceipt(unnormalizedTrx));
            Assert.Throws<ArgumentException>(() => store.SaveGateReceipt(absentTrx));
            Assert.Throws<ArgumentException>(() => store.SaveGateReceipt(malformed));
            Assert.Throws<ArgumentException>(() => store.SaveGateReceipt(incoherent));
            Assert.Null(store.TryReadReceipt(identity.Value));
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public void ExactTestedCombinedCommit_LandsOnce_AndBothGoalsShareReceiptCoverage()
    {
        var repo = CreateAcceptanceCohortRepository();
        try
        {
            Directory.CreateDirectory(Path.Combine(repo, "config"));
            File.WriteAllText(Path.Combine(repo, "config", "acceptance-manifest.json"), "{}");
            RunGit(repo, "add", "config/acceptance-manifest.json");
            RunGit(repo, "commit", "-m", "Add manifest");
            var kernel = new AgentOrchestratorKernel();
            var firstGoal = CreateCompletedGoal(kernel, "First cohort member", repo);
            var secondGoal = CreateCompletedGoal(kernel, "Second cohort member", repo);
            var main = RunGitOutput(repo, "rev-parse", "main").Trim();
            var first = CreateCandidate(repo, firstGoal.Id.Value, "src/First.cs", "first");
            var second = CreateCandidate(repo, secondGoal.Id.Value, "tests/Second.cs", "second");
            var bindings = new[]
            {
                Bind(first.GoalId, first.Revision, "src/First.cs", "resource:first"),
                Bind(second.GoalId, second.Revision, "tests/Second.cs", "resource:second")
            };
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var store = new CohortAcceptanceStore(Path.Combine(workspace.OrchestratorDirectory, "cohort-acceptance.db"));

            using var integration = GoalWorktrees.CreateAcceptanceCohortWorkspace(repo, main, bindings);
            var identity = AcceptanceCohortIdentity.Create(
                bindings,
                main,
                integration.TreeRevision,
                GoalAcceptanceVerifier.ComputeEffectiveAcceptancePlanIdentity(
                    integration.Path,
                    bindings.SelectMany(member => member.LandingPaths).ToArray()));
            var receipt = store.SaveGateReceipt(new AcceptanceCohortReceipt(
                "receipt-shared",
                identity,
                AcceptanceCohortGateOutcome.Passed,
                DateTimeOffset.UtcNow,
                100,
                [],
                GateExitCode: 0,
                GateTestResultPaths: [WritePassingTrx(repo, "receipt-shared.trx")],
                ValidForLanding: true));

            var result = LandingExecutor.ExecuteCohort(
                kernel,
                [firstGoal, secondGoal],
                workspace,
                receipt,
                integration.CommitRevision,
                store,
                ConductorAutonomyPolicy.Conservative);

            Assert.True(result.MainAdvanced);
            Assert.Equal(integration.CommitRevision, RunGitOutput(repo, "rev-parse", "main").Trim());
            Assert.Equal(integration.TreeRevision, RunGitOutput(repo, "rev-parse", "main^{tree}").Trim());
            Assert.Equal(2, Assert.IsAssignableFrom<IReadOnlyList<AcceptanceCohortCoverage>>(result.Coverage).Count);
            Assert.All(result.Coverage!, coverage =>
            {
                Assert.True(coverage.Landed);
                Assert.Equal(receipt.ReceiptId, coverage.ReceiptId);
            });
            Assert.Equal(first.Revision, RunGitOutput(repo, "rev-parse", $"refs/heads/{GoalWorktrees.BranchName(first.GoalId)}").Trim());
            Assert.Equal(second.Revision, RunGitOutput(repo, "rev-parse", $"refs/heads/{GoalWorktrees.BranchName(second.GoalId)}").Trim());
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Fact]
    public void PostGateMainMovement_LandsNeitherMember_AndCreatesNoCoverage()
    {
        var repo = CreateAcceptanceCohortRepository();
        try
        {
            Directory.CreateDirectory(Path.Combine(repo, "config"));
            File.WriteAllText(Path.Combine(repo, "config", "acceptance-manifest.json"), "{}");
            RunGit(repo, "add", "config/acceptance-manifest.json");
            RunGit(repo, "commit", "-m", "Add manifest");
            var kernel = new AgentOrchestratorKernel();
            var firstGoal = CreateCompletedGoal(kernel, "First invalidated member", repo);
            var secondGoal = CreateCompletedGoal(kernel, "Second invalidated member", repo);
            var boundMain = RunGitOutput(repo, "rev-parse", "main").Trim();
            var first = CreateCandidate(repo, firstGoal.Id.Value, "src/First.cs", "first");
            var second = CreateCandidate(repo, secondGoal.Id.Value, "tests/Second.cs", "second");
            var bindings = new[]
            {
                Bind(first.GoalId, first.Revision, "src/First.cs", "resource:first"),
                Bind(second.GoalId, second.Revision, "tests/Second.cs", "resource:second")
            };
            var orchestratorWorkspace = OrchestratorWorkspace.ForDirectory(repo);
            var databasePath = Path.Combine(orchestratorWorkspace.OrchestratorDirectory, "cohort-acceptance.db");
            var store = new CohortAcceptanceStore(databasePath);
            using var integration = GoalWorktrees.CreateAcceptanceCohortWorkspace(repo, boundMain, bindings);
            var identity = AcceptanceCohortIdentity.Create(
                bindings,
                boundMain,
                integration.TreeRevision,
                GoalAcceptanceVerifier.ComputeEffectiveAcceptancePlanIdentity(
                    integration.Path,
                    bindings.SelectMany(member => member.LandingPaths).ToArray()));
            var receipt = store.SaveGateReceipt(new AcceptanceCohortReceipt(
                "receipt-invalidated",
                identity,
                AcceptanceCohortGateOutcome.Passed,
                DateTimeOffset.UtcNow,
                100,
                [],
                GateExitCode: 0,
                GateTestResultPaths: [WritePassingTrx(repo, "receipt-invalidated.trx")],
                ValidForLanding: true));

            File.WriteAllText(Path.Combine(repo, "main-moved.txt"), "new main");
            RunGit(repo, "add", "main-moved.txt");
            RunGit(repo, "commit", "-m", "Move main after gate");

            var result = LandingExecutor.ExecuteCohort(
                kernel,
                [firstGoal, secondGoal],
                orchestratorWorkspace,
                receipt,
                integration.CommitRevision,
                store,
                ConductorAutonomyPolicy.Conservative);

            Assert.False(result.MainAdvanced);
            Assert.Contains("main changed", result.Message, StringComparison.OrdinalIgnoreCase);
            using var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM cohort_members WHERE landed=1;";
            Assert.Equal(0, Convert.ToInt32(command.ExecuteScalar()));
            Assert.Equal(first.Revision, RunGitOutput(repo, "rev-parse", $"refs/heads/{GoalWorktrees.BranchName(first.GoalId)}").Trim());
            Assert.Equal(second.Revision, RunGitOutput(repo, "rev-parse", $"refs/heads/{GoalWorktrees.BranchName(second.GoalId)}").Trim());
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Fact]
    public void MainCasFailure_RestoresIntegrationRef_AndLandsNeitherMember()
    {
        var repo = CreateAcceptanceCohortRepository();
        var previousGitRunner = LandingExecutor.GitRunner;
        try
        {
            AddAcceptanceManifest(repo);
            var kernel = new AgentOrchestratorKernel();
            var firstGoal = CreateCompletedGoal(kernel, "First CAS member", repo);
            var secondGoal = CreateCompletedGoal(kernel, "Second CAS member", repo);
            var main = RunGitOutput(repo, "rev-parse", "main").Trim();
            var first = CreateCandidate(repo, firstGoal.Id.Value, "src/First.cs", "first");
            var second = CreateCandidate(repo, secondGoal.Id.Value, "tests/Second.cs", "second");
            var bindings = new[]
            {
                Bind(first.GoalId, first.Revision, "src/First.cs", "resource:first"),
                Bind(second.GoalId, second.Revision, "tests/Second.cs", "resource:second")
            };
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var store = new CohortAcceptanceStore(Path.Combine(workspace.OrchestratorDirectory, "cohort-acceptance.db"));
            RunGit(repo, "branch", LandingExecutor.IntegrationBranchName, main);
            var integrationBefore = RunGitOutput(
                repo,
                "rev-parse",
                $"refs/heads/{LandingExecutor.IntegrationBranchName}").Trim();
            using var integration = GoalWorktrees.CreateAcceptanceCohortWorkspace(repo, main, bindings);
            var identity = AcceptanceCohortIdentity.Create(
                bindings,
                main,
                integration.TreeRevision,
                GoalAcceptanceVerifier.ComputeEffectiveAcceptancePlanIdentity(
                    integration.Path,
                    bindings.SelectMany(member => member.LandingPaths).ToArray()));
            var receipt = store.SaveGateReceipt(new AcceptanceCohortReceipt(
                "receipt-cas", identity, AcceptanceCohortGateOutcome.Passed, DateTimeOffset.UtcNow,
                100, [], 0, [WritePassingTrx(repo, "receipt-cas.trx")], ValidForLanding: true));
            var moved = false;
            LandingExecutor.GitRunner = (workingDirectory, args) =>
            {
                var isOldMerge = args.SequenceEqual(["merge", "--ff-only", integration.CommitRevision]);
                var isMainCas = args.Length == 4 && args[0] == "update-ref" && args[1] == "refs/heads/main";
                if (!moved && (isOldMerge || isMainCas))
                {
                    moved = true;
                    var movement = GitCli.Run(workingDirectory, "reset", "--hard", first.Revision);
                    Assert.Equal(0, movement.ExitCode);
                }
                return GitCli.Run(workingDirectory, args);
            };

            var result = LandingExecutor.ExecuteCohort(
                kernel, [firstGoal, secondGoal], workspace, receipt, integration.CommitRevision, store,
                ConductorAutonomyPolicy.Conservative);

            Assert.True(moved);
            Assert.Equal(AcceptanceCohortLandingOutcome.StateInvalidated, result.Outcome);
            Assert.False(result.MainAdvanced);
            Assert.Equal(first.Revision, RunGitOutput(repo, "rev-parse", "main").Trim());
            Assert.Equal(
                integrationBefore,
                RunGitOutput(repo, "rev-parse", $"refs/heads/{LandingExecutor.IntegrationBranchName}").Trim());
            using var connection = new SqliteConnection($"Data Source={Path.Combine(workspace.OrchestratorDirectory, "cohort-acceptance.db")};Pooling=False");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM cohort_members WHERE landed=1;";
            Assert.Equal(0, Convert.ToInt32(command.ExecuteScalar()));
        }
        finally
        {
            LandingExecutor.GitRunner = previousGitRunner;
            DeleteDirectory(repo);
        }
    }

    [Fact]
    public void PreparedLanding_RecoversCoverageOnlyAfterExactCommitIsReachableFromMain()
    {
        var repo = CreateAcceptanceCohortRepository();
        try
        {
            var firstGoal = new GoalId("11111111111111111111111111111111");
            var secondGoal = new GoalId("22222222222222222222222222222222");
            var main = RunGitOutput(repo, "rev-parse", "main").Trim();
            var first = CreateCandidate(repo, firstGoal.Value, "src/A.cs", "a");
            var second = CreateCandidate(repo, secondGoal.Value, "tests/B.cs", "b");
            var bindings = new[]
            {
                Bind(first.GoalId, first.Revision, "src/A.cs", "resource:a"),
                Bind(second.GoalId, second.Revision, "tests/B.cs", "resource:b")
            };
            var databasePath = Path.Combine(repo, ".orchestrator", "cohort-acceptance.db");
            var store = new CohortAcceptanceStore(databasePath);
            using var integration = GoalWorktrees.CreateAcceptanceCohortWorkspace(repo, main, bindings);
            var identity = AcceptanceCohortIdentity.Create(
                bindings, main, integration.TreeRevision, "manifest-v1");
            var receipt = store.SaveGateReceipt(new AcceptanceCohortReceipt(
                "recoverable-receipt",
                identity,
                AcceptanceCohortGateOutcome.Passed,
                DateTimeOffset.UtcNow,
                100,
                [],
                GateExitCode: 0,
                GateTestResultPaths: [WritePassingTrx(repo, "recoverable-receipt.trx")],
                ValidForLanding: true));
            RunGit(repo, "branch", LandingExecutor.IntegrationBranchName, main);
            store.PrepareLanding(receipt, integration.CommitRevision, main);
            RunGit(
                repo,
                "update-ref",
                $"refs/heads/{LandingExecutor.IntegrationBranchName}",
                integration.CommitRevision,
                main);

            Assert.Empty(store.RecoverPreparedLandings(repo));
            Assert.Equal(
                main,
                RunGitOutput(repo, "rev-parse", $"refs/heads/{LandingExecutor.IntegrationBranchName}").Trim());
            store.PrepareLanding(receipt, integration.CommitRevision, main);
            RunGit(
                repo,
                "update-ref",
                $"refs/heads/{LandingExecutor.IntegrationBranchName}",
                integration.CommitRevision,
                main);
            RunGit(repo, "merge", "--ff-only", integration.CommitRevision);
            var recovery = Assert.Single(store.RecoverPreparedLandings(repo));
            Assert.Equal(identity.Value, recovery.Receipt.Identity.Value);
            store.CompleteLandingEffects(identity.Value, receipt.ReceiptId);
            Assert.Empty(store.RecoverPreparedLandings(repo));

            using var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM cohort_members WHERE cohort_id=$cohort AND landed=1;";
            command.Parameters.AddWithValue("$cohort", identity.Value);
            Assert.Equal(2, Convert.ToInt32(command.ExecuteScalar()));
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Fact]
    public void ConstructorRecovery_DefersEntireCohortUntilReplacementLeaseIsReleased()
    {
        var repo = CreateAcceptanceCohortRepository();
        try
        {
            AddAcceptanceManifest(repo);
            var kernel = new AgentOrchestratorKernel();
            var firstGoal = CreateCompletedGoal(kernel, "First recovered member", repo);
            var secondGoal = CreateCompletedGoal(kernel, "Second recovered member", repo);
            var firstRevision = CreateWorktreeCandidate(
                repo,
                firstGoal.Id,
                "src/Mcg.AgentOrchestrator.Infrastructure/First.cs",
                "first");
            var secondRevision = CreateWorktreeCandidate(repo, secondGoal.Id, "tests/Second.cs", "second");
            var main = RunGitOutput(repo, "rev-parse", "main").Trim();
            var bindings = new[]
            {
                Bind(firstGoal.Id, firstRevision, "src/Mcg.AgentOrchestrator.Infrastructure/First.cs", "resource:first"),
                Bind(secondGoal.Id, secondRevision, "tests/Second.cs", "resource:second")
            };
            using var integration = GoalWorktrees.CreateAcceptanceCohortWorkspace(repo, main, bindings);
            var identity = AcceptanceCohortIdentity.Create(
                bindings,
                main,
                integration.TreeRevision,
                GoalAcceptanceVerifier.ComputeEffectiveAcceptancePlanIdentity(
                    integration.Path,
                    bindings.SelectMany(member => member.LandingPaths).ToArray()));
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var store = new CohortAcceptanceStore(
                Path.Combine(workspace.OrchestratorDirectory, "cohort-acceptance.db"));
            var receipt = store.SaveGateReceipt(new AcceptanceCohortReceipt(
                "constructor-recovery-receipt",
                identity,
                AcceptanceCohortGateOutcome.Passed,
                DateTimeOffset.UtcNow,
                100,
                [],
                GateExitCode: 0,
                GateTestResultPaths: [WritePassingTrx(repo, "constructor-recovery.trx")],
                ValidForLanding: true));
            RunGit(repo, "branch", LandingExecutor.IntegrationBranchName, main);
            store.PrepareLanding(receipt, integration.CommitRevision, main);
            RunGit(
                repo,
                "update-ref",
                $"refs/heads/{LandingExecutor.IntegrationBranchName}",
                integration.CommitRevision,
                main);
            RunGit(repo, "update-ref", "refs/heads/main", integration.CommitRevision, main);

            var replacementProtectedGoal = new[] { firstGoal, secondGoal }
                .OrderBy(goal => goal.Id.Value, StringComparer.Ordinal)
                .Last();
            using (var replacementLease = new ReconcileSweepRemediationStore(workspace.SqliteStatePath)
                       .TryAcquireAcceptanceLease(
                           replacementProtectedGoal.Id.Value,
                           $"goal-replace:test:{Guid.NewGuid():N}",
                           TimeSpan.FromMinutes(30)))
            {
                Assert.NotNull(replacementLease);
                _ = new ConductorDriver(
                    kernel,
                    workspace,
                    FakeAcceptanceVerifier.Throws(new InvalidOperationException("Blocked recovery must not run a gate.")),
                    AgentCatalog.Default().Agents,
                    WorkerProfileCatalog.Default(),
                cleanupHooks: CreateIsolatedCleanupContext(workspace.ExecutionDirectory).Hooks);

                Assert.All(new[] { firstGoal, secondGoal }, goal =>
                    Assert.False(GoalOperationJournal.HasCompletedLandingEvidence(
                        GoalOperationJournal.Read(repo, goal.Id))));
                var blockedLifecycleText = Directory.Exists(workspace.GoalLifecycleEventsDirectory)
                    ? string.Join(
                        '\n',
                        Directory.EnumerateFiles(workspace.GoalLifecycleEventsDirectory, "*.jsonl")
                            .Select(File.ReadAllText))
                    : string.Empty;
                Assert.DoesNotContain("\"eventType\":\"GoalLanded\"", blockedLifecycleText, StringComparison.Ordinal);
                Assert.Single(store.RecoverPreparedLandings(repo));
            }

            var recoveredDriver = new ConductorDriver(
                kernel,
                workspace,
                FakeAcceptanceVerifier.Throws(new InvalidOperationException("Recovery must not run a gate.")),
                AgentCatalog.Default().Agents,
                WorkerProfileCatalog.Default(),
                cleanupHooks: CreateIsolatedCleanupContext(workspace.ExecutionDirectory).Hooks);
            var recoveredReceipts = new List<ConductorLandingReceipt>();
            recoveredDriver.SuccessfulLandingSink = recoveredReceipts.Add;

            Assert.Equal(2, recoveredReceipts.Count);
            Assert.Equal(
                new[] { firstGoal.Id.Value, secondGoal.Id.Value }.Order(StringComparer.Ordinal),
                recoveredReceipts.Select(item => item.GoalId).Order(StringComparer.Ordinal));
            Assert.All(new[] { firstGoal, secondGoal }, goal =>
                Assert.True(GoalOperationJournal.HasCompletedLandingEvidence(
                    GoalOperationJournal.Read(repo, goal.Id))));
            var lifecycleText = string.Join(
                '\n',
                Directory.EnumerateFiles(workspace.GoalLifecycleEventsDirectory, "*.jsonl")
                    .Select(File.ReadAllText));
            Assert.Equal(2, lifecycleText.Split("\"eventType\":\"GoalLanded\"", StringSplitOptions.None).Length - 1);
            Assert.Empty(store.RecoverPreparedLandings(repo));
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Fact]
    public void StaleMaterialization_PersistsOutcomeAndUsesOrdinaryFallback()
    {
        var repo = CreateAcceptanceCohortRepository();
        try
        {
            AddAcceptanceManifest(repo);
            var kernel = new AgentOrchestratorKernel();
            var firstGoal = CreateCompletedGoal(kernel, "First materialization member", repo);
            var secondGoal = CreateCompletedGoal(kernel, "Second materialization member", repo);
            var firstRevision = CreateWorktreeCandidate(repo, firstGoal.Id, "src/Mcg.AgentOrchestrator.Infrastructure/First.cs", "first");
            var secondRevision = CreateWorktreeCandidate(repo, secondGoal.Id, "tests/Second.cs", "second");
            var verifier = new FakeAcceptanceVerifier(
                result: null,
                exception: new InvalidOperationException("The gate must not run after materialization fails."));
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var driver = new ConductorDriver(
                kernel,
                workspace,
                verifier,
                AgentCatalog.Default().Agents,
                WorkerProfileCatalog.Default(),
                cleanupHooks: CreateIsolatedCleanupContext(workspace.ExecutionDirectory).Hooks);
            var selection = ProjectSelection(driver, firstGoal, secondGoal);
            var store = new CohortAcceptanceStore(
                Path.Combine(workspace.OrchestratorDirectory, "cohort-acceptance.db"));
            store.RecordOvertake(firstGoal.Id);
            var gateAdmissions = 0;

            File.WriteAllText(Path.Combine(repo, "main-moved.txt"), "new main");
            RunGit(repo, "add", "main-moved.txt");
            RunGit(repo, "commit", "-m", "Move main before cohort materialization");

            var result = driver.RunAcceptanceCohort(
                selection,
                [firstGoal, secondGoal],
                ConductorAutonomyPolicy.Permissive,
                onGateAdmitted: () =>
                {
                    gateAdmissions++;
                    driver.RecordCohortAdmissionFairness([firstGoal, secondGoal], selection);
                });

            Assert.Null(result.Receipt);
            Assert.Empty(result.MemberResults);
            Assert.Contains("outcome=materialization-failure", result.Detail, StringComparison.Ordinal);
            Assert.Contains("fallback=ordinary", result.Detail, StringComparison.Ordinal);
            Assert.Equal(0, verifier.RunCount);
            Assert.Equal(0, gateAdmissions);
            Assert.Equal(1, store.ReadOvertakeCount(firstGoal.Id));
            AssertNoCohortWorkspaces(repo);
            Assert.Equal(firstRevision, RunGitOutput(repo, "rev-parse", $"refs/heads/{GoalWorktrees.BranchName(firstGoal.Id)}").Trim());
            Assert.Equal(secondRevision, RunGitOutput(repo, "rev-parse", $"refs/heads/{GoalWorktrees.BranchName(secondGoal.Id)}").Trim());

            using var connection = new SqliteConnection($"Data Source={Path.Combine(workspace.OrchestratorDirectory, "cohort-acceptance.db")};Pooling=False");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT outcome FROM cohort_materialization_failures;";
            Assert.Equal("StaleBinding", Assert.IsType<string>(command.ExecuteScalar()));
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Fact]
    public void RetryableLandingHold_PreservesPassingReceipt_AndLaterRelandsIdenticalState()
    {
        var repo = CreateReducedAcceptanceCohortRepository();
        try
        {
            AddAcceptanceManifest(repo);
            var kernel = new AgentOrchestratorKernel();
            var firstGoal = CreateCompletedGoal(kernel, "First retryable landing member", repo);
            var secondGoal = CreateCompletedGoal(kernel, "Second retryable landing member", repo);
            var firstRevision = CreateWorktreeCandidate(repo, firstGoal.Id, "src/Mcg.AgentOrchestrator.Infrastructure/First.cs", "first");
            var secondRevision = CreateWorktreeCandidate(repo, secondGoal.Id, "tests/Second.cs", "second");
            var verifier = new FakeAcceptanceVerifier(
                result: null,
                exception: new InvalidOperationException("An identical-state landing retry must reuse its exact gate receipt."));
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var driver = new ConductorDriver(
                kernel,
                workspace,
                verifier,
                AgentCatalog.Default().Agents,
                WorkerProfileCatalog.Default(),
                cleanupHooks: CreateIsolatedCleanupContext(workspace.ExecutionDirectory).Hooks);
            var selection = ProjectSelection(driver, firstGoal, secondGoal);
            var bindings = selection.BindMembers();
            var store = new CohortAcceptanceStore(Path.Combine(workspace.OrchestratorDirectory, "cohort-acceptance.db"));
            AcceptanceCohortIdentity identity;
            using (var integration = GoalWorktrees.CreateAcceptanceCohortWorkspace(
                       repo,
                       selection.Members[0].MainRevision,
                       bindings))
            {
                identity = AcceptanceCohortIdentity.Create(
                    bindings,
                    selection.Members[0].MainRevision,
                    integration.TreeRevision,
                    GoalAcceptanceVerifier.ComputeEffectiveAcceptancePlanIdentity(
                        integration.Path,
                        bindings.SelectMany(member => member.LandingPaths).ToArray()));
            }
            _ = store.SaveGateReceipt(new AcceptanceCohortReceipt(
                "retryable-hold-receipt",
                identity,
                AcceptanceCohortGateOutcome.Passed,
                DateTimeOffset.UtcNow,
                100,
                [],
                GateExitCode: 0,
                GateTestResultPaths: [WritePassingTrx(repo, "retryable-hold-receipt.trx")],
                ValidForLanding: true));

            driver.LandingMutationBlocker = () => "operator-controlled landing pause";
            var held = driver.RunAcceptanceCohort(
                selection,
                [firstGoal, secondGoal],
                ConductorAutonomyPolicy.Permissive);

            Assert.Equal(AcceptanceCohortGateOutcome.Passed, held.Receipt?.Outcome);
            Assert.True(held.Receipt?.ValidForLanding);
            Assert.All(held.MemberResults.Values, result => Assert.IsType<ConductorAdvanceOutcome.Held>(result.Outcome));
            Assert.Equal(identity.ObservedMainRevision, RunGitOutput(repo, "rev-parse", "main").Trim());

            driver.LandingMutationBlocker = null;
            var landed = driver.RunAcceptanceCohort(
                selection,
                [firstGoal, secondGoal],
                ConductorAutonomyPolicy.Permissive);

            Assert.Equal(AcceptanceCohortGateOutcome.Passed, landed.Receipt?.Outcome);
            Assert.True(landed.Receipt?.ValidForLanding);
            Assert.All(landed.MemberResults.Values, result => Assert.IsType<ConductorAdvanceOutcome.Executed>(result.Outcome));
            Assert.Equal(identity.CombinedTreeRevision, RunGitOutput(repo, "rev-parse", "main^{tree}").Trim());
            Assert.Equal(0, verifier.RunCount);
            Assert.Equal(firstRevision, RunGitOutput(repo, "rev-parse", $"refs/heads/{GoalWorktrees.BranchName(firstGoal.Id)}").Trim());
            Assert.Equal(secondRevision, RunGitOutput(repo, "rev-parse", $"refs/heads/{GoalWorktrees.BranchName(secondGoal.Id)}").Trim());
            AssertNoCohortWorkspaces(repo);
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Fact]
    public void CachedPassWithoutEvidence_InvalidatesBeforeLanding()
    {
        var repo = CreateAcceptanceCohortRepository();
        try
        {
            AddAcceptanceManifest(repo);
            var kernel = new AgentOrchestratorKernel();
            var firstGoal = CreateCompletedGoal(kernel, "First cached receipt member", repo);
            var secondGoal = CreateCompletedGoal(kernel, "Second cached receipt member", repo);
            _ = CreateWorktreeCandidate(
                repo,
                firstGoal.Id,
                "src/Mcg.AgentOrchestrator.Infrastructure/First.cs",
                "first");
            _ = CreateWorktreeCandidate(repo, secondGoal.Id, "tests/Second.cs", "second");
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var verifier = FakeAcceptanceVerifier.Throws(
                new InvalidOperationException("An invalid cached pass must not be reused or rerun in place."));
            var driver = new ConductorDriver(
                kernel, workspace, verifier, AgentCatalog.Default().Agents, WorkerProfileCatalog.Default(),
                cleanupHooks: CreateIsolatedCleanupContext(workspace.ExecutionDirectory).Hooks);
            var selection = ProjectSelection(driver, firstGoal, secondGoal);
            var bindings = selection.BindMembers();
            AcceptanceCohortIdentity identity;
            using (var integration = GoalWorktrees.CreateAcceptanceCohortWorkspace(
                       repo, selection.Members[0].MainRevision, bindings))
            {
                identity = AcceptanceCohortIdentity.Create(
                    bindings,
                    selection.Members[0].MainRevision,
                    integration.TreeRevision,
                    GoalAcceptanceVerifier.ComputeEffectiveAcceptancePlanIdentity(
                        integration.Path,
                        bindings.SelectMany(member => member.LandingPaths).ToArray()));
            }
            var databasePath = Path.Combine(workspace.OrchestratorDirectory, "cohort-acceptance.db");
            var store = new CohortAcceptanceStore(databasePath);
            _ = store.SaveGateReceipt(new AcceptanceCohortReceipt(
                "legacy-cached-pass",
                identity,
                AcceptanceCohortGateOutcome.Passed,
                DateTimeOffset.UtcNow,
                100,
                [],
                GateExitCode: 0,
                GateTestResultPaths: [WritePassingTrx(repo, "legacy-cached-pass.trx")],
                ValidForLanding: true));
            using (var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "UPDATE cohort_receipts SET gate_exit_code=NULL, gate_test_result_paths_json='[]';";
                Assert.Equal(1, command.ExecuteNonQuery());
            }

            var result = driver.RunAcceptanceCohort(
                selection, [firstGoal, secondGoal], ConductorAutonomyPolicy.Permissive);

            Assert.Equal(AcceptanceCohortGateOutcome.Passed, result.Receipt?.Outcome);
            Assert.Equal(AcceptanceCohortInvalidationReason.EvidenceUnavailable, result.Receipt?.Invalidation?.Reason);
            Assert.False(result.Receipt?.HasAuthoritativeLandingEvidence);
            Assert.Equal(2, result.MemberResults.Count);
            Assert.All(result.MemberResults.Values, member => Assert.IsType<ConductorAdvanceOutcome.Held>(member.Outcome));
            Assert.Contains("lacks successful exit", result.Detail, StringComparison.Ordinal);
            Assert.Equal(0, verifier.RunCount);
            Assert.Equal(identity.ObservedMainRevision, RunGitOutput(repo, "rev-parse", "main").Trim());
            using var verifyConnection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
            verifyConnection.Open();
            using var verifyCommand = verifyConnection.CreateCommand();
            verifyCommand.CommandText = """
                SELECT outcome, valid_for_landing,
                       (SELECT COUNT(*) FROM cohort_invalidations WHERE cohort_id=$cohort)
                FROM cohort_receipts WHERE cohort_id=$cohort;
                """;
            verifyCommand.Parameters.AddWithValue("$cohort", identity.Value);
            using var verifyReader = verifyCommand.ExecuteReader();
            Assert.True(verifyReader.Read());
            Assert.Equal("Passed", verifyReader.GetString(0));
            Assert.Equal(1, verifyReader.GetInt32(1));
            Assert.Equal(1, verifyReader.GetInt32(2));
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Fact]
    public void PostGateBranchMove_HoldsMembersForFreshProjection()
    {
        var repo = CreateAcceptanceCohortRepository();
        var trx = Path.Combine(Path.GetTempPath(), $"cohort-branch-movement-{Guid.NewGuid():N}.trx");
        try
        {
            AddAcceptanceManifest(repo);
            File.WriteAllText(trx, ValidPassingTrx());
            var kernel = new AgentOrchestratorKernel();
            var firstGoal = CreateCompletedGoal(kernel, "First moving member", repo);
            var secondGoal = CreateCompletedGoal(kernel, "Second moving member", repo);
            _ = CreateWorktreeCandidate(
                repo,
                firstGoal.Id,
                "src/Mcg.AgentOrchestrator.Infrastructure/First.cs",
                "first");
            _ = CreateWorktreeCandidate(repo, secondGoal.Id, "tests/Second.cs", "second");
            var firstWorktree = Assert.IsType<string>(GoalWorktrees.TryResolve(repo, firstGoal.Id));
            var verifier = new FakeAcceptanceVerifier(
                new AcceptanceVerificationResult(
                    Passed: true,
                    Skipped: false,
                    ExitCode: 0,
                    OutputTail: null,
                    TestResultPaths: [trx]),
                onRun: () =>
                {
                    File.WriteAllText(Path.Combine(firstWorktree, "post-gate.txt"), "branch moved");
                    RunGit(firstWorktree, "add", "post-gate.txt");
                    RunGit(firstWorktree, "commit", "-m", "Move branch during cohort gate");
                });
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var driver = new ConductorDriver(
                kernel, workspace, verifier, AgentCatalog.Default().Agents, WorkerProfileCatalog.Default(),
                cleanupHooks: CreateIsolatedCleanupContext(workspace.ExecutionDirectory).Hooks);
            var selection = ProjectSelection(driver, firstGoal, secondGoal);

            var result = driver.RunAcceptanceCohort(
                selection, [firstGoal, secondGoal], ConductorAutonomyPolicy.Permissive);

            Assert.Equal(AcceptanceCohortGateOutcome.Passed, result.Receipt?.Outcome);
            Assert.Equal(AcceptanceCohortInvalidationReason.GoalBranchChanged, result.Receipt?.Invalidation?.Reason);
            Assert.Equal(2, result.MemberResults.Count);
            Assert.All(result.MemberResults.Values, member => Assert.IsType<ConductorAdvanceOutcome.Held>(member.Outcome));
            Assert.Contains("fresh Ready projection", result.Detail, StringComparison.Ordinal);
            Assert.Equal(1, verifier.RunCount);
            AssertNoCohortWorkspaces(repo);
        }
        finally
        {
            if (File.Exists(trx)) File.Delete(trx);
            DeleteDirectory(repo);
        }
    }

}
