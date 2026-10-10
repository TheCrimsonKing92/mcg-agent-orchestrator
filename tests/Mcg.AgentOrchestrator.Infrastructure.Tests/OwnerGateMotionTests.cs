using Mcg.AgentOrchestrator.App.OwnerConsole;

// Parallel-safe: all samples and timestamps are local and fixed.
public sealed class OwnerGateMotionTests
{
    private const string Goal = "57cf8f07be1944ce91efaa1b16dd85e0";
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void GrowingBytesAcrossTwentyMinutesAreMoving()
    {
        var samples = Enumerable.Range(1, 20).Select(minutes => Sample(minutes, 2000 - minutes)).ToArray();
        Assert.Equal("output moving", OwnerGateMotion.Describe(Goal, Now, samples));
    }

    [Fact]
    public void IdenticalOutputAcrossFourteenMinutesHasNotChanged()
    {
        Assert.Equal("no output change 14m", OwnerGateMotion.Describe(Goal, Now, Unchanged(14)));
    }

    [Fact]
    public void FourMinutesOfIdenticalOutputAreInsufficient()
    {
        Assert.Null(OwnerGateMotion.Describe(Goal, Now, Unchanged(4)));
    }

    [Fact]
    public void FiveMinuteOldNewestSampleHasNoProgressReport()
    {
        Assert.Equal("no progress report 5m", OwnerGateMotion.Describe(Goal, Now, [Sample(5)]));
    }

    [Fact]
    public void OtherGoalsCannotMakeUnchangedOutputLookMoving()
    {
        var samples = Unchanged(14).SelectMany((sample, index) => new[]
        {
            sample,
            Sample(index + 1.5, index, goal: "a1b2c3d4")
        }).ToArray();
        Assert.Equal("no output change 14m", OwnerGateMotion.Describe(Goal, Now, samples));
    }

    [Fact]
    public void PhaseChangeWithEqualBytesIsMovement()
    {
        var samples = Unchanged(14).Append(Sample(2.5, phase: "build")).ToArray();
        Assert.Equal("output moving", OwnerGateMotion.Describe(Goal, Now, samples));
    }

    [Theory]
    [InlineData("456")]
    [InlineData("unknown")]
    public void ChildPidChangeWithEqualBytesIsMovement(string pid)
    {
        var samples = Unchanged(14).Append(Sample(2.5, pid: pid)).ToArray();
        Assert.Equal("output moving", OwnerGateMotion.Describe(Goal, Now, samples));
    }

    [Fact]
    public void MissingOutputBytesIsIgnoredWithoutSplittingTheUnchangedRun()
    {
        var malformed = new OwnerConductEvent(Now.AddMinutes(-2.5), "gate-progress", "57cf8f07",
            "PHASE_PROGRESS phase=build target=\"other lane\" child_pid=456");
        Assert.Equal("no output change 14m",
            OwnerGateMotion.Describe(Goal, Now, Unchanged(14).Append(malformed).ToArray()));
    }

    [Theory]
    [InlineData("phase")]
    [InlineData("target")]
    [InlineData("child_pid")]
    public void EveryMotionFieldIsRequired(string missing)
    {
        var fields = new Dictionary<string, string>
        { ["phase"] = "test", ["target"] = "\"lane with spaces\"", ["child_pid"] = "123", ["output_bytes"] = "3165" };
        fields.Remove(missing);
        var detail = string.Join(" ", fields.Select(field => $"{field.Key}={field.Value}"));
        Assert.Null(OwnerGateMotion.Describe(Goal, Now, [new(Now.AddMinutes(-5), "gate-progress", "57cf8f07", detail)]));
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("3165bad")]
    [InlineData("999999999999999999999")]
    public void InvalidByteCountsAreIgnored(string bytes)
    {
        var sample = Sample(5) with { Detail = $"phase=test target=\"lane\" child_pid=123 output_bytes={bytes}" };
        Assert.Null(OwnerGateMotion.Describe(Goal, Now, [sample]));
    }

    [Fact]
    public void TargetChangeAndDecreasingBytesEachCountAsMovement()
    {
        Assert.Equal("output moving", OwnerGateMotion.Describe(Goal, Now,
            [Sample(1), Sample(2) with { Detail = "phase=test target=\"another lane\" child_pid=123 output_bytes=3165" }]));
        Assert.Equal("output moving", OwnerGateMotion.Describe(Goal, Now, [Sample(1, 10), Sample(2, 20)]));
    }

    [Fact]
    public void MovementOutsideWindowDoesNotOverrideUnchangedRun()
    {
        Assert.Equal("no output change 14m", OwnerGateMotion.Describe(Goal, Now,
            Unchanged(14).Append(Sample(15, 99)).ToArray()));
    }

    [Fact]
    public void ThresholdsUseStrictReportAgeAndInclusiveMotionWindow()
    {
        Assert.Null(OwnerGateMotion.Describe(Goal, Now, [Sample(3)]));
        Assert.Equal("output moving", OwnerGateMotion.Describe(Goal, Now, [Sample(1), Sample(11, 99)]));
        Assert.Equal("no output change 11m", OwnerGateMotion.Describe(Goal, Now, [Sample(1), Sample(11)]));
        Assert.Null(OwnerGateMotion.Describe(Goal, Now, [Sample(1), Sample(11.1, 99)]));
    }

    [Fact]
    public void SortingAndPrefixFilteringUseOnlyThisGoalsProgress()
    {
        Assert.Equal("output moving", OwnerGateMotion.Describe(" " + Goal + " ", Now,
            [Sample(2, 99) with { GoalId = " 57cf8f07 " }, Sample(1)]));
        Assert.Null(OwnerGateMotion.Describe(" ", Now, [Sample(5)]));
        Assert.Null(OwnerGateMotion.Describe("57cf", Now, [Sample(5)]));
        Assert.Null(OwnerGateMotion.Describe(Goal, Now, [Sample(5) with { GoalId = " " }]));
        Assert.Null(OwnerGateMotion.Describe(Goal, Now, [Sample(5) with { EventKind = "remote-lane" }]));
        Assert.Null(OwnerGateMotion.Describe(Goal, Now, []));
    }

    private static OwnerConductEvent[] Unchanged(int minutes) =>
        Enumerable.Range(1, minutes).Select(minutesAgo => Sample(minutesAgo)).ToArray();

    private static OwnerConductEvent Sample(double minutesAgo, long bytes = 3165, string phase = "test",
        string pid = "123", string goal = "57cf8f07") => new(Now.AddMinutes(-minutesAgo), "gate-progress", goal,
            $"PHASE_PROGRESS goal={goal} phase={phase} elapsed_ms={minutesAgo * 60000} " +
            $"target=\"lane with spaces\" child_pid={pid} output_bytes={bytes} heartbeat=ignored");
}
