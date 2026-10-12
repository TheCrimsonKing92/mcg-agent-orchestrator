using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

// Parallel-safe: xUnit serializes cases in this class, which owns its fixture and SQLite root.
public sealed class MergeTrainDeferralFixtureEquivalenceTests(
    MergeTrainDeferralSharedFixture fixture, ITestOutputHelper output)
    : HostCapacityBoundTestBase, IClassFixture<MergeTrainDeferralSharedFixture>
{
    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void Deferral_SharedAndFreshFixtures_ProduceEquivalentResults(int busyRounds)
    {
        var shared = fixture.RunDeferral(busyRounds);
        var fresh = MergeTrainDeferralSharedFixture.RunFreshDeferral(busyRounds);

        Assert.Equal(fresh.Detail, shared.Detail);
        Assert.Equal(fresh.Acquisitions, shared.Acquisitions);
        Assert.Equal(fresh.Delays, shared.Delays);
        Assert.Equal(fresh.Ejections, shared.Ejections);
        Assert.Equal(fresh.OutcomeTypes, shared.OutcomeTypes);
        Assert.Equal(fresh.HeldStates, shared.HeldStates);
        Assert.Equal(fresh.ReceiptIsNull, shared.ReceiptIsNull);
        Assert.Equal(fresh.RecordedReceiptIsNull, shared.RecordedReceiptIsNull);
        Assert.Equal(fresh.ReceiptOutcome, shared.ReceiptOutcome);
        Assert.Equal(fresh.VerifierRunCount, shared.VerifierRunCount);
        // Assert the selected path too: equivalently broken arms must not pass.
        Assert.Equal(3, shared.Acquisitions);
        Assert.Equal(2, shared.Delays);
        Assert.Equal(busyRounds == 3, shared.RecordedReceiptIsNull);
        Assert.Equal(busyRounds == 3 ? 0 : 1, shared.VerifierRunCount);
        Assert.Equal(busyRounds == 3 ? 3 : 0, shared.OutcomeTypes.Length);
        Assert.All(shared.OutcomeTypes, type => Assert.Equal(typeof(ConductorAdvanceOutcome.Held), type));
        Assert.All(shared.HeldStates, state => Assert.Equal(GoalLifecycleState.Verified, state));
    }

    [Fact]
    public void Reset_FiveOrderedScenarios_RestoreStateWithoutRebuilding()
    {
        var original = new MergeTrainGateOnlySlotsBusyDeferralTests(fixture);
        Action[] scenarios = [
            () => original.GateOnlyRetriesThenRecordsReceiptOrDefers(2),
            () => original.GateOnlyRetriesThenRecordsReceiptOrDefers(3),
            () => original.GroupedDeferralRecoversAndRetriesUntilSlotIsFree(false),
            () => original.GroupedDeferralRecoversAndRetriesUntilSlotIsFree(true),
            original.GenuineGateExceptionStillPublishesFailureAndRecoveryReceipt];
        var earlierReceipts = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < scenarios.Length; index++)
        {
            fixture.Reset();
            var baseline = fixture.DirectoryListing();
            AssertEmptyReceiptState();
            var goalsBefore = GoalState();
            scenarios[index]();
            Assert.Equal(goalsBefore, GoalState());

            var receipts = ReadReceiptPayloads();
            Assert.Equal(index == 1 ? 0 : 1, receipts.Length);
            Assert.All(receipts, payload => Assert.DoesNotContain(payload, earlierReceipts));
            foreach (var payload in receipts) earlierReceipts.Add(payload);
            Assert.Equal(new[] { fixture.MainRevision }.Concat(fixture.CandidateRevisions),
                fixture.ReadRevisions());
            AssertNoLandingOrEjectionState();
            if (index != 1)
                Assert.False(baseline.SequenceEqual(fixture.DirectoryListing()),
                    "A recorded receipt must create evidence paths for Reset to remove.");

            // Outcomes are inspected before cleanup; filesystem equality is checked after
            // restoration because successful scenarios necessarily create receipt evidence.
            fixture.Reset();
            AssertEmptyReceiptState();
            Assert.Equal(baseline, fixture.DirectoryListing());
        }
        Assert.Equal(1, fixture.BuildCount);
    }

    [Fact]
    public void Deferral_FullScenario_DoesNotMutateSharedGoalState()
    {
        fixture.Reset();
        var before = GoalState();
        new MergeTrainGateOnlySlotsBusyDeferralTests(fixture)
            .GroupedDeferralRecoversAndRetriesUntilSlotIsFree(true);
        Assert.Equal(before, GoalState());
    }

    [Fact]
    public void Reset_OmittedBetweenSetups_LeavesReceiptUntilRestoration()
    {
        var passed = fixture.RunDeferral(2);
        Assert.Equal(MergeTrainGateOutcome.Passed, passed.ReceiptOutcome);
        var earlier = Assert.Single(ReadReceiptPayloads());

        // Intentional negative control: prepare the next case without resetting or running
        // its gate, so receipt replay cannot hide the earlier row's survival.
        using (fixture.PrepareDeferral(3))
            Assert.Equal(earlier, Assert.Single(ReadReceiptPayloads()));
        fixture.Reset();
        Assert.Empty(ReadReceiptPayloads());
        AssertEmptyReceiptState();
    }

    [Fact]
    public void Reset_AfterMeasuredFixtureBuild_LaunchesNoGitProbes()
    {
        Assert.True(fixture.BuildGitProbeLaunchCount >= 11);
        output.WriteLine($"Fixture-build Git-probe launch count: {fixture.BuildGitProbeLaunchCount}; " +
            "before: once per case (5 builds); after: once per class (1 build). " +
            "This is a probe count, not a timing or a census of production GitCli launches.");
        using var launches = InfrastructureTestSupport.CountGitProbeLaunches();
        fixture.Reset();
        Assert.Equal(0, launches.Count);
        Assert.Equal(1, fixture.BuildCount);
    }

    private (GoalStatus Status, int Tasks, int Verifications)[] GoalState() => fixture.Goals
        .Select(goal => (goal.Status, goal.Tasks.Count,
            goal.Tasks.Sum(task => task.VerificationHistory.Count))).ToArray();

    private string[] ReadReceiptPayloads()
    {
        using var connection = OpenDatabase();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT payload_json FROM merge_train_receipts ORDER BY train_id;";
        using var reader = command.ExecuteReader();
        var payloads = new List<string>();
        while (reader.Read()) payloads.Add(reader.GetString(0));
        return payloads.ToArray();
    }

    private void AssertEmptyReceiptState()
    {
        Assert.Equal(0L, CountRows("merge_train_receipts"));
        Assert.Equal(0L, CountRows("merge_train_receipt_members"));
        AssertNoLandingOrEjectionState();
    }

    private void AssertNoLandingOrEjectionState()
    {
        Assert.Equal(0L, CountRows("merge_train_landings"));
        Assert.Equal(0L, CountRows("merge_train_ejections"));
        Assert.Equal(0L, CountRows("merge_train_implicated_candidates"));
        Assert.Equal(0L, CountRows("merge_train_pair_suppressions"));
    }

    private long CountRows(string table)
    {
        using var connection = OpenDatabase();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table};";
        return (long)command.ExecuteScalar()!;
    }

    private SqliteConnection OpenDatabase()
    {
        var connection = new SqliteConnection($"Data Source={fixture.DatabasePath};Pooling=False");
        connection.Open();
        return connection;
    }
}
