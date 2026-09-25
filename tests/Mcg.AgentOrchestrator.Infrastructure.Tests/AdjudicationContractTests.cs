using System.Security.Cryptography;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(TestCollections.DotnetBuildSlots)]
public sealed class AdjudicationContractTests : ConductorBatchLoopTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public AdjudicationContractTests(ITestOutputHelper output) : base(output) { }

    [Xunit.Fact]
    public async Task Decision_and_effect_round_trip_reversibility_precedent_and_final_outcome()
    {
        await WithHarness(async harness =>
        {
            var (kernel, goal) = SimpleGoal();
            var task = goal.Tasks.Single();
            kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Failed, "failed");
            var payload = Payload("close", harness.Root) with
            {
                Reversibility = "irreversible",
                Precedent = "decision:abc123"
            };
            var intent = await harness.Enqueue(goal, task, payload);

            harness.Coordinator.ExecutePending(kernel, goal);
            harness.Coordinator.CompletePersisted([goal.Id]);

            var reopened = CollaborationItemStore.ForDirectory(harness.Root);
            var state = await reopened.GetDecisionStateAsync($"adjudicate-{intent.Id}");
            Assert.Equal(DecisionReversibility.Irreversible, state!.Receipt!.Reversibility);
            Assert.Equal("decision:abc123", state.Receipt.PrecedentRef);
            Assert.Equal(EffectReceiptStatus.Applied, state.Effect!.Status);
            Assert.Equal("applied task=Completed goal=Verified", state.Effect.Outcome);
            Assert.Equal(GoalStatus.Verified, goal.Status);
        });
    }

    [Xunit.Fact]
    public async Task Missing_named_reference_refuses_without_changing_task_and_real_files_apply()
    {
        await WithHarness(async harness =>
        {
            var (kernel, goal) = SimpleGoal();
            var task = goal.Tasks.Single();
            kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Failed, "failed");
            var before = JsonSerializer.Serialize(kernel.ExportGoalSnapshot(goal.Id) with { Timeline = [] }, JsonOptions);
            var missing = await harness.Enqueue(goal, task,
                Payload("close", harness.Root) with { EvidenceReferences = ["trx:missing.trx", "receipt-1"] });

            harness.Coordinator.ExecutePending(kernel, goal);

            var refused = await harness.Decisions.GetDecisionStateAsync($"adjudicate-{missing.Id}");
            Assert.Equal(EffectReceiptStatus.Rejected, refused!.Effect!.Status);
            Assert.Equal("evidence-reference-unresolved", refused.Effect.Result);
            Assert.Equal("refused evidence-reference-unresolved", refused.Effect.Outcome);
            Assert.Equal(OperatorIntentStatus.Rejected, (await harness.Store.GetAsync(missing.Id))!.Status);
            Assert.Equal(before, JsonSerializer.Serialize(kernel.ExportGoalSnapshot(goal.Id) with { Timeline = [] }, JsonOptions));

            var trxPath = Path.Combine(harness.Root, "focused.trx");
            var operatorPath = Path.Combine(harness.Root, "operator.txt");
            await File.WriteAllTextAsync(trxPath, "<TestRun />");
            await File.WriteAllTextAsync(operatorPath, "verified by operator");
            var valid = await harness.Enqueue(goal, task, Payload("close", harness.Root) with
            {
                EvidenceReferences = ["trx:focused.trx", "operator-evidence:operator.txt"]
            });

            harness.Coordinator.ExecutePending(kernel, goal);
            harness.Coordinator.CompletePersisted([goal.Id]);

            var applied = await harness.Decisions.GetDecisionStateAsync($"adjudicate-{valid.Id}");
            Assert.Equal(EffectReceiptStatus.Applied, applied!.Effect!.Status);
            Assert.Equal(OperatorIntentStatus.Applied, (await harness.Store.GetAsync(valid.Id))!.Status);
            Assert.Equal(GoalStatus.Verified, goal.Status);
            Assert.Equal(Hash(trxPath), applied.Receipt!.EvidenceHashes.Single(e => e.ReceiptId == "trx:focused.trx").ContentHash);
            Assert.Equal(Hash(operatorPath), applied.Receipt.EvidenceHashes.Single(e => e.ReceiptId == "operator-evidence:operator.txt").ContentHash);
        });
    }

    [Xunit.Fact]
    public void Focused_and_acceptance_references_resolve_from_their_own_stores()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mcg-adjudication-evidence-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var (kernel, goal) = ConductorDriverTests.SoftwareGoal();
            var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
            kernel.RecordPreReviewEvidence(goal.Id, reviewer.Id, new PreReviewEvidenceReceipt(
                goal.Id.Value, 1, "candidate-sha", [], PreReviewEvidenceDisposition.Green,
                1, 0, [new PreReviewEvidenceCheckReceipt("focused", "command", true, 0)],
                [], "focused result", "receipt-pointer", DateTimeOffset.UtcNow));
            var attemptDirectory = Path.Combine(root, "acceptance-gate-attempts", goal.Id.Value);
            Directory.CreateDirectory(attemptDirectory);
            File.WriteAllText(Path.Combine(attemptDirectory, "attempt-1.attempt.json"), "{\"attemptId\":\"attempt-1\"}");
            File.WriteAllText(Path.Combine(attemptDirectory, "attempt-1.result.json"), "{\"kind\":\"accepted\",\"acceptance\":{\"passed\":true}}");
            File.WriteAllText(Path.Combine(attemptDirectory, "attempt-1.exit.txt"), "0");
            var resolver = new AdjudicationEvidenceResolver(root);
            var payload = Payload("close", root);

            Assert.True(resolver.TryResolve("focused-evidence:receipt-pointer", goal, payload, out var focused));
            Assert.Equal("focused-evidence:receipt-pointer", focused.ReceiptId);
            Assert.True(resolver.TryResolve("acceptance-attempt:attempt-1", goal, payload, out var acceptance));
            Assert.Equal(Hash(Path.Combine(attemptDirectory, "attempt-1.attempt.json")), acceptance.ContentHash);
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }

    [Xunit.Fact]
    public async Task Close_accepts_recovered_assigned_task_with_default_reversibility()
    {
        await WithHarness(async harness =>
        {
            var (kernel, goal) = SimpleGoal();
            var task = goal.Tasks.Single();
            var intent = await harness.Enqueue(goal, task, Payload("close", harness.Root));
            harness.Coordinator.ExecutePending(kernel, goal);
            harness.Coordinator.CompletePersisted([goal.Id]);
            var state = await harness.Decisions.GetDecisionStateAsync($"adjudicate-{intent.Id}");
            Assert.Equal(DecisionReversibility.ReversibleWithCost, state!.Receipt!.Reversibility);
            Assert.Equal(EffectReceiptStatus.Applied, state.Effect!.Status);
        });
    }

    [Xunit.Fact]
    public void Escalation_remedy_names_one_adjudicate_action_for_each_goal_state()
    {
        var close = ConductorDriver.BuildAdjudicateRemedy(GoalStatus.Failed);
        var reopen = ConductorDriver.BuildAdjudicateRemedy(GoalStatus.AcceptanceFailed);
        Assert.Contains("adjudicate --goal <goal> <task#> close", close, StringComparison.Ordinal);
        Assert.Contains("adjudicate --goal <goal> <task#> reopen-regate", reopen, StringComparison.Ordinal);
        Assert.Contains("--evidence", close, StringComparison.Ordinal);
        Assert.DoesNotContain("--mechanical", close, StringComparison.Ordinal);
        Assert.DoesNotContain("verify-manual", close, StringComparison.Ordinal);
    }

    private static string Hash(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private static AdjudicateOperatorIntentPayload Payload(string shape, string root) =>
        new(shape, "Operator explanation", ["receipt-1"], Harness.Version, root);

    private static async Task WithHarness(Func<Harness, Task> action)
    {
        var root = Path.Combine(Path.GetTempPath(), $"mcg-adjudication-contract-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try { await action(new Harness(root)); }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }

    private sealed class Harness
    {
        internal const long Version = 7;
        internal Harness(string root)
        {
            Root = root;
            Store = new SqliteOperatorIntentStore(Path.Combine(root, "operator-intents.db"), Path.Combine(root, "logs"));
            Decisions = CollaborationItemStore.ForDirectory(root);
            Coordinator = new OperatorIntentCoordinator(Store, decisions: Decisions, goalStateVersionResolver: _ => Version);
        }

        internal string Root { get; }
        internal SqliteOperatorIntentStore Store { get; }
        internal CollaborationItemStore Decisions { get; }
        internal OperatorIntentCoordinator Coordinator { get; }

        internal Task<OperatorIntentRecord> Enqueue(Goal goal, TaskSpec task, AdjudicateOperatorIntentPayload payload)
        {
            var id = Guid.NewGuid().ToString("N");
            return Store.EnqueueAsync(new OperatorIntentRecord(
                id, id, OperatorIntentVerbs.Adjudicate, goal.Id.Value, task.Id.Value,
                JsonSerializer.Serialize(payload, JsonOptions), [], "operator", "cli", "local-process",
                DateTimeOffset.UtcNow));
        }
    }
}
