using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: private state roots and async-local console capture; no environment mutation.
public sealed class CliOperatorIntentSubmitJsonTestsCriterionEvidence : CliTaskQueryTestSupport
{
    private const string Candidate = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Warning =
        "WARNING: intent queued but NO conduct loop is running - it will not apply until a loop starts.";

    [Fact]
    public Task Map_Json_PreservesRecordHumanTextAndIdempotency() => CheckSingle("criterion-evidence-map");

    [Fact]
    public Task Record_Json_PreservesRecordAndHumanText() => CheckSingle("criterion-evidence-record");

    [Fact]
    public Task Repair_Json_PreservesRecordAndHumanText() => CheckSingle("criterion-evidence-repair");

    private static async Task CheckSingle(string verb)
    {
        using var fixture = await Fixture.Create();
        var args = fixture.Arguments(verb, "1", "same-key");
        var human = fixture.Run(args, fixture.TextWorkspace);
        Assert.Equal(0, human.ExitCode);
        Assert.Empty(human.Error);
        var textRecord = Assert.Single(await fixture.Intents(fixture.TextWorkspace).ListForGoalAsync(fixture.Goal.Id.Value));
        var targetLine = verb == "criterion-evidence-record" ? "" :
            $"Target: criterion 1 (v1) criterion-v1-0 Requirement 1{Environment.NewLine}";
        Assert.Equal(QueuedLine(textRecord) + Environment.NewLine + targetLine + Warning + Environment.NewLine, human.Output);

        // Place JSON before positionals to catch accidental argument consumption.
        string[] jsonArgs = [args[0], "--json", .. args.Skip(1)];
        var result = fixture.Run(jsonArgs, fixture.JsonWorkspace);
        var jsonRecord = Assert.Single(await fixture.Intents(fixture.JsonWorkspace).ListForGoalAsync(fixture.Goal.Id.Value));
        Assert.Equal(0, result.ExitCode);
        AssertSingleOutputLine(result.Output);
        AssertJsonStreams(result, targetLine);
        using var json = JsonDocument.Parse(result.Output);
        AssertEnvelope(json.RootElement, jsonRecord);
        Assert.Equal(verb, jsonRecord.Verb);
        Assert.Equal(fixture.Goal.Id.Value, jsonRecord.GoalId);
        Assert.Null(jsonRecord.TaskId);
        Assert.Equal(OperatorIntentStatus.Pending, jsonRecord.Status);
        Assert.Equal("same-key", jsonRecord.IdempotencyKey);
        Assert.NotEqual(textRecord.Id, jsonRecord.Id);
        // Compare every stored field except generated identity and creation time.
        Assert.Equal(JsonSerializer.Serialize(textRecord with { Id = "", CreatedAt = default }),
            JsonSerializer.Serialize(jsonRecord with { Id = "", CreatedAt = default }));

        if (verb == "criterion-evidence-map")
        {
            var repeated = fixture.Run(jsonArgs, fixture.JsonWorkspace);
            Assert.Equal(0, repeated.ExitCode);
            AssertSingleOutputLine(repeated.Output);
            AssertJsonStreams(repeated, targetLine);
            using var repeatedJson = JsonDocument.Parse(repeated.Output);
            AssertEnvelope(repeatedJson.RootElement, jsonRecord);
            var afterRepeat = Assert.Single(await fixture.Intents(fixture.JsonWorkspace).ListForGoalAsync(fixture.Goal.Id.Value));
            Assert.Equal(JsonSerializer.Serialize(jsonRecord), JsonSerializer.Serialize(afterRepeat));
        }
    }

    [Fact]
    public async Task Batch_Json_EmitsOrderedArrayAndEchoesIdempotentIds()
    {
        using var fixture = await Fixture.Create();
        var args = fixture.Arguments("criterion-evidence-map", "1,2,3", "k").Append("--json").ToArray();
        var result = fixture.Run(args, fixture.JsonWorkspace);
        var intents = await fixture.Intents(fixture.JsonWorkspace).ListForGoalAsync(fixture.Goal.Id.Value);
        Assert.Equal(3, intents.Count);
        var byKey = intents.ToDictionary(intent => intent.IdempotencyKey);
        var ordered = Enumerable.Range(1, 3).Select(number => byKey[$"k-{number}"]).ToArray();
        var ids = AssertBatch(result, ordered);

        var repeated = fixture.Run(args, fixture.JsonWorkspace);
        Assert.Equal(ids, AssertBatch(repeated, ordered));
        var afterRepeat = await fixture.Intents(fixture.JsonWorkspace).ListForGoalAsync(fixture.Goal.Id.Value);
        Assert.Equal(3, afterRepeat.Count);
        Assert.Equal(intents.Select(intent => intent.Id).OrderBy(id => id),
            afterRepeat.Select(intent => intent.Id).OrderBy(id => id));
    }

