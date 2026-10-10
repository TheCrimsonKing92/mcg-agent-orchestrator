using System.Reflection;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

// Each case owns its database and TRX files; the process-wide cache counter requires serial execution.
[Collection(TestCollections.EnvMutation)]
public sealed class MergeTrainAcceptanceStoreGoalScopedEvidenceTests
{
    [Fact]
    public void ReadPassedReceiptsForGoal_LoadsOnlyMatchingGoalsTrxEvidence()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"train-goal-evidence-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var store = new MergeTrainAcceptanceStore(Path.Combine(directory, "merge-train-acceptance.db"));
            var receiptA = SavePassedReceipt(store, directory, "goal-a", 'a', 'c');
            var receiptB1 = SavePassedReceipt(store, directory, "goal-b-first", 'b', 'd');
            var receiptB2 = SavePassedReceipt(store, directory, "goal-b-second", 'b', 'e');
            Assert.True(receiptA.HasAuthoritativeLandingEvidence);
            Assert.True(receiptB1.HasAuthoritativeLandingEvidence);
            Assert.True(receiptB2.HasAuthoritativeLandingEvidence);

            ResetCache();
            var receipts = store.ReadPassedReceiptsForGoal(new GoalId(new string('a', 32)));

            Assert.Equal(receiptA.ReceiptId, Assert.Single(receipts).ReceiptId);
            Assert.Equal((long)receiptA.GateTestResultPaths.Count, CacheLoadCount());
        }
        finally
        {
            ResetCache();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ReadPassedReceiptsForGoal_ExcludesChangedTrxContentForMatchingGoal()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"train-goal-evidence-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var store = new MergeTrainAcceptanceStore(Path.Combine(directory, "merge-train-acceptance.db"));
            var coherent = SavePassedReceipt(store, directory, "coherent", 'a', 'c');
            var changed = SavePassedReceipt(store, directory, "changed", 'a', 'd');
            Assert.True(coherent.HasAuthoritativeLandingEvidence);
            Assert.True(changed.HasAuthoritativeLandingEvidence);
            File.WriteAllText(Assert.Single(changed.GateTestResultPaths), PassingTrx("rewritten"));

            ResetCache();
            var receipts = store.ReadPassedReceiptsForGoal(new GoalId(new string('a', 32)));

            Assert.Equal(coherent.ReceiptId, Assert.Single(receipts).ReceiptId);
        }
        finally
        {
            ResetCache();
            Directory.Delete(directory, recursive: true);
        }
    }

    private static MergeTrainReceipt SavePassedReceipt(
        MergeTrainAcceptanceStore store, string directory, string receiptId, char goal, char otherGoal)
    {
        var trxPath = Path.Combine(directory, $"{receiptId}.trx");
        File.WriteAllText(trxPath, PassingTrx(receiptId));
        var identity = MergeTrainIdentity.Create(
            [Binding(goal), Binding(otherGoal)], new string('f', 40), new string('1', 40), "manifest-v1");
        return store.SaveGateReceipt(new MergeTrainReceipt(
            receiptId, identity, MergeTrainGateOutcome.Passed, DateTimeOffset.UnixEpoch,
            1, [], 0, [trxPath], ValidForLanding: true));
    }

    private static MergeTrainMemberBinding Binding(char goal) => new(
        new GoalId(new string(goal, 32)), new string(goal, 40), new string(goal, 40),
        ["src/Example.cs"], [], ChangeRiskTier.Behavior, ConductorTransitionDecision.Auto,
        "Clean", "NoConflictsDetected", new string(goal, 40));

    private static string PassingTrx(string testName) => $$"""
        <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
          <Results><UnitTestResult testName="{{testName}}" outcome="Passed" /></Results>
          <ResultSummary><Counters total="1" executed="1" passed="1" failed="0" /></ResultSummary>
        </TestRun>
        """;

    private static Type CacheType => typeof(MergeTrainReceipt).Assembly.GetType(
        "Mcg.AgentOrchestrator.Core.TrxCoherenceCache", throwOnError: true)!;

    private static void ResetCache() =>
        (CacheType.GetMethod("Reset", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingMethodException(CacheType.FullName, "Reset")).Invoke(null, null);

    private static long CacheLoadCount() => (long)(
        (CacheType.GetProperty("LoadCount", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingMemberException(CacheType.FullName, "LoadCount")).GetValue(null)
        ?? throw new InvalidOperationException("TRX cache load count was unavailable."));
}
