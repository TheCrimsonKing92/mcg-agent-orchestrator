using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each fixture owns its SQLite databases, files, kernel and injected clock.
public sealed class ProviderResetHoldLookupWorkCountTests
{
    private const string ResetText = "2026-10-20T21:03:00-05:00";
    private const string Rationale = "Provider usage page confirms this reset.";

    [Fact]
    public async Task Execute_SixtyDispatches_EvaluatesThreeBindingsAndQueuesOneIntent()
    {
        using var fixture = new Fixture();
        var dispatches = fixture.Kernel.Goals.SelectMany(goal => goal.Tasks).SelectMany(task => task.DispatchHistory).ToArray();
        Assert.Equal(60, dispatches.Length);
        Assert.Equal(30, dispatches.Count(dispatch => dispatch.ProviderName == "xAI"));
        Assert.Equal(15, dispatches.Count(dispatch => dispatch.ClaudeCredentialSourceDirectory == @"C:\profiles\a"));
        Assert.Equal(15, dispatches.Count(dispatch => dispatch.ClaudeCredentialSourceDirectory == @"C:\profiles\b"));
        Assert.Single(fixture.Kernel.Goals.SelectMany(goal => goal.Tasks).SelectMany(task => task.VerificationHistory));
        var memo = new ProviderBudgetHoldMemo(fixture.Kernel.Goals);

        InfrastructureTestSupport.CaptureConsole(() =>
            Assert.False(CliProviderResetCommand.Execute(fixture.Parts("xAI"), fixture.Context, fixture.Store, memo)));

        var intent = Assert.Single(await fixture.Intents());
        Assert.Equal(OperatorIntentVerbs.ProviderReset, intent.Verb);
        Assert.Equal(fixture.Source.Id.Value, intent.GoalId);
        Assert.Equal(fixture.Source.Tasks.Single().Id.Value, intent.TaskId);
        Assert.Equal(3, memo.EvaluationCount);
    }

    [Theory]
    [InlineData("xai-tester", false, false)]
    [InlineData("xAI", false, false)]
    [InlineData("xai::<provider-default>", false, false)]
    [InlineData("Anthropic", false, false)]
    [InlineData("Anthropic", true, false)]
    [InlineData("xAI", false, true)]
    public async Task Execute_TargetAndSourceVariants_MatchPerDispatchBaseline(
        string target, bool anthropicHeld, bool terminal)
    {
        using var fixture = new Fixture(anthropicHeld);
        if (terminal) await fixture.CompleteSourceAndReload();
        Assert.Equal(terminal ? GoalStatus.Completed : GoalStatus.Active,
            fixture.Kernel.GetGoal(fixture.Source.Id).Status);
        var memo = new ProviderBudgetHoldMemo(fixture.Kernel.Goals);
        OperatorIntentRecord? expected = null;
        var expectedOutput = "";
        var expectedError = Record.Exception(() => expectedOutput = InfrastructureTestSupport.CaptureConsole(() =>
            expected = fixture.ReferenceSubmission(target)));
        var actualOutput = "";
        var actualError = Record.Exception(() => actualOutput = InfrastructureTestSupport.CaptureConsole(() =>
            Assert.False(CliProviderResetCommand.Execute(fixture.Parts(target), fixture.Context, fixture.Store, memo))));

        Assert.Equal(expectedError?.GetType(), actualError?.GetType());
        Assert.Equal(expectedError?.Message, actualError?.Message);
        Assert.Equal(3, memo.EvaluationCount);
        var intents = await fixture.Intents();
        if (target == "Anthropic")
        {
            Assert.IsType<ArgumentException>(expectedError);
            Assert.Equal(anthropicHeld
                ? @"ambiguous-binding: anthropic::c:\profiles\a, anthropic::c:\profiles\b"
                : "no-current-hold: Anthropic", expectedError.Message);
            Assert.Null(expected);
            Assert.Empty(intents);
            Assert.Equal(expectedOutput, actualOutput);
            return;
        }

        Assert.Null(expectedError);
        Assert.NotNull(expected);
        var actual = Assert.Single(intents);
        Assert.Equal(expected.Verb, actual.Verb);
        Assert.Equal(expected.GoalId, actual.GoalId);
        Assert.Equal(expected.TaskId, actual.TaskId);
        Assert.Equal(expected.PayloadJson, actual.PayloadJson);
        Assert.Equal(expected.Actor, actual.Actor);
        Assert.Equal(expected.ActorKind, actual.ActorKind);
        Assert.Equal(expected.Channel, actual.Channel);
        Assert.Equal(expected.AuthenticationAssurance, actual.AuthenticationAssurance);
        Assert.Equal(expected.Status, actual.Status);
        Assert.Empty(actual.PayloadFileReferences);
        Assert.Equal(actual.Id, actual.IdempotencyKey);
        Assert.Equal(expectedOutput.Replace(expected.Id, "<intent-id>", StringComparison.Ordinal),
            actualOutput.Replace(actual.Id, "<intent-id>", StringComparison.Ordinal));
        if (terminal)
        {
            Assert.Equal(OperatorIntentScopes.Workspace, actual.GoalId);
            Assert.Null(actual.TaskId);
        }
        else
        {
            Assert.Equal(fixture.Source.Id.Value, actual.GoalId);
            Assert.Equal(fixture.Source.Tasks.Single().Id.Value, actual.TaskId);
        }
    }

