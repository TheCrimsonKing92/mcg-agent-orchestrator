using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;

// Parallel-safe: each case owns its SQLite database.
public sealed class ExperimentStoreTests
{
    [Fact]
    public void ListOpen_ExcludesDecidedRecordsAndPreservesOrderAndDatabaseBytes()
    {
        var root = Path.Combine(Path.GetTempPath(), "experiment-open-list-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "experiments.db");
            var store = new ExperimentStore(path);
            Assert.Empty(store.ListOpenAsync().GetAwaiter().GetResult());
            var spec = new ExperimentSpec("Trial", new(ExperimentInterventionKind.Policy, "Measure a policy"),
                new(ExperimentBaselineKind.TwinGoal, TwinGoalId: "twin"), ["rounds-per-landing"],
                new("productive-rounds", new("productive-rounds", "<", -10)), new(1, ExperimentStopUnit.Goals),
                new([new("rounds-per-landing", "<", 0)], [new("rounds-per-landing", ">", 0)]));
            var records = Enumerable.Range(0, 3).Select(i => store.AddAsync(spec with
                { Hypothesis = $"Trial {i}" }).GetAwaiter().GetResult()).ToArray();
            store.DecideAsync(records[1].Id, ExperimentOutcomeState.Confirmed, "receipt:open-list", "Keep").GetAwaiter().GetResult();
            var before = File.ReadAllBytes(path);
            var open = store.ListOpenAsync().GetAwaiter().GetResult();
            Assert.Equal(before, File.ReadAllBytes(path));
            var expected = new[] { records[0], records[2] }.OrderBy(record => record.CreatedAt)
                .ThenBy(record => record.Id, StringComparer.Ordinal).ToArray();
            Assert.Equal(expected.Select(record => record.Id), open.Select(record => record.Id));
            Assert.All(open, record => Assert.Equal(ExperimentOutcomeState.Open, record.Outcome));
            Assert.Equal(JsonSerializer.Serialize(expected, ExperimentStore.JsonOptions), JsonSerializer.Serialize(open, ExperimentStore.JsonOptions));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ListAll_ReturnsEveryFullRecordInCreationThenIdOrderWithoutWriting()
    {
        var root = Path.Combine(Path.GetTempPath(), "experiment-list-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "experiments.db");
            var store = new ExperimentStore(path);
            var emptyBefore = File.ReadAllBytes(path);
            Assert.Empty(await store.ListAllAsync());
            Assert.Equal(emptyBefore, File.ReadAllBytes(path));
            var spec = new ExperimentSpec("Measure a manual policy",
                new(ExperimentInterventionKind.Policy, "Change the brief"),
                new(ExperimentBaselineKind.TwinGoal, TwinGoalId: "twin"), ["rounds-per-landing"],
                new("productive-rounds", new("productive-rounds", "<", -10)), new(1, ExperimentStopUnit.Goals),
                new([new("rounds-per-landing", "<", 0)], [new("rounds-per-landing", ">", 0)]));
            var late = await store.AddAsync(spec with { Hypothesis = "Later experiment", EpicId = "epic-id" });
            var firstTie = await store.AddAsync(spec with { Hypothesis = "First tied experiment" });
            var secondTie = await store.AddAsync(spec with { Hypothesis = "Second tied experiment" });
            await store.DecideAsync(firstTie.Id, ExperimentOutcomeState.Refuted, "receipt:list", "Revert");
            // Fix creation timestamps to prove ordering even when insertion order differs and dates tie.
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = path, Pooling = false }.ToString()))
            {
                await connection.OpenAsync();
                using var command = connection.CreateCommand();
                command.CommandText = "UPDATE experiments SET created_at=CASE WHEN id=$late THEN $later ELSE $earlier END";
                command.Parameters.AddWithValue("$late", late.Id);
                command.Parameters.AddWithValue("$later", "2026-10-02T00:00:00.0000000+00:00");
                command.Parameters.AddWithValue("$earlier", "2026-10-01T00:00:00.0000000+00:00");
                await command.ExecuteNonQueryAsync();
            }
            var before = File.ReadAllBytes(path);
            var listed = await store.ListAllAsync();
            Assert.Equal(before, File.ReadAllBytes(path));
            var tiedIds = new[] { firstTie.Id, secondTie.Id }.OrderBy(id => id, StringComparer.Ordinal).ToArray();
            Assert.Equal(new[] { tiedIds[0], tiedIds[1], late.Id }, listed.Select(record => record.Id));
            Assert.Equal(3, listed.Count);
            foreach (var record in listed)
            {
                var resolved = await store.ResolveAsync(record.Id);
                Assert.Equal(JsonSerializer.Serialize(resolved, ExperimentStore.JsonOptions),
                    JsonSerializer.Serialize(record, ExperimentStore.JsonOptions));
            }
            var decided = Assert.Single(listed.Where(record => record.Id == firstTie.Id));
            Assert.Equal(ExperimentOutcomeState.Refuted, decided.Outcome);
            Assert.Equal("receipt:list", decided.Decision!.Evidence);
            Assert.Equal("epic-id", listed[2].Spec.EpicId);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task FullRecord_RoundTripsEveryFieldAndDecidedOutcome()
    {
        var root = Path.Combine(Path.GetTempPath(), "experiment-store-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "experiments.db");
            var spec = new ExperimentSpec("An evidence-only spike reduces rounds",
                new(ExperimentInterventionKind.EvidenceOnlyCodeSpike, "Measure the brief preamble"),
                new(ExperimentBaselineKind.BeforeAfterWindow, DateTimeOffset.Parse("2026-10-01T00:00:00Z"),
                    DateTimeOffset.Parse("2026-10-02T00:00:00Z"), "twin-reference"),
                ["rounds-per-landing", "wasted-rounds", "landings-per-hour"],
                new("productive-rounds", new("productive-rounds", "<", -10)),
                new(5, ExperimentStopUnit.Goals),
                new([new("rounds-per-landing", "<=", -10), new("landings-per-hour", ">", 5)],
                    [new("wasted-rounds", ">=", 20)]), "epic-id");
            var store = new ExperimentStore(path);
            var added = await store.AddAsync(spec);
            await store.DecideAsync(added.Id, ExperimentOutcomeState.Confirmed, "receipt:trial", "Keep the brief");
            var decided = (await store.ResolveAsync(added.Id))!;
            var loaded = (await new ExperimentStore(path).ResolveAsync(added.Id[..8]))!;
            Assert.Equal(added.Id, loaded.Id);
            Assert.Equal(added.CreatedAt, loaded.CreatedAt);
            // JSON structural comparison covers every nested field and each list element, not list reference equality.
            Assert.Equal(JsonSerializer.Serialize(spec, ExperimentStore.JsonOptions),
                JsonSerializer.Serialize(loaded.Spec, ExperimentStore.JsonOptions));
            Assert.Equal(ExperimentOutcomeState.Confirmed, loaded.Outcome);
            Assert.Equal(decided.Decision, loaded.Decision);
            Assert.Equal("receipt:trial", loaded.Decision!.Evidence);
            Assert.Equal("Keep the brief", loaded.Decision.Action);
            Assert.Equal(ExperimentOutcomeState.Confirmed, loaded.Decision.Outcome);
            Assert.NotEqual(default, loaded.Decision.DecidedAt);
            Assert.Equal(1, await store.CountAsync());
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ConcurrentDecisions_OnlyOneConditionalUpdateSucceeds()
    {
        var root = Path.Combine(Path.GetTempPath(), "experiment-decide-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "experiments.db");
            var store = new ExperimentStore(path);
            var record = await store.AddAsync(new ExperimentSpec("One decision per experiment",
                new(ExperimentInterventionKind.Policy, "Compare a manual policy"),
                new(ExperimentBaselineKind.TwinGoal, TwinGoalId: "twin"), ["rounds-per-landing"],
                new("productive-rounds", new("productive-rounds", "<", -10)), new(1, ExperimentStopUnit.Goals),
                new([new("rounds-per-landing", "<", 0)], [new("rounds-per-landing", ">", 0)])));
            using var start = new ManualResetEventSlim();
            Task<bool> Attempt(ExperimentOutcomeState outcome) => Task.Run(async () =>
            {
                start.Wait();
                try { await new ExperimentStore(path).DecideAsync(record.Id, outcome, outcome.ToString(), outcome.ToString()); return true; }
                catch (InvalidOperationException) { return false; }
            });
            var first = Attempt(ExperimentOutcomeState.Confirmed);
            var second = Attempt(ExperimentOutcomeState.Refuted);
            start.Set();
            var results = await Task.WhenAll(first, second);
            Assert.Single(results.Where(result => result));
            var decided = (await store.ResolveAsync(record.Id))!;
            Assert.NotEqual(ExperimentOutcomeState.Open, decided.Outcome);
            Assert.Equal(decided.Outcome.ToString(), decided.Decision!.Evidence);
            Assert.Equal(decided.Outcome.ToString(), decided.Decision.Action);
        }
        finally { Directory.Delete(root, true); }
    }
}
