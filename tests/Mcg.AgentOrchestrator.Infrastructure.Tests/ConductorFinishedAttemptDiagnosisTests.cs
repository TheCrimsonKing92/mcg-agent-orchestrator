using Mcg.AgentOrchestrator.App.Orchestration;
using Fixture = ConductorInvariantSliceTwoWiringTests.Fixture;

// Parallel-safe: every test owns its filesystem root, database and injected clock.
public sealed class ConductorFinishedAttemptDiagnosisTests
{
    [Fact]
    public void Describe_NoGoalLines_ReportsNoHistory()
    {
        using var fixture = new Fixture();
        Assert.Equal("holderSettled:none,holderPhase:none,lastGoalLineAt:none," +
            "goalLinesSinceFinish:0,heldOnAttemptSinceFinish:0",
            ConductorFinishedAttemptDiagnosis.Describe("attempt", fixture.Clock.Now,
                fixture.Attempt().MetadataPath, []));
    }

    [Theory]
    [InlineData(null, null, "none", "none")]
    [InlineData("{\"Settled\":true}", "{\"Phase\":\"Claimed\"}", "true", "Claimed")]
    [InlineData("{\"settled\":false}", "{\"phase\":\"Released\"}", "false", "Released")]
    [InlineData("{bad", "{bad", "unreadable", "unreadable")]
    [InlineData("[]", "null", "unreadable", "unreadable")]
    [InlineData("{}", "{}", "unreadable", "unreadable")]
    [InlineData("{\"Settled\":\"true\"}", "{\"Phase\":42}", "unreadable", "unreadable")]
    [InlineData("{\"Settled\":null}", "{\"Phase\":null}", "unreadable", "unreadable")]
    [InlineData("{bad", "{\"Phase\":\"Claimed\"}", "unreadable", "Claimed")]
    [InlineData("{\"Settled\":true}", "{bad", "true", "unreadable")]
    public void ReadHolder_SidecarStates_ReportsEachFileIndependently(
        string? settlement, string? lease, string settled, string phase)
    {
        using var fixture = new Fixture();
        var path = Path.Combine(fixture.Root, "attempt.json");
        if (settlement is not null) File.WriteAllText(path + ".holder-settled.json", settlement);
        if (lease is not null) File.WriteAllText(path + ".holder.json", lease);
        Assert.Equal((settled, phase), ConductorFinishedAttemptDiagnosis.ReadHolder(path));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Claimed now")]
    [InlineData("Claimed\t")]
    [InlineData("Claimed,Released")]
    [InlineData("Phase:Claimed")]
    public void ReadHolder_InvalidPhase_RejectsEvidenceDelimiters(string phase)
    {
        using var fixture = new Fixture();
        var path = Path.Combine(fixture.Root, "attempt.json");
        File.WriteAllText(path + ".holder.json", System.Text.Json.JsonSerializer.Serialize(new { Phase = phase }));
        Assert.Equal(("none", "unreadable"), ConductorFinishedAttemptDiagnosis.ReadHolder(path));
    }

    [Fact]
    public void Describe_MixedLines_UsesStrictTimeAndOrdinalHeldMatch()
    {
        using var fixture = new Fixture();
        var finish = fixture.Clock.Now;
        ConductorGoalLine[] lines = [new(finish, "held", "attempt"),
            new(finish.AddMinutes(4), "held", "running_attempt."),
            new(finish.AddMinutes(1), "advanced", "attempt"),
            new(finish.AddMinutes(2), "held", "ATTEMPT"),
            new(finish.AddMinutes(3), "Held", "attempt")];
        Assert.Equal($"holderSettled:none,holderPhase:none,lastGoalLineAt:{finish.AddMinutes(4):O}," +
            "goalLinesSinceFinish:4,heldOnAttemptSinceFinish:1",
            ConductorFinishedAttemptDiagnosis.Describe("attempt", finish, fixture.Attempt().MetadataPath, lines));
    }

    [Fact]
    public void Read_FortyGoalLines_RetainsNewestThirtyTwoInReadOrder()
    {
        using var fixture = new Fixture();
        var finish = fixture.Clock.Now;
        var attempt = fixture.Attempt();
        fixture.Save(attempt);
        for (var index = 0; index < 40; index++)
            fixture.Append($"GOAL goal={fixture.Goals[0].Id.Value[..8]} result=held reason=" +
                (index < 8 ? attempt.AttemptId : "another-attempt"), finish.AddSeconds(index + 1));
        // A different goal's history must not enter this attempt's counts.
        fixture.Append($"GOAL goal={fixture.Goals[1].Id.Value[..8]} result=held reason={attempt.AttemptId}", finish.AddMinutes(1));
        var observed = Assert.Single(fixture.Reader.Read(fixture.Goals, finish.AddMinutes(1)).FinishedAttempts!);
        Assert.Equal($"holderSettled:none,holderPhase:none,lastGoalLineAt:{finish.AddSeconds(40):O}," +
            "goalLinesSinceFinish:32,heldOnAttemptSinceFinish:0", observed.Diagnosis);
    }

    [Theory]
    [InlineData("ambiguous")]
    [InlineData("future")]
    public void Read_UnresolvedOrFutureGoalLine_ContributesNothing(string control)
    {
        using var fixture = new Fixture();
        var finish = fixture.Clock.Now;
        var attempt = fixture.Attempt();
        fixture.Save(attempt);
        var label = control == "ambiguous" ? "0000000" : fixture.Goals[0].Id.Value[..8];
        var at = finish.AddMinutes(control == "future" ? 2 : 1);
        var goals = fixture.Goals.Take(2).ToArray(); // The ambiguous label resolves to exactly two goals.
        fixture.Append($"GOAL goal={label} result=held reason={attempt.AttemptId}", at);
        Assert.Equal("holderSettled:none,holderPhase:none,lastGoalLineAt:none," +
            "goalLinesSinceFinish:0,heldOnAttemptSinceFinish:0",
            Assert.Single(fixture.Reader.Read(goals, finish.AddMinutes(1)).FinishedAttempts!).Diagnosis);
    }

    [Fact]
    public void Evaluate_NullDiagnosis_PreservesExactBaselineEvidence()
    {
        using var fixture = new Fixture();
        var start = fixture.Clock.Now;
        var observation = new ConductorInvariantObservation(start.AddMinutes(6), [], null, [],
            [new("attempt", "goal", "gate", start.AddMinutes(-1), start)]);
        var result = ConductorInvariantWatchdog.Evaluate(observation, new Dictionary<string, DateTimeOffset>());
        Assert.Equal($"goal:goal,kind:gate,finishedAt:{start:O},unreconciledFor:6m", Assert.Single(result.Raise).Evidence);
    }
}
