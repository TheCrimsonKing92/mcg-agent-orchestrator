using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: isolated state and intent databases, fixed clock, AsyncLocal console capture.
public sealed class CliGateCancelCommandTests
{
    [Theory]
    [InlineData(null, false)]
    [InlineData("ABC-123", false)]
    [InlineData(null, true)]
    public async Task Submit_ValidRequest_QueuesTypedPendingIntent(string? attempt, bool json)
    {
        using var f = new Fixture();
        var output = f.Submit([.. (attempt is null ? Array.Empty<string>() : new[] { "--attempt", attempt }),
            .. (json ? new[] { "--json" } : Array.Empty<string>())], out var exit);
        Assert.Equal(0, exit);
        var intent = Assert.Single(await f.Store.ListForGoalAsync(f.First.Id.Value));
        Assert.Equal("gate-cancel", intent.Verb);
        Assert.Equal(f.First.Id.Value, intent.GoalId);
        Assert.Null(intent.TaskId);
        Assert.Equal(OperatorIntentStatus.Pending, intent.Status);
        Assert.Equal(Fixture.Now, intent.CreatedAt);
        var payload = JsonSerializer.Deserialize<GateCancelOperatorIntentPayload>(intent.PayloadJson, OperatorIntentJson.Options)!;
        Assert.Equal("stale candidate", payload.Reason);
        Assert.Equal(attempt, payload.AttemptId);
        using var raw = JsonDocument.Parse(intent.PayloadJson);
        Assert.Equal("stale candidate", raw.RootElement.GetProperty("reason").GetString());
        Assert.True(raw.RootElement.TryGetProperty("attemptId", out _));
        if (json)
        {
            using var envelope = JsonDocument.Parse(output);
            Assert.Equal("mcg.operator-intent", envelope.RootElement.GetProperty("schema").GetString());
            Assert.Equal("Pending", envelope.RootElement.GetProperty("payload").GetProperty("status").GetString());
        }
        else Assert.Equal($"Operator intent queued: id={intent.Id} verb=gate-cancel goal={f.First.Id.Value} status=Pending; poll with operator-intent-status {intent.Id} (or add --wait).",
            output.Split(Environment.NewLine)[0]);
    }

    [Fact]
    public async Task Submit_RepeatedIdempotencyKey_KeepsOneRow()
    {
        using var f = new Fixture();
        var first = f.Submit(["--idempotency-key", "same"], out var firstExit);
        var second = f.Submit(["--idempotency-key", "same"], out var secondExit);
        Assert.Equal(0, firstExit);
        Assert.Equal(0, secondExit);
        Assert.Equal(first, second);
        Assert.Single(await f.Store.ListForGoalAsync(f.First.Id.Value));
    }

    [Theory]
    [InlineData("prefix")]
    [InlineData("missing")]
    [InlineData("empty")]
    [InlineData("unknown")]
    [InlineData("ambiguous")]
    [InlineData("slash")]
    [InlineData("long")]
    [InlineData("positional")]
    [InlineData("flag")]
    [InlineData("value")]
    [InlineData("no-text")]
    public async Task Submit_InvalidRequest_RejectsWithoutEnqueue(string scenario)
    {
        using var f = new Fixture();
        string[] args = ["gate-cancel", f.First.Id.Value[..8], "--text-file", f.TextPath];
        var expected = CliCommandHelp.GateCancelUsage;
        switch (scenario)
        {
            case "prefix": args = ["gate-cancel"]; break;
            case "missing": File.Delete(f.TextPath); expected = "gate-cancel requires an existing --text-file."; break;
            case "no-text": args = ["gate-cancel", f.First.Id.Value[..8]]; expected = "gate-cancel requires an existing --text-file."; break;
            case "empty": File.WriteAllText(f.TextPath, " \r\n "); expected = "gate-cancel requires a nonempty --text-file."; break;
            case "unknown": args[1] = "ffffeeee"; expected = "unknown-goal: ffffeeee"; break;
            case "ambiguous": args[1] = "1234567"; expected = $"ambiguous-goal-prefix: {f.First.Id.Value}, {f.Second.Id.Value}"; break;
            case "slash": args = [.. args, "--attempt", "bad/id"]; expected = "invalid-attempt-id"; break;
            case "long": args = [.. args, "--attempt", new string('a', 65)]; expected = "invalid-attempt-id"; break;
            case "positional": args = [.. args, "extra"]; break;
            case "flag": args = [.. args, "--zz-not-a-real-flag"]; break;
            case "value": args = [.. args, "--attempt"]; expected = $"--attempt requires a value. {CliCommandHelp.GateCancelUsage}"; break;
        }
        var exit = -1;
        var stdout = "";
        var stderr = InfrastructureTestSupport.CaptureConsoleError(() =>
            stdout = InfrastructureTestSupport.CaptureConsole(() => exit = CliGateCancelCommand.Run(args, f.Workspace, new FixedTime())));
        Assert.Equal(1, exit);
        Assert.Equal("", stdout);
        Assert.StartsWith("Error: ", stderr);
        Assert.Contains(expected, stderr);
        Assert.Empty(await f.Store.ListForGoalAsync(f.First.Id.Value));
        Assert.Empty(await f.Store.ListForGoalAsync(f.Second.Id.Value));
    }

