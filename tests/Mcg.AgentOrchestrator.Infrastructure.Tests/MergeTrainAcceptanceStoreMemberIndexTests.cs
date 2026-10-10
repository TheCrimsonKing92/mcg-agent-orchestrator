using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

namespace Mcg.AgentOrchestrator.Infrastructure.Tests;

public sealed class MergeTrainAcceptanceStoreMemberIndexTests
{
    [Fact]
    public void ReadPassedReceiptsForGoal_DoesNotDeserializeOtherGoalsReceipts()
    {
        var directory = Path.Combine(Path.GetTempPath(), "merge-train-member-index-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var databasePath = Path.Combine(directory, "receipts.db");
            var store = new MergeTrainAcceptanceStore(databasePath);
            var receiptA = SavePassedReceipt(store, directory, "goal-a", 'a', 'd');
            var receiptB1 = SavePassedReceipt(store, directory, "goal-b-first", 'b', 'e');
            var receiptB2 = SavePassedReceipt(store, directory, "goal-b-second", 'b', 'f');
            var receiptB3 = SavePassedReceipt(store, directory, "goal-b-third", 'b', 'g');
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                   { DataSource = databasePath, Pooling = false }.ConnectionString))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    UPDATE merge_train_receipts SET payload_json='not-json{'
                    WHERE train_id IN ($b1, $b2, $b3);
                    """;
                command.Parameters.AddWithValue("$b1", receiptB1.Identity.Value);
                command.Parameters.AddWithValue("$b2", receiptB2.Identity.Value);
                command.Parameters.AddWithValue("$b3", receiptB3.Identity.Value);
                Assert.Equal(3, command.ExecuteNonQuery());
            }

            Assert.Equal(receiptA.ReceiptId,
                Assert.Single(store.ReadPassedReceiptsForGoal(new GoalId(new string('a', 32)))).ReceiptId);
            Assert.ThrowsAny<JsonException>(() => store.ReadPassedReceiptsForGoal(new GoalId(new string('b', 32))));
        }
        finally
        {
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
}
