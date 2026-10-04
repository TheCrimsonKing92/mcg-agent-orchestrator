using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: every test owns all SQLite stores and uses the fixture clock.
public sealed class CliOwnerDigestCommandFitTests
{
    [Fact(DisplayName = "Fit rows report exact outcome counts, usage, landing and shadow differences")]
    public async Task WindowRowsMatchHandComputedOutcomes()
    {
        using var fixture = await OwnerDigestTestFixture.CreateAsync();
        await OwnerDigestFitFixture.AddRoundsAsync(fixture, "adequate");
        var json = OwnerDigestFitFixture.Run(fixture, "--json");
        using var document = JsonDocument.Parse(json);
        var rows = document.RootElement.GetProperty("rounds").GetProperty("fitByRoleModelTaskClass");
        // Calculated from the fixture: two docs rounds share one landed goal; the third
        // docs round belongs to an unlanded goal and has no usage. The pre-window task is excluded.
        Assert.Equal("""
            [{"role":"Developer","provider":"openai","model":"gpt","taskClass":"docs-only","rounds":3,"firstPassRounds":2,"reworkRounds":1,"reworkRate":0.3333333333333333,"reworkByCauseFamily":{"ReviewFinding":1},"stopCauses":{"Completed":1,"Superseded":2},"inputTokens":30,"cachedInputTokens":6,"outputTokens":9,"usageUnreported":1,"goalsLanded":1,"distinctGoals":2,"shadowDiffersRounds":2},{"role":"Developer","provider":"openai","model":"gpt","taskClass":"pure-move","rounds":1,"firstPassRounds":0,"reworkRounds":1,"reworkRate":1,"reworkByCauseFamily":{"GateRed":1},"stopCauses":{"Completed":1},"inputTokens":30,"cachedInputTokens":6,"outputTokens":9,"usageUnreported":0,"goalsLanded":1,"distinctGoals":1,"shadowDiffersRounds":1},{"role":"Reviewer","provider":"anthropic","model":"claude","taskClass":"other","rounds":1,"firstPassRounds":1,"reworkRounds":0,"reworkRate":0,"reworkByCauseFamily":{},"stopCauses":{"Failed":1},"inputTokens":40,"cachedInputTokens":8,"outputTokens":12,"usageUnreported":0,"goalsLanded":1,"distinctGoals":1,"shadowDiffersRounds":0},{"role":"Reviewer","provider":"anthropic","model":"claude","taskClass":"unrecorded","rounds":1,"firstPassRounds":1,"reworkRounds":0,"reworkRate":0,"reworkByCauseFamily":{},"stopCauses":{"Completed":1},"inputTokens":50,"cachedInputTokens":10,"outputTokens":15,"usageUnreported":0,"goalsLanded":0,"distinctGoals":1,"shadowDiffersRounds":0}]
            """, rows.GetRawText());
        var text = OwnerDigestFitFixture.FitText(OwnerDigestFitFixture.Run(fixture));
        Assert.Equal(string.Join(Environment.NewLine,
            OwnerDigestFitFixture.Heading,
            "Developer | openai/gpt | docs-only | 3 | 2 | 1 | 0.333 | ReviewFinding=1 | Completed=1, Superseded=2 | 30 | 6 | 9 | 1 | 1/2 | 2",
            "Developer | openai/gpt | pure-move | 1 | 0 | 1 | 1 | GateRed=1 | Completed=1 | 30 | 6 | 9 | 0 | 1/1 | 1",
            "Reviewer | anthropic/claude | other | 1 | 1 | 0 | 0 | none | Failed=1 | 40 | 8 | 12 | 0 | 1/1 | 0",
            "Reviewer | anthropic/claude | unrecorded | 1 | 1 | 0 | 0 | none | Completed=1 | 50 | 10 | 15 | 0 | 0/1 | 0", ""), text);
    }

    [Fact(DisplayName = "An empty window emits the fit section with no rows")]
    public async Task EmptyWindowHasStableTextAndJsonSection()
    {
        using var fixture = await OwnerDigestTestFixture.CreateAsync();
        using var json = JsonDocument.Parse(OwnerDigestFitFixture.Run(fixture, "--json"));
        Assert.Empty(json.RootElement.GetProperty("rounds").GetProperty("fitByRoleModelTaskClass").EnumerateArray());
        Assert.Equal(OwnerDigestFitFixture.Heading + Environment.NewLine + "none" + Environment.NewLine,
            OwnerDigestFitFixture.FitText(OwnerDigestFitFixture.Run(fixture)));
    }
}

internal static class OwnerDigestFitFixture
{
    internal const string Heading = "Fit by role, model and task class | Provider/model | Task class | Rounds | First pass | Rework | Rework rate | Rework families | Stop causes | Input | Cached input | Output | Usage unreported | Goals landed/distinct | Shadow differs";
    private static DateTimeOffset Start => OwnerDigestTestFixture.Start;

