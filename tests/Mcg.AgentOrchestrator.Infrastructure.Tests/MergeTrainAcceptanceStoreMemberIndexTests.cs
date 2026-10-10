using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
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
            Assert.All(new[] { receiptA, receiptB1, receiptB2, receiptB3 },
                receipt => Assert.True(receipt.HasAuthoritativeLandingEvidence));
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

    [Fact]
    public void ReadPassedReceiptsForGoal_ReturnsSharedReceiptForEachMemberAndExcludesChangedEvidence()
    {
        var directory = Path.Combine(Path.GetTempPath(), "merge-train-member-index-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var databasePath = Path.Combine(directory, "receipts.db");
            var store = new MergeTrainAcceptanceStore(databasePath);
            var shared = SavePassedReceipt(store, directory, "shared", 'a', 'c');
            var changed = SavePassedReceipt(store, directory, "changed", 'a', 'd');
            var goalA = new GoalId(new string('a', 32));
            var goalC = new GoalId(new string('c', 32));
            Assert.True(shared.HasAuthoritativeLandingEvidence);
            Assert.True(changed.HasAuthoritativeLandingEvidence);
            Assert.Equal(2, store.ReadPassedReceiptsForGoal(goalA).Count);
            Assert.Equal(shared.ReceiptId, Assert.Single(store.ReadPassedReceiptsForGoal(goalC)).ReceiptId);

            // Different-length coherent content invalidates the captured evidence without relying on timestamp resolution.
            File.WriteAllText(Assert.Single(changed.GateTestResultPaths), PassingTrx("changed-content-is-longer"));

            Assert.Equal(shared.ReceiptId, Assert.Single(store.ReadPassedReceiptsForGoal(goalA)).ReceiptId);
            Assert.Equal(shared.ReceiptId, Assert.Single(store.ReadPassedReceiptsForGoal(goalC)).ReceiptId);
            Assert.False(changed.HasAuthoritativeLandingEvidence);
            Assert.Equal(shared.ReceiptId, store.SaveGateReceipt(shared).ReceiptId);
            using var connection = OpenDatabase(databasePath);
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM merge_train_receipt_members WHERE goal_id=$goal;";
            command.Parameters.AddWithValue("$goal", goalA.Value);
            Assert.Equal(2L, (long)command.ExecuteScalar()!);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Constructor_BackfillsMissingMemberTableOnceWithoutRestoringDeletedRows()
    {
        var directory = Path.Combine(Path.GetTempPath(), "merge-train-member-index-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var databasePath = Path.Combine(directory, "receipts.db");
            var store = new MergeTrainAcceptanceStore(databasePath);
            var receiptA = SavePassedReceipt(store, directory, "goal-a", 'a', 'c');
            var receiptB = SavePassedReceipt(store, directory, "goal-b", 'b', 'd');
            Assert.True(receiptA.HasAuthoritativeLandingEvidence);
            Assert.True(receiptB.HasAuthoritativeLandingEvidence);
            using var connection = OpenDatabase(databasePath);
            using var command = connection.CreateCommand();
            command.CommandText = "DROP TABLE merge_train_receipt_members;";
            command.ExecuteNonQuery();
            // Legacy membership must be indexed even when its TRX is unavailable.
            File.Delete(Assert.Single(receiptB.GateTestResultPaths));

            var reopened = new MergeTrainAcceptanceStore(databasePath);
            command.CommandText = "SELECT goal_id, train_id FROM merge_train_receipt_members ORDER BY goal_id;";
            var rows = new List<(string Goal, string Train)>();
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                    rows.Add((reader.GetString(0), reader.GetString(1)));
            }
            Assert.Equal(new[]
            {
                (new string('a', 32), receiptA.Identity.Value),
                (new string('b', 32), receiptB.Identity.Value),
                (new string('c', 32), receiptA.Identity.Value),
                (new string('d', 32), receiptB.Identity.Value)
            }, rows);
            var goalA = new GoalId(new string('a', 32));
            Assert.Equal(receiptA.ReceiptId, Assert.Single(reopened.ReadPassedReceiptsForGoal(goalA)).ReceiptId);

            command.CommandText = "DELETE FROM merge_train_receipt_members WHERE goal_id=$goal AND train_id=$train;";
            command.Parameters.AddWithValue("$goal", goalA.Value);
            command.Parameters.AddWithValue("$train", receiptA.Identity.Value);
            Assert.Equal(1, command.ExecuteNonQuery());
            var reopenedAgain = new MergeTrainAcceptanceStore(databasePath);
            command.CommandText = "SELECT COUNT(*) FROM merge_train_receipt_members;";
            Assert.Equal(3L, (long)command.ExecuteScalar()!);
            Assert.Empty(reopenedAgain.ReadPassedReceiptsForGoal(goalA));
            Assert.Equal(receiptA.ReceiptId, Assert.Single(
                reopenedAgain.ReadPassedReceiptsForGoal(new GoalId(new string('c', 32)))).ReceiptId);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static SqliteConnection OpenDatabase(string databasePath)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = databasePath, Pooling = false }.ConnectionString);
        connection.Open();
        return connection;
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
