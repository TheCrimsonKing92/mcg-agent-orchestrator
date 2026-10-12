using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: private SQLite roots, shared fixture inputs, and async-local console capture;
// no environment, current-directory, or process-wide Console mutation.
public sealed class CliOperatorIntentSubmitJsonTestsWorkspaceVerbs : CliTaskQueryTestSupport
{
    private const string Warning =
        "WARNING: intent queued but NO conduct loop is running - it will not apply until a loop starts.";

    [Fact]
    public Task LessonRecord_Json_PreservesRecordHumanTextAndIdempotency() => CheckSubmit("lesson-record");

    [Fact]
    public Task LessonRetire_Json_PreservesRecordAndHumanText() => CheckSubmit("lesson-retire");

    [Fact]
    public Task EscapeRecord_Json_PreservesRecordAndHumanText() => CheckSubmit("escape-record");

    [Fact]
    public Task ExperimentApplyFlag_Json_PreservesRecordAndHumanText() => CheckSubmit("experiment-apply-flag");

    private static async Task CheckSubmit(string verb)
    {
        using var fixture = await SubmitFixture.Create(CreateTempDirectory(), verb);
        var args = fixture.Arguments(verb);
        var human = fixture.Run(args, fixture.TextWorkspace);
        Assert.Empty(human.Error);
        var textRecord = Assert.Single(await fixture.Intents(fixture.TextWorkspace)
            .ListForGoalAsync(OperatorIntentScopes.Workspace));
        var expectedLine = verb switch
        {
            "lesson-record" =>
                $"Operator intent queued: id={textRecord.Id} verb=lesson-record scope=workspace status=Pending; poll with operator-intent-status {textRecord.Id} (or add --wait).",
            "lesson-retire" =>
                $"Operator intent queued: id={textRecord.Id} verb=lesson-retire scope=workspace status=Pending; poll with operator-intent-status {textRecord.Id} (or add --wait).",
            "escape-record" =>
                $"Operator intent queued: id={textRecord.Id} verb=escape-record scope=workspace status=Pending; poll with operator-intent-status {textRecord.Id} (or add --wait).",
            "experiment-apply-flag" =>
                $"Operator intent queued: id={textRecord.Id} verb=experiment-apply-flag scope=workspace status=Pending; poll with operator-intent-status {textRecord.Id} (or add --wait).",
            _ => throw new ArgumentOutOfRangeException(nameof(verb))
        };
        Assert.Equal($"{expectedLine}{Environment.NewLine}{Warning}{Environment.NewLine}", human.Output);

        // Place the flag before the action/selector to prove it is removed before positional parsing.
        string[] jsonArgs = [args[0], "--json", .. args.Skip(1)];
        var result = fixture.Run(jsonArgs, fixture.JsonWorkspace);
        var jsonRecord = Assert.Single(await fixture.Intents(fixture.JsonWorkspace)
            .ListForGoalAsync(OperatorIntentScopes.Workspace));
        AssertEnvelope(result, jsonRecord);
        Assert.Equal(verb, jsonRecord.Verb);
        Assert.Equal(OperatorIntentScopes.Workspace, jsonRecord.GoalId);
        Assert.Null(jsonRecord.TaskId);
        Assert.Equal(OperatorIntentStatus.Pending, jsonRecord.Status);
        Assert.NotEqual(textRecord.Id, jsonRecord.Id);
        // Compare every stored field, normalizing only generated identity and creation time.
        Assert.Equal(JsonSerializer.Serialize(textRecord with { Id = "", CreatedAt = default }),
            JsonSerializer.Serialize(jsonRecord with { Id = "", CreatedAt = default }));
        Assert.Equal("same-key", jsonRecord.IdempotencyKey);

        if (verb == "lesson-record")
        {
            var repeated = fixture.Run(jsonArgs, fixture.JsonWorkspace);
            AssertEnvelope(repeated, jsonRecord);
            var afterRepeat = Assert.Single(await fixture.Intents(fixture.JsonWorkspace)
                .ListForGoalAsync(OperatorIntentScopes.Workspace));
            Assert.Equal(jsonRecord.Id, afterRepeat.Id);
            Assert.Equal(JsonSerializer.Serialize(jsonRecord), JsonSerializer.Serialize(afterRepeat));
        }
    }

    private static void AssertEnvelope(SubmitResult result, OperatorIntentRecord intent)
    {
        Assert.Equal($"{Warning}{Environment.NewLine}", result.Error);
        Assert.DoesNotContain(Warning, result.Output);
        Assert.DoesNotContain("Operator intent queued:", result.Output);
        // Parsing the complete output rejects trailing text and a second JSON document.
        using var json = JsonDocument.Parse(result.Output);
        Assert.Equal("mcg.operator-intent", json.RootElement.GetProperty("schema").GetString());
        Assert.Equal(1, json.RootElement.GetProperty("schemaVersion").GetInt32());
        var payload = json.RootElement.GetProperty("payload");
        Assert.Equal(intent.Id, payload.GetProperty("id").GetString());
        Assert.Equal(intent.Verb, payload.GetProperty("verb").GetString());
        Assert.Equal(intent.GoalId, payload.GetProperty("goalId").GetString());
        Assert.Equal(intent.TaskId, payload.GetProperty("taskId").GetString());
        Assert.Equal(intent.Status.ToString(), payload.GetProperty("status").GetString());
    }

