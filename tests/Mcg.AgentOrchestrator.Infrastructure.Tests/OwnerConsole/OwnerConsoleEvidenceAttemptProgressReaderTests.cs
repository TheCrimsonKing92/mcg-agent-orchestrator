using System.Text.Json;
using Mcg.AgentOrchestrator.App.OwnerConsole;

// Parallel-safe: each fixture owns a unique temp root and uses a fixed clock.
public sealed class OwnerConsoleEvidenceAttemptProgressReaderTests
{
    private const string Goal = "11111111-evidence";
    private const string Attempt = "running-attempt";
    private static readonly DateTimeOffset Now = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Read_CompleteFixture_ReportsExplicitSourcesWithoutChangingFiles()
    {
        using var fixture = new Fixture();
        var progress = fixture.Read();

        Assert.Equal(Now, progress.ObservedAt);
        Assert.Equal(2, progress.Ordinal);
        Assert.Equal(Now.AddSeconds(-192), progress.StartedAt);
        Assert.Equal("verification-check", progress.Phase);
        Assert.Equal("Infrastructure.Tests: AlphaTests +1 classes", progress.Lane);
        Assert.Equal(Now.AddSeconds(-4), progress.LastProgressAt);
        Assert.Equal(2, progress.Passed);
        Assert.Equal(1, progress.Failed);
        Assert.Null(progress.Previous);
    }

    [Theory]
    [InlineData("trx")]
    [InlineData("phase.log")]
    [InlineData("lane.gate-heartbeat.json")]
    [InlineData("heartbeat.json")]
    public void Read_MissingSource_ClearsOnlyDependentFields(string suffix)
    {
        using var fixture = new Fixture();
        File.Delete(fixture.PathFor(suffix));

        AssertAvailableFields(fixture.Read(), suffix);
    }

    [Theory]
    [InlineData("heartbeat.json", "{\"lastProgressAt\":")]
    [InlineData("heartbeat.json", "{\"lastProgressAt\":\"not-a-time\"}")]
    [InlineData("lane.gate-heartbeat.json", "{\"goalId\":")]
    [InlineData("phase.log", "phase=invalid utc=not-a-time pid=7\n")]
    [InlineData("trx", "<TestRun><ResultsBroken>")]
    public void Read_MalformedSource_PreservesOtherSources(string suffix, string text)
    {
        using var fixture = new Fixture();
        fixture.Write(suffix, text);

        AssertAvailableFields(fixture.Read(), suffix);
    }

    [Theory]
    [InlineData("heartbeat.json")]
    [InlineData("phase.log")]
    [InlineData("lane.gate-heartbeat.json")]
    [InlineData("trx")]
    public void Read_LockedSource_PreservesOtherSources(string suffix)
    {
        using var fixture = new Fixture();
        using var locked = new FileStream(fixture.PathFor(suffix), FileMode.Open,
            FileAccess.ReadWrite, FileShare.None);

        // The exclusive handle is the controlled unavailable source, not a timing race.
        AssertAvailableFields(fixture.Read(checkUnchanged: false), suffix);
    }

    [Fact]
    public void Read_NonexistentDirectory_LeavesEveryProgressFieldEmpty()
    {
        using var fixture = new Fixture();
        var missing = Path.Combine(fixture.Root, "absent");
        Assert.False(Directory.Exists(missing));

        var progress = OwnerConsoleEvidenceAttemptProgressReader.Read(missing, Goal, fixture.Running, Now);

        Assert.Equal(new OwnerConsoleEvidenceAttemptProgress(Now), progress);
        Assert.False(Directory.Exists(missing));
    }

    [Fact]
    public void Read_StaleHeartbeat_UsesLastProgressRatherThanObservationOrFileTime()
    {
        using var fixture = new Fixture();
        fixture.Write("heartbeat.json", JsonSerializer.Serialize(new
        {
            lastProgressAt = Now.AddMinutes(-11), lastObservedAt = Now, startedAt = Now.AddHours(-1)
        }));

        Assert.Equal(Now.AddMinutes(-11), fixture.Read().LastProgressAt);
    }

