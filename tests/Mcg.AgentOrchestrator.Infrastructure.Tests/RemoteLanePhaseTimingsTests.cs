using System.Text.Json;
using System.Xml.Linq;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class RemoteLanePhaseTimingsTests : IDisposable
{
    private static readonly DateTimeOffset T = new(2026, 10, 17, 0, 0, 0, TimeSpan.Zero);
    private readonly string _root = InfrastructureTestSupport.CreateTempDirectory();
    private static RemoteLaneStep Step(string name, double start, double end) =>
        new(name, 0, false, T.AddSeconds(start), T.AddSeconds(end), "");
    private static readonly RemoteLaneStep[] Steps = [Step("push", 0, 1), Step("trigger", 1, 3)];
    private static readonly RemoteLaneStep[] Fetches = [Step("fetch-0", 490, 495)];
    private static JsonElement Status(bool utc = true)
    {
        var fields = new Dictionary<string, object?>
        {
            ["seconds"] = 490,
            ["phases"] = new { worktreeSeconds = 4.2, buildWaitSeconds = 0, buildSeconds = 118.5, buildSkipped = false }
        };
        if (utc) fields["utc"] = T.AddSeconds(500).ToString("o");
        return JsonSerializer.SerializeToElement(fields);
    }
    private string Trx(string name, double? start = 150, double? finish = 480)
    {
        var path = Path.Combine(_root, name + ".trx");
        XNamespace ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
        new XDocument(new XElement(ns + "TestRun", start is null ? null : new XElement(ns + "Times",
            new XAttribute("start", T.AddSeconds(start.Value).ToString("o")),
            new XAttribute("finish", T.AddSeconds(finish!.Value).ToString("o"))))).Save(path);
        return path;
    }

    [Fact]
    public void Compute_CompleteEvidence_RecordsEveryPhase()
    {
        var p = Assert.IsType<RemoteLanePhaseTimings>(RemoteLanePhaseTimings.Compute(Steps, Status(), [Trx("one")], Fetches));
        Assert.Equal(new RemoteLanePhaseTimings(3, 7, 490, 4.2, 0, 118.5, false, 140, 330, 15), p);
    }

    [Fact]
    public void Compute_MultipleTrx_UsesEarliestStartAndLatestFinish()
    {
        var p = RemoteLanePhaseTimings.Compute(Steps, Status(), [Trx("one", 160, 490), Trx("two", 140, 470)], Fetches)!;
        Assert.Equal(130, p.PreambleSeconds);
        Assert.Equal(350, p.TestSeconds);
        Assert.Equal(5, p.CollectSeconds);
    }

    [Fact]
    public void Compute_NoTimes_PreservesOtherPhases()
    {
        var p = RemoteLanePhaseTimings.Compute(Steps, Status(), [Trx("one", null)], Fetches);
        Assert.Equal(new RemoteLanePhaseTimings(3, 7, 490, 4.2, 0, 118.5, false), p);
    }

    [Fact]
    public void Compute_NoUtc_LeavesDependentPhasesNull()
    {
        var p = RemoteLanePhaseTimings.Compute(Steps, Status(false), [Trx("one")], Fetches)!;
        Assert.Null(p.QueueSeconds);
        Assert.Null(p.PreambleSeconds);
        Assert.Equal(490, p.RunnerSeconds);
        Assert.Equal(330, p.TestSeconds);
        Assert.Equal(15, p.CollectSeconds);
    }

    [Fact]
    public void Compute_WrongJsonTypes_LeavesDependentPhasesNull()
    {
        using var status = JsonDocument.Parse("{\"seconds\":\"490\",\"phases\":[]}");
        var p = RemoteLanePhaseTimings.Compute(Steps, status.RootElement, [], Fetches);
        Assert.Equal(new RemoteLanePhaseTimings(SubmitSeconds: 3), p);
    }

    [Fact]
    public void Compute_UnreadableTrx_LeavesTestNull()
    {
        var p = RemoteLanePhaseTimings.Compute(Steps, Status(), [Path.Combine(_root, "missing.trx")], Fetches)!;
        Assert.Null(p.TestSeconds);
        Assert.Equal(490, p.RunnerSeconds);
        File.WriteAllText(Path.Combine(_root, "malformed.trx"), "<broken");
        Assert.Null(RemoteLanePhaseTimings.Compute([], null, [Path.Combine(_root, "malformed.trx")], []));
    }

    [Fact]
    public void Compute_NoInputs_ReturnsNull() => Assert.Null(RemoteLanePhaseTimings.Compute([], null, [], []));

    [Fact]
    public void Compute_DisposedOrNonObjectStatus_PreservesIndependentInputs()
    {
        var document = JsonDocument.Parse("{\"seconds\":490}");
        var disposed = document.RootElement;
        document.Dispose();
        Assert.Equal(new RemoteLanePhaseTimings(SubmitSeconds: 3),
            RemoteLanePhaseTimings.Compute(Steps, disposed, [], []));
        using var array = JsonDocument.Parse("[]");
        Assert.Null(RemoteLanePhaseTimings.Compute([], array.RootElement, [], []));
    }

    [Fact]
    public void Compute_FractionalDurations_RoundsEachOutputToTenths()
    {
        var status = JsonSerializer.SerializeToElement(new
        { seconds = 490.04, utc = T.AddSeconds(500.08).ToString("o"), phases = new { buildSeconds = 118.56 } });
        var p = RemoteLanePhaseTimings.Compute([Step("push", 0, 3.06)], status,
            [Trx("rounded", 150.08, 480.04)], [Step("fetch-0", 490, 495.02)])!;
        Assert.Equal(3.1, p.SubmitSeconds);
        Assert.Equal(7, p.QueueSeconds);
        Assert.Equal(490, p.RunnerSeconds);
        Assert.Equal(118.6, p.BuildSeconds);
        Assert.Equal(140, p.PreambleSeconds);
        Assert.Equal(330, p.TestSeconds);
        Assert.Equal(15, p.CollectSeconds);
    }

    [Fact]
    public void Compute_ClockSkew_KeepsNegativeValuesAndIgnoresRunnerLogStep()
    {
        var p = RemoteLanePhaseTimings.Compute([..Steps, Step("runner-log", 600, 700)], Status(),
            [Trx("one", 150, 500)], [Step("fetch-0", 480, 490)])!;
        Assert.Equal(3, p.SubmitSeconds);
        Assert.Equal(-10, p.CollectSeconds);
        var skew = RemoteLanePhaseTimings.Compute([Step("trigger", 15, 20)], Status(), [], [])!;
        Assert.Equal(-10, skew.QueueSeconds);
    }

    public void Dispose() => Directory.Delete(_root, true);
}