    [Fact]
    public async Task Batch_Human_OutputRetainsSevenOrderedLines()
    {
        using var fixture = await Fixture.Create();
        var result = fixture.Run(fixture.Arguments("criterion-evidence-map", "1,2,3", "k"), fixture.TextWorkspace);
        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.Error);
        var intents = await fixture.Intents(fixture.TextWorkspace).ListForGoalAsync(fixture.Goal.Id.Value);
        Assert.Equal(3, intents.Count);
        var byKey = intents.ToDictionary(intent => intent.IdempotencyKey);
        var expected = Enumerable.Range(1, 3).Select(number => QueuedLine(byKey[$"k-{number}"]))
            .Concat(Enumerable.Range(1, 3).Select(TargetLine)).Append(Warning).ToArray();
        Assert.Equal(7, expected.Length);
        Assert.Equal(expected, result.Output.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
        Assert.Equal(string.Join(Environment.NewLine, expected) + Environment.NewLine, result.Output);
    }

    [Theory]
    [InlineData("1,1", "Criterion 1 appears more than once in '1,1'.")]
    [InlineData("1,0", "Invalid criterion element '0' at position 2 in '1,0'; criterion numbers are positive brief numbers.")]
    public async Task InvalidBatch_Json_PreservesErrorAndEnqueuesNothing(string numbers, string message)
    {
        using var fixture = await Fixture.Create();
        var args = fixture.Arguments("criterion-evidence-map", numbers, "invalid");
        var human = fixture.Run(args, fixture.TextWorkspace);
        var json = fixture.Run(args.Append("--json").ToArray(), fixture.JsonWorkspace);
        Assert.Equal(1, human.ExitCode);
        Assert.Equal(human.ExitCode, json.ExitCode);
        Assert.Equal($"Error: {message}{Environment.NewLine}", human.Error);
        Assert.Equal(human.Error, json.Error);
        Assert.Empty(human.Output);
        Assert.Empty(json.Output);
        Assert.Empty(await fixture.Intents(fixture.TextWorkspace).ListForGoalAsync(fixture.Goal.Id.Value));
        Assert.Empty(await fixture.Intents(fixture.JsonWorkspace).ListForGoalAsync(fixture.Goal.Id.Value));
    }

    [Theory]
    [InlineData("criterion-evidence-map")]
    [InlineData("criterion-evidence-record")]
    [InlineData("criterion-evidence-repair")]
    public async Task Json_DoesNotAdmitUnknownFlags(string verb)
    {
        using var fixture = await Fixture.Create();
        var args = fixture.Arguments(verb, "1", "unknown").Concat(new[] { "--waive-evidence", "--json" }).ToArray();
        var stderr = "";
        var stdout = CaptureConsole(() => stderr = AsyncLocalConsoleRouter.Error.CaptureLocal(() =>
        {
            var error = Assert.Throws<ArgumentException>(() => fixture.Run(args, fixture.JsonWorkspace));
            Assert.Contains("Unknown option '--waive-evidence'", error.Message, StringComparison.Ordinal);
        }));
        Assert.Empty(stdout);
        Assert.Empty(stderr);
        Assert.Empty(await fixture.Intents(fixture.JsonWorkspace).ListForGoalAsync(fixture.Goal.Id.Value));
    }

    private static string[] AssertBatch(SubmitResult result, IReadOnlyList<OperatorIntentRecord> ordered)
    {
        Assert.Equal(0, result.ExitCode);
        var line = AssertSingleOutputLine(result.Output);
        AssertJsonStreams(result, string.Concat(Enumerable.Range(1, 3).Select(number => TargetLine(number) + Environment.NewLine)));
        using var json = JsonDocument.Parse(result.Output);
        Assert.Equal(JsonValueKind.Array, json.RootElement.ValueKind);
        var elements = json.RootElement.EnumerateArray().ToArray();
        Assert.Equal(3, elements.Length);
        Assert.Equal("[" + string.Join(",", elements.Select(element => element.GetRawText())) + "]", line);
        for (var index = 0; index < elements.Length; index++)
            AssertEnvelope(elements[index], ordered[index]);
        var ids = elements.Select(element => element.GetProperty("payload").GetProperty("id").GetString()!).ToArray();
        Assert.Equal(ordered.Select(intent => intent.Id), ids);
        return ids;
    }

