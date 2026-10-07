using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each fact owns its root and event writer path.
public sealed class ConductorDriverTestsRemoteLaneEvents : IDisposable
{
    private readonly string _root = InfrastructureTestSupport.CreateTempDirectory();

    [Theory]
    [InlineData("12345678")]
    [InlineData(null)]
    public void HelperAppendsRemoteLaneEvent(string? goalId)
    {
        var writer = new ConductEventLogWriter(Path.Combine(_root, ConductEventLogWriter.CurrentFileName));
        const string detail = "REMOTE_LANE executor=one lane=lane attempt=attempt outcome=accepted reason=none";
        ConductorDriver.AppendRemoteLaneEvent(writer, goalId, detail);
        using var document = JsonDocument.Parse(Assert.Single(File.ReadAllLines(writer.CurrentPath)));
        Assert.Equal("remote-lane", document.RootElement.GetProperty("eventKind").GetString());
        Assert.Equal(goalId, document.RootElement.GetProperty("goalId").GetString());
        Assert.Equal(detail, document.RootElement.GetProperty("detail").GetString());
    }

    [Fact]
    public void HelperSwallowsUnwritableLog()
    {
        var blocked = Path.Combine(_root, "file");
        File.WriteAllText(blocked, "parent is a file");
        var writer = new ConductEventLogWriter(Path.Combine(blocked, ConductEventLogWriter.CurrentFileName));
        Assert.Null(Record.Exception(() => ConductorDriver.AppendRemoteLaneEvent(writer, "goal", "detail")));
        Assert.Equal("parent is a file", File.ReadAllText(blocked));
    }

    [Fact]
    public async Task OwnerAndViewForwardRemoteLaneEvent()
    {
        var lines = new List<string>();
        var identity = new AcceptanceAttemptIdentity("attempt", "goal", _root, "candidate", "main", "commit",
            Path.Combine(_root, ".orchestrator", "attempts", "attempt", "result"), 0, Environment.ProcessId, null);
        await using var owner = new AcceptanceAttemptExecutionOwner(identity, new AcceptanceGateEngineSettings(),
            options: new AcceptanceRunExecutionOptions(RemoteLaneEventSink: lines.Add));
        owner.ReportRemoteLaneEvent("direct");
        IAcceptanceRunExecutionContext view = new AcceptanceRunExecutionContextView(owner,
            Path.Combine(_root, ".orchestrator", "attempts", "attempt", "view"));
        view.ReportRemoteLaneEvent("view");
        Assert.Equal(new[] { "direct", "view" }, lines);
    }

    public void Dispose() => Directory.Delete(_root, true);
}
