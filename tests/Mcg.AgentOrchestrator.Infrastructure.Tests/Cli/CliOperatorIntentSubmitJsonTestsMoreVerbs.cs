using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: private state roots and async-local console capture; no environment mutation.
public sealed class CliOperatorIntentSubmitJsonTestsMoreVerbs : CliTaskQueryTestSupport
{
    private const string Warning =
        "WARNING: intent queued but NO conduct loop is running - it will not apply until a loop starts.";

    [Fact]
    public Task Retry_Json_PreservesRecordHumanTextAndIdempotency() => CheckSubmit("retry");

    [Fact]
    public Task Progress_Json_PreservesRecordAndHumanText() => CheckSubmit("progress");

    [Fact]
    public Task VerifyManual_Json_PreservesRecordAndHumanText() => CheckSubmit("verify-manual");

    [Fact]
    public Task ApprovePolicyChange_Json_PreservesRecordAndHumanText() => CheckSubmit("approve-policy-change");

    [Fact]
    public Task CancelDispatch_Json_PreservesRecordHumanTextAndRecoveryHint() => CheckSubmit("cancel-dispatch");

    private static async Task CheckSubmit(string verb)
    {
        using var fixture = await SubmitFixture.Create(CreateTempDirectory(), verb);
        var args = fixture.Arguments(verb, "same-key");
        var human = fixture.Run(args, fixture.TextWorkspace);
        Assert.False(human.Changed);
        Assert.Empty(human.Error);
        var textRecord = Assert.Single(await fixture.Intents(fixture.TextWorkspace).ListForGoalAsync(fixture.Goal.Id.Value));
        var goalId = fixture.Goal.Id.Value;
        var taskId = fixture.Goal.Tasks.Single().Id.Value;
        var expectedLine = verb switch
        {
            "approve-policy-change" =>
                $"Operator intent queued: id={textRecord.Id} verb=approve-policy-change goal={goalId} " +
                $"status=Pending; poll with operator-intent-status {textRecord.Id} (or add --wait).",
            "cancel-dispatch" =>
                $"Operator intent queued: id={textRecord.Id} verb=cancel-dispatch goal={goalId} task={taskId} " +
                $"status=Pending; poll with operator-intent-status {textRecord.Id} (or add --wait).",
            _ => $"Operator intent queued: id={textRecord.Id} verb={verb} selector=current goal={goalId} " +
                $"task={taskId} status=Pending; poll with operator-intent-status {textRecord.Id} (or add --wait)."
        };
        var nextStepLine = verb == "cancel-dispatch"
            ? $"Next step once applied: adjudicate --goal {goalId[..8]} 1 route --cause <cause> --text-file <note> --evidence <reference> " +
                "(close and reopen-regate do not apply to a cancelled task)." + Environment.NewLine
            : "";
        Assert.Equal($"{expectedLine}{Environment.NewLine}{nextStepLine}{Warning}{Environment.NewLine}", human.Output);

        // Before the first positional argument, so the flag cannot be read as a selector.
        string[] jsonArgs = [args[0], "--json", .. args.Skip(1)];
        var result = fixture.Run(jsonArgs, fixture.JsonWorkspace);
        Assert.False(result.Changed);
        var jsonRecord = Assert.Single(await fixture.Intents(fixture.JsonWorkspace).ListForGoalAsync(goalId));
        AssertEnvelope(result, jsonRecord, nextStepLine);
        Assert.Equal(verb, jsonRecord.Verb);
        Assert.Equal(goalId, jsonRecord.GoalId);
        Assert.Equal(verb == "approve-policy-change" ? null : taskId, jsonRecord.TaskId);
        Assert.Equal(OperatorIntentStatus.Pending, jsonRecord.Status);
        Assert.NotEqual(textRecord.Id, jsonRecord.Id);
        // Serialization checks every stored field; only generated identity and creation time differ.
        Assert.Equal(JsonSerializer.Serialize(textRecord with { Id = "", CreatedAt = default }),
            JsonSerializer.Serialize(jsonRecord with { Id = "", CreatedAt = default }));
        Assert.Equal("same-key", jsonRecord.IdempotencyKey);

        if (verb == "retry")
        {
            var repeated = fixture.Run(jsonArgs, fixture.JsonWorkspace);
            Assert.False(repeated.Changed);
            AssertEnvelope(repeated, jsonRecord, nextStepLine);
            var afterRepeat = Assert.Single(await fixture.Intents(fixture.JsonWorkspace).ListForGoalAsync(goalId));
            Assert.Equal(jsonRecord.Id, afterRepeat.Id);
            Assert.Equal(JsonSerializer.Serialize(jsonRecord), JsonSerializer.Serialize(afterRepeat));
        }
    }

