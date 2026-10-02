using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

// Parallel-safe: workspaces, databases, evidence and output writers are unique to each test.
public sealed class CliOwnerDigestEscapesViewTests
{
    [Fact(DisplayName = "Escape details immediately follow totals and identify the defect and discovering goal")]
    public void TextListsRecordedDefectAfterTotals()
    {
        var digest = TwoPassingLandings([new(OwnerDigestTestFixture.GoalA, OwnerDigestTestFixture.GoalC,
            OwnerDigestTestFixture.Start.AddHours(20), "Feature never worked")]);
        using var output = new StringWriter();
        CliOwnerDigestCommand.WriteDigestText(output, digest);
        var lines = output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        var totalsIndex = Array.FindIndex(lines, line => line.StartsWith("Totals:", StringComparison.Ordinal));

        Assert.True(totalsIndex >= 0);
        Assert.Contains("correct=1 escapes=1 pending=0 correct-rate=0.5", lines[totalsIndex], StringComparison.Ordinal);
        Assert.Equal("Escapes: records=1", lines[totalsIndex + 1]);
        Assert.Equal($"aaaaaaaa | cccccccc | {OwnerDigestTestFixture.Start.AddHours(20):O} | Feature never worked",
            lines[totalsIndex + 2]);
        Assert.Contains(" | escape(record) | ", output.ToString(), StringComparison.Ordinal);
    }