    internal static async Task AddRoundsAsync(OwnerDigestTestFixture fixture, string fitNote)
    {
        TaskDispatchSnapshot Dispatch(double hour, AgentRole role, DispatchTaskClass? taskClass,
            long? input, string effort = "medium") => new("worker", "command", "root", Start.AddHours(hour),
            role == AgentRole.Developer ? "openai" : "anthropic", role == AgentRole.Developer ? "gpt" : "claude",
            ReasoningEffort: effort, ContextPackageReceipt: new WorkerContextPackageReceipt("package", [],
                Value(input), Value(input / 5), Value(input * 3 / 10)),
            ShadowDecision: taskClass is { } recorded
                ? ShadowCascadePolicy.Decide(role, recorded, role == AgentRole.Developer ? "openai" : "anthropic",
                    role == AgentRole.Developer ? "gpt" : "claude", effort) : null);
        TaskSnapshot Task(string id, AgentRole role, params TaskDispatchSnapshot[] dispatches)
        {
            var verification = new TaskVerificationSnapshot("command", "root", 0,
                $"Model fit: provider/model - {fitNote} - fixed outcomes", "", Start.AddHours(7), ModelFitNote: fitNote);
            return new(id, "Work", role, WorkTaskStatus.Completed, null, null, verification, [verification],
                dispatches[^1], null, DispatchHistory: dispatches);
        }
        ProgressEventSnapshot Event(string goal, string task, double hour, ProgressKind kind, string message = "done") =>
            new(goal, task, kind, message, Start.AddHours(hour));
        var a = OwnerDigestTestFixture.GoalA;
        var c = OwnerDigestTestFixture.GoalC;
        var landed = new GoalSnapshot(a, "Landed", GoalStatus.Active,
            [Task("developer-a", AgentRole.Developer,
                 Dispatch(3, AgentRole.Developer, DispatchTaskClass.PureMove, 30),
                 Dispatch(1, AgentRole.Developer, DispatchTaskClass.DocsOnly, 10),
                 Dispatch(2, AgentRole.Developer, DispatchTaskClass.DocsOnly, 20)),
             Task("reviewer-a", AgentRole.Reviewer, Dispatch(5, AgentRole.Reviewer, DispatchTaskClass.Other, 40)),
             Task("outside", AgentRole.Developer, Dispatch(-1, AgentRole.Developer, DispatchTaskClass.GateEngine, 999))],
            [Event(a, "developer-a", 1.5, ProgressKind.TaskRetried, "auto-review-retry round 2"),
             Event(a, "developer-a", 2.5, ProgressKind.TaskRetried, "Acceptance criteria unmet; retrying task with feedback"),
             Event(a, "developer-a", 3.5, ProgressKind.TaskCompleted),
             Event(a, "reviewer-a", 5.5, ProgressKind.TaskFailed),
             Event(a, "outside", -0.5, ProgressKind.TaskCompleted)]);
        var unlanded = new GoalSnapshot(c, "Unlanded", GoalStatus.Active,
            [Task("developer-c", AgentRole.Developer, Dispatch(4, AgentRole.Developer, DispatchTaskClass.DocsOnly, null, "low")),
             Task("reviewer-c", AgentRole.Reviewer, Dispatch(6, AgentRole.Reviewer, null, 50))],
            [Event(c, "developer-c", 4.5, ProgressKind.TaskCompleted),
             Event(c, "reviewer-c", 6.5, ProgressKind.TaskCompleted)]);
        await new SqliteOrchestratorStateRepository(fixture.Workspace.SqliteStatePath)
            .SaveGoalSnapshotsAsync([landed, unlanded]);
    }

    internal static string Run(OwnerDigestTestFixture fixture, params string[] flags)
    {
        var args = new[] { "owner-digest", "--rounds", "--since", Start.ToString("O"),
            "--until", OwnerDigestTestFixture.End.ToString("O") }.Concat(flags).ToArray();
        using var output = new StringWriter();
        Assert.Equal(0, CliOwnerDigestCommand.Run(args, fixture.Workspace, fixture.Clock, output));
        return output.ToString();
    }

    internal static string FitText(string text)
    {
        var index = text.IndexOf(Heading, StringComparison.Ordinal);
        Assert.True(index >= 0, "Fit section was not rendered.");
        var end = text.IndexOf("Rounds by role |", index, StringComparison.Ordinal);
        Assert.True(end > index, "Legacy rounds section must follow the fit section.");
        return text[index..end];
    }

    private static ProviderUsageValue Value(long? value) => value is { } count
        ? ProviderUsageValue.Reported(count) : ProviderUsageValue.Unknown("unreported");
}
