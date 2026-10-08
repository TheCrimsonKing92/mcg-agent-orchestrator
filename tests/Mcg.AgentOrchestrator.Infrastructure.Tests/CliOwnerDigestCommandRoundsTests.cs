using System.Security.Cryptography;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class CliOwnerDigestCommandRoundsTests
{
    private const string RoundGoal = "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee";
    private const string DeveloperTask = "task-developer";
    private const string ReviewerTask = "task-reviewer";
    private const string OutsideTask = "task-outside";

    [Fact]
    public async Task RoundsAggregateByRoleAndModelWithinWindowWithoutWritingStores()
    {
        using var fixture = await OwnerDigestTestFixture.CreateAsync();
        await AddRoundsAsync(fixture);
        var before = HashFiles(fixture.Workspace.OrchestratorDirectory);
        var walBefore = HashWalFiles(fixture.Workspace.OrchestratorDirectory);

        var text = Run(fixture, "--rounds");
        Assert.Equal(0, text.Code);
        Assert.Contains("Developer | 2 | 1 | 1 | 0 | 0 | 100 | 10 | 20 | 1", text.Output);
        Assert.Contains("Reviewer | 1 | 0 | 0 | 0 | 1 | 200 | 50 | 40 | 0", text.Output);
        Assert.Contains("anthropic/claude | 2 | 0 | 1 | 0 | 1 | 300 | 60 | 60 | 0", text.Output);
        Assert.Contains("openai/gpt | 1 | 1 | 0 | 0 | 0 | 0 | 0 | 0 | 1", text.Output);

        var json = Run(fixture, "--rounds", "--json");
        Assert.Equal(0, json.Code);
        using var document = JsonDocument.Parse(json.Output);
        var rounds = document.RootElement.GetProperty("rounds");
        AssertRows(rounds.GetProperty("byRole"),
            ("Developer", 2, 100, 1), ("Reviewer", 1, 200, 0));
        AssertRows(rounds.GetProperty("byModel"),
            ("anthropic/claude", 2, 300, 0), ("openai/gpt", 1, 0, 1));
        Assert.Equal(1, rounds.GetProperty("byRole")[0].GetProperty("failed").GetInt32());
        Assert.Equal(1, rounds.GetProperty("byRole")[0].GetProperty("completed").GetInt32());
        Assert.Equal(1, rounds.GetProperty("byRole")[1].GetProperty("other").GetInt32());
        Assert.Equal(10, rounds.GetProperty("byRole")[0].GetProperty("cachedInputTokens").GetInt64());
        Assert.Equal(20, rounds.GetProperty("byRole")[0].GetProperty("outputTokens").GetInt64());
        Assert.Equal(50, rounds.GetProperty("byRole")[1].GetProperty("cachedInputTokens").GetInt64());
        Assert.Equal(40, rounds.GetProperty("byRole")[1].GetProperty("outputTokens").GetInt64());
        Assert.Equal(60, rounds.GetProperty("byModel")[0].GetProperty("cachedInputTokens").GetInt64());
        Assert.Equal(60, rounds.GetProperty("byModel")[0].GetProperty("outputTokens").GetInt64());
        Assert.Equal(before, HashFiles(fixture.Workspace.OrchestratorDirectory));
        foreach (var (path, hash) in HashWalFiles(fixture.Workspace.OrchestratorDirectory))
            Assert.True(hash == EmptyFileHash ||
                (walBefore.TryGetValue(path, out var original) && hash == original),
                $"SQLite WAL file changed and is not empty: {path}");
    }

    [Fact]
    public async Task WithoutRoundsOutputPinsDigestAndUnknownFlagShowsUsage()
    {
        using var fixture = await OwnerDigestTestFixture.CreateAsync();
        var originalText = Run(fixture);
        var originalJson = Run(fixture, "--json");
        Assert.Equal(0, originalText.Code);
        Assert.Equal(0, originalJson.Code);
        Assert.Equal(BaselineText, originalText.Output);
        Assert.Equal(BaselineJson, originalJson.Output);
        await AddRoundsAsync(fixture);

        var laterText = Run(fixture);
        var laterJson = Run(fixture, "--json");
        Assert.Equal(0, laterText.Code);
        Assert.Equal(0, laterJson.Code);
        // The added goal has in-window events and no decision; other fields stay fixed.
        Assert.Equal(BaselineText.Replace("none=4", "none=5", StringComparison.Ordinal), laterText.Output);
        Assert.Equal(BaselineJson.Replace("\"none\":4", "\"none\":5", StringComparison.Ordinal), laterJson.Output);
        Assert.DoesNotContain("Rounds by role", originalText.Output);
        using var document = JsonDocument.Parse(originalJson.Output);
        Assert.False(document.RootElement.TryGetProperty("rounds", out _));
        var exception = Assert.Throws<ArgumentException>(() =>
            CliOwnerDigestCommand.Parse(["owner-digest", "--bogus"]));
        Assert.Equal(CliCommandHelp.OwnerDigestUsage, exception.Message);
        Assert.Contains("--rounds", exception.Message);
    }

    // Fixed expectations for the existing digest fixture and legacy output.
    // Keep these independent of the candidate command and report implementation.
    private static readonly string BaselineText = string.Join(Environment.NewLine,
        "Owner digest [2026-09-24T00:00:00.0000000+00:00, 2026-09-25T00:00:00.0000000+00:00) | reverts=not tracked",
        "Goal | Landed UTC | Interventions H/A/O | Landing | Tail h | Mechanical h H/A/O",
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa | 2026-09-24T08:00:00.0000000+00:00 | 2/1/0 | correct | 4 | 1.5/1/0",
        "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb | 2026-09-24T12:00:00.0000000+00:00 | 0/0/0 | escape | 2 | 0/0/0",
        "Totals: landed=2 interventions=3 H/A/O=2/1/0 mean=1.5 correct=1 escapes=1 pending=0 correct-rate=0.5 tail-median-h=2 tail-p90-h=4 tail-known=2 tail-unknown=0 mechanical-h=2.5 H/A/O=1.5/1/0 unresolved-h=0",
        "Non-landed goals with interventions in window: 1", "Latest decisions:", "none=4", "");

    private static readonly string BaselineJson =
        "{\"since\":\"2026-09-24T00:00:00+00:00\",\"until\":\"2026-09-25T00:00:00+00:00\",\"reverts\":\"not tracked\",\"goals\":[" +
        "{\"goalId\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"landedAt\":\"2026-09-24T08:00:00+00:00\",\"landingSha\":\"aaaa\",\"interventions\":{\"human\":2,\"agent\":1,\"other\":0,\"total\":3},\"landingStatus\":\"correct\",\"tailHours\":4,\"mechanicalHours\":{\"human\":1.5,\"agent\":1,\"other\":0,\"total\":2.5},\"unresolvedHoldHours\":0,\"observedAfterLandingHours\":16}," +
        "{\"goalId\":\"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\",\"landedAt\":\"2026-09-24T12:00:00+00:00\",\"landingSha\":\"bbbb\",\"interventions\":{\"human\":0,\"agent\":0,\"other\":0,\"total\":0},\"landingStatus\":\"escape\",\"tailHours\":2,\"mechanicalHours\":{\"human\":0,\"agent\":0,\"other\":0,\"total\":0},\"unresolvedHoldHours\":0,\"observedAfterLandingHours\":12}]," +
        "\"totals\":{\"landedGoals\":2,\"interventions\":{\"human\":2,\"agent\":1,\"other\":0,\"total\":3},\"meanInterventionsPerLanding\":1.5,\"correctLandings\":1,\"escapes\":1,\"pending\":0,\"correctLandingRate\":0.5,\"tailMedianHours\":2,\"tailP90Hours\":4,\"knownTailCount\":2,\"unknownTailCount\":0,\"mechanicalHours\":{\"human\":1.5,\"agent\":1,\"other\":0,\"total\":2.5},\"unresolvedHoldHours\":0},\"nonLandedGoalsWithInterventions\":1,\"malformedLifecycleLines\":0,\"latestDecisions\":{\"entries\":[],\"none\":4}}" + Environment.NewLine;

    private static void AssertRows(JsonElement rows,
        params (string Key, int Rounds, long Input, int Unreported)[] expected)
    {
        Assert.Equal(expected.Length, rows.GetArrayLength());
        for (var index = 0; index < expected.Length; index++)
        {
            Assert.Equal(expected[index].Key, rows[index].GetProperty("key").GetString());
            Assert.Equal(expected[index].Rounds, rows[index].GetProperty("rounds").GetInt32());
            Assert.Equal(expected[index].Input, rows[index].GetProperty("inputTokens").GetInt64());
            Assert.Equal(expected[index].Unreported, rows[index].GetProperty("usageUnreported").GetInt32());
        }
    }

    private static async Task AddRoundsAsync(OwnerDigestTestFixture fixture)
    {
        var start = OwnerDigestTestFixture.Start;
        TaskDispatchSnapshot Dispatch(double hour, string provider, long? input, long? cached, long? output) =>
            new("worker", "command", "working-directory", start.AddHours(hour), provider,
                provider == "anthropic" ? "claude" : "gpt",
                ContextPackageReceipt: new WorkerContextPackageReceipt("package", [],
                    Value(input), Value(cached), Value(output)));
        ProgressEventSnapshot Event(string task, double hour, ProgressKind kind) =>
            new(RoundGoal, task, kind, "event", start.AddHours(hour));
        var goal = new GoalSnapshot(RoundGoal, "Round report", GoalStatus.Active,
            [new TaskSnapshot(DeveloperTask, "Develop", AgentRole.Developer, WorkTaskStatus.Completed,
                null, null, null, [], null, null, DispatchHistory:
                [Dispatch(1, "anthropic", 100, 10, 20), Dispatch(2, "openai", null, null, null)]),
             new TaskSnapshot(ReviewerTask, "Review", AgentRole.Reviewer, WorkTaskStatus.Running,
                null, null, null, [], null, null, DispatchHistory:
                [Dispatch(-2, "anthropic", 1000, 0, 500), Dispatch(5, "anthropic", 200, 50, 40)]),
             new TaskSnapshot(OutsideTask, "Outside", AgentRole.Reviewer, WorkTaskStatus.Completed,
                null, null, null, [], null, null, DispatchHistory:
                [Dispatch(25, "openai", 9999, 9999, 9999)])],
            [Event(DeveloperTask, 1.5, ProgressKind.TaskFailed),
             Event(DeveloperTask, 3, ProgressKind.TaskCompleted),
             Event(ReviewerTask, -1, ProgressKind.TaskRetried),
             Event(OutsideTask, 25.5, ProgressKind.TaskCompleted)]);
        await new SqliteOrchestratorStateRepository(fixture.Workspace.SqliteStatePath)
            .SaveGoalSnapshotsAsync([goal]);
    }

    private static ProviderUsageValue Value(long? count) => count is { } value
        ? ProviderUsageValue.Reported(value) : ProviderUsageValue.Unknown("unavailable");

    private static (int Code, string Output) Run(OwnerDigestTestFixture fixture,
        params string[] flags)
    {
        var args = new[] { "owner-digest", "--since", OwnerDigestTestFixture.Start.ToString("O"),
            "--until", OwnerDigestTestFixture.End.ToString("O") }.Concat(flags).ToArray();
        using var output = new StringWriter();
        var code = CliOwnerDigestCommand.Run(args, fixture.Workspace, fixture.Clock, output);
        return (code, output.ToString());
    }

    private static readonly string EmptyFileHash = Convert.ToHexString(SHA256.HashData([]));

    private static Dictionary<string, string> HashFiles(string root) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(path => !path.EndsWith("-shm", StringComparison.OrdinalIgnoreCase) &&
                !path.EndsWith("-wal", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(path => Path.GetRelativePath(root, path),
                path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))),
                StringComparer.OrdinalIgnoreCase);

    private static Dictionary<string, string> HashWalFiles(string root) =>
        Directory.EnumerateFiles(root, "*-wal", SearchOption.AllDirectories)
            .ToDictionary(path => Path.GetRelativePath(root, path),
                path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))),
                StringComparer.OrdinalIgnoreCase);
}
