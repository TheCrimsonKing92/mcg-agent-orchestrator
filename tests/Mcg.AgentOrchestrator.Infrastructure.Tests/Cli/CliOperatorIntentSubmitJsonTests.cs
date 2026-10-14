using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: private state roots and async-local console capture; no environment mutation.
public sealed class CliOperatorIntentSubmitJsonTests : CliTaskQueryTestSupport
{
    private const string Warning =
        "WARNING: intent queued but NO conduct loop is running - it will not apply until a loop starts.";

    [Theory]
    [InlineData("adjudicate")]
    [InlineData("answer")]
    public async Task Submit_Json_PreservesRecordHumanTextAndIdempotency(string verb)
    {
        using var fixture = await SubmitFixture.Create(CreateTempDirectory(), verb);
        var args = fixture.Arguments(verb, "same-key");
        var human = fixture.Run(args, fixture.TextWorkspace);
        Assert.False(human.Changed);
        Assert.Empty(human.Error);
        var textRecord = Assert.Single(await fixture.Intents(fixture.TextWorkspace).ListForGoalAsync(fixture.Goal.Id.Value));
        var expectedLine = verb == "adjudicate"
            ? $"Operator intent queued: id={textRecord.Id} verb=adjudicate selector=current goal={fixture.Goal.Id.Value} " +
                $"task={fixture.Goal.Tasks.Single().Id.Value} status=Pending; poll with operator-intent-status {textRecord.Id} (or add --wait)."
            : $"Operator intent queued: id={textRecord.Id} verb=answer goal={fixture.Goal.Id.Value} " +
                $"status=Pending; poll with operator-intent-status {textRecord.Id} (or add --wait).";
        Assert.Equal($"{expectedLine}{Environment.NewLine}{Warning}{Environment.NewLine}", human.Output);

        // Put the flag before the positional task or goal so it cannot become a selector.
        string[] jsonArgs = verb == "adjudicate"
            ? [args[0], "--json", .. args.Skip(1)]
            : [args[0], args[1], "--json", .. args.Skip(2)];
        var result = fixture.Run(jsonArgs, fixture.JsonWorkspace);
        Assert.False(result.Changed);
        var jsonRecord = Assert.Single(await fixture.Intents(fixture.JsonWorkspace).ListForGoalAsync(fixture.Goal.Id.Value));
        AssertEnvelope(result, jsonRecord);
        Assert.NotEqual(textRecord.Id, jsonRecord.Id);
        // State roots are separate; the source directory, input file and seeded identities are shared.
        Assert.Equal(verb == "adjudicate", textRecord.AdmissionExpiresAt.HasValue);
        Assert.Equal(verb == "adjudicate", jsonRecord.AdmissionExpiresAt.HasValue);
        // Pin every record field, including payload paths, except generated identity and timestamps.
        Assert.Equal(JsonSerializer.Serialize(textRecord with { Id = "", CreatedAt = default, AdmissionExpiresAt = null }),
            JsonSerializer.Serialize(jsonRecord with { Id = "", CreatedAt = default, AdmissionExpiresAt = null }));
        Assert.Equal("same-key", jsonRecord.IdempotencyKey);
        Assert.Equal(OperatorIntentStatus.Pending, jsonRecord.Status);

        var repeated = fixture.Run(jsonArgs, fixture.JsonWorkspace);
        Assert.False(repeated.Changed);
        AssertEnvelope(repeated, jsonRecord);
        var afterRepeat = Assert.Single(await fixture.Intents(fixture.JsonWorkspace).ListForGoalAsync(fixture.Goal.Id.Value));
        Assert.Equal(jsonRecord.Id, afterRepeat.Id);
        Assert.Equal(JsonSerializer.Serialize(jsonRecord), JsonSerializer.Serialize(afterRepeat));
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public async Task Attention_InlineJson_MatchesHumanRecord(bool repl, bool scoped, bool trailing)
    {
        using var fixture = await SubmitFixture.Create(CreateTempDirectory(), "answer");
        var target = scoped
            ? new[] { fixture.Goal.Id.Value[..8], fixture.Request!.Id.Value[..8] }
            : new[] { fixture.Request!.Id.Value[..8] };
        string[] args = ["attention", "answer", .. target, "Use", "\"selected   scope\"", "today",
            "--operator-actor", "agent-one", "--actor-kind", "Agent", "--evidence", "receipt-1",
            "--precedent", "rule-1", "--idempotency-key", "inline-key"];
        string[] jsonArgs = trailing ? [.. args, "--json"] : [args[0], args[1], "--json", .. args.Skip(2)];
        var humanParts = repl ? CliArgumentParser.SplitCommand(string.Join(' ', args)) : CliArgumentParser.NormalizeArgs(args);
        var jsonParts = repl ? CliArgumentParser.SplitCommand(string.Join(' ', jsonArgs)) : CliArgumentParser.NormalizeArgs(jsonArgs);
        Assert.Equal(humanParts.ToArray(), jsonParts.Where(part => part != "--json").ToArray());
        var human = fixture.Run(humanParts.ToArray(), fixture.TextWorkspace, normalize: false);
        Assert.Empty(human.Error);
        var textRecord = Assert.Single(await fixture.Intents(fixture.TextWorkspace).ListForGoalAsync(fixture.Goal.Id.Value));
        var result = fixture.Run(jsonParts.ToArray(), fixture.JsonWorkspace, normalize: false);
        var intent = Assert.Single(await fixture.Intents(fixture.JsonWorkspace).ListForGoalAsync(fixture.Goal.Id.Value));
        AssertEnvelope(result, intent);
        var payload = JsonSerializer.Deserialize<AnswerOperatorIntentPayload>(intent.PayloadJson, OperatorIntentJson.Options)!;
        Assert.Equal("Use \"selected   scope\" today --operator-actor agent-one --actor-kind Agent " +
            "--evidence receipt-1 --precedent rule-1 --idempotency-key inline-key", payload.Text);
        Assert.Equal(fixture.Request!.Id.Value, payload.TargetId);
        // Inline metadata remains answer text in the existing parser, so the default key is the new id.
        Assert.Equal(textRecord.Id, textRecord.IdempotencyKey);
        Assert.Equal(intent.Id, intent.IdempotencyKey);
        Assert.Equal(JsonSerializer.Serialize(textRecord with { Id = "", IdempotencyKey = "", CreatedAt = default }),
            JsonSerializer.Serialize(intent with { Id = "", IdempotencyKey = "", CreatedAt = default }));
    }

    [Fact]
    public void Attention_JsonFollowingInlineEvidence_KeepsHumanParsing()
    {
        string[] args = ["attention", "answer", "12345678", "Use scope", "--evidence"];
        Assert.Equal(CliArgumentParser.NormalizeArgs(args).ToArray(),
            CliArgumentParser.NormalizeArgs([.. args, "--json"]).SkipLast(1).ToArray());
    }

    [Theory]
    [InlineData("attention")]
    public void OtherSubmits_Json_RemainsUnknownOption(string command)
    {
        string[] args = command == "attention"
            ? ["attention", "dismiss", "--item", "x", "--json"]
            : [command, "1", "--json"];
        var error = Assert.Throws<ArgumentException>(() => CliCommandHelp.ThrowIfInvalidFlags(args));
        Assert.Contains("Unknown option '--json'", error.Message);
    }

    private static void AssertEnvelope(SubmitResult result, OperatorIntentRecord intent)
    {
        Assert.Equal($"{Warning}{Environment.NewLine}", result.Error);
        Assert.DoesNotContain(Warning, result.Output);
        Assert.DoesNotContain("Operator intent queued:", result.Output);
        using var json = JsonDocument.Parse(result.Output);
        Assert.Equal("mcg.operator-intent", json.RootElement.GetProperty("schema").GetString());
        Assert.Equal(1, json.RootElement.GetProperty("schemaVersion").GetInt32());
        var payload = json.RootElement.GetProperty("payload");
        Assert.Equal(intent.Id, payload.GetProperty("id").GetString());
        Assert.Equal(intent.Verb, payload.GetProperty("verb").GetString());
        Assert.Equal(intent.GoalId, payload.GetProperty("goalId").GetString());
        Assert.Equal("Pending", payload.GetProperty("status").GetString());
    }

    private sealed record SubmitResult(string Output, string Error, bool Changed);

    private sealed class SubmitFixture : IDisposable
    {
        private readonly string _root;
        private readonly AgentOrchestratorKernel _kernel;
        private readonly string _textFile;
        public OrchestratorWorkspace TextWorkspace { get; }
        public OrchestratorWorkspace JsonWorkspace { get; }
        public Goal Goal { get; }
        public HumanInputRequest? Request { get; }

        private SubmitFixture(string root, string verb)
        {
            _root = root;
            // Two independent state roots for the same target repository keep persisted paths equal.
            TextWorkspace = OrchestratorWorkspace.ForDirectory(Path.Combine(root, "text-state")) with { RootDirectory = root };
            JsonWorkspace = OrchestratorWorkspace.ForDirectory(Path.Combine(root, "json-state")) with { RootDirectory = root };
            _kernel = new AgentOrchestratorKernel();
            Goal = _kernel.CreateGoal("Submit envelope", [new TaskSpec(TaskId.New(), "Do work", AgentRole.Developer)]);
            _kernel.ActivateGoal(Goal.Id, AgentCatalog.Default().Agents);
            _kernel.ReportTaskProgress(Goal.Id, Goal.Tasks.Single().Id, WorkTaskStatus.Failed, "failed");
            if (verb == "answer")
                Request = _kernel.RequestHumanInput(Goal.Id, null, "Which scope?");
            _textFile = Path.Combine(root, "operator-text.txt");
            File.WriteAllText(_textFile, "Use selected scope");
        }

        public static async Task<SubmitFixture> Create(string root, string verb)
        {
            var fixture = new SubmitFixture(root, verb);
            foreach (var workspace in new[] { fixture.TextWorkspace, fixture.JsonWorkspace })
            {
                StateDbMigrations.EnsureUpToDate(workspace.SqliteStatePath);
                await new SqliteOrchestratorStateRepository(workspace.SqliteStatePath).SaveAsync(fixture._kernel);
                _ = fixture.Intents(workspace);
            }
            return fixture;
        }

        public string[] Arguments(string verb, string key) => verb == "adjudicate"
            ? ["adjudicate", "1", "close", "--text-file", _textFile, "--evidence", "receipt-1", "--idempotency-key", key]
            : ["attention", "answer", Goal.Id.Value[..8], Request!.Id.Value[..8], "--text-file", _textFile,
                "--idempotency-key", key];

        public SqliteOperatorIntentStore Intents(OrchestratorWorkspace workspace) =>
            SqliteOperatorIntentStore.ForDirectories(workspace.OrchestratorDirectory, workspace.LogDirectory);

        public SubmitResult Run(string[] args, OrchestratorWorkspace workspace, bool normalize = true)
        {
            var parts = normalize ? CliArgumentParser.NormalizeArgs(args) : args;
            CliCommandHelp.ThrowIfInvalidFlags(parts);
            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var providers = new InMemoryModelProviderRegistry([]);
            var profiles = WorkerProfileCatalog.Default();
            Goal? currentGoal = Goal;
            var changed = true;
            var error = "";
            var output = CaptureConsole(() => error = AsyncLocalConsoleRouter.Error.CaptureLocal(() =>
            {
                if (parts[0].Equals("attention", StringComparison.OrdinalIgnoreCase))
                    changed = CliCommandDispatcher.ExecuteCommand(parts, _kernel, workspace,
                        ref agents, providers, ref profiles, ref currentGoal);
                else
                    changed = CliPersistentStateRunner.ExecuteCommand(parts,
                        new SqliteOrchestratorStateRepository(workspace.SqliteStatePath), workspace,
                        ref agents, providers, ref profiles, ref currentGoal);
            }));
            return new SubmitResult(output, error, changed);
        }

        public void Dispose() => Directory.Delete(_root, true);
    }
}
