using System.Text.Json;
using Mcg.AgentOrchestrator.App.Simulation;

public sealed class GateSchedulingSimulatorTests
{
    [Fact]
    public void Current_policy_replays_F1_exactly_and_repeats_with_the_same_seed()
    {
        var trace = BoardSimulationFixture.F1();
        var replay = GateSchedulingSimulator.Simulate(trace, GateSchedulingPolicy.Current);
        Assert.Equal(4, replay.Gates.Count);
        for (var i = 0; i < trace.GateRuns.Count; i++)
        {
            var expected = trace.GateRuns[i];
            var actual = replay.Gates[i];
            Assert.Equal(expected.Kind, actual.Kind);
            Assert.Equal(expected.Members.Count, actual.Members.Count);
            for (var memberIndex = 0; memberIndex < expected.Members.Count; memberIndex++)
            {
                var expectedMember = expected.Members[memberIndex];
                var actualMember = actual.Members[memberIndex];
                Assert.Equal(expectedMember.GoalId, actualMember.GoalId);
                Assert.Equal(expectedMember.CandidateRevision, actualMember.CandidateRevision);
                Assert.Equal(expectedMember.LandingPaths, actualMember.LandingPaths);
                Assert.Equal(expectedMember.ResourceKeys, actualMember.ResourceKeys);
            }
            Assert.Equal(expected.BeginAt, actual.BeginAt);
            Assert.Equal(expected.EndAt, actual.EndAt);
            Assert.Equal(expected.Outcome, actual.Outcome);
            Assert.Equal(SimulatedGateSource.Recorded, actual.Source);
        }
        Assert.Equal(trace.Landings.Select(l => (l.GoalId, l.LandedAt)), replay.Landings.Select(l => (l.GoalId, l.LandedAt)));
        Assert.Equal(110, replay.TotalGateMinutes);
        var calibration = BoardSimulationCalibration.Compare(trace, replay);
        Assert.Equal("within-bar", calibration.Verdict);
        Assert.Equal(0, calibration.MaxAbsDeltaMinutes);
        Assert.Equal(JsonSerializer.Serialize(replay), JsonSerializer.Serialize(GateSchedulingSimulator.Simulate(trace, GateSchedulingPolicy.Current)));
    }

    [Fact]
    public void Width_two_samples_A_solo_and_BC_cohort_with_even_median_duration()
    {
        var replay = GateSchedulingSimulator.Simulate(BoardSimulationFixture.F1(), GateSchedulingPolicy.Current with { Width = 2 });
        AssertGate(replay.Gates[1], GateRunKind.Solo, "A", "01:00", "01:35", SimulatedGateSource.Sampled);
        AssertGate(replay.Gates[2], GateRunKind.Cohort, "BC", "01:10", "01:30", SimulatedGateSource.Sampled);
        AssertLandings(replay, [('B', "01:32"), ('C', "01:32"), ('A', "01:37"), ('E', "03:52")]);
        Assert.Equal(145, replay.TotalGateMinutes);
    }

    [Fact]
    public void Trains_off_uses_AB_cohort_then_C_solo()
    {
        var replay = GateSchedulingSimulator.Simulate(BoardSimulationFixture.F1(), GateSchedulingPolicy.Current with { TrainSize = 0 });
        AssertGate(replay.Gates[1], GateRunKind.Cohort, "AB", "01:10", "01:30", SimulatedGateSource.Sampled);
        AssertGate(replay.Gates[2], GateRunKind.Solo, "C", "01:30", "02:05", SimulatedGateSource.Sampled);
        AssertLandings(replay, [('A', "01:32"), ('B', "01:32"), ('C', "02:07"), ('E', "03:52")]);
        Assert.DoesNotContain(replay.Gates, g => g.Kind == GateRunKind.Train);
    }

    [Theory]
    [InlineData(false, "GH")]
    [InlineData(true, "HI")]
    public void First_failed_train_drops_by_policy_and_immediately_regates_the_remainder(bool lowest, string expected)
    {
        var replay = GateSchedulingSimulator.Simulate(BoardSimulationFixture.Drops(), GateSchedulingPolicy.Current with
        { DropRule = lowest ? GateDropRule.LowestPassRate : GateDropRule.Newest });
        Assert.Equal(GateRunKind.Train, replay.Gates[1].Kind);
        Assert.Equal(expected.Select(BoardSimulationFixture.Id), replay.Gates[1].Members.Select(m => m.GoalId));
        Assert.Equal(BoardSimulationFixture.At("05:20"), replay.Gates[1].BeginAt);
    }

