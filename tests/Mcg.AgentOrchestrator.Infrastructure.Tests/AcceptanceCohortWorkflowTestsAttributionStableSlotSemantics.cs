using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

public sealed class AcceptanceCohortWorkflowTestsAttributionStableSlotSemantics : AcceptanceCohortWorkflowTests
{
    [Theory]
    [InlineData(AcceptanceCohortAttributionOutcome.FirstMemberFailed)]
    [InlineData(AcceptanceCohortAttributionOutcome.SecondMemberFailed)]
    [InlineData(AcceptanceCohortAttributionOutcome.BothMembersFailed)]
    [InlineData(AcceptanceCohortAttributionOutcome.InteractionOnly)]
    [InlineData(AcceptanceCohortAttributionOutcome.Indeterminate)]
    public void ShardedPartitions_PreserveAttributionAndPartitionReceiptFields(
        AcceptanceCohortAttributionOutcome expected)
    {
        var repo = CreateReducedAcceptanceCohortRepository();
        try
        {
            AddAcceptanceManifest(repo);
            var kernel = new AgentOrchestratorKernel();
            var first = CreateCompletedGoal(kernel, "First attributed member", repo);
            var second = CreateCompletedGoal(kernel, "Second attributed member", repo);
            CreateWorktreeCandidate(repo, first.Id,
                "src/Mcg.AgentOrchestrator.Infrastructure/First.cs", "first");
            CreateWorktreeCandidate(repo, second.Id, "tests/Second.cs", "second");
            var combined = Red(repo, "combined", "Tests.T");
            var firstResult = expected is AcceptanceCohortAttributionOutcome.FirstMemberFailed or
                AcceptanceCohortAttributionOutcome.BothMembersFailed
                ? Red(repo, "first", "Tests.T", "Tests.U")
                : expected == AcceptanceCohortAttributionOutcome.Indeterminate
                    ? new AcceptanceVerificationResult(false, true, null, "skipped", Checks: [], TestResultPaths: [])
                    : Green(repo, "first");
            var secondResult = expected is AcceptanceCohortAttributionOutcome.SecondMemberFailed or
                AcceptanceCohortAttributionOutcome.BothMembersFailed
                ? Red(repo, "second", "Tests.T", "Tests.V")
                : Green(repo, "second");
            var verifier = new SequenceAcceptanceVerifier([combined, firstResult, secondResult]);
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var driver = new ConductorDriver(kernel, workspace, verifier,
                AgentCatalog.Default().Agents, WorkerProfileCatalog.Default(),
                cleanupHooks: CreateIsolatedCleanupContext(workspace.ExecutionDirectory).Hooks)
            {
                CohortPartitionStableSlotLeaseSource = (_, _) =>
                    AcceptanceStableSlotTestSupport.CreateFakeStableSlotLease(repo)
            };
            var selection = ProjectSelection(driver, first, second);

            var run = driver.RunAcceptanceCohort(selection, [first, second],
                ConductorAutonomyPolicy.Permissive);
            var receipt = Assert.IsType<AcceptanceCohortReceipt>(run.Receipt);
            Assert.Equal(expected, receipt.Attribution);
            Assert.Equal<GoalId?>([null, first.Id, second.Id], verifier.GoalIds);
            GoalId[] attributed = expected switch
            {
                AcceptanceCohortAttributionOutcome.FirstMemberFailed => [first.Id],
                AcceptanceCohortAttributionOutcome.SecondMemberFailed => new[] { second.Id },
                AcceptanceCohortAttributionOutcome.BothMembersFailed => [first.Id, second.Id],
                _ => Array.Empty<GoalId>()
            };
            Assert.Equal(attributed, receipt.AttributedMembers.Select(member => member.GoalId));
            Assert.All(receipt.AttributedMembers, member =>
            {
                Assert.Equal("Tests.T", Assert.Single(member.ReproducedFailingTests));
                Assert.Equal(selection.Members[member.MemberOrdinal].CandidateRevision,
                    member.CandidateRevision);
            });
            string[] unrelated = expected switch
            {
                AcceptanceCohortAttributionOutcome.FirstMemberFailed => ["Tests.U"],
                AcceptanceCohortAttributionOutcome.SecondMemberFailed => new[] { "Tests.V" },
                AcceptanceCohortAttributionOutcome.BothMembersFailed => ["Tests.U", "Tests.V"],
                _ => Array.Empty<string>()
            };
            Assert.Equal(unrelated, receipt.UnrelatedFailures.SelectMany(failure => failure.FailingTests));
            Assert.Equal(unrelated.Length, receipt.UnrelatedFailures.Count);
            Assert.Equal(AcceptanceCohortGateOutcome.Failed, receipt.Outcome);
            Assert.False(receipt.ValidForLanding);

            using var connection = new SqliteConnection($"Data Source={Path.Combine(
                workspace.OrchestratorDirectory, "cohort-acceptance.db")};Pooling=False");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT cohort_id, member_ordinal, receipt_id, goal_id, candidate_revision,
                       main_revision, tree_revision, manifest_identity, outcome, test_result_paths_json
                  FROM cohort_partition_receipts
                 WHERE cohort_id=$cohort ORDER BY member_ordinal
                """;
            command.Parameters.AddWithValue("$cohort", receipt.Identity.Value);
            using var rows = command.ExecuteReader();
            for (var ordinal = 0; ordinal < 2; ordinal++)
            {
                Assert.True(rows.Read());
                var goal = ordinal == 0 ? first : second;
                var fixture = ordinal == 0 ? firstResult : secondResult;
                Assert.Equal(receipt.Identity.Value, rows.GetString(0));
                Assert.Equal(ordinal, rows.GetInt32(1));
                Assert.StartsWith("cohort-partition-v1-", rows.GetString(2));
                Assert.Equal(goal.Id.Value, rows.GetString(3));
                Assert.Equal(selection.Members[ordinal].CandidateRevision, rows.GetString(4));
                Assert.Equal(receipt.Identity.ObservedMainRevision, rows.GetString(5));
                Assert.False(rows.IsDBNull(6));
                Assert.False(string.IsNullOrWhiteSpace(rows.GetString(7)));
                Assert.Equal(expected == AcceptanceCohortAttributionOutcome.Indeterminate && ordinal == 0
                    ? "InfrastructureFailure"
                    : fixture.Passed ? "Passed" : "Failed", rows.GetString(8));
                Assert.Equal(fixture.TestResultPaths?.ToArray() ?? [],
                    JsonSerializer.Deserialize<string[]>(rows.GetString(9)));
            }
            Assert.False(rows.Read());
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    private static AcceptanceVerificationResult Red(string repo, string name, params string[] identities) =>
        FailedVerification(repo, $"{name}-red.trx", name) with
        {
            Checks = [new AcceptanceCheckResult(name, false, 1, name,
                FailingTestIdentities: identities)]
        };

    private static AcceptanceVerificationResult Green(string repo, string name) =>
        new(true, false, 0, null,
            Checks: [new AcceptanceCheckResult(name, true, 0, null)],
            TestResultPaths: [WritePassingTrx(repo, $"{name}-green.trx")]);
}
