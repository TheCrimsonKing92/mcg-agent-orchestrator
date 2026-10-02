using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;

// Parallel-safe: each case owns its log directory and the writer uses a path-scoped mutex.
public sealed class ConductEventLogWriterOperatorFieldTests : IDisposable
{
    private static readonly DateTimeOffset Timestamp = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("mcg-conduct-operator-");

    [Theory]
    [InlineData("goal-escalation", "GOAL goal=beba848c result=escalated state=AwaitingHumanInput reason=x", "decision")]
    [InlineData("goal-stalled", "GOAL_STALLED goal=beba848c state=Blocked owner=none", "decision")]
    [InlineData("sweep-blocker", "SWEEP_BLOCKER goal=beba848c kind=x", "decision")]
    [InlineData("loop-handoff", "ACTIVATION_REVERTED candidateCommit=a", "decision")]
    [InlineData("loop-handoff", "ACTIVATION_FAILED_BOTH candidateCommit=a", "decision")]
    [InlineData("author", "item=i kind=ask-owner reason=r", "decision")]
    [InlineData("author", "item=i kind=model-failure reason=timeout", "decision")]
    [InlineData("acceptance", "ACCEPTANCE goal=6610a998 slot=slot-0 result=failed attempt=a tick=1", "decision")]
    [InlineData("acceptance", "ACCEPTANCE goal=6610a998 slot=slot-0 result=blocked attempt=a tick=1", "decision")]
    [InlineData("canary-gate", "CANARY_GATE sha=s result=failed reason=r receipt=r escalation=CanaryGateFailure", "decision")]
    [InlineData("acceptance-cohort", "ACCEPTANCE_COHORT tick=1 members=a,b outcome=failed", "decision")]
    [InlineData("loop-handoff", "ACTIVATION_ADOPTED candidateCommit=a", "outcome")]
    [InlineData("loop-relaunch", "LOOP_RELAUNCH_SCHEDULED tick=1 goal=g", "outcome")]
    [InlineData("acceptance", "ACCEPTANCE goal=6610a998 slot=slot-0 result=passed attempt=a tick=1", "outcome")]
    [InlineData("canary-gate", "CANARY_GATE sha=s result=passed executed=3 receipt=r", "outcome")]
    [InlineData("acceptance-cohort", "ACCEPTANCE_COHORT tick=1 members=a,b outcome=passed", "outcome")]
    [InlineData("acceptance-cohort", "ACCEPTANCE_COHORT_RECONCILED_DEAD kind=k attempt=a", "outcome")]
    public void Append_TableRow_WritesOperator(string eventKind, string detail, string expected)
    {
        var writer = CreateWriter();

        writer.Append(eventKind, "beba848c", detail);

        using var document = JsonDocument.Parse(Assert.Single(File.ReadAllLines(writer.CurrentPath)));
        Assert.True(document.RootElement.TryGetProperty("operator", out var operatorClass),
            "Expected classified event to contain the operator property.");
        Assert.Equal(expected, operatorClass.GetString());
        Assert.Equal(eventKind, document.RootElement.GetProperty("eventKind").GetString());
        Assert.Equal(detail, document.RootElement.GetProperty("detail").GetString());
    }

    [Theory]
    [InlineData("acceptance", "ACCEPTANCE goal=6610a998 slot=slot-0 result=running attempt=a tick=1", "6610a998")]
    [InlineData("acceptance-cohort", "ACCEPTANCE_COHORT_INFLIGHT tick=1 goal=g members=a,b outcome=inflight", "g")]
    [InlineData("gate-progress", "GATE_PROGRESS goal=g check=build result=running", "g")]
    [InlineData("speculative-cohort-plan", "SPECULATIVE_COHORT_PLAN tick=1 members=a,b", null)]
    [InlineData("goal", "GOAL goal=g result=running state=Active", "g")]
    public void Append_RoutineEvent_PreservesLegacyBytes(string eventKind, string detail, string? goalId)
    {
        var writer = CreateWriter();
        var expected = JsonSerializer.Serialize(
            new LegacyConductEventRecord(Timestamp, eventKind, goalId, detail),
            new JsonSerializerOptions(JsonSerializerDefaults.Web)) + Environment.NewLine;

        writer.Append(eventKind, goalId, detail);

        var actual = File.ReadAllText(writer.CurrentPath);
        Assert.DoesNotContain("\"operator\"", actual, StringComparison.Ordinal);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void AppendRequired_Escalation_DrainsDecisionToLiveLog()
    {
        var writer = CreateWriter();

        Assert.True(writer.AppendRequired("goal-escalation", "beba848c",
            "GOAL goal=beba848c result=escalated state=AwaitingHumanInput reason=x"));

        var line = Assert.Single(File.ReadAllLines(writer.CurrentPath));
        Assert.Contains("\"operator\":\"decision\"", line, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(line);
        Assert.Equal("decision", document.RootElement.GetProperty("operator").GetString());
        Assert.Empty(Directory.GetFiles(
            Path.Combine(_root.FullName, ConductEventLogWriter.PendingEventsDirectoryName), "*.jsonl"));
    }

    private ConductEventLogWriter CreateWriter() => new(
        Path.Combine(_root.FullName, ConductEventLogWriter.CurrentFileName), utcNow: () => Timestamp);

    public void Dispose() => _root.Delete(recursive: true);

    private sealed record LegacyConductEventRecord(
        DateTimeOffset Timestamp, string EventKind, string? GoalId, string Detail);
}