    [Fact(DisplayName = "Escape reasons collapse whitespace and stop at 80 characters without an ellipsis")]
    public void ReasonsStayOnOneLineAndMissingFoundByIsDash()
    {
        var reason = "First\n  second\t" + new string('R', 90);
        var digest = TwoPassingLandings([new(OwnerDigestTestFixture.GoalA, null,
            OwnerDigestTestFixture.Start.AddHours(20), reason)]);
        using var output = new StringWriter();
        CliOwnerDigestCommand.WriteDigestText(output, digest);
        var expected = ("First second " + new string('R', 90))[..80];
        Assert.Contains($"aaaaaaaa | - | {OwnerDigestTestFixture.Start.AddHours(20):O} | {expected}{Environment.NewLine}",
            output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("…", output.ToString(), StringComparison.Ordinal);
    }

    [Theory(DisplayName = "CLI queued escapes applied by the tick reach both digest JSON surfaces and text")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueuedRecordFlowsThroughTickAndDigest(bool rounds)
    {
        using var fixture = await OwnerDigestTestFixture.CreateAsync();
        var workspace = fixture.Workspace;
        MakeBothCanariesPass(workspace);
        var intentPath = Path.Combine(workspace.OrchestratorDirectory, SqliteOperatorIntentStore.DatabaseFileName);
        File.Delete(intentPath);
        var proof = Path.Combine(workspace.RootDirectory, "escape-proof.txt");
        File.WriteAllText(proof, "Live-dispatch defect receipt\n");
        using var queuedOutput = new StringWriter();
        Assert.Equal(0, CliEscapeCommands.Run(["escape", "record", "--goal", "aaaaaaaa",
            "--reason", "Feature never worked", "--evidence", "operator-evidence:" + proof,
            "--found-by-goal", "cccccccc", "--actor-kind", "agent"], workspace, queuedOutput));
        Assert.False(File.Exists(workspace.OperatorEscapesStorePath));
        var intents = SqliteOperatorIntentStore.ForDirectories(workspace.OrchestratorDirectory, workspace.LogDirectory);
        var coordinator = new OperatorIntentCoordinator(intents,
            utcNow: () => OwnerDigestTestFixture.Start.AddHours(20))
        {
            Lessons = new OperatorLessonIntentServices(new SqliteOperatorLessonStore(workspace.OperatorLessonsStorePath),
                new AdjudicationEvidenceResolver(workspace.OrchestratorDirectory), _ => null),
            Escapes = new OperatorEscapeIntentServices(new SqliteOperatorEscapeStore(workspace.OperatorEscapesStorePath),
                () => SqliteOrchestratorStateRepository.OpenReadOnly(workspace.SqliteStatePath)
                    .ListGoalMetadataAsync().GetAwaiter().GetResult().Select(g => g.Id).ToArray(),
                id => OperatorIntentCoordinator.HasGoalLandedEvent(workspace.GoalLifecycleEventsDirectory, id))
        };
        var applied = Assert.Single(coordinator.ExecuteWorkspacePending(new AgentOrchestratorKernel()));
        Assert.Contains("result=applied", applied, StringComparison.Ordinal);
        var record = Assert.Single(new SqliteOperatorEscapeStore(workspace.OperatorEscapesStorePath).List());
        Assert.Equal(OwnerDigestTestFixture.GoalA, record.GoalId);
        Assert.Equal(OwnerDigestTestFixture.GoalC, record.FoundByGoalId);

        var text = RunDigest(fixture, json: false, rounds);
        Assert.Contains("Escapes: records=1", text, StringComparison.Ordinal);
        Assert.Contains("aaaaaaaa | cccccccc |", text, StringComparison.Ordinal);
        Assert.Contains("Feature never worked", text, StringComparison.Ordinal);
        Assert.Contains(" | escape(record) | ", text, StringComparison.Ordinal);
        using var json = JsonDocument.Parse(RunDigest(fixture, json: true, rounds));
        var rows = json.RootElement.GetProperty("goals");
        Assert.Equal("escape", rows[0].GetProperty("landingStatus").GetString());
        Assert.Equal("record", rows[0].GetProperty("escapeSource").GetString());
        Assert.Equal("correct", rows[1].GetProperty("landingStatus").GetString());
        Assert.False(rows[1].TryGetProperty("escapeSource", out _));
        var totals = json.RootElement.GetProperty("totals");
        Assert.Equal(1, totals.GetProperty("escapes").GetInt32());
        Assert.Equal(1, totals.GetProperty("correctLandings").GetInt32());
        Assert.Equal(0.5, totals.GetProperty("correctLandingRate").GetDouble());
        Assert.Equal(1, json.RootElement.GetProperty("escapes").GetArrayLength());
    }

    [Theory(DisplayName = "No qualifying records leaves text and JSON byte-identical and does not create the store")]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task IgnoredRecordsLeaveOutputUnchanged(bool json, bool rounds)
    {
        using var fixture = await OwnerDigestTestFixture.CreateAsync();
        var baseline = RunDigest(fixture, json, rounds);
        Assert.False(File.Exists(fixture.Workspace.OperatorEscapesStorePath));
        var store = new SqliteOperatorEscapeStore(fixture.Workspace.OperatorEscapesStorePath);
        var late = new OperatorEscape("late", OwnerDigestTestFixture.GoalA, "Future defect", [], null,
            "tester", OperatorActorKind.Human, "cli", OwnerDigestTestFixture.End);
        Assert.True(store.TryAppendEscape(late, "late-intent"));
        Assert.True(store.TryAppendEscape(late with
        {
            Id = "outside", GoalId = OwnerDigestTestFixture.GoalD,
            RecordedAt = OwnerDigestTestFixture.Start.AddHours(2)
        }, "outside-intent"));

        Assert.Equal(baseline, RunDigest(fixture, json, rounds));
        Assert.DoesNotContain("escapeSource", baseline, StringComparison.Ordinal);
        Assert.DoesNotContain("Escapes:", baseline, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "JSON and text name both sources and label canary-only rows when records qualify")]
    public void AllEscapeSourcesAreRenderedConsistently()
    {
        var digest = OwnerDigestReport.Build(
            [new("aaaaaaaa", OwnerDigestTestFixture.Start.AddHours(8), "a", []),
             new("bbbbbbbb", OwnerDigestTestFixture.Start.AddHours(12), "b", [])],
            [new("a", OwnerDigestTestFixture.Start.AddHours(9), false),
             new("b", OwnerDigestTestFixture.Start.AddHours(13), false)],
            new OwnerDigestTestFixture.MutableClock(OwnerDigestTestFixture.End),
            OwnerDigestTestFixture.Start, OwnerDigestTestFixture.End,
            escapeRecords: [new("aaaaaaaa", null, OwnerDigestTestFixture.Start.AddHours(20), "Defect")]);
        using var text = new StringWriter();
        CliOwnerDigestCommand.WriteDigestText(text, digest);
        Assert.Contains(" | escape(canary,record) | ", text.ToString(), StringComparison.Ordinal);
        Assert.Contains(" | escape(canary) | ", text.ToString(), StringComparison.Ordinal);
        using var output = new StringWriter();
        CliOwnerDigestCommand.WriteJson(output, digest);
        using var json = JsonDocument.Parse(output.ToString());
        var rows = json.RootElement.GetProperty("goals");
        Assert.Equal("canary,record", rows[0].GetProperty("escapeSource").GetString());
        Assert.Equal("canary", rows[1].GetProperty("escapeSource").GetString());
    }

    private static OwnerDigestResult TwoPassingLandings(IReadOnlyList<OwnerDigestEscapeRecord> records) =>
        OwnerDigestReport.Build(
            [new(OwnerDigestTestFixture.GoalA, OwnerDigestTestFixture.Start.AddHours(8), "a", []),
             new(OwnerDigestTestFixture.GoalB, OwnerDigestTestFixture.Start.AddHours(12), "b", [])],
            [new("a", OwnerDigestTestFixture.Start.AddHours(9), true),
             new("b", OwnerDigestTestFixture.Start.AddHours(13), true)],
            new OwnerDigestTestFixture.MutableClock(OwnerDigestTestFixture.End),
            OwnerDigestTestFixture.Start, OwnerDigestTestFixture.End, escapeRecords: records);

    private static string RunDigest(OwnerDigestTestFixture fixture, bool json, bool rounds)
    {
        var args = new List<string> { "owner-digest", "--since", OwnerDigestTestFixture.Start.ToString("O"),
            "--until", OwnerDigestTestFixture.End.ToString("O") };
        if (json) args.Add("--json");
        if (rounds) args.Add("--rounds");
        using var output = new StringWriter();
        Assert.Equal(0, CliOwnerDigestCommand.Run(args, fixture.Workspace, fixture.Clock, output));
        return output.ToString();
    }

    private static void MakeBothCanariesPass(OrchestratorWorkspace workspace)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = workspace.RunEventStorePath, Mode = SqliteOpenMode.ReadWrite, Pooling = false
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE run_events SET status='Passed' WHERE event_type=$type AND operation='receipt' AND status='Failed'";
        command.Parameters.AddWithValue("$type", RunEventTypes.PostLandingCanary);
        Assert.Equal(1, command.ExecuteNonQuery());
    }
}
