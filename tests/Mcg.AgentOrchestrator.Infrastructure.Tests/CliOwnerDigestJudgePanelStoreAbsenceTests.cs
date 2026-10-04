using System.Security.Cryptography;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using static OwnerDigestJudgePanelFixture;

public sealed class CliOwnerDigestJudgePanelStoreAbsenceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AbsentStoreLeavesLegacyTextAndJsonByteIdenticalAndCreatesNoStore(bool rounds)
    {
        using var fixture = await CreateAsync();
        var digest = CliOwnerDigestCommand.Read(fixture.Workspace,
            new OwnerDigestTestFixture.MutableClock(OwnerDigestTestFixture.End), out var goals, Start, OwnerDigestTestFixture.End);
        var intents = CliOwnerDigestRetryIntents.Read(fixture.Workspace, digest.Until);
        using var expectedText = new StringWriter();
        CliOwnerDigestCommand.WriteDigestText(expectedText, digest);
        CliOwnerDigestLessons.WriteText(expectedText, fixture.Workspace, digest.Since, digest.Until);
        if (rounds) CliOwnerDigestRounds.WriteText(expectedText, digest, goals, intents);
        using var expectedJson = new StringWriter();
        if (rounds) CliOwnerDigestRounds.WriteJson(expectedJson, digest, goals, intents);
        else CliOwnerDigestCommand.WriteJson(expectedJson, digest);

        Assert.Equal(expectedText.ToString(), fixture.Run(rounds: rounds));
        Assert.Equal(expectedJson.ToString(), fixture.Run(json: true, rounds: rounds));
        Assert.False(File.Exists(fixture.PanelPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EmptyStoreAppendsOnlyNewSectionAndPreservesExistingJsonProperties(bool rounds)
    {
        using var fixture = await CreateAsync();
        var absentText = fixture.Run(rounds: rounds);
        var absentJson = fixture.Run(json: true, rounds: rounds);
        fixture.CreateEmptyPanelStore();
        var hash = SHA256.HashData(File.ReadAllBytes(fixture.PanelPath));

        var emptyText = fixture.Run(rounds: rounds);
        Assert.StartsWith(absentText, emptyText);
        Assert.StartsWith("Judge panel shadow cases" + Environment.NewLine, emptyText[absentText.Length..]);
        Assert.Contains("Judge panel shadow cases: none in window", emptyText);
        Assert.DoesNotContain("Panel valid output |", emptyText);
        Assert.DoesNotContain("Panel next_action agreement |", emptyText);
        Assert.DoesNotContain("Panel provisional-match |", emptyText);
        using var absent = JsonDocument.Parse(absentJson);
        using var empty = JsonDocument.Parse(fixture.Run(json: true, rounds: rounds));
        Assert.Equal(absent.RootElement.EnumerateObject().Count() + 1, empty.RootElement.EnumerateObject().Count());
        foreach (var property in absent.RootElement.EnumerateObject())
            Assert.Equal(property.Value.GetRawText(), empty.RootElement.GetProperty(property.Name).GetRawText());
        Assert.Empty(empty.RootElement.GetProperty("judgePanel").GetProperty("cases").EnumerateArray());
        Assert.Equal(hash, SHA256.HashData(File.ReadAllBytes(fixture.PanelPath)));
    }

    [Fact]
    public async Task UnreadablePanelStoreIsOmittedWithoutWritingIt()
    {
        using var fixture = await CreateAsync();
        var text = fixture.Run();
        var json = fixture.Run(json: true);
        File.WriteAllText(fixture.PanelPath, "not a database");
        var hash = SHA256.HashData(File.ReadAllBytes(fixture.PanelPath));

        Assert.Equal(text, fixture.Run());
        Assert.Equal(json, fixture.Run(json: true));
        Assert.Equal(hash, SHA256.HashData(File.ReadAllBytes(fixture.PanelPath)));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("not-a-database")]
    [InlineData("legacy-schema")]
    public async Task UnavailableIntentStoreStillAllowsTimelineResolutionAndIsNotWritten(string kind)
    {
        using var fixture = await CreateAsync();
        await fixture.AddGoalAsync(GoalE, [Retry(GoalE, 2)]);
        fixture.AddCase(GoalE, 1, new Call("sol"), new Call("sonnet"));
        File.Delete(fixture.IntentsPath);
        if (kind == "not-a-database") File.WriteAllText(fixture.IntentsPath, "unreadable");
        else if (kind == "legacy-schema")
        {
            using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={fixture.IntentsPath};Pooling=False");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE operator_intents(task_id TEXT, completed_at TEXT)";
            command.ExecuteNonQuery();
        }
        var hash = File.Exists(fixture.IntentsPath) ? SHA256.HashData(File.ReadAllBytes(fixture.IntentsPath)) : null;

        Assert.EndsWith(" | developer-retry-by-conductor", CaseLine(fixture.Run(), GoalE));
        if (hash is null) Assert.False(File.Exists(fixture.IntentsPath));
        else Assert.Equal(hash, SHA256.HashData(File.ReadAllBytes(fixture.IntentsPath)));
    }
}