    private sealed record SubmitResult(string Output, string Error);

    private sealed class SubmitFixture : IDisposable
    {
        private readonly string _root;
        private readonly AgentOrchestratorKernel _kernel = new();
        private readonly string _evidence;
        private string? _experimentId;
        public OrchestratorWorkspace TextWorkspace { get; }
        public OrchestratorWorkspace JsonWorkspace { get; }
        public Goal Goal { get; }

        private SubmitFixture(string root)
        {
            _root = root;
            TextWorkspace = OrchestratorWorkspace.ForDirectory(Path.Combine(root, "text-state")) with { RootDirectory = root };
            JsonWorkspace = OrchestratorWorkspace.ForDirectory(Path.Combine(root, "json-state")) with { RootDirectory = root };
            Goal = _kernel.CreateGoal("Workspace submit envelope",
                [new TaskSpec(TaskId.New(), "Do work", AgentRole.Developer)]);
            _kernel.ActivateGoal(Goal.Id, AgentCatalog.Default().Agents);
            var proof = Path.Combine(root, "proof.txt");
            File.WriteAllText(proof, "Checked receipt");
            _evidence = "operator-evidence:" + proof;
        }

        public static async Task<SubmitFixture> Create(string root, string verb)
        {
            var fixture = new SubmitFixture(root);
            foreach (var workspace in new[] { fixture.TextWorkspace, fixture.JsonWorkspace })
            {
                StateDbMigrations.EnsureUpToDate(workspace.SqliteStatePath);
                await new SqliteOrchestratorStateRepository(workspace.SqliteStatePath).SaveAsync(fixture._kernel);
                _ = fixture.Intents(workspace);
            }
            if (verb == "experiment-apply-flag")
            {
                var experiment = await new ExperimentStore(fixture.TextWorkspace.ExperimentStorePath)
                    .AddAsync(CliCommandTestsExperiments.Spec() with
                    {
                        Intervention = new(ExperimentInterventionKind.ConfigFlag, "Trial a conductor boolean",
                            new(ExperimentFlagFileKind.ConductorPolicy, "followerGatesEnabled", true))
                    });
                fixture._experimentId = experiment.Id;
                // AddAsync closes its unpooled connection; copy that database to preserve identity.
                File.Copy(fixture.TextWorkspace.ExperimentStorePath, fixture.JsonWorkspace.ExperimentStorePath);
                foreach (var workspace in new[] { fixture.TextWorkspace, fixture.JsonWorkspace })
                {
                    var seeded = await ExperimentStore.OpenReadOnly(workspace.ExperimentStorePath)
                        .ResolveAsync(experiment.Id);
                    Assert.NotNull(seeded);
                    Assert.Equal(experiment.Id, seeded.Id);
                }
            }
            return fixture;
        }

        public string[] Arguments(string verb)
        {
            string[] attribution = ["--operator-actor", "workspace-test", "--actor-kind", "agent",
                "--idempotency-key", "same-key"];
            return verb switch
            {
                "lesson-record" => ["lesson", "record", "--situation", "When a run fails",
                    "--rule", "Read its receipt", "--evidence", _evidence, "--applies-to", "tests",
                    "--goal", Goal.Id.Value, "--until-goal", Goal.Id.Value, .. attribution],
                "lesson-retire" => ["lesson", "retire", "existing-lesson", "--reason", "Outdated rule",
                    "--evidence", _evidence, .. attribution],
                "escape-record" => ["escape", "record", "--goal", Goal.Id.Value, "--reason", "Escaped defect",
                    "--evidence", _evidence, "--found-by-goal", Goal.Id.Value, .. attribution],
                "experiment-apply-flag" => [verb, _experimentId!, .. attribution],
                _ => throw new ArgumentOutOfRangeException(nameof(verb))
            };
        }

        public SqliteOperatorIntentStore Intents(OrchestratorWorkspace workspace) =>
            SqliteOperatorIntentStore.ForDirectories(workspace.OrchestratorDirectory, workspace.LogDirectory);

        public SubmitResult Run(string[] args, OrchestratorWorkspace workspace)
        {
            var error = "";
            using var injectedOutput = new StringWriter();
            var consoleOutput = CaptureConsole(() => error = AsyncLocalConsoleRouter.Error.CaptureLocal(() =>
            {
                if (args[0] == "lesson")
                    Assert.Equal(0, CliLessonCommands.Run(args, workspace, injectedOutput, _root));
                else if (args[0] == "escape")
                    Assert.Equal(0, CliEscapeCommands.Run(args, workspace, injectedOutput));
                else
                {
                    var parts = CliArgumentParser.NormalizeArgs(args);
                    CliCommandHelp.ThrowIfInvalidFlags(parts);
                    IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
                    var providers = new InMemoryModelProviderRegistry([]);
                    var profiles = WorkerProfileCatalog.Default();
                    Goal? currentGoal = Goal;
                    Assert.False(CliPersistentStateRunner.ExecuteCommand(parts,
                        new SqliteOrchestratorStateRepository(workspace.SqliteStatePath), workspace,
                        ref agents, providers, ref profiles, ref currentGoal));
                }
            }));
            if (args[0] is "lesson" or "escape")
            {
                Assert.Empty(consoleOutput);
                return new SubmitResult(injectedOutput.ToString(), error);
            }
            Assert.Equal("", injectedOutput.ToString());
            return new SubmitResult(consoleOutput, error);
        }

        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