    private static string AssertSingleOutputLine(string output)
    {
        var line = Assert.Single(output.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
        Assert.Equal(line + Environment.NewLine, output);
        return line;
    }

    private static void AssertJsonStreams(SubmitResult result, string targetLines)
    {
        Assert.Equal(targetLines + Warning + Environment.NewLine, result.Error);
        Assert.DoesNotContain("Operator intent queued:", result.Output);
        Assert.DoesNotContain("Target:", result.Output);
        Assert.DoesNotContain(Warning, result.Output);
    }

    private static void AssertEnvelope(JsonElement envelope, OperatorIntentRecord intent)
    {
        Assert.Equal("mcg.operator-intent", envelope.GetProperty("schema").GetString());
        Assert.Equal(1, envelope.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(JsonValueKind.String, envelope.GetProperty("generatedAt").ValueKind);
        var payload = envelope.GetProperty("payload");
        Assert.Equal(intent.Id, payload.GetProperty("id").GetString());
        Assert.Equal(intent.Verb, payload.GetProperty("verb").GetString());
        Assert.Equal(intent.GoalId, payload.GetProperty("goalId").GetString());
        Assert.Null(intent.TaskId);
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("taskId").ValueKind);
        Assert.Equal(OperatorIntentStatus.Pending, intent.Status);
        Assert.Equal("Pending", payload.GetProperty("status").GetString());
    }

    private static string QueuedLine(OperatorIntentRecord intent) =>
        $"Operator intent queued: id={intent.Id} verb={intent.Verb} goal={intent.GoalId} " +
        $"status=Pending; poll with operator-intent-status {intent.Id} (or add --wait).";

    private static string TargetLine(int number) =>
        $"Target: criterion {number} (v1) criterion-v1-{number - 1} Requirement {number}";

    private sealed record SubmitResult(string Output, string Error, int ExitCode);

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = CreateTempDirectory();
        private readonly AgentOrchestratorKernel _kernel = new();
        public OrchestratorWorkspace TextWorkspace { get; }
        public OrchestratorWorkspace JsonWorkspace { get; }
        public Goal Goal { get; }

        private Fixture()
        {
            TextWorkspace = OrchestratorWorkspace.ForDirectory(Path.Combine(_root, "text-state"));
            JsonWorkspace = OrchestratorWorkspace.ForDirectory(Path.Combine(_root, "json-state"));
            Goal = _kernel.CreateGoal("Criterion submit envelopes");
            _kernel.RecordGoalRefinement(Goal.Id, new RefinedSpec("Three criteria",
                Enumerable.Range(1, 3).Select(number => $"Requirement {number}").ToArray(),
                VerificationClass.TestVerifiable, [], []));
        }

        public static async Task<Fixture> Create()
        {
            var fixture = new Fixture();
            foreach (var workspace in new[] { fixture.TextWorkspace, fixture.JsonWorkspace })
            {
                StateDbMigrations.EnsureUpToDate(workspace.SqliteStatePath);
                await new SqliteOrchestratorStateRepository(workspace.SqliteStatePath).SaveAsync(fixture._kernel);
                _ = fixture.Intents(workspace);
            }
            return fixture;
        }

        public string[] Arguments(string verb, string numbers, string key)
        {
            string[] values = verb switch
            {
                "criterion-evidence-map" => ["--criterion", numbers, "operator", "observation", "finding", Candidate],
                "criterion-evidence-record" => ["criterion-v1-0", "operator", Candidate, "receipt", "observation", "passed", "Checked"],
                "criterion-evidence-repair" => ["malformed", "--criterion", numbers, "operator", "observation", "finding", Candidate, "Repair"],
                _ => throw new ArgumentOutOfRangeException(nameof(verb))
            };
            return [verb, "--goal", Goal.Id.Value[..8], .. values, "--idempotency-key", key,
                "--operator-actor", "tester@example"];
        }

        public SqliteOperatorIntentStore Intents(OrchestratorWorkspace workspace) =>
            SqliteOperatorIntentStore.ForDirectories(workspace.OrchestratorDirectory, workspace.LogDirectory);

        public SubmitResult Run(string[] args, OrchestratorWorkspace workspace)
        {
            var parts = CliArgumentParser.NormalizeArgs(args);
            CliCommandHelp.ThrowIfInvalidFlags(parts);
            var exitCode = -1;
            var error = "";
            var output = CaptureConsole(() => error = AsyncLocalConsoleRouter.Error.CaptureLocal(() =>
            {
                if (parts[0] == "criterion-evidence-map")
                    exitCode = CliOperatorIntentRoute.Run(parts, workspace);
                else
                {
                    IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
                    var providers = new InMemoryModelProviderRegistry([]);
                    var profiles = WorkerProfileCatalog.Default();
                    Goal? currentGoal = Goal;
                    Assert.False(CliPersistentStateRunner.ExecuteCommand(parts,
                        new SqliteOrchestratorStateRepository(workspace.SqliteStatePath), workspace,
                        ref agents, providers, ref profiles, ref currentGoal));
                    exitCode = 0;
                }
            }));
            return new SubmitResult(output, error, exitCode);
        }

        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
