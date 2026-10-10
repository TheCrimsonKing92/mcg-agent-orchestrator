using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: each test owns its state and intents databases; console capture is async-local.
public sealed class CliOperatorIntentRouteTestsCriterionBatch : CliTaskQueryTestSupport
{
    private const string Candidate = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public async Task BatchQueuesOrderedDistinctIntentsAndExistingConsumerAppliesThem()
    {
        using var fixture = new Fixture();
        var args = fixture.Arguments("1,2,3").Concat(new[] { "--idempotency-key", "k", "--operator-actor", "reviewer@example" }).ToArray();
        var output = CaptureConsole(() => Assert.Equal(0, CliOperatorIntentRoute.Run(args, fixture.Workspace)));
        var intents = await fixture.Store.ListForGoalAsync(fixture.Goal.Id.Value);
        Assert.Equal(3, intents.Count);
        Assert.Equal(3, intents.Select(intent => intent.Id).Distinct().Count());
        var byKey = intents.ToDictionary(intent => intent.IdempotencyKey);
        for (var number = 1; number <= 3; number++)
        {
            var intent = byKey[$"k-{number}"];
            Assert.Equal(OperatorIntentVerbs.CriterionEvidenceMap, intent.Verb);
            Assert.Null(intent.TaskId);
            Assert.Equal("reviewer@example", intent.Actor);
            Assert.Equal("cli", intent.Channel);
            Assert.Equal("local-process", intent.AuthenticationAssurance);
            Assert.Equal(new CriterionEvidenceMappingOperatorIntentPayload(number - 1, 1, CriterionEvidenceOwner.Operator,
                "observation", "finding", Candidate), Payload(intent));
        }
        var lines = output.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(7, lines.Length);
        for (var index = 0; index < 3; index++)
        {
            Assert.StartsWith($"Operator intent queued: id={byKey[$"k-{index + 1}"].Id} ", lines[index], StringComparison.Ordinal);
            Assert.Equal($"Target: criterion {index + 1} (v1) criterion-v1-{index} Requirement {index + 1}", lines[index + 3]);
        }
        Assert.Equal(ConductorLoopLease.InactiveWarning, lines[6]);

        // Repeating the batch resolves the same idempotent rows.
        Assert.Equal(output, CaptureConsole(() => Assert.Equal(0, CliOperatorIntentRoute.Run(args, fixture.Workspace))));
        Assert.Equal(3, (await fixture.Store.ListForGoalAsync(fixture.Goal.Id.Value)).Count);
        var coordinator = new OperatorIntentCoordinator(fixture.Store);
        // Compatible map intents share a tick; completion follows persistence of the entire batch.
        var repository = new SqliteOrchestratorStateRepository(fixture.Workspace.SqliteStatePath);
        var expectedIds = new[] { "criterion-v1-0", "criterion-v1-1", "criterion-v1-2" };
        Assert.True(coordinator.ExecutePending(fixture.Kernel, fixture.Goal).MutatedGoalState);
        Assert.Equal(expectedIds,
            fixture.Goal.CriterionEvidenceObligations.Select(obligation => obligation.Id).OrderBy(id => id, StringComparer.Ordinal));
        foreach (var intent in intents)
            Assert.Equal(OperatorIntentStatus.Claimed, (await fixture.Store.GetAsync(intent.Id))!.Status);
        var beforeSave = await repository.LoadGoalAsync(fixture.Goal.Id);
        Assert.NotNull(beforeSave);
        Assert.Empty(beforeSave.CriterionEvidenceObligations ?? []);
        await repository.SaveAsync(fixture.Kernel);
        var persisted = await repository.LoadGoalAsync(fixture.Goal.Id);
        Assert.NotNull(persisted);
        Assert.Equal(expectedIds,
            persisted.CriterionEvidenceObligations.Select(obligation => obligation.Id).OrderBy(id => id, StringComparer.Ordinal));
        coordinator.CompletePersisted([fixture.Goal.Id]);
        foreach (var intent in intents)
            Assert.Equal(OperatorIntentStatus.Applied, (await fixture.Store.GetAsync(intent.Id))!.Status);
    }

    [Theory]
    [InlineData("1,9", "Criterion 9")]
    [InlineData("1,1", "Criterion 1")]
    [InlineData("1,,2", "''")]
    [InlineData("0,1", "'0'")]
    [InlineData("1,x", "'x'")]
    [InlineData("1, 2", "' 2'")]
    [InlineData("1,02", "'02'")]
    [InlineData("1,2147483648", "'2147483648'")]
    public async Task InvalidElementFailsBeforeAnyAppend(string numbers, string badElement)
    {
        using var fixture = new Fixture();
        var error = AsyncLocalConsoleRouter.Error.CaptureLocal(() =>
        {
            var output = CaptureConsole(() => Assert.Equal(1, CliOperatorIntentRoute.Run(fixture.Arguments(numbers), fixture.Workspace)));
            Assert.Empty(output);
        });
        Assert.StartsWith("Error: ", error, StringComparison.Ordinal);
        Assert.Contains(badElement, error, StringComparison.Ordinal);
        Assert.Empty(await fixture.Store.ListForGoalAsync(fixture.Goal.Id.Value));
        Assert.Empty(Directory.EnumerateFiles(fixture.Workspace.LogDirectory, "*" + SqliteOperatorIntentStore.WakeFileSuffix));
    }