    private sealed class Clock : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.Parse("2026-10-17T11:07:00Z");
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "provider-reset-count-" + Guid.NewGuid().ToString("N"));
        private readonly Clock _clock = new();
        private readonly OrchestratorWorkspace _workspace;
        private readonly IReadOnlyList<AgentDefinition> _agents;
        private readonly string _textPath;
        internal AgentOrchestratorKernel Kernel { get; private set; }
        internal SqliteOperatorIntentStore Store { get; }
        internal Goal Source { get; }
        internal CliExecutionContext Context => new(Kernel, _workspace, new InMemoryModelProviderRegistry([]),
            _agents, WorkerProfileCatalog.Default(), null);

        internal Fixture(bool anthropicHeld = false)
        {
            Directory.CreateDirectory(_root);
            Kernel = new(_clock);
            _workspace = OrchestratorWorkspace.ForDirectory(_root);
            _agents = [new(new("xai-tester"), "xai-tester", AgentRole.Tester,
                new("xAI", "fixture", ModelCapability.Text | ModelCapability.Code | ModelCapability.ToolUse, SubscriptionMode.ApiKey))];
            Store = SqliteOperatorIntentStore.ForDirectories(_workspace.OrchestratorDirectory, _workspace.LogDirectory);
            Source = Add("xAI", null, exhausted: true);
            for (var i = 1; i < 30; i++) Add("xAI", null, exhausted: false);
            for (var i = 0; i < 30; i++)
                Add("Anthropic", i % 2 == 0 ? @"C:\profiles\a" : @"C:\profiles\b", exhausted: anthropicHeld && i < 2);
            _textPath = Path.Combine(_root, "rationale.txt");
            File.WriteAllText(_textPath, "  " + Rationale + "\r\n");
        }

        internal string[] Parts(string target) => ["provider-reset", target, ResetText, "--text-file", _textPath];

        private Goal Add(string provider, string? credential, bool exhausted)
        {
            var goal = Kernel.CreateGoal("Lookup count fixture", [new TaskSpec(TaskId.New(), "Verify", AgentRole.Tester)]);
            Kernel.ActivateGoal(goal.Id, _agents);
            var task = goal.Tasks.Single();
            var at = _clock.UtcNow;
            Kernel.RecordTaskDispatch(goal.Id, task.Id, new("fixture", "fixture", _root, at,
                ProviderName: provider, ClaudeCredentialSourceDirectory: credential));
            _clock.UtcNow = at.AddMinutes(1);
            if (exhausted)
                Kernel.RecordTaskVerification(goal.Id, task.Id, new("fixture", _root, 1, "", "budget exhausted",
                    _clock.UtcNow, ProviderFailureKind: ProviderFailureKind.BudgetExhausted, DispatchStartedAt: at));
            return goal;
        }

        internal async Task CompleteSourceAndReload()
        {
            var repository = InfrastructureTestSupport.CreateMigratedStateRepository(_workspace.SqliteStatePath);
            await repository.SaveAsync(Kernel);
            Kernel.ReplaceGoalWithSnapshot(Kernel.ExportGoalSnapshot(Source.Id) with { Status = GoalStatus.Completed });
            await repository.SaveAsync(Kernel);
            Assert.Equal(1, Kernel.EvictTerminalGoalAggregates([Source.Id]));
            var loaded = await repository.LoadAsync();
            Kernel = AgentOrchestratorKernel.FromSnapshot(loaded.ExportSnapshot(), _clock);
            Assert.Equal(GoalStatus.Completed, Kernel.GetGoal(Source.Id).Status);
        }

        internal async Task<OperatorIntentRecord[]> Intents()
        {
            var scopes = Kernel.Goals.Select(goal => goal.Id.Value).Append(OperatorIntentScopes.Workspace);
            var rows = await Task.WhenAll(scopes.Select(scope => Store.ListForGoalAsync(scope)));
            return rows.SelectMany(row => row).ToArray();
        }

        // Baseline selection deliberately evaluates every dispatch, as the pre-memo command did.
        // It shares goal identities with the real invocation and never enqueues an expected intent.
        internal OperatorIntentRecord ReferenceSubmission(string target)
        {
            var parts = Parts(target);
            var agent = _agents.FirstOrDefault(item => item.Id.Value.Equals(target, StringComparison.OrdinalIgnoreCase));
            if (agent is not null) target = agent.Model.ProviderName;
            var holds = new Dictionary<string, ProviderBudgetExhaustionHold>(StringComparer.OrdinalIgnoreCase);
            foreach (var dispatch in Kernel.Goals.SelectMany(goal => goal.Tasks).SelectMany(task => task.DispatchHistory))
            {
                var provider = dispatch.ProviderName ?? dispatch.WorkerName;
                if (!DispatchFailureClassifier.TryGetProviderBudgetExhaustionHold(Kernel.Goals,
                        provider, dispatch.ClaudeCredentialSourceDirectory, out var hold)) continue;
                if (provider.Equals(target, StringComparison.OrdinalIgnoreCase) || hold.BindingKey.Equals(target, StringComparison.OrdinalIgnoreCase))
                    holds.TryAdd(hold.BindingKey, hold);
            }
            if (holds.Count == 0) throw new ArgumentException($"no-current-hold: {parts[1]}");
            if (holds.Count > 1) throw new ArgumentException($"ambiguous-binding: {string.Join(", ", holds.Keys.Order(StringComparer.OrdinalIgnoreCase))}");
            var selected = holds.Values.Single();
            var terminal = Kernel.Goals.Any(goal => goal.Id == selected.SourceGoalId && goal.IsTerminal);
            var attribution = CliPersistentStateRunner.ResolveOperatorIntentAttribution(parts,
                CliPersistentStateRunner.OperatorIntentSubmissionSource.Cli);
            var expected = new OperatorIntentRecord("reference-intent", "reference-intent", OperatorIntentVerbs.ProviderReset,
                terminal ? OperatorIntentScopes.Workspace : selected.SourceGoalId.Value,
                terminal ? null : selected.SourceTaskId.Value,
                JsonSerializer.Serialize(new ProviderResetOperatorIntentPayload(selected.BindingKey,
                    DateTimeOffset.Parse(ResetText).ToUniversalTime(), File.ReadAllText(_textPath).Trim()), OperatorIntentJson.Options),
                [], attribution.Actor, attribution.Channel, attribution.AuthenticationAssurance, _clock.UtcNow, ActorKind: attribution.ActorKind);
            if (terminal)
                OperatorIntentSubmissionOutput.PrintWorkspace(expected, _workspace.OrchestratorDirectory, false);
            else
                OperatorIntentSubmissionOutput.PrintAnswer(expected, _workspace.OrchestratorDirectory, selected.SourceGoalId.Value, false);
            return expected;
        }

        public void Dispose() => SharedTestSupport.RemoveTempDirectory(_root);
    }
}
