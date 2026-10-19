using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each fixture owns its SQLite databases, files and clock.
public sealed class ProviderResetIntentTests
{
    private const string ResetText = "2026-10-20T21:03:00-05:00";

    [Theory]
    [InlineData("offset", "ISO-8601")]
    [InlineData("missing", "existing --text-file")]
    [InlineData("empty", "nonempty --text-file")]
    [InlineData("hold", "no-current-hold")]
    [InlineData("ambiguous", "ambiguous-binding")]
    public async Task InvalidSubmissionPrintsReasonAndFilesNoIntent(string scenario, string reason)
    {
        using var fixture = new Fixture(held: scenario != "hold");
        if (scenario == "empty") File.WriteAllText(fixture.TextPath, " \r\n");
        var target = "xAI";
        if (scenario == "ambiguous")
        {
            fixture.Exhaust("Anthropic", "C:\\profiles\\first");
            fixture.Exhaust("Anthropic", "C:\\profiles\\second");
            target = "Anthropic";
        }
        var error = Assert.Throws<ArgumentException>(() => fixture.Submit(target,
            scenario == "offset" ? "2026-10-20T21:03:00" : ResetText,
            scenario == "missing" ? Path.Combine(fixture.Root, "missing.txt") : fixture.TextPath));
        Assert.Contains(reason, error.Message);
        if (scenario == "ambiguous")
        {
            Assert.Contains("anthropic::c:\\profiles\\first", error.Message);
            Assert.Contains("anthropic::c:\\profiles\\second", error.Message);
        }
        foreach (var goal in fixture.Kernel.Goals)
            Assert.Empty(await fixture.Store.ListForGoalAsync(goal.Id.Value));
    }

    [Theory]
    [InlineData("xai-tester")]
    [InlineData("xAI")]
    [InlineData("xai::<provider-default>")]
    [InlineData("XAI::<PROVIDER-DEFAULT>")]
    public async Task TargetsResolveToTheHoldSource(string target)
    {
        using var fixture = new Fixture();
        fixture.Submit(target, ResetText, fixture.TextPath);
        var intent = Assert.Single(await fixture.Store.ListForGoalAsync(fixture.Source.Id.Value));
        Assert.Equal(OperatorIntentVerbs.ProviderReset, intent.Verb);
        Assert.Equal(fixture.Source.Id.Value, intent.GoalId);
        Assert.Equal(fixture.Source.Tasks.Single().Id.Value, intent.TaskId);
        var payload = JsonSerializer.Deserialize<ProviderResetOperatorIntentPayload>(intent.PayloadJson, OperatorIntentJson.Options)!;
        Assert.Equal("xai::<provider-default>", payload.BindingKey);
        Assert.Equal(DateTimeOffset.Parse("2026-10-21T02:03:00Z"), payload.ResetAt);
        Assert.Equal("Provider usage page confirms this reset.", payload.Rationale);
        Assert.Empty(fixture.Markers());
    }

