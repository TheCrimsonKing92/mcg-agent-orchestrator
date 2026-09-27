using System.Security.Cryptography;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class ConductorVerbStatusTests
{
    [Fact]
    public async Task StatusReadsCrlfEventsGoalsWaitsAndLandingWithoutMutatingSourceFiles()
    {
        using var fixture = new ConductorVerbStartTests.Fixture();
        var workspace = fixture.Workspace;
        var kernel = new AgentOrchestratorKernel(new SystemClock());
        var goal = kernel.CreateGoal(new GoalId("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"), "Goal A");
        var secondGoal = kernel.CreateGoal(new GoalId("cccccccccccccccccccccccccccccccc"), "Goal C");
        kernel.RequestHumanInput(goal.Id, goal.Tasks[0].Id, "Which branch?");
        kernel.RequestHumanInput(secondGoal.Id, null, "Review risk?", HumanWaitKind.RiskReview);
        await InfrastructureTestSupport.CreateMigratedStateRepository(workspace.SqliteStatePath).SaveAsync(kernel);

        Directory.CreateDirectory(workspace.LogDirectory);
        var start = new DateTimeOffset(2026, 9, 26, 8, 0, 0, TimeSpan.Zero);
        var stop = start.AddHours(2);
        var events = new[]
        {
            new { timestamp = start, eventKind = "loop-start", goalId = (string?)null, detail = "LOOP_START" },
            new { timestamp = start.AddMinutes(10), eventKind = "supervisor-build", goalId = (string?)null, detail = "SUPERVISOR_BUILD {\"commitSha\":\"abc123\"}" },
            new { timestamp = start.AddHours(1), eventKind = "tick-load-recovered", goalId = (string?)null, detail = "TICK tick=4" },
            new { timestamp = stop, eventKind = "loop-stop", goalId = (string?)null, detail = "LOOP_STOP tick=5 reason=operator-stop\r" }
        };
        File.WriteAllText(workspace.ConductEventsLogPath,
            string.Join("\r\n", events.Select(item => JsonSerializer.Serialize(item))) + "\r\n");
        var lockFile = Path.Combine(workspace.OrchestratorDirectory, "conduct-loop.lock");
        File.WriteAllText(lockFile, "old lock");
        Directory.CreateDirectory(workspace.GoalLifecycleEventsDirectory);
        File.WriteAllText(Path.Combine(workspace.GoalLifecycleEventsDirectory, "landings.jsonl"),
            JsonSerializer.Serialize(new { eventType = "GoalLanded", goalId = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", timestamp = start.AddMinutes(30) }) + "\r\n");
        var original = new[] { workspace.SqliteStatePath, lockFile, workspace.ConductEventsLogPath }
            .ToDictionary(path => path, Hash);

        using var output = new StringWriter();
        var exit = CliConductorCommand.Run(["conductor", "status"], workspace,
            lockProbe: new ConductorVerbStartTests.FixedProbe(null), bootTime: () => start.AddDays(-1), output: output);
        Assert.Equal(0, exit);
        var text = output.ToString();
        Assert.Contains("Conductor: stopped", text);
        Assert.Contains($"Stopped since {stop:O}; reason=operator-stop{Environment.NewLine}", text);
        Assert.Contains("Supervisor build: abc123", text);
        Assert.Contains($"Latest tick: {start.AddHours(1):O}", text);
        Assert.Contains($"  {goal.Id.Value} | {goal.Status} | role={goal.Tasks[0].RequiredRole}{Environment.NewLine}", text);
        Assert.Contains($"  {secondGoal.Id.Value} | {secondGoal.Status} | role={secondGoal.Tasks[0].RequiredRole}{Environment.NewLine}", text);
        Assert.Contains("Pending clarifications: 1; human waits: 1", text);
        Assert.Contains("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", text);
        foreach (var (path, hash) in original) Assert.Equal(hash, Hash(path));

        output.GetStringBuilder().Clear();
        Assert.Equal(0, CliConductorCommand.Run(["conductor", "status"], workspace,
            lockProbe: new ConductorVerbStartTests.FixedProbe(null), bootTime: () => stop.AddHours(1), output: output));
        Assert.Contains($"stopped since host restart at {stop.AddHours(1):O}", output.ToString());
        foreach (var (path, hash) in original) Assert.Equal(hash, Hash(path));
    }

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
}
