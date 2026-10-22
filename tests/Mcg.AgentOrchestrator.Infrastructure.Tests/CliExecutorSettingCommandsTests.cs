using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: isolated workspace/database and AsyncLocal stderr capture.
public sealed class CliExecutorSettingCommandsTests
{
    [Theory]
    [InlineData("executor-set-enabled", "mcg-exec-1", "1")]
    [InlineData("executor-set-enabled", "mcg-exec-1", "yes")]
    [InlineData("executor-set-enabled", "mcg-exec-1", " true")]
    [InlineData("executor-set-enabled", "mcg-exec-1", "false ")]
    [InlineData("executor-set-enabled", "mcg-exec-1", "")]
    [InlineData("executor-set-enabled", "mcg-exec-1")]
    [InlineData("executor-set-enabled", "mcg-exec-1", "false", "extra")]
    [InlineData("executor-set-enabled", "no-such", "false")]
    [InlineData("executor-set-slots", "mcg-exec-1", "0")]
    [InlineData("executor-set-slots", "mcg-exec-1", "17")]
    [InlineData("executor-set-slots", "mcg-exec-1", "2.5")]
    [InlineData("executor-set-slots", "mcg-exec-1", "two")]
    [InlineData("executor-set-slots", "mcg-exec-1", "2147483648")]
    [InlineData("executor-set-slots", "no-such", "2")]
    [InlineData("focused-evidence-set")]
    [InlineData("focused-evidence-set", "--mode", "on")]
    [InlineData("focused-evidence-set", "--mode", "Shadow")]
    [InlineData("focused-evidence-set", "--sample-every", "0")]
    [InlineData("focused-evidence-set", "--sample-every", "x")]
    [InlineData("focused-evidence-set", "--sample-every", "2147483648")]
    [InlineData("executor-set-slots", "mcg-exec-1")]
    [InlineData("executor-set-slots", "mcg-exec-1", "2", "extra")]
    [InlineData("focused-evidence-set", "extra", "--mode", "off")]
    [InlineData("focused-evidence-set", "--mode")]
    public async Task Submit_InvalidArguments_RefusesWithoutIntentOrFileChange(params string[] args)
    {
        using var fixture = new ExecutorSettingIntentScenarioTests.Harness();
        var original = File.ReadAllBytes(fixture.Path);
        using var output = new StringWriter();
        var exit = -1;
        var error = AsyncLocalConsoleRouter.CaptureError(() => exit = CliExecutorSettingCommands.Run(args,
            fixture.Workspace, output, new ExecutorSettingIntentScenarioTests.FixedTime()));
        Assert.Equal(1, exit);
        Assert.Equal("", output.ToString());
        Assert.StartsWith("Error: ", error);
        Assert.Empty(await fixture.Intents.ListForGoalAsync(OperatorIntentScopes.Workspace));
        Assert.Equal(original, File.ReadAllBytes(fixture.Path));
    }

    [Fact]
    public void Submit_JsonEnvelope_HasDirectivePropertyOrderAndRemainsPending()
    {
        using var fixture = new ExecutorSettingIntentScenarioTests.Harness();
        using var executorOutput = new StringWriter();
        using var directiveOutput = new StringWriter();
        var time = new ExecutorSettingIntentScenarioTests.FixedTime();
        Assert.Equal(0, CliExecutorSettingCommands.Run(["executor-set-slots", "mcg-exec-1", "2", "--json"], fixture.Workspace, executorOutput, time));
        Assert.Equal(0, CliDirectiveCommands.Run(["directive", "handoff", "--json"], fixture.Workspace, directiveOutput, time));
        using var executor = JsonDocument.Parse(executorOutput.ToString());
        using var directive = JsonDocument.Parse(directiveOutput.ToString());
        Assert.Equal(directive.RootElement.EnumerateObject().Select(p => p.Name), executor.RootElement.EnumerateObject().Select(p => p.Name));
        Assert.Equal(directive.RootElement.GetProperty("payload").EnumerateObject().Select(p => p.Name),
            executor.RootElement.GetProperty("payload").EnumerateObject().Select(p => p.Name));
        Assert.Equal("mcg.operator-intent", executor.RootElement.GetProperty("schema").GetString());
        Assert.Equal("Pending", executor.RootElement.GetProperty("payload").GetProperty("status").GetString());
    }

    [Theory]
    [InlineData("executor-set-slots")]
    [InlineData("executor-set-enabled")]
    [InlineData("focused-evidence-set")]
    public void Registration_DocumentedFlags_AreAcceptedAndUnknownFlagsShowUsage(string verb)
    {
        Assert.Contains(verb, CliArgumentParser.RecognizedCommands);
        Assert.True(CliExecutorSettingCommands.IsCommand([verb]));
        Assert.Equal(CliCommandCapability.Execution, CliCommandCapabilities.Classify([verb]));
        var flags = new List<string> { "--operator-actor", "--actor-kind", "--idempotency-key", "--json", "--help", "-h" };
        if (verb == "focused-evidence-set") flags.AddRange(["--mode", "--sample-every"]);
        foreach (var flag in flags)
            CliCommandHelp.ThrowIfInvalidFlags([verb, flag]);
        var error = Assert.Throws<ArgumentException>(() => CliCommandHelp.ThrowIfInvalidFlags([verb, "--zz-not-a-real-flag"]));
        Assert.Contains("Usage:", error.Message);
    }

    [Fact]
    public async Task Submit_SameIdempotencyKey_ReturnsPersistedIntentWithoutWritingFile()
    {
        using var fixture = new ExecutorSettingIntentScenarioTests.Harness();
        var original = File.ReadAllBytes(fixture.Path);
        var first = await fixture.Queue("executor-set-slots", "mcg-exec-1", "2", "--idempotency-key", "same-key");
        var second = await fixture.Queue("executor-set-slots", "mcg-exec-1", "2", "--idempotency-key", "same-key");
        Assert.Equal(first.Id, second.Id);
        Assert.Single(await fixture.Intents.ListForGoalAsync(OperatorIntentScopes.Workspace));
        Assert.Equal(original, File.ReadAllBytes(fixture.Path));
    }

    [Theory]
    [InlineData("executor-set-slots", "mcg-exec-1", "1")]
    [InlineData("executor-set-enabled", "mcg-exec-1", "true")]
    [InlineData("executor-set-enabled", "mcg-exec-1", "FALSE")]
    [InlineData("executor-set-slots", "mcg-exec-1", "16")]
    [InlineData("focused-evidence-set", "--mode", "remote-first")]
    [InlineData("focused-evidence-set", "--sample-every", "1")]
    [InlineData("focused-evidence-set", "--sample-every", "2147483647")]
    public async Task Submit_ValidBounds_QueuesExactlyOneIntent(params string[] args)
    {
        using var fixture = new ExecutorSettingIntentScenarioTests.Harness();
        var original = File.ReadAllBytes(fixture.Path);
        await fixture.Queue(args);
        Assert.Single(await fixture.Intents.ListForGoalAsync(OperatorIntentScopes.Workspace));
        Assert.Equal(original, File.ReadAllBytes(fixture.Path));
    }
}