    [Theory]
    [InlineData("<TestRun><Results/>", 0, 0)]
    [InlineData("<TestRun><Results><UnitTestResult executionId=\"x\" outcome=\"Passed\"/>", 1, 0)]
    [InlineData("<TestRun><Results><UnitTestResult outcome=\"Failed\">", 0, 0)]
    [InlineData("<TestRun><Results><UnitTestResult outcome=\"NotExecuted\"/>", 0, 0)]
    public void Read_StreamingResults_CountsOnlyCompletePassedAndFailed(
        string trx, int passed, int failed)
    {
        using var fixture = new Fixture();
        fixture.Write("trx", trx);

        var progress = fixture.Read();

        Assert.Equal(passed, progress.Passed);
        Assert.Equal(failed, progress.Failed);
    }

    [Fact]
    public void Read_DuplicateResultsAcrossFiles_CountsExplicitIdentitiesOnce()
    {
        using var fixture = new Fixture();
        var trx = "<TestRun><Results><UnitTestResult executionId=\"x\" outcome=\"Passed\"/>" +
            "<UnitTestResult testId=\"y\" outcome=\"Failed\"/></Results></TestRun>";
        fixture.Write("trx", trx);
        fixture.Write("copy.trx", trx);

        var progress = fixture.Read();

        Assert.Equal(1, progress.Passed);
        Assert.Equal(1, progress.Failed);
    }

    [Fact]
    public void Read_OtherAttemptsAndGoals_DoNotSupplyProgress()
    {
        using var fixture = new Fixture();
        fixture.WriteLane("lane.gate-heartbeat.json", "other-goal", "AAA.Tests", "OtherTests");
        File.WriteAllText(Path.Combine(fixture.Directory, "other.trx"),
            "<TestRun><Results><UnitTestResult outcome=\"Failed\"/></Results></TestRun>");
        File.WriteAllText(Path.Combine(fixture.Directory, "other.gate-heartbeat.json"),
            JsonSerializer.Serialize(new { goalId = Goal, currentTarget = "AAA.Tests" }));

        var progress = fixture.Read();

        Assert.Null(progress.Lane);
        Assert.Equal(2, progress.Passed);
        Assert.Equal(1, progress.Failed);
    }

    [Theory]
    [InlineData("FullyQualifiedName~Example.AlphaTests", "CustomLane: AlphaTests")]
    [InlineData("Name~SomeMethod", "CustomLane")]
    [InlineData("FullyQualifiedName~AlphaTests&Name~Method", "CustomLane")]
    [InlineData("", "CustomLane")]
    public void Read_LaneTarget_PreservesExplicitNameAndShortensKnownClassFilters(
        string filter, string expected)
    {
        using var fixture = new Fixture();
        fixture.Write("lane.gate-heartbeat.json", JsonSerializer.Serialize(new
        {
            goalId = Goal, currentTarget = "reviewer focused evidence: CustomLane " + filter
        }));

        Assert.Equal(expected, fixture.Read().Lane);
    }

    [Fact]
    public void Read_MultipleLanes_SelectsByOrdinalLaneName()
    {
        using var fixture = new Fixture();
        fixture.WriteLane("z.gate-heartbeat.json", Goal, "AAA.Tests", "FirstTests");
        fixture.WriteLane("a.gate-heartbeat.json", Goal, "ZZZ.Tests", "LastTests");

        Assert.Equal("AAA.Tests: FirstTests", fixture.Read().Lane);
    }

    [Fact]
    public void Read_ObservedEvents_AttachesOnlyRunningGoalsAndPreviousFailedAttempt()
    {
        using var fixture = new Fixture();
        var reader = new OwnerConsoleEvidenceAttemptProgressReader(fixture.Root,
            new OwnerConsoleTestClock { Now = Now });
        var previous = OwnerConsoleHostEvidenceRuns.Parse(JsonSerializer.Serialize(new
        {
            eventKind = "EVIDENCE_END", goalId = Goal, attempt = "previous",
            duration_s = 390, outcome = "failed", tests_executed = 163
        }))!.End!;
        var observed = OwnerConsoleGateProgress.Idle() with
        {
            HostActivity = [fixture.Running], EvidenceEnded = [previous]
        };

        var progress = Assert.IsType<OwnerConsoleEvidenceAttemptProgress>(reader.Read(Goal, observed));

        Assert.Equal(previous, progress.Previous);
        Assert.Equal(2, progress.Ordinal);
        Assert.Null(reader.Read("another-goal", observed));
        Assert.Null(reader.Read(Goal, observed with { HostActivity = [] }));
        Assert.Null(reader.Read(Goal, null));
    }