    [Fact]
    public void Registration_StartupRouteAndHelp_ExposeExecutionCommand()
    {
        Assert.Contains("gate-cancel", CliArgumentParser.RecognizedCommands);
        Assert.True(CliGateCancelCommand.IsCommand(["GATE-CANCEL"]));
        Assert.False(CliGateCancelCommand.IsCommand([]));
        Assert.Equal(CliCommandCapability.Execution, CliCommandCapabilities.Classify(["gate-cancel"]));
        Assert.False(CliOperatorIntentRoute.IsServed(["gate-cancel"]));
        var help = InfrastructureTestSupport.CaptureConsole(() => Assert.True(CliCommandHelp.TryPrintStartupHelp(["help", "gate-cancel"])));
        Assert.Contains(CliCommandHelp.GateCancelUsage, help.Split(Environment.NewLine));
        foreach (var flag in new[] { "--text-file", "--attempt", "--idempotency-key", "--operator-actor", "--actor-kind", "--json", "--help", "-h" })
            CliCommandHelp.ThrowIfInvalidFlags(["gate-cancel", flag]);
        Assert.Contains("Usage:", Assert.Throws<ArgumentException>(() =>
            CliCommandHelp.ThrowIfInvalidFlags(["gate-cancel", "--zz-not-a-real-flag"])).Message);
        var program = File.ReadAllText(Path.Combine(VerifiedRepositoryRoot.Find(), "src", "Mcg.AgentOrchestrator.App", "Program.cs"));
        var route = program.IndexOf("if (CliGateCancelCommand.IsCommand(startupArgs))", StringComparison.Ordinal);
        Assert.True(route > program.IndexOf("if (CliExecutorSettingCommands.IsCommand(startupArgs))", StringComparison.Ordinal));
        Assert.True(route < program.IndexOf("ProgramStartupLifecycle.EnsureStateDbInitialized(", StringComparison.Ordinal));
    }

    private sealed class FixedTime : TimeProvider { public override DateTimeOffset GetUtcNow() => Fixture.Now; }
    private sealed class Clock : IClock { public DateTimeOffset UtcNow => Fixture.Now; }
    private sealed class Fixture : IDisposable
    {
        internal static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch.AddDays(20000);
        private readonly string root = Path.Combine(Path.GetTempPath(), "gate-cancel-cli-" + Guid.NewGuid().ToString("N"));
        internal OrchestratorWorkspace Workspace { get; }
        internal SqliteOperatorIntentStore Store { get; }
        internal Goal First { get; }
        internal Goal Second { get; }
        internal string TextPath { get; }
        internal Fixture()
        {
            Directory.CreateDirectory(root);
            Workspace = OrchestratorWorkspace.ForDirectory(root);
            var kernel = new AgentOrchestratorKernel(new Clock());
            First = kernel.CreateGoal(new GoalId("12345670" + new string('a', 24)), "First",
                [new TaskSpec(TaskId.New(), "Verify first goal", AgentRole.Tester)]);
            Second = kernel.CreateGoal(new GoalId("12345671" + new string('b', 24)), "Second",
                [new TaskSpec(TaskId.New(), "Verify second goal", AgentRole.Tester)]);
            InfrastructureTestSupport.CreateMigratedStateRepository(Workspace.SqliteStatePath).SaveAsync(kernel).GetAwaiter().GetResult();
            Store = SqliteOperatorIntentStore.ForDirectories(Workspace.OrchestratorDirectory, Workspace.LogDirectory);
            TextPath = Path.Combine(root, "reason.txt");
            File.WriteAllText(TextPath, "  stale candidate\r\n");
        }
        internal string Submit(string[] flags, out int exit)
        {
            var result = -1;
            var output = InfrastructureTestSupport.CaptureConsole(() => result = CliGateCancelCommand.Run(
                ["gate-cancel", First.Id.Value[..8], "--text-file", TextPath, .. flags], Workspace, new FixedTime()));
            exit = result;
            return output;
        }
        public void Dispose() => SharedTestSupport.RemoveTempDirectory(root);
    }
}