    [Fact]
    public async Task ApplyIsIdempotentAndMarkerSurvivesRealStateDatabaseRoundTrip()
    {
        using var fixture = new Fixture();
        fixture.Submit("xAI", ResetText, fixture.TextPath);
        var intent = Assert.Single(await fixture.Store.ListForGoalAsync(fixture.Source.Id.Value));
        var coordinator = new OperatorIntentCoordinator(fixture.Store, utcNow: () => fixture.Clock.UtcNow);
        Assert.True(coordinator.ExecutePending(fixture.Kernel, fixture.Source).MutatedGoalState);
        var marker = Assert.Single(fixture.Markers());
        Assert.Contains("reset_at=2026-10-21T02:03:00.0000000+00:00 source=operator intent=" + intent.Id, marker.Message);
        ProviderResetIntentHandler.Apply(fixture.Kernel, fixture.Source, intent, fixture.Clock.UtcNow);
        Assert.Single(fixture.Markers());
        var repository = InfrastructureTestSupport.CreateMigratedStateRepository(fixture.Context.Workspace.SqliteStatePath);
        await repository.SaveAsync(fixture.Kernel);
        var restored = await repository.LoadAsync();
        var restoredMarker = Assert.Single(restored.Goals.SelectMany(goal => goal.Timeline)
            .Where(evt => ProviderBudgetHoldSchedules.TryParseReset(evt, out _)));
        Assert.Equal(marker, restoredMarker);
        Assert.Equal(fixture.Schedule(), ProviderBudgetHoldSchedules.Evaluate(restored.Goals, fixture.Hold, fixture.Clock.UtcNow));
        coordinator.CompletePersisted([fixture.Source.Id]);
        Assert.Equal(OperatorIntentStatus.Applied, (await fixture.Store.GetAsync(intent.Id))!.Status);
        // A durable typed application marker also makes a coordinator replay a no-op.
        Assert.False(new OperatorIntentCoordinator(fixture.Store).ExecutePending(restored, restored.GetGoal(fixture.Source.Id)).MutatedGoalState);
        Assert.Single(restored.Goals.SelectMany(goal => goal.Timeline).Where(evt => ProviderBudgetHoldSchedules.TryParseReset(evt, out _)));
    }

    [Theory]
    [InlineData(false, "no-current-hold")]
    [InlineData(true, "reset-too-far")]
    public async Task ApplyRevalidatesHoldAndRejectsMoreThanThirtyOneDays(bool tooFar, string expected)
    {
        using var fixture = new Fixture();
        fixture.Submit("xAI", tooFar ? "2026-12-01T00:00:00Z" : ResetText, fixture.TextPath);
        var intent = Assert.Single(await fixture.Store.ListForGoalAsync(fixture.Source.Id.Value));
        if (!tooFar) fixture.Succeed();
        var result = new OperatorIntentCoordinator(fixture.Store, utcNow: () => fixture.Clock.UtcNow)
            .ExecutePending(fixture.Kernel, fixture.Source);
        Assert.Contains(result.ProgressLines, line => line.Contains(expected, StringComparison.Ordinal));
        Assert.Equal(OperatorIntentStatus.Rejected, (await fixture.Store.GetAsync(intent.Id))!.Status);
        Assert.Empty(fixture.Markers());
    }

    [Fact]
    public async Task PastOperatorResetIsDueImmediatelyAndSubmissionIsIdempotent()
    {
        using var fixture = new Fixture();
        fixture.Submit("xAI", "2026-10-17T10:00:00Z", fixture.TextPath, "--idempotency-key", "same-reset");
        fixture.Submit("xAI", "2026-10-17T10:00:00Z", fixture.TextPath, "--idempotency-key", "same-reset");
        Assert.Single(await fixture.Store.ListForGoalAsync(fixture.Source.Id.Value));
        new OperatorIntentCoordinator(fixture.Store, utcNow: () => fixture.Clock.UtcNow).ExecutePending(fixture.Kernel, fixture.Source);
        Assert.Single(fixture.Markers());
        Assert.Equal("operator", fixture.Schedule().ResetSource);
        Assert.True(fixture.Schedule().NextProbeAt < fixture.Clock.UtcNow);
    }

    [Fact]
    public void CommandCatalogAndHelpExposeTheVerb()
    {
        Assert.Contains("provider-reset", CliArgumentParser.RecognizedCommands);
        var output = InfrastructureTestSupport.CaptureConsole(() =>
            Assert.True(CliCommandHelp.TryPrintStartupHelp(["help", "provider-reset"])));
        Assert.Contains(CliProviderResetCommand.Usage, output.Split(Environment.NewLine));
    }