    [Fact]
    public void Carried_gate_holds_slot_and_consumes_its_epoch()
    {
        var original = BoardSimulationFixture.F1();
        var trace = original with { WindowStart = BoardSimulationFixture.At("01:00") };
        var replay = GateSchedulingSimulator.Simulate(trace, GateSchedulingPolicy.Current);
        AssertGate(replay.Gates[0], GateRunKind.Solo, "F", "00:20", "01:10", SimulatedGateSource.CarriedIn);
        Assert.Equal(4, replay.Gates.Count);
        Assert.Equal(BoardSimulationFixture.At("01:10"), replay.Gates[1].BeginAt);
    }

    [Fact]
    public void Infrastructure_returns_members_and_consumed_records_cannot_be_reused()
    {
        var original = BoardSimulationFixture.F1();
        var gate = BoardSimulationFixture.Gate(GateRunKind.Solo, "A", "01:00", "01:10", GateRunOutcome.Infrastructure);
        var pass = gate with { BeginAt = gate.EndAt, EndAt = gate.EndAt.AddMinutes(10), Outcome = GateRunOutcome.Passed };
        var trace = original with { GateRuns = [gate, pass], Epochs = [BoardSimulationFixture.Epoch('A', "01:00")],
            Landings = [new(BoardSimulationFixture.Id('A'), BoardSimulationFixture.At("01:22"))] };
        var replay = GateSchedulingSimulator.Simulate(trace, GateSchedulingPolicy.Current);
        Assert.Equal(2, replay.Gates.Count);
        Assert.Equal(GateRunOutcome.Infrastructure, replay.Gates[0].Outcome);
        Assert.Equal(GateRunOutcome.Passed, replay.Gates[1].Outcome);
        Assert.Equal(BoardSimulationFixture.At("01:22"), Assert.Single(replay.Landings).LandedAt);
    }

    [Fact]
    public void Epoch_leaves_and_replacement_apply_before_admission_at_the_same_instant()
    {
        var trace = BoardSimulationFixture.F1();
        var replacement = BoardSimulationFixture.Epoch('A', "01:10") with { CandidateRevision = new string('2', 40) };
        trace = trace with { GateRuns = [trace.GateRuns[0]], Landings = [],
            Epochs = [trace.Epochs[0], BoardSimulationFixture.Epoch('A', "01:00", "01:10"), replacement] };
        var replay = GateSchedulingSimulator.Simulate(trace, GateSchedulingPolicy.Current);
        Assert.Equal(2, replay.Gates.Count);
        Assert.Equal(replacement.CandidateRevision, replay.Gates[1].Members.Single().CandidateRevision);
        Assert.Equal(BoardSimulationFixture.At("01:10"), replay.Gates[1].BeginAt);
    }

    [Fact]
    public void Reused_pass_without_a_landing_reenters_pool_then_samples_once()
    {
        var pass = BoardSimulationFixture.Gate(GateRunKind.Solo, "A", "01:00", "01:10", GateRunOutcome.Passed);
        var trace = BoardSimulationFixture.F1() with { GateRuns = [pass], Epochs = [BoardSimulationFixture.Epoch('A', "01:00")], Landings = [] };
        var replay = GateSchedulingSimulator.Simulate(trace, GateSchedulingPolicy.Current);
        Assert.Equal([SimulatedGateSource.Recorded, SimulatedGateSource.Sampled], replay.Gates.Select(g => g.Source));
        Assert.Equal(BoardSimulationFixture.At("01:20"), Assert.Single(replay.Landings).LandedAt);
    }

    private static void AssertGate(SimulatedGate gate, GateRunKind kind, string members, string begin, string end, SimulatedGateSource source)
    {
        Assert.Equal(kind, gate.Kind);
        Assert.Equal(members.Select(BoardSimulationFixture.Id), gate.Members.Select(m => m.GoalId));
        Assert.Equal(BoardSimulationFixture.At(begin), gate.BeginAt);
        Assert.Equal(BoardSimulationFixture.At(end), gate.EndAt);
        Assert.Equal(source, gate.Source);
    }

    private static void AssertLandings(BoardSimulationResult result, (char Goal, string At)[] expected) =>
        Assert.Equal(expected.Select(e => (BoardSimulationFixture.Id(e.Goal), BoardSimulationFixture.At(e.At))),
            result.Landings.Select(l => (l.GoalId, l.LandedAt)));
}