    private static void AssertEnvelope(SubmitResult result, OperatorIntentRecord intent, string nextStepLine)
    {
        Assert.Equal($"{nextStepLine}{Warning}{Environment.NewLine}", result.Error);
        Assert.DoesNotContain(Warning, result.Output);
        Assert.DoesNotContain("Next step once applied:", result.Output);
        Assert.DoesNotContain("Operator intent queued:", result.Output);
        // Parse rejects trailing text or a second JSON document.
        using var json = JsonDocument.Parse(result.Output);
        Assert.Equal("mcg.operator-intent", json.RootElement.GetProperty("schema").GetString());
        Assert.Equal(1, json.RootElement.GetProperty("schemaVersion").GetInt32());
        var payload = json.RootElement.GetProperty("payload");
        Assert.Equal(intent.Id, payload.GetProperty("id").GetString());
        Assert.Equal(intent.Verb, payload.GetProperty("verb").GetString());
        Assert.Equal(intent.GoalId, payload.GetProperty("goalId").GetString());
        Assert.Equal(intent.TaskId, payload.GetProperty("taskId").GetString());
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

        private SubmitFixture(string root, string verb)
        {
            _root = root;
            TextWorkspace = OrchestratorWorkspace.ForDirectory(Path.Combine(root, "text-state")) with { RootDirectory = root };
            JsonWorkspace = OrchestratorWorkspace.ForDirectory(Path.Combine(root, "json-state")) with { RootDirectory = root };
            if (verb == "cancel-dispatch")
            {
                _kernel = new AgentOrchestratorKernel();
                Goal = _kernel.CreateGoal("Submit envelope", [new TaskSpec(TaskId.New(), "Do work", AgentRole.Developer)]);
                _kernel.ActivateGoal(Goal.Id, AgentCatalog.Default().Agents);
                var task = Goal.Tasks.Single();
                var startedAt = DateTimeOffset.UtcNow;
                _kernel.RecordTaskDispatch(Goal.Id, task.Id,
                    new TaskDispatchRecord("worker", "worker command", root, startedAt));
                _kernel.RecordTaskProcessStarted(Goal.Id, task.Id,
                    new TaskProcessRecord(4242, "worker command", root, Path.Combine(root, "out.log"),
                        Path.Combine(root, "err.log"), Path.Combine(root, "missing-cancel-exit"),
                        startedAt, null, null));
            }
            else
            {
                _kernel = new AgentOrchestratorKernel();
                Goal = _kernel.CreateGoal("Submit envelope", [new TaskSpec(TaskId.New(), "Do work", AgentRole.Developer)]);
                _kernel.ActivateGoal(Goal.Id, AgentCatalog.Default().Agents);
                _kernel.ReportTaskProgress(Goal.Id, Goal.Tasks.Single().Id, WorkTaskStatus.Failed, "failed");
            }
            _textFile = Path.Combine(root, "operator-text.txt");
            File.WriteAllText(_textFile, "Checked by operator");
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

        public string[] Arguments(string verb, string key) => verb switch
        {
            "retry" => [verb, "1", "Retry with feedback", "--idempotency-key", key],
            "progress" => [verb, "1", "running", "Working", "--idempotency-key", key],
            "verify-manual" => [verb, "1", "passed", "Checked by hand", "--idempotency-key", key],
            "approve-policy-change" => [verb, Goal.Id.Value[..8], new string('a', 40), "--text-file", _textFile,
                "--idempotency-key", key],
            "cancel-dispatch" => [verb, "1", "--idempotency-key", key],
            _ => throw new ArgumentOutOfRangeException(nameof(verb))
        };

        public SqliteOperatorIntentStore Intents(OrchestratorWorkspace workspace) =>
            SqliteOperatorIntentStore.ForDirectories(workspace.OrchestratorDirectory, workspace.LogDirectory);

        public SubmitResult Run(string[] args, OrchestratorWorkspace workspace)
        {
            var parts = CliArgumentParser.NormalizeArgs(args);
            CliCommandHelp.ThrowIfInvalidFlags(parts);
            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var providers = new InMemoryModelProviderRegistry([]);
            var profiles = WorkerProfileCatalog.Default();
            Goal? currentGoal = Goal;
            var changed = true;
            var error = "";
            var output = CaptureConsole(() => error = AsyncLocalConsoleRouter.Error.CaptureLocal(() =>
            {
                if (parts[0] == "cancel-dispatch")
                {
                    var context = new CliExecutionContext(_kernel, workspace, providers, agents, profiles, Goal);
                    changed = CliCommandHandlers.ExecuteCancelDispatch(parts, context, Intents(workspace), _ => { });
                }
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
