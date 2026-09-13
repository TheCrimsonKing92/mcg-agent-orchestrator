using Mcg.AgentOrchestrator.App.Orchestration;

/// <summary>
/// The durable failing-test census: appended at every gate completion, read at classification. It is
/// an accelerator, so every read path tolerates damage and every write path is best effort.
/// </summary>
public sealed class AcceptanceFailingTestIndexTests
{
    private const string Identity = "Example.Tests.FlakyTests.RacesOnHeartbeat";

    [Fact(DisplayName = "AcceptanceFailingTestIndex_appends_and_reads_back_census_records")]
    public void AppendsAndReadsBackCensusRecords()
    {
        var root = SharedTestSupport.CreateTempDirectory();
        try
        {
            var now = DateTimeOffset.Parse("2026-09-05T12:00:00Z", null);
            var index = CreateIndex(root);
            index.Append([Census("goal-a", now, Identity)], now);
            index.Append([Census("goal-b", now, Identity)], now);

            var records = index.Read();

            Assert.Equal(2, records.Count);
            Assert.All(records, record => Assert.Equal(Identity, record.TestIdentity));
            Assert.All(records, record =>
                Assert.Equal(AcceptanceFailingTestIndexKinds.GateFailure, record.Kind));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact(DisplayName = "AcceptanceFailingTestIndex_cross_goal_lookup_excludes_the_asking_goal")]
    public void CrossGoalLookupExcludesTheAskingGoalAndCandidateOwnedFailures()
    {
        var now = DateTimeOffset.Parse("2026-09-05T12:00:00Z", null);
        var window = TimeSpan.FromHours(72);
        var sameGoal = new[] { Census("goal-a", now.AddHours(-1), Identity) };
        var candidateOwned = new[] { Census("goal-b", now.AddHours(-1), Identity) with { InsideChangedPaths = true } };
        var otherGoal = new[] { Census("goal-b", now.AddHours(-1), Identity) };

        Assert.False(AcceptanceFailingTestIndex.HasCrossGoalOccurrence(sameGoal, "goal-a", Identity, now, window));
        Assert.False(AcceptanceFailingTestIndex.HasCrossGoalOccurrence(candidateOwned, "goal-a", Identity, now, window));
        Assert.True(AcceptanceFailingTestIndex.HasCrossGoalOccurrence(otherGoal, "goal-a", Identity, now, window));
    }

    [Theory(DisplayName = "AcceptanceFailingTestIndex_cross_goal_lookup_pins_both_window_edges")]
    [InlineData(-71, true)]
    [InlineData(-73, false)]
    public void CrossGoalLookupPinsBothWindowEdges(int ageHours, bool expected)
    {
        var now = DateTimeOffset.Parse("2026-09-05T12:00:00Z", null);
        var records = new[] { Census("goal-b", now.AddHours(ageHours), Identity) };

        Assert.Equal(
            expected,
            AcceptanceFailingTestIndex.HasCrossGoalOccurrence(
                records,
                "goal-a",
                Identity,
                now,
                TimeSpan.FromHours(72)));
    }

    [Fact(DisplayName = "AcceptanceFailingTestIndex_tolerates_a_corrupt_line")]
    public void ToleratesCorruptLineWithoutHidingTheRestOfTheCensus()
    {
        var root = SharedTestSupport.CreateTempDirectory();
        try
        {
            var now = DateTimeOffset.Parse("2026-09-05T12:00:00Z", null);
            var index = CreateIndex(root);
            index.Append([Census("goal-a", now, Identity)], now);
            File.AppendAllText(index.Path, "{ not json" + Environment.NewLine);
            index.Append([Census("goal-b", now, Identity)], now);

            var records = index.Read();

            Assert.Equal(2, records.Count);
            Assert.Contains(records, record => record.GoalId.Equals("goal-b", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact(DisplayName = "AcceptanceFailingTestIndex_prunes_census_records_but_never_regate_records")]
    public void PrunesStaleCensusRecordsButNeverRegateRecords()
    {
        var root = SharedTestSupport.CreateTempDirectory();
        try
        {
            var now = DateTimeOffset.Parse("2026-09-05T12:00:00Z", null);
            var index = new AcceptanceFailingTestIndex(
                Path.Combine(root, AcceptanceFailingTestIndex.FileName),
                censusRetention: TimeSpan.FromHours(24));
            index.Append(
                [
                    Census("goal-a", now.AddDays(-30), Identity),
                    Regate("goal-a", now.AddDays(-30))
                ],
                now.AddDays(-30));

            // Pruning happens on write, so a later append is what evicts the stale census record.
            index.Append([Census("goal-b", now, Identity)], now);

            var records = index.Read();

            Assert.DoesNotContain(
                records,
                record => record.Kind.Equals(AcceptanceFailingTestIndexKinds.GateFailure, StringComparison.Ordinal) &&
                    record.GoalId.Equals("goal-a", StringComparison.Ordinal));

            // Pruning a re-gate record by age would silently reset the per-goal bound.
            Assert.Equal(1, AcceptanceFailingTestIndex.CountRegates(records, "goal-a"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact(DisplayName = "AcceptanceFailingTestIndex_counts_regates_per_goal")]
    public void CountsRegatesPerGoal()
    {
        var now = DateTimeOffset.Parse("2026-09-05T12:00:00Z", null);
        var records = new[]
        {
            Regate("goal-a", now),
            Regate("goal-a", now.AddMinutes(5)),
            Regate("goal-b", now),
            Census("goal-a", now, Identity)
        };

        Assert.Equal(2, AcceptanceFailingTestIndex.CountRegates(records, "goal-a"));
        Assert.Equal(1, AcceptanceFailingTestIndex.CountRegates(records, "goal-b"));
        Assert.Equal(0, AcceptanceFailingTestIndex.CountRegates(records, "goal-c"));
    }

    [Fact(DisplayName = "AcceptanceFailingTestIndex_survives_concurrent_appends")]
    public void SurvivesConcurrentAppends()
    {
        var root = SharedTestSupport.CreateTempDirectory();
        try
        {
            var now = DateTimeOffset.Parse("2026-09-05T12:00:00Z", null);
            var index = CreateIndex(root);
            using var ready = new CountdownEvent(2);
            using var start = new ManualResetEventSlim(false);
            var writers = Enumerable.Range(0, 2).Select(writerIndex => new Thread(() =>
            {
                ready.Signal();
                start.Wait();
                for (var append = 0; append < 20; append++)
                {
                    index.Append([Census($"goal-{writerIndex}", now, Identity)], now);
                }
            })).ToArray();
            foreach (var writer in writers)
            {
                writer.Start();
            }

            ready.Wait();
            start.Set();
            foreach (var writer in writers)
            {
                Assert.True(writer.Join(TimeSpan.FromSeconds(30)));
            }

            var records = index.Read();

            Assert.Equal(40, records.Count);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact(DisplayName = "AcceptanceFailingTestIndex_ignores_records_from_another_contract_version")]
    public void IgnoresRecordsFromAnotherContractVersion()
    {
        var root = SharedTestSupport.CreateTempDirectory();
        try
        {
            var now = DateTimeOffset.Parse("2026-09-05T12:00:00Z", null);
            var index = CreateIndex(root);
            index.Append([Census("goal-a", now, Identity) with { ContractVersion = 99 }], now);

            Assert.Empty(index.Read());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static AcceptanceFailingTestIndex CreateIndex(string root) =>
        new(Path.Combine(root, "acceptance-gate-attempts", AcceptanceFailingTestIndex.FileName));

    private static AcceptanceFailingTestIndexRecord Census(
        string goalId,
        DateTimeOffset recordedAt,
        string identity) =>
        new(
            AcceptanceFailingTestIndex.ContractVersion,
            AcceptanceFailingTestIndexKinds.GateFailure,
            goalId,
            recordedAt,
            CheckName: "infrastructure tests: lane",
            TestIdentity: identity,
            ResolvedSourcePath: "tests/Example.Tests/FlakyTests.cs",
            InsideChangedPaths: false);

    private static AcceptanceFailingTestIndexRecord Regate(string goalId, DateTimeOffset recordedAt) =>
        new(
            AcceptanceFailingTestIndex.ContractVersion,
            AcceptanceFailingTestIndexKinds.ApparatusRegate,
            goalId,
            recordedAt,
            TestIdentity: Identity,
            EvidenceKind: ApparatusInfrastructureSignatures.EvidenceKind);
}
