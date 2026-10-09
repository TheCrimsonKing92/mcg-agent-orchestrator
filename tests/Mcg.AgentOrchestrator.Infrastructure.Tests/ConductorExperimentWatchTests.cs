using System.Runtime.CompilerServices;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: each fixture owns its stores, log, kernel, clock and AsyncLocal console captures.
public sealed class ConductorExperimentWatchTests(ITestOutputHelper output) : ConductorBatchLoopTests(output)
{
    private static readonly DateTimeOffset AsOf = new(2026, 10, 3, 0, 0, 0, TimeSpan.Zero);
    private const string Prefix = "experiment-reading-due:";

    [Fact]
    public void Run_RepeatedTicksRaiseOneEventAndQuestionPerExperimentAndTrigger()
    {
        WithWorkspace(workspace =>
        {
            var fixture = new WatchFixture(workspace);
            var stopped = fixture.Add(Spec());
            var breached = fixture.Add(BreachedSpec(99));
            var both = fixture.Add(BreachedSpec(2));
            var quiet = fixture.Add(Spec() with { StopRule = new(99, ExperimentStopUnit.Goals) });
            var stateBefore = File.ReadAllBytes(workspace.SqliteStatePath);
            fixture.Run(3);

            Assert.Equal(stateBefore, File.ReadAllBytes(workspace.SqliteStatePath));
            var events = fixture.Events();
            Assert.Equal(4, events.Count);
            var items = fixture.Attention();
            Assert.Equal(4, items.Count);
            foreach (var (record, trigger) in new[]
                { (stopped, "stop-rule"), (breached, "guardrail"), (both, "stop-rule"), (both, "guardrail") })
            {
                var entry = Assert.Single(events.Where(entry => entry.GetProperty("detail").GetString()!
                    .Contains($"experiment={record.Id} trigger={trigger} ", StringComparison.Ordinal)));
                Assert.Equal("experiment-reading-due", entry.GetProperty("eventKind").GetString());
                Assert.Equal(JsonValueKind.Null, entry.GetProperty("goalId").ValueKind);
                Assert.Equal("decision", entry.GetProperty("operator").GetString());
                Assert.Equal(AsOf, entry.GetProperty("timestamp").GetDateTimeOffset());
                var detail = entry.GetProperty("detail").GetString()!;
                Assert.Contains("observed=2 ", detail);
                Assert.Contains($"stopRuleMet={(record.Id == breached.Id ? "false" : "true")} ", detail);
                Assert.Contains($"verdict={(record.Id == stopped.Id ? "keep" : "inconclusive")} ", detail);
                Assert.EndsWith($"guardrailBreached={(record.Id == stopped.Id ? "false" : "true")}", detail);
                var item = Assert.Single(items.Where(item => item.CorrelationKey == Key(record, trigger)));
                Assert.Equal(CollaborationItemType.Decision, item.Type);
                Assert.Null(item.GoalId);
                Assert.Contains($"experiment-show {record.Id}", item.Body);
                Assert.Contains($"experiment-decide {record.Id}", item.Body);
            }
            Assert.DoesNotContain(items, item => item.CorrelationKey!.Contains(quiet.Id, StringComparison.Ordinal));
            Assert.DoesNotContain(events, entry => entry.GetProperty("detail").GetString()!.Contains(quiet.Id, StringComparison.Ordinal));
            fixture.Restart();
            fixture.Run(2);
            Assert.Equal(4, fixture.Events().Count);
            Assert.Equal(4, fixture.Attention().Count);
            Assert.Equal(4, fixture.AllItems().Count);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Run_DecisionResolvesBothQuestionsOnNextTickIncludingAfterRestart(bool restart)
    {
        WithWorkspace(workspace =>
        {
            var fixture = new WatchFixture(workspace);
            var decided = fixture.Add(BreachedSpec(2));
            var remaining = fixture.Add(Spec());
            fixture.Run(1);
            Assert.Equal(3, fixture.Attention().Count);
            Execute(["experiment-decide", decided.Id, "--outcome", "refuted",
                "--evidence", "receipt:watch", "--action", "Revert manually"], workspace);
            Assert.Equal(ExperimentOutcomeState.Refuted, new ExperimentStore(workspace.ExperimentStorePath)
                .ResolveAsync(decided.Id).GetAwaiter().GetResult()!.Outcome);
            if (restart) fixture.Restart();
            fixture.Run(1);
            Assert.Equal(Key(remaining, "stop-rule"), Assert.Single(fixture.Attention()).CorrelationKey);
            foreach (var trigger in new[] { "stop-rule", "guardrail" })
            {
                var item = Assert.Single(fixture.AllItems().Where(item => item.CorrelationKey == Key(decided, trigger)));
                Assert.Equal(CollaborationItemStatus.Resolved, item.Status);
                Assert.Equal($"experiment {decided.Id} decided: refuted", item.Resolution);
            }
            Assert.Equal(3, fixture.Events().Count);
            fixture.Run(1);
            Assert.Single(fixture.Attention());
            Assert.Equal(3, fixture.Events().Count);
        });
    }

    [Fact]
    public void Run_ReadsTerminalGoalsPersistedBetweenTicks()
    {
        WithWorkspace(workspace =>
        {
            var fixture = new WatchFixture(workspace);
            var record = fixture.Add(Spec() with { StopRule = new(3, ExperimentStopUnit.Goals) });
            fixture.Run(1);
            Assert.Empty(fixture.Events());
            Assert.Empty(fixture.Attention());

            var id = new string('e', 32);
            var task = new TaskSnapshot($"{id}-0", "Developer", AgentRole.Developer, WorkTaskStatus.Completed,
                null, null, null, [], null, null, DispatchHistory:
                [new TaskDispatchSnapshot("worker", "command", workspace.RootDirectory, AsOf.AddMinutes(-5))]);
            var goal = new GoalSnapshot(id, id, GoalStatus.Completed, [task],
                [new ProgressEventSnapshot(id, task.Id, ProgressKind.TaskCompleted, "done", AsOf.AddMinutes(-4))]);
            new SqliteOrchestratorStateRepository(workspace.SqliteStatePath).SaveGoalSnapshotsAsync([goal])
                .GetAwaiter().GetResult();

            fixture.Run(1);
            var detail = Assert.Single(fixture.Events()).GetProperty("detail").GetString()!;
            Assert.Contains($"experiment={record.Id} trigger=stop-rule observed=3 stopRuleMet=true ", detail);
            Assert.Equal(Key(record, "stop-rule"), Assert.Single(fixture.Attention()).CorrelationKey);
            Assert.Contains("stop rule: 3 of 3 goals (met)",
                Execute(["experiment-show", record.Id, "--as-of", AsOf.ToString("O")], workspace));
            fixture.Run(1);
            Assert.Single(fixture.Events());
            Assert.Single(fixture.Attention());
        });
    }

    [Theory]
    [InlineData(ExperimentStopUnit.Gates)]
    [InlineData(ExperimentStopUnit.Ticks)]
    [InlineData(ExperimentStopUnit.Goals)]
    public void Run_MissingDecisionMetricStillRaisesEarlyGuardrailQuestion(ExperimentStopUnit unit)
    {
        WithWorkspace(workspace =>
        {
            var fixture = new WatchFixture(workspace);
            var spec = BreachedSpec(99) with
            {
                StopRule = new(99, unit),
                DecisionRule = new([new("landings-per-hour", ">", 0)], [new("rounds-per-landing", ">", 0)])
            };
            var record = fixture.Add(spec);
            Directory.Delete(workspace.GoalLifecycleEventsDirectory, true);
            fixture.Run(2);
            var detail = Assert.Single(fixture.Events()).GetProperty("detail").GetString()!;
            Assert.Contains($"experiment={record.Id} trigger=guardrail ", detail);
            Assert.Contains($"observed={(unit == ExperimentStopUnit.Goals ? "2" : "unavailable")} stopRuleMet=false verdict=inconclusive guardrailBreached=true", detail);
            Assert.Equal(Key(record, "guardrail"), Assert.Single(fixture.Attention()).CorrelationKey);
        });
    }

    [Fact]
    public void Run_MissingGuardrailMetricProducesNoSignal()
    {
        WithWorkspace(workspace =>
        {
            var fixture = new WatchFixture(workspace);
            fixture.Add(Spec() with
            {
                StopRule = new(99, ExperimentStopUnit.Goals), Metrics = ["rounds-per-landing"],
                Guardrail = new("landings-per-hour", new("landings-per-hour", ">", 0))
            });
            Directory.Delete(workspace.GoalLifecycleEventsDirectory, true);
            fixture.Run(2);
            Assert.Empty(fixture.Events());
            Assert.Empty(fixture.Attention());
        });
    }

    [Fact]
    public void Run_AcknowledgedQuestionDoesNotRearmAfterRestart()
    {
        WithWorkspace(workspace =>
        {
            var fixture = new WatchFixture(workspace);
            var record = fixture.Add(Spec());
            fixture.Run(1);
            Assert.Single(fixture.Attention());
            Assert.True(CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory)
                .TryResolveAsync(Key(record, "stop-rule"), "acknowledged").GetAwaiter().GetResult());
            fixture.Restart();
            fixture.Run(2);
            Assert.Empty(fixture.Attention());
            Assert.Single(fixture.Events());
            Assert.Single(fixture.AllItems());
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Run_StoreFailureIsLoggedAndDoesNotStopTicks(bool collaborationFailure)
    {
        WithWorkspace(workspace =>
        {
            var fixture = new WatchFixture(workspace);
            fixture.Add(Spec());
            var attempts = 0;
            if (collaborationFailure)
            {
                // Isolate the observer's failing real SQLite store from other tick collaborators.
                var path = Path.Combine(workspace.RootDirectory, "broken-collaboration.db");
                File.WriteAllText(path, "not a SQLite database");
                ConductorExperimentWatch.Attach(fixture.Loop, new ConductorExperimentWatch(workspace, fixture.Writer, () =>
                {
                    attempts++;
                    return new CollaborationItemStore(path);
                }));
            }
            else File.WriteAllText(workspace.ExperimentStorePath, "not a SQLite database");
            var error = CaptureConsoleError(() => fixture.Run(2));
            Assert.Contains("EXPERIMENT_WATCH_FAILED exception=SqliteException", error);
            if (collaborationFailure) Assert.Equal(2, attempts);
            Assert.Empty(fixture.Events());
        });
    }

    [Fact]
    public void Run_NoExperimentDatabaseDoesNotCreateOne()
    {
        WithWorkspace(workspace =>
        {
            var fixture = new WatchFixture(workspace);
            Assert.False(File.Exists(workspace.ExperimentStorePath));
            fixture.Run(2);
            Assert.False(File.Exists(workspace.ExperimentStorePath));
            Assert.Empty(fixture.Events());
        });
    }

    [Fact]
    public void Runbook_DescribesExperimentReadingDue()
    {
        var text = File.ReadAllText(Path.Combine(RepositoryRoot(), "docs", "operator-runbook.md"));
        var start = text.IndexOf("### Experiments", StringComparison.Ordinal);
        Assert.True(start >= 0);
        var end = text.IndexOf("### ", start + "### Experiments".Length, StringComparison.Ordinal);
        var section = text[start..end];
        foreach (var token in new[] { "experiment-reading-due", "stop-rule", "guardrail", "attention queue", "experiment-show", "experiment-decide", "automatically resolves",
            "experiment-apply-flag", "experiment-revert-flag", "Mutate", "owner decision", "priorValue" })
            Assert.Contains(token, section);
    }

    [Theory]
    [InlineData("revert")]
    [InlineData("guardrail")]
    [InlineData("keep")]
    public void ObserveTick_AppliedFlagsRevertOnceOrKeepOwnerQuestion(string trigger)
    {
        WithWorkspace(workspace =>
        {
            Seed(workspace);
            var spec = Spec() with { Intervention = new(ExperimentInterventionKind.ConfigFlag, "Trial flag",
                new(ExperimentFlagFileKind.ConductorPolicy, "followerGatesEnabled", true)) };
            if (trigger == "revert") spec = spec with { DecisionRule = new([new("rounds-per-landing", ">", 0)], [new("rounds-per-landing", "<", 0)]) };
            if (trigger == "guardrail") spec = spec with { Guardrail = BreachedSpec(99).Guardrail,
                StopRule = new(99, ExperimentStopUnit.Goals) };
            var experiments = new ExperimentStore(workspace.ExperimentStorePath);
            var record = experiments.AddAsync(spec).GetAwaiter().GetResult();
            experiments.RecordFlagPriorAsync(record.Id, false).GetAwaiter().GetResult();
            var path = Path.Combine(workspace.OrchestratorDirectory, "conductor-policy.json");
            File.WriteAllText(path, ExperimentFlagTestFixture.PolicyJson(enabled: true));
            var before = File.ReadAllBytes(path);
            var watch = new ConductorExperimentWatch(workspace, null);
            watch.ObserveTick(AsOf);
            watch.ObserveTick(AsOf);
            new ConductorExperimentWatch(workspace, null).ObserveTick(AsOf);
            if (trigger == "keep")
                Assert.False(File.Exists(Path.Combine(workspace.OrchestratorDirectory, SqliteOperatorIntentStore.DatabaseFileName)));
            var intents = SqliteOperatorIntentStore.ForDirectories(workspace.OrchestratorDirectory, workspace.LogDirectory);
            var queued = intents.ListForGoalAsync(OperatorIntentScopes.Workspace).GetAwaiter().GetResult();
            var items = CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory).ListAsync().GetAwaiter().GetResult();
            if (trigger == "keep")
            {
                Assert.Empty(queued);
                Assert.Equal(Key(record, "stop-rule"), Assert.Single(items).CorrelationKey);
                Assert.Equal(before, File.ReadAllBytes(path));
                Assert.Equal(ExperimentOutcomeState.Open, experiments.ResolveAsync(record.Id).GetAwaiter().GetResult()!.Outcome);
            }
            else
            {
                var revert = Assert.Single(queued);
                Assert.Equal(OperatorIntentVerbs.ExperimentRevertFlag, revert.Verb);
                Assert.Empty(items);
                Assert.Equal(ExperimentOutcomeState.Refuted, experiments.ResolveAsync(record.Id).GetAwaiter().GetResult()!.Outcome);
                Assert.Equal($"operator-intent:{revert.Id}", experiments.ResolveAsync(record.Id).GetAwaiter().GetResult()!.Decision!.Evidence);
                OperatorIntentCoordinator.CreateDefault(workspace).ExecuteWorkspacePending(new AgentOrchestratorKernel());
                using var restored = JsonDocument.Parse(File.ReadAllText(path));
                Assert.False(restored.RootElement.GetProperty("followerGatesEnabled").GetBoolean());
                Assert.Equal(OperatorIntentStatus.Applied, intents.GetAsync(revert.Id).GetAwaiter().GetResult()!.Status);
            }
        });
    }

    [Fact]
    public void ObserveTick_FailedRevertSubmissionStillRaisesGuardrailQuestion()
    {
        WithWorkspace(workspace =>
        {
            Seed(workspace);
            var spec = BreachedSpec(99) with { Intervention = new(ExperimentInterventionKind.ConfigFlag, "Trial flag",
                new(ExperimentFlagFileKind.ConductorPolicy, "followerGatesEnabled", true)) };
            var experiments = new ExperimentStore(workspace.ExperimentStorePath);
            var record = experiments.AddAsync(spec).GetAwaiter().GetResult();
            experiments.RecordFlagPriorAsync(record.Id, false).GetAwaiter().GetResult();
            var path = Path.Combine(workspace.OrchestratorDirectory, "conductor-policy.json");
            File.WriteAllText(path, ExperimentFlagTestFixture.PolicyJson(enabled: true));
            var before = File.ReadAllBytes(path);
            var intents = SqliteOperatorIntentStore.ForDirectories(workspace.OrchestratorDirectory, workspace.LogDirectory);
            intents.EnqueueAsync(new OperatorIntentRecord(Guid.NewGuid().ToString("n"), $"experiment-revert-flag:{record.Id}",
                OperatorIntentVerbs.ExperimentRevertFlag, OperatorIntentScopes.Workspace, null,
                JsonSerializer.Serialize(new ExperimentRevertFlagOperatorIntentPayload(record.Id), OperatorIntentJson.Options),
                [], "different", "conductor-experiment-revert", OperatorIntentAdjudication.StewardAssurance, AsOf,
                ActorKind: OperatorActorKind.Agent)).GetAwaiter().GetResult();
            var watch = new ConductorExperimentWatch(workspace, null);
            watch.ObserveTick(AsOf);
            watch.ObserveTick(AsOf);
            var items = CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory).ListAsync().GetAwaiter().GetResult();
            Assert.Equal(Key(record, "guardrail"), Assert.Single(items).CorrelationKey);
            Assert.Equal(ExperimentOutcomeState.Open, experiments.ResolveAsync(record.Id).GetAwaiter().GetResult()!.Outcome);
            Assert.Equal(before, File.ReadAllBytes(path));
            Assert.Single(intents.ListForGoalAsync(OperatorIntentScopes.Workspace).GetAwaiter().GetResult());
        });
    }

    private static string RepositoryRoot([CallerFilePath] string source = "") => VerifiedRepositoryRoot.Find(source);
    private static string Key(ExperimentRecord record, string trigger) => $"{Prefix}{record.Id}:{trigger}";
    private static ExperimentSpec Spec() => new("A shorter brief reduces rounds per landing",
        new(ExperimentInterventionKind.BriefOrPromptChange, "Remove repeated preamble"),
        new(ExperimentBaselineKind.BeforeAfterWindow, AsOf.AddDays(-2), AsOf.AddDays(-1)),
        ["rounds-per-landing", "landings-per-hour"], new("productive-rounds", new("productive-rounds", "<", -10)),
        new(2, ExperimentStopUnit.Goals),
        new([new("rounds-per-landing", "<", 0)], [new("rounds-per-landing", ">", 0)]));

    private static void WithWorkspace(Action<OrchestratorWorkspace> action)
    {
        var root = Path.Combine(Path.GetTempPath(), "experiment-watch-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);
        try { action(OrchestratorWorkspace.ForDirectory(root)); }
        finally { Directory.Delete(root, true); }
    }

    private static string Execute(string[] args, OrchestratorWorkspace workspace)
    {
        var context = new CliExecutionContext(new AgentOrchestratorKernel(), workspace, new InMemoryModelProviderRegistry([]),
            DefaultAgents(), WorkerProfileCatalog.Default(), null);
        return CaptureConsole(() => Assert.Equal(false, CliExperimentCommands.TryExecute(args[0], args, context)));
    }

    private static ExperimentRecord AddExperiment(OrchestratorWorkspace workspace, ExperimentSpec spec)
    {
        var path = Path.Combine(workspace.RootDirectory, "spec.json");
        File.WriteAllText(path, JsonSerializer.Serialize(spec, ExperimentStore.JsonOptions));
        var output = Execute(["experiment-add", "--spec", path], workspace);
        var line = output.TrimEnd().Split(Environment.NewLine)[^1];
        Assert.StartsWith("experiment: ", line);
        var id = line["experiment: ".Length..];
        File.Delete(path);
        return new ExperimentStore(workspace.ExperimentStorePath).ResolveAsync(id).GetAwaiter().GetResult()!;
    }

    private static AgentOrchestratorKernel Seed(OrchestratorWorkspace workspace)
    {
        GoalSnapshot Completed(string id, DateTimeOffset at, GoalStatus status, params AgentRole[] roles)
        {
            var tasks = roles.Select((role, i) => new TaskSnapshot($"{id}-{i}", role.ToString(), role, WorkTaskStatus.Completed,
                null, null, null, [], null, null, DispatchHistory:
                [new TaskDispatchSnapshot("worker", "command", workspace.RootDirectory, at.AddMinutes(i))])).ToArray();
            var events = tasks.Select((task, i) => new ProgressEventSnapshot(id, task.Id, ProgressKind.TaskCompleted,
                "done", at.AddMinutes(i).AddSeconds(30))).ToArray();
            return new(id, id, status, tasks, events);
        }
        var snapshots = new[]
        {
            Completed(new string('a', 32), AsOf.AddDays(-2).AddHours(1), GoalStatus.Completed,
                AgentRole.Planner, AgentRole.Developer, AgentRole.Tester, AgentRole.Reviewer),
            Completed(new string('b', 32), AsOf.AddDays(-2).AddHours(2), GoalStatus.Cancelled, AgentRole.Developer, AgentRole.Tester),
            Completed(new string('c', 32), AsOf.AddDays(-1).AddHours(1), GoalStatus.Completed,
                AgentRole.Developer, AgentRole.Tester, AgentRole.Reviewer),
            Completed(new string('d', 32), AsOf.AddDays(-1).AddHours(2), GoalStatus.Completed,
                AgentRole.Developer, AgentRole.Tester, AgentRole.Reviewer)
        };
        var kernel = AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot(snapshots, []));
        StateDbMigrations.EnsureUpToDate(workspace.SqliteStatePath);
        new SqliteOrchestratorStateRepository(workspace.SqliteStatePath).SaveAsync(kernel).GetAwaiter().GetResult();
        Directory.CreateDirectory(workspace.GoalLifecycleEventsDirectory);
        File.WriteAllLines(Path.Combine(workspace.GoalLifecycleEventsDirectory, "seed.jsonl"),
            snapshots.Where(goal => goal.Status == GoalStatus.Completed).Select(goal => JsonSerializer.Serialize(new
            {
                eventType = "GoalLanded", goalId = goal.Id,
                timestamp = goal.Id[0] == 'a' ? AsOf.AddDays(-2).AddHours(12) : AsOf.AddDays(-1).AddHours(12)
            })));
        return kernel;
    }

    private static ExperimentSpec BreachedSpec(int count) => Spec() with
    {
        StopRule = new(count, ExperimentStopUnit.Goals),
        Guardrail = new("productive-rounds", new("productive-rounds", ">", 20))
    };

    private sealed class WatchFixture
    {
        private readonly OrchestratorWorkspace _workspace;
        private readonly AgentOrchestratorKernel _kernel;
        private readonly Goal _held;
        private readonly ConductorDriver _driver;
        internal ConductEventLogWriter Writer { get; }
        internal ConductorBatchLoop Loop { get; private set; }

        internal WatchFixture(OrchestratorWorkspace workspace)
        {
            _workspace = workspace;
            var seed = Seed(workspace);
            var held = GoalLifecycleCommands.CreateAndActivateSimpleGoal(seed, DefaultAgents(), "Hold while experiments are observed");
            var repository = new SqliteOrchestratorStateRepository(workspace.SqliteStatePath);
            repository.SaveAsync(seed).GetAwaiter().GetResult();
            // Historical terminal goals stay in state.db; production conduct hydrates only active work.
            Assert.Contains(seed.Goals, goal => goal.Status == GoalStatus.Completed);
            Assert.Contains(seed.Goals, goal => goal.Status == GoalStatus.Cancelled);
            _kernel = CliPersistentStateRunner.LoadConductLoopKernel(repository, executionDirectory: workspace.RootDirectory);
            _held = Assert.Single(_kernel.Goals);
            Assert.Equal(held.Id, _held.Id);
            Assert.Equal(GoalStatus.Active, _held.Status);
            _driver = MakeDriver(getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true), getRunningCount: () => 2,
                dispatchAndStart: _ => throw new InvalidOperationException("The worker cap must prevent dispatch."),
                hasGateReadyGoal: () => false, utcNow: () => AsOf);
            Writer = new ConductEventLogWriter(workspace.ConductEventsLogPath, utcNow: () => AsOf);
            Loop = NewLoop();
        }

        internal ExperimentRecord Add(ExperimentSpec spec)
        {
            var record = AddExperiment(_workspace, spec);
            Assert.True(File.Exists(_workspace.ExperimentStorePath));
            return record;
        }

        internal void Restart() => Loop = NewLoop();
        private ConductorBatchLoop NewLoop() => new(conductEventLogWriter: Writer, utcNow: () => AsOf, workspace: _workspace);

        internal void Run(int ticks) => CaptureConsole(() =>
        {
            Assert.Equal(_held.Id, Assert.Single(_kernel.Goals).Id);
            Assert.Equal(GoalStatus.Active, _held.Status);
            var summary = Loop.Run(_kernel, _driver, ConductorAutonomyPolicy.Conservative with { MaxConcurrentPaidWorkers = 2 },
                Path.Combine(_workspace.RootDirectory, "stop"), onlyGoalId: _held.Id.Value, maxIterations: ticks,
                watchInterval: TimeSpan.FromSeconds(1), sleepFunc: _ => false);
            Assert.Equal(ticks, summary.Ticks);
            Assert.Equal(ticks, summary.Held);
        });

        internal List<JsonElement> Events() => !File.Exists(Writer.CurrentPath) ? [] :
            File.ReadAllLines(Writer.CurrentPath).Select(line => JsonSerializer.Deserialize<JsonElement>(line))
                .Where(entry => entry.GetProperty("eventKind").GetString() == "experiment-reading-due").ToList();

        internal IReadOnlyList<CollaborationItem> Attention() => CollaborationItemStore.ForDirectory(_workspace.OrchestratorDirectory)
            .GetAttentionQueueAsync().GetAwaiter().GetResult().Where(item => item.CorrelationKey?.StartsWith(Prefix, StringComparison.Ordinal) == true).ToArray();

        internal IReadOnlyList<CollaborationItem> AllItems() => CollaborationItemStore.ForDirectory(_workspace.OrchestratorDirectory)
            .ListAsync().GetAwaiter().GetResult().Where(item => item.CorrelationKey?.StartsWith(Prefix, StringComparison.Ordinal) == true).ToArray();
    }
}