    [Fact]
    public async Task SingleNumberRetainsOriginalPayloadAndOutput()
    {
        using var fixture = new Fixture();
        var args = fixture.Arguments("2").Concat(new[] { "--idempotency-key", "single" }).ToArray();
        var legacy = CaptureConsole(() => CliCriterionEvidenceIntents.Submit(args, fixture.Workspace, fixture.Goal,
            new CliPersistentStateRunner.OperatorIntentAttribution("operator", "cli", "local-process")));
        var original = Assert.Single(await fixture.Store.ListForGoalAsync(fixture.Goal.Id.Value));
        var routed = CaptureConsole(() => Assert.Equal(0, CliOperatorIntentRoute.Run(args, fixture.Workspace)));
        var row = Assert.Single(await fixture.Store.ListForGoalAsync(fixture.Goal.Id.Value));
        Assert.Equal(original.Id, row.Id);
        Assert.Equal(original.IdempotencyKey, row.IdempotencyKey);
        Assert.Equal(original.Verb, row.Verb);
        Assert.Equal(original.PayloadJson, row.PayloadJson);
        Assert.Equal(original.CreatedAt, row.CreatedAt);
        Assert.Equal(new CriterionEvidenceMappingOperatorIntentPayload(1, 1, CriterionEvidenceOwner.Operator,
            "observation", "finding", Candidate), Payload(row));
        Assert.Equal($"Operator intent queued: id={row.Id} verb=criterion-evidence-map goal={fixture.Goal.Id.Value} " +
            $"status=Pending; poll with operator-intent-status {row.Id} (or add --wait).{Environment.NewLine}" +
            $"Target: criterion 2 (v1) criterion-v1-1 Requirement 2{Environment.NewLine}" +
            $"{ConductorLoopLease.InactiveWarning}{Environment.NewLine}", routed);
        Assert.Equal(legacy, routed);
    }

    [Fact]
    public async Task BadPayloadOrRepeatedCriterionFlagFailsBeforeAppending()
    {
        using var fixture = new Fixture();
        var cases = new[]
        {
            fixture.Arguments("1,2").Concat(new[] { "--criterion", "3" }).ToArray(),
            fixture.Arguments("1,2").Where(arg => arg != "operator").ToArray(),
            fixture.Arguments("1,2").Concat(new[] { "--version", "9" }).ToArray()
        };
        foreach (var args in cases)
        {
            AsyncLocalConsoleRouter.Error.CaptureLocal(() => Assert.Equal(1, CliOperatorIntentRoute.Run(args, fixture.Workspace)));
            Assert.Empty(await fixture.Store.ListForGoalAsync(fixture.Goal.Id.Value));
        }
    }

    [Fact]
    public async Task MissingSingleCriterionValueKeepsExistingArgumentDiagnostic()
    {
        using var fixture = new Fixture();
        var args = new[] { "criterion-evidence-map", "--goal", fixture.Goal.Id.Value, "operator", "observation",
            "finding", Candidate, "--criterion" };
        var error = AsyncLocalConsoleRouter.Error.CaptureLocal(() =>
            Assert.Equal(1, CliOperatorIntentRoute.Run(args, fixture.Workspace)));
        Assert.Equal($"Error: Criterion numbers are 1-based, as the brief numbers them.{Environment.NewLine}", error);
        Assert.Empty(await fixture.Store.ListForGoalAsync(fixture.Goal.Id.Value));
    }

    private static CriterionEvidenceMappingOperatorIntentPayload Payload(OperatorIntentRecord intent) =>
        JsonSerializer.Deserialize<CriterionEvidenceMappingOperatorIntentPayload>(intent.PayloadJson, OperatorIntentJson.Options)!;

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = CreateTempDirectory();
        public AgentOrchestratorKernel Kernel { get; } = new();
        public Goal Goal { get; }
        public OrchestratorWorkspace Workspace { get; }
        public SqliteOperatorIntentStore Store { get; }
        public Fixture()
        {
            Workspace = OrchestratorWorkspace.ForDirectory(_root);
            StateDbMigrations.EnsureUpToDate(Workspace.SqliteStatePath);
            Goal = Kernel.CreateGoal("Batch criterion intents");
            Kernel.RecordGoalRefinement(Goal.Id, new RefinedSpec("Five criteria",
                Enumerable.Range(1, 5).Select(n => $"Requirement {n}").ToArray(), VerificationClass.TestVerifiable, [], []));
            new SqliteOrchestratorStateRepository(Workspace.SqliteStatePath).SaveAsync(Kernel).GetAwaiter().GetResult();
            Store = SqliteOperatorIntentStore.ForDirectories(Workspace.OrchestratorDirectory, Workspace.LogDirectory);
        }
        public string[] Arguments(string numbers) => ["criterion-evidence-map", "--goal", Goal.Id.Value[..8],
            "--criterion", numbers, "operator", "observation", "finding", Candidate];
        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
