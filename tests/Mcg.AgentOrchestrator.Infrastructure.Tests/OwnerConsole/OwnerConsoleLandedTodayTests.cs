using System.Text.Json;
using Mcg.AgentOrchestrator.App.OwnerConsole;

// Parallel-safe: each test owns its temporary files, loader and fixed clock.
public sealed class OwnerConsoleLandedTodayTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("mcg-console-landed-");
    private readonly OwnerConsoleTestClock _clock = new()
    {
        Now = new DateTimeOffset(2026, 3, 10, 8, 0, 0, TimeSpan.Zero),
        Zone = TimeZoneInfo.CreateCustomTimeZone("owner-test-plus-ten", TimeSpan.FromHours(10), "UTC+10", "UTC+10")
    };
    private string LifecycleDirectory => Path.Combine(_root.FullName, "goal-events");
    private string ConductPath => Path.Combine(_root.FullName, "conduct-events.log");
    private static readonly DateTimeOffset Earlier = new(2026, 3, 9, 23, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Later = new(2026, 3, 10, 7, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task LoadAsync_RotatedAwayLanding_CountsBothDurableGoals()
    {
        ArrangeRotatedLandings();

        var history = OwnerConsoleStartupActivity.ReadHistory(ConductPath, null, _clock, TestContext.Current.CancellationToken);
        Assert.Equal(1, history.LandedToday);
        Assert.Equal(2, (await LoadAsync()).LandedToday);
    }

    [Fact]
    public async Task LoadAsync_PreviousLocalDateLanding_DoesNotIncreaseCount()
    {
        ArrangeRotatedLandings();
        WriteEvents("previous", Event("previous", Earlier.AddHours(-10)));

        Assert.Equal(2, (await LoadAsync()).LandedToday);
    }

    [Fact]
    public async Task LoadAsync_CompletionAndRepeatedLanding_CountsOnlyOneNewGoal()
    {
        ArrangeRotatedLandings();
        WriteEvents("completed", Event("completed", Later, "GoalLifecycleDecision", "Goal completed without landing."));
        Assert.Equal(2, (await LoadAsync()).LandedToday);

        WriteEvents("repeated", Event("repeated", Earlier), Event("repeated", Later));
        // The id, rather than the filename or number of records, identifies the landing.
        WriteEvents("repeated-copy", Event("repeated", Later));
        Assert.Equal(3, (await LoadAsync()).LandedToday);
    }

    [Fact]
    public void Count_AfterLocalMidnight_ExcludesPriorDayEventsInFreshFiles()
    {
        ArrangeRotatedLandings();
        Assert.Equal(2, OwnerConsoleLandedToday.Count(LifecycleDirectory, _clock));
        _clock.Now = new DateTimeOffset(2026, 3, 10, 14, 0, 30, TimeSpan.Zero);
        // Force the scan to parse timestamps instead of excluding files by last write.
        foreach (var file in Directory.GetFiles(LifecycleDirectory, "*.jsonl"))
            File.SetLastWriteTimeUtc(file, _clock.Now.UtcDateTime);

        Assert.Equal(0, OwnerConsoleLandedToday.Count(LifecycleDirectory, _clock));
    }

    [Fact]
    public void Count_MalformedLines_SkipsThemAndReadsValidLandings()
    {
        WriteEvents("mixed",
            "{\"eventType\":\"GoalLanded\",",
            "[\"GoalLanded\"]",
            "\"GoalLanded\"",
            "{\"eventType\":\"GoalLanded\"}",
            "{\"eventType\":\"GoalLanded\",\"goalId\":7,\"timestamp\":false}",
            "{\"eventType\":\"GoalLanded\",\"goalId\":\"bad-time\",\"timestamp\":\"invalid\"}",
            "{\"eventType\":\"GoalLanded\",\"goalId\":\"bad-kind\",\"timestamp\":7}",
            Event("", Later),
            Event("wrong-case", Later, "goallanded", "GoalLanded"),
            Event("valid", Earlier));

        Assert.Equal(1, OwnerConsoleLandedToday.Count(LifecycleDirectory, _clock));
    }

    [Fact]
    public void Count_OldFilesAndNestedFiles_AreExcluded()
    {
        WriteEvents("old-file", Event("old-file", Later));
        File.SetLastWriteTimeUtc(Path.Combine(LifecycleDirectory, "old-file.jsonl"), Earlier.AddHours(-10).UtcDateTime);
        var nested = Directory.CreateDirectory(Path.Combine(LifecycleDirectory, "nested")).FullName;
        var nestedFile = Path.Combine(nested, "nested.jsonl");
        File.WriteAllText(nestedFile, Event("nested", Later));
        File.SetLastWriteTimeUtc(nestedFile, _clock.Now.UtcDateTime);
        WriteEvents("visible", Event("visible", Earlier));

        Assert.Equal(1, OwnerConsoleLandedToday.Count(LifecycleDirectory, _clock));
    }

    [Fact]
    public void Count_UnreadableFile_SkipsItAndAllowsAnActiveWriter()
    {
        WriteEvents("locked", Event("locked", Later));
        WriteEvents("shared", Event("shared", Earlier));
        using var locked = new FileStream(Path.Combine(LifecycleDirectory, "locked.jsonl"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using var writer = new FileStream(Path.Combine(LifecycleDirectory, "shared.jsonl"), FileMode.Open, FileAccess.Write, FileShare.ReadWrite);

        Assert.Equal(1, OwnerConsoleLandedToday.Count(LifecycleDirectory, _clock));
    }

    [Fact]
    public async Task LoadAsync_NoDurableLandings_KeepsConductCountAsFloor()
    {
        ArrangeRotatedLandings();
        foreach (var file in Directory.GetFiles(LifecycleDirectory, "*.jsonl")) File.Delete(file);

        Assert.Equal(0, OwnerConsoleLandedToday.Count(LifecycleDirectory, _clock));
        Assert.Equal(1, (await LoadAsync()).LandedToday);
    }

    [Fact]
    public void Count_MissingOrUnsetDirectory_ReturnsZero()
    {
        Assert.Equal(0, OwnerConsoleLandedToday.Count(LifecycleDirectory, _clock));
        Assert.Equal(0, OwnerConsoleLandedToday.Count(null, _clock));
        Assert.Equal(0, OwnerConsoleLandedToday.Count("", _clock));
    }

    private void ArrangeRotatedLandings()
    {
        WriteEvents("earlier", Event("earlier", Earlier));
        WriteEvents("later", Event("later", Later));
        // The earlier relaunch has rotated away; only the later landing remains.
        File.WriteAllText(ConductPath, JsonSerializer.Serialize(new
        {
            timestamp = Later, eventKind = "loop-relaunch", goalId = "later", detail = "LOOP_RELAUNCH_SCHEDULED tick=1"
        }) + "\n");
    }

    private async Task<OwnerConsoleActivityLoad> LoadAsync()
    {
        await using var loader = new OwnerConsoleActivityLoader(ConductPath, LifecycleDirectory, _clock);
        return await loader.LoadAsync([], TestContext.Current.CancellationToken);
    }

    private void WriteEvents(string fileName, params string[] lines)
    {
        Directory.CreateDirectory(LifecycleDirectory);
        var path = Path.Combine(LifecycleDirectory, fileName + ".jsonl");
        File.WriteAllLines(path, lines);
        File.SetLastWriteTimeUtc(path, _clock.Now.UtcDateTime);
    }

    private static string Event(string id, DateTimeOffset timestamp, string type = "GoalLanded", string message = "Goal landed.") =>
        JsonSerializer.Serialize(new { timestamp, eventType = type, goalId = id, message });

    public void Dispose() => _root.Delete(true);
}
