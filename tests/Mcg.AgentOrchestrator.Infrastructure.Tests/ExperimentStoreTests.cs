using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: each case owns its SQLite database.
public sealed class ExperimentStoreTests
{
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
