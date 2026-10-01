using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

// Isolated database per case; no process-wide environment or shared database is mutated.
public sealed class MergeTrainAcceptanceStoreTrainImplicationTests
{
    [Fact]
    public void ImplicationAndSuppression_SurviveReopeningAndAreIdempotent()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"train-implication-{Guid.NewGuid():N}");
        var database = Path.Combine(directory, "merge-train-acceptance.db");
        try
        {
            var goal = new GoalId(new string('a', 32));
            var revision = new string('b', 40);
            var main = new string('c', 40);
            var other = new GoalId(new string('d', 32));
            var otherRevision = new string('e', 40);
            var pair = $"{goal.Value}:{revision}:{other.Value}:{otherRevision}:{main}";
            var store = new MergeTrainAcceptanceStore(database);
            Assert.Empty(store.ReadTrainImplicatedCandidateKeys());
            Assert.Empty(store.ReadSuppressedPairs());
            store.RecordTrainImplicatedCandidate(goal, revision, "red-train", main);
            store.SuppressPair(pair, "red-pair-train");

            var reopened = new MergeTrainAcceptanceStore(database);
            Assert.Equal($"{goal.Value}:{revision}", Assert.Single(reopened.ReadTrainImplicatedCandidateKeys()));
            Assert.Equal(pair, Assert.Single(reopened.ReadSuppressedPairs()));
            reopened.RecordTrainImplicatedCandidate(goal, revision, "later-replay", new string('f', 40));
            reopened.SuppressPair(pair, "later-replay");
            Assert.Single(new MergeTrainAcceptanceStore(database).ReadTrainImplicatedCandidateKeys());
            Assert.Single(new MergeTrainAcceptanceStore(database).ReadSuppressedPairs());
            Assert.DoesNotContain($"{goal.Value}:{new string('f', 40)}", reopened.ReadTrainImplicatedCandidateKeys());

            using var connection = new SqliteConnection($"Data Source={database};Pooling=False");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT train_id, observed_main_revision FROM merge_train_implicated_candidates;";
            using (var reader = command.ExecuteReader())
            {
                Assert.True(reader.Read());
                Assert.Equal("red-train", reader.GetString(0));
                Assert.Equal(main, reader.GetString(1));
                Assert.False(reader.Read());
            }
            command.CommandText = "SELECT COUNT(*) FROM merge_train_pair_suppressions;";
            Assert.Equal(1L, command.ExecuteScalar());
            command.CommandText = "SELECT COUNT(*) FROM merge_train_ejections;";
            Assert.Equal(0L, command.ExecuteScalar());
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}
