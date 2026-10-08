using System.Globalization;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each fixture owns its SQLite stores, directory and mutable clock.
public sealed class CliOwnerDigestLatestDecisionsTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Run_RecordedTickReplay_ReportsLatestDecisionInTextAndJson(bool rounds)
    {
        using var fixture = await OwnerDigestTestFixture.CreateAsync();
        var repository = new SqliteOrchestratorStateRepository(fixture.Workspace.SqliteStatePath);
        var loaded = await repository.LoadAsync();
        var kernel = AgentOrchestratorKernel.FromSnapshot(loaded.ExportSnapshot(), fixture.Clock);
        var id = new GoalId(OwnerDigestTestFixture.GoalA);
        void Record(int hour, ConductorAdvanceOutcome outcome)
        {
            fixture.Clock.UtcNow = OwnerDigestTestFixture.Start.AddHours(hour);
            kernel.RecordGoalPolicyDecision(id, "tick",
                VerifiedAcceptanceEscalationDecision.BuildTickOutcomePayload(outcome));
        }
        Record(15, new ConductorAdvanceOutcome.Held(GoalLifecycleState.Verifying, "pending")
            { Decision = Decision("Verifying", "Hold", 1, "pending") });
        Record(16, new ConductorAdvanceOutcome.Escalated(GoalLifecycleState.Verified, "gate failed",
            ConductorEscalationKind.AcceptanceVerificationFailed)
            { Decision = Decision("Verified", "Escalate", 2, "gate failed") });
        Record(17, new ConductorAdvanceOutcome.Executed(GoalLifecycleState.Verified, "start landing")
            { Decision = Decision("Landing", "Execute", 3, "candidate verified") });
        Record(18, new ConductorAdvanceOutcome.Held(GoalLifecycleState.Verified, "later undecided"));
        // The exclusive end boundary must not replace the in-window decision.
        Record(24, new ConductorAdvanceOutcome.Executed(GoalLifecycleState.Verified, "outside")
            { Decision = Decision("Outside", "Execute", 99, "outside window") });
        await repository.SaveAsync(kernel);
        fixture.Clock.UtcNow = OwnerDigestTestFixture.End;

        var text = Run(fixture, rounds);
        Assert.Contains(string.Join(Environment.NewLine,
            "Latest decisions:", "aaaaaaaa | Landing | rung 3 | candidate verified", "none=3"), text);
        Assert.DoesNotContain("| Outside |", text);
        using var json = JsonDocument.Parse(Run(fixture, rounds, json: true));
        var section = json.RootElement.GetProperty("latestDecisions");
        Assert.Equal(3, section.GetProperty("none").GetInt32());
        var entry = Assert.Single(section.GetProperty("entries").EnumerateArray());
        Assert.Equal(OwnerDigestTestFixture.GoalA, entry.GetProperty("goalId").GetString());
        Assert.Equal("Landing", entry.GetProperty("stage").GetString());
        Assert.Equal("Execute", entry.GetProperty("action").GetString());
        Assert.Equal(3, entry.GetProperty("rung").GetInt32());
        Assert.Equal("candidate verified", entry.GetProperty("evidence").GetString());
        Assert.Equal(OwnerDigestTestFixture.Start.AddHours(17),
            entry.GetProperty("occurredAt").GetDateTimeOffset());
    }

    [Fact]
    public async Task Run_NoDecisions_AlwaysShowsSectionAndNoneCount()
    {
        using var fixture = await OwnerDigestTestFixture.CreateAsync();
        Assert.Contains("Latest decisions:" + Environment.NewLine + "none=4", Run(fixture));
        using var json = JsonDocument.Parse(Run(fixture, json: true));
        var section = json.RootElement.GetProperty("latestDecisions");
        Assert.Empty(section.GetProperty("entries").EnumerateArray());
        Assert.Equal(4, section.GetProperty("none").GetInt32());
    }

    [Fact]
    public void Build_WindowAndOrdering_ExcludeOutsideEventsAndSortEntries()
    {
        var start = OwnerDigestTestFixture.Start;
        ProgressEvent Tick(string goal, int hour, PolicyDecisionRecord? decision) =>
            new(new GoalId(goal), null, ProgressKind.GoalPolicyDecision, "tick", start.AddHours(hour),
                TickOutcome: new("Held", "Verified", null, decision));
        var decision = Decision("Verified", "Hold", 2, "candidate unchanged");
        var report = OwnerDigestReport.Build(
        [
            new("bbbbbbbb", null, null, [Tick("bbbbbbbb", 2, decision)]),
            new("zzzzzzzz", null, null, [Tick("zzzzzzzz", 3, decision)]),
            new("aaaaaaaa", null, null, [Tick("aaaaaaaa", 2, decision)]),
            new("no-decision", null, null, [Tick("no-decision", -1, decision), Tick("no-decision", 0, null)]),
            new("outside", null, null, [Tick("outside", 24, decision)]),
            new("empty", start.AddHours(1), "sha", [])
        ], [], new OwnerDigestTestFixture.MutableClock(OwnerDigestTestFixture.End), start);
        Assert.Equal(new[] { "zzzzzzzz", "aaaaaaaa", "bbbbbbbb" },
            report.LatestDecisions.Entries.Select(e => e.GoalId));
        Assert.Equal(1, report.LatestDecisions.None);
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        CliOwnerDigestCommand.WriteDigestText(output, report);
        Assert.Contains(string.Join(Environment.NewLine, "Latest decisions:",
            "zzzzzzzz | Verified | rung 2 | candidate unchanged",
            "aaaaaaaa | Verified | rung 2 | candidate unchanged",
            "bbbbbbbb | Verified | rung 2 | candidate unchanged", "none=1"), output.ToString());
    }

    [Theory]
    [InlineData("", "-")]
    [InlineData(" \t\n ", "-")]
    [InlineData("first\r\n second;  third", "first second; third")]
    public void WriteText_EvidenceWhitespace_KeepsOneLine(string evidence, string expected)
    {
        var start = OwnerDigestTestFixture.Start;
        var evt = new ProgressEvent(new GoalId("goal"), null, ProgressKind.GoalPolicyDecision, "tick", start,
            TickOutcome: new("Held", "Verified", null, Decision("Verified", "Hold", 2, evidence)));
        var report = OwnerDigestReport.Build([new("goal", null, null, [evt])], [],
            new OwnerDigestTestFixture.MutableClock(OwnerDigestTestFixture.End), start);
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        CliOwnerDigestCommand.WriteDigestText(output, report);
        Assert.Contains($"goal | Verified | rung 2 | {expected}" + Environment.NewLine, output.ToString());
    }

    private static string Run(OwnerDigestTestFixture fixture, bool rounds = false, bool json = false)
    {
        var args = new List<string> { "owner-digest", "--since", OwnerDigestTestFixture.Start.ToString("O"),
            "--until", OwnerDigestTestFixture.End.ToString("O") };
        if (rounds) args.Add("--rounds");
        if (json) args.Add("--json");
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        Assert.Equal(0, CliOwnerDigestCommand.Run(args, fixture.Workspace, fixture.Clock, output));
        return output.ToString();
    }

    private static PolicyDecisionRecord Decision(string stage, string action, int rung, string evidence) =>
        new(stage, action, rung, evidence, "Policy reason", []);
}