    private static void AssertAvailableFields(OwnerConsoleEvidenceAttemptProgress progress, string missing)
    {
        Assert.Equal(2, progress.Ordinal);
        Assert.Equal(Now.AddSeconds(-192), progress.StartedAt);
        Assert.Equal(missing == "phase.log" ? null : "verification-check", progress.Phase);
        Assert.Equal(missing == "lane.gate-heartbeat.json" ? null :
            "Infrastructure.Tests: AlphaTests +1 classes", progress.Lane);
        Assert.Equal(missing == "heartbeat.json" ? (DateTimeOffset?)null : Now.AddSeconds(-4),
            progress.LastProgressAt);
        Assert.Equal(missing == "trx" ? (int?)null : 2, progress.Passed);
        Assert.Equal(missing == "trx" ? (int?)null : 1, progress.Failed);
    }

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = InfrastructureTestSupport.CreateTempDirectory();
        internal string Directory { get; }
        internal OwnerConsoleHostActivity Running { get; }

        internal Fixture()
        {
            Directory = Path.Combine(Root, Goal);
            System.IO.Directory.CreateDirectory(Directory);
            Running = Assert.Single(OwnerConsoleHostEvidenceRuns.Read([JsonSerializer.Serialize(new
            {
                eventKind = "EVIDENCE_START", goalId = Goal, attempt = Attempt,
                ordinal = 2, timestamp = Now.AddSeconds(-192)
            })], Now));
            Write("heartbeat.json", JsonSerializer.Serialize(new
            {
                lastProgressAt = Now.AddSeconds(-4), lastObservedAt = Now
            }));
            Write("phase.log", "phase=build utc=2026-10-28T11:57:00.0000000Z pid=7\n" +
                "phase=verification-check utc=2026-10-28T11:58:00.0000000Z pid=7\n" +
                "phase=torn utc=2026-10-28T11:59:00.0000000Z pid=7");
            WriteLane("lane.gate-heartbeat.json", Goal, "Infrastructure.Tests",
                "AlphaTests", "BetaTests", "AlphaTests");
            Write("trx", "<TestRun xmlns=\"http://microsoft.com/schemas/VisualStudio/TeamTest/2010\"><Results>" +
                "<UnitTestResult executionId=\"p1\" outcome=\"Passed\"/>" +
                "<UnitTestResult executionId=\"p2\" outcome=\"Passed\"/>" +
                "<UnitTestResult executionId=\"f1\" outcome=\"Failed\"><Output/></UnitTestResult>" +
                "<UnitTestResult executionId=\"torn\" outcome=\"Passed\"><Output>");
        }

        internal string PathFor(string suffix) => Path.Combine(Directory, Attempt + "." + suffix);
        internal void Write(string suffix, string text) => File.WriteAllText(PathFor(suffix), text);
        internal void WriteLane(string suffix, string goal, string lane, params string[] classes) =>
            Write(suffix, JsonSerializer.Serialize(new
            {
                goalId = goal, phase = "verification-check", currentTarget = "reviewer focused evidence: " +
                    lane + " " + string.Join('|', classes.Select(name => "FullyQualifiedName~Example." + name))
            }));

        internal OwnerConsoleEvidenceAttemptProgress Read(bool checkUnchanged = true)
        {
            var before = checkUnchanged ? Files() : [];
            var progress = OwnerConsoleEvidenceAttemptProgressReader.Read(Directory, Goal, Running, Now);
            if (checkUnchanged) Assert.Equal(before, Files());
            return progress;
        }

        private (string Path, string Bytes, DateTime Written)[] Files() =>
            System.IO.Directory.GetFiles(Directory).Order(StringComparer.Ordinal).Select(path =>
                (path, Convert.ToBase64String(File.ReadAllBytes(path)), File.GetLastWriteTimeUtc(path))).ToArray();

        public void Dispose() => System.IO.Directory.Delete(Root, recursive: true);
    }
}
