using System.Text.Json;
using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: fixed clocks, in-memory state and a unique filesystem root per fixture.
public sealed class OwnerLandingsQueryTests
{
    [Fact]
    public void Render_PromotionFacts_ShowsEachDeliveryAndSharedCohortRevision()
    {
        using var fixture = new Fixture();
        fixture.Add("10000001", fixture.Clock.Now, GoalPromotionFact.SoloProvenance);
        fixture.Add("20000002", fixture.Clock.Now.AddMinutes(-1), GoalPromotionFact.MergeTrainProvenance);
        fixture.Add("30000003", fixture.Clock.Now.AddMinutes(-2), GoalPromotionFact.CohortProvenance, new string('d', 40));
        fixture.Add("40000004", fixture.Clock.Now.AddMinutes(-2), GoalPromotionFact.CohortProvenance, new string('d', 40));

        var result = fixture.Load();
        var lines = OwnerLandingsQuery.Render(result);

        Assert.Equal(4, result.Rows.Count);
        Assert.Contains("10000001  2026-10-16 12:00 local  ccccccccc  solo  current title: Goal 10000001", lines);
        Assert.Contains("20000002  2026-10-16 11:59 local  ccccccccc  merge train  current title: Goal 20000002", lines);
        Assert.Contains("30000003  2026-10-16 11:58 local  ddddddddd  cohort  current title: Goal 30000003", lines);
        Assert.Contains("40000004  2026-10-16 11:58 local  ddddddddd  cohort  current title: Goal 40000004", lines);
        Assert.DoesNotContain(lines, line => line.Contains("Objective body", StringComparison.Ordinal));
        Assert.Equal("Undated: 0 landed goals have no recorded landing time", lines[^1]);
    }

    internal sealed class Fixture : IOrchestratorStateQueries, IDisposable
    {
        internal const string Candidate = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        internal string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "owner-landings-" + Guid.NewGuid().ToString("N"));
        internal OwnerConsoleTestClock Clock { get; } = new()
        {
            Now = new(2026, 10, 16, 18, 0, 0, TimeSpan.Zero),
            Zone = TimeZoneInfo.CreateCustomTimeZone("LandingsTest", TimeSpan.FromHours(-6), "LandingsTest", "LandingsTest")
        };
        internal List<GoalSummary> Goals { get; } = [];
        internal int Reads { get; private set; }
        internal Exception? ReadError { get; set; }

        internal Fixture() => Directory.CreateDirectory(DirectoryPath);

        internal GoalSummary Add(string prefix, DateTimeOffset at,
            string kind = GoalPromotionFact.SoloProvenance, string? promoted = null,
            string status = nameof(GoalStatus.Completed))
        {
            var id = prefix.PadRight(32, '0');
            var fact = new GoalPromotionFact(id, Candidate, new string('b', 40),
                promoted ?? new string('c', 40), at.ToUniversalTime(), kind);
            var goal = new GoalSummary(id, status, "# Goal " + prefix + "\nObjective body",
                Clock.Now.ToString("O"), PromotionFact: fact);
            Goals.Add(goal);
            return goal;
        }

        internal string WriteAttempt(GoalSummary goal, string id, DateTimeOffset at,
            string candidate = Candidate, string detail = "acceptance passed", string kind = "gate")
        {
            var folder = Path.Combine(DirectoryPath, "acceptance-gate-attempts", goal.Id);
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, id + ".attempt.json");
            File.WriteAllText(path, JsonSerializer.Serialize(new
            { attemptId = id, kind, branchHeadSha = candidate, completedAt = at, detail }));
            return path;
        }

        internal OwnerLandingsQuery.Result Load(string argument = "") =>
            OwnerLandingsQuery.Load(this, DirectoryPath, Clock, argument);
        public Task<IReadOnlyList<GoalSummary>> ListGoalMetadataAsync(CancellationToken cancellationToken = default)
        {
            Reads++;
            return ReadError is { } error ? Task.FromException<IReadOnlyList<GoalSummary>>(error)
                : Task.FromResult<IReadOnlyList<GoalSummary>>(Goals);
        }
        public Task<AgentOrchestratorKernel> LoadGoalsAsync(IReadOnlyCollection<GoalId> goalIds,
            CancellationToken cancellationToken = default) => throw new InvalidOperationException("metadata only");
        public void Dispose() => Directory.Delete(DirectoryPath, true);
    }
}