    private sealed class FakeClock : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.Parse("2026-10-17T11:07:00Z");
    }

    private sealed class Fixture : IDisposable
    {
        internal readonly string Root = Path.Combine(Path.GetTempPath(), "provider-reset-" + Guid.NewGuid().ToString("N"));
        internal readonly FakeClock Clock = new();
        internal readonly AgentOrchestratorKernel Kernel;
        internal readonly CliExecutionContext Context;
        internal readonly SqliteOperatorIntentStore Store;
        internal readonly Goal Source;
        internal readonly string TextPath;
        internal Fixture(bool held = true)
        {
            Directory.CreateDirectory(Root);
            Kernel = new(Clock);
            var workspace = OrchestratorWorkspace.ForDirectory(Root);
            var agent = ProviderHoldAgentSubstitutionTests.Scenario.Agent("xai-tester", "xAI", "grok-cli", "grok-4.7");
            Context = new(Kernel, workspace, new InMemoryModelProviderRegistry([]), [agent], WorkerProfileCatalog.Default(), null);
            Store = SqliteOperatorIntentStore.ForDirectories(workspace.OrchestratorDirectory, workspace.LogDirectory);
            Source = held ? Exhaust("xAI", null) : Kernel.CreateGoal("No hold");
            Clock.UtcNow = DateTimeOffset.Parse("2026-10-17T11:40:00Z");
            TextPath = Path.Combine(Root, "rationale.txt");
            File.WriteAllText(TextPath, "Provider usage page confirms this reset.");
        }
        internal ProviderBudgetExhaustionHold Hold
        {
            get
            {
                Assert.True(DispatchFailureClassifier.TryGetProviderBudgetExhaustionHold(Kernel.Goals, "xAI", null, out var hold));
                return hold;
            }
        }
        internal ProviderBudgetHoldSchedule Schedule() => ProviderBudgetHoldSchedules.Evaluate(Kernel.Goals, Hold, Clock.UtcNow);
        internal IEnumerable<ProgressEvent> Markers() => Kernel.Goals.SelectMany(goal => goal.Timeline)
            .Where(evt => ProviderBudgetHoldSchedules.TryParseReset(evt, out _));
        internal Goal Exhaust(string provider, string? credential)
        {
            var goal = Kernel.CreateGoal("Hold source", [new TaskSpec(TaskId.New(), "Verify", AgentRole.Tester)]);
            Kernel.ActivateGoal(goal.Id, Context.Agents);
            var at = Clock.UtcNow;
            Kernel.RecordTaskDispatch(goal.Id, goal.Tasks.Single().Id, new("grok-cli", "grok", Root, at,
                ProviderName: provider, ClaudeCredentialSourceDirectory: credential));
            var stderr = "API error (status 402 Payment Required): Grok Build usage balance exhausted";
            Clock.UtcNow = at.AddMinutes(1);
            Kernel.RecordTaskVerification(goal.Id, goal.Tasks.Single().Id, new("grok", Root, 1, "", stderr, Clock.UtcNow,
                ProviderFailureKind: WorkerProviderCatalog.Default().ResolveProfile("grok-cli").ParseOutcome(new(1, "", stderr)), DispatchStartedAt: at));
            return goal;
        }
        internal void Succeed()
        {
            var goal = Kernel.CreateGoal("Fresh success", [new TaskSpec(TaskId.New(), "Verify", AgentRole.Tester)]);
            Kernel.ActivateGoal(goal.Id, Context.Agents);
            Kernel.RecordTaskDispatch(goal.Id, goal.Tasks.Single().Id, new("grok-cli", "grok", Root, Clock.UtcNow, ProviderName: "xAI"));
            Clock.UtcNow = Clock.UtcNow.AddMinutes(1);
            Kernel.RecordTaskVerification(goal.Id, goal.Tasks.Single().Id, new("grok", Root, 0, "Success", "", Clock.UtcNow));
            Assert.False(DispatchFailureClassifier.TryGetProviderBudgetExhaustionHold(Kernel.Goals, "xAI", null, out _));
        }
        internal void Submit(string target, string reset, string path, params string[] flags) =>
            InfrastructureTestSupport.CaptureConsole(() => CliProviderResetCommand.Execute(
                ["provider-reset", target, reset, "--text-file", path, .. flags], Context, Store));
        public void Dispose() => SharedTestSupport.RemoveTempDirectory(Root);
    }
}
