using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(TestCollections.DotnetBuildSlots)]
public sealed class ConductorBatchLoopTestsDeterministicStewardRoute
{
    private const string PlanFailure = "[orchestrator Planner output contract rejection] acceptance criterion 1 with disposition=planned must include a non-empty plan";

    [Xunit.Fact]
    public async Task Known_planner_rejection_routes_without_a_model_round()
    {
        using var harness = new StewardHarness("B");
        SeedPlanner(harness, PlanFailure);
        var host = CreateHost(harness, new ConductorStewardDeterministicRoute());

        host.ServiceTick(harness.Kernel);
        Xunit.Assert.Equal(0, harness.Model.Calls);
        host.ServiceTick(harness.Kernel);

        var intent = Xunit.Assert.Single(await harness.Intents.ListForGoalAsync(harness.Goal.Id.Value));
        var payload = Payload(intent);
        Xunit.Assert.Equal(OperatorIntentVerbs.Adjudicate, intent.Verb);
        Xunit.Assert.Equal(harness.Task.Id.Value, intent.TaskId);
        Xunit.Assert.Equal("ContractClarification", payload.Cause);
        Xunit.Assert.Contains("same", payload.Text, StringComparison.OrdinalIgnoreCase);
        Xunit.Assert.Contains("plan=<non-empty one-sentence summary>", payload.Text);
        Xunit.Assert.Equal(0, harness.Model.Calls);
    }

    [Xunit.Fact]
    public async Task Wildcard_citation_routes_two_injected_files_without_a_model_round()
    {
        using var harness = new StewardHarness("B");
        SeedPlanner(harness, "[orchestrator Planner output contract rejection] " +
            "target citation 'src/Foo*.cs' does not exist and is not marked as a new file; source span [1..12). " +
            "Offending citation: 'src/Foo*.cs'");
        var host = CreateHost(harness, new ConductorStewardDeterministicRoute(new FixedFiles()));

        host.ServiceTick(harness.Kernel);
        host.ServiceTick(harness.Kernel);

        var payload = Payload(Xunit.Assert.Single(await harness.Intents.ListForGoalAsync(harness.Goal.Id.Value)));
        Xunit.Assert.Contains("src/Foo*.cs", payload.Text);
        Xunit.Assert.Contains("src/FooOne.cs", payload.Text);
        Xunit.Assert.Contains("src/FooTwo.cs", payload.Text);
        Xunit.Assert.Equal(0, harness.Model.Calls);
    }

    [Xunit.Fact]
    public async Task Collection_guard_routes_developer_with_required_lane_substring()
    {
        using var harness = new StewardHarness("C");
        harness.Seed("C");
        var host = CreateHost(harness, new ConductorStewardDeterministicRoute(lanes: new FixedLane()),
            new ConductorStewardTriggerDetector((_, _) => "GoalAcceptanceVerifier",
                _ => [harness.TrxPath]));

        host.ServiceTick(harness.Kernel);
        host.ServiceTick(harness.Kernel);

        var intent = Xunit.Assert.Single(await harness.Intents.ListForGoalAsync(harness.Goal.Id.Value));
        var payload = Payload(intent);
        Xunit.Assert.Equal(harness.Task.Id.Value, intent.TaskId);
        Xunit.Assert.Equal("ContractClarification", payload.Cause);
        Xunit.Assert.Contains("ExampleTests", payload.Text);
        Xunit.Assert.Contains("GoalAcceptanceVerifier", payload.Text);
        Xunit.Assert.Contains("GoalAcceptanceVerifierTests", payload.Text);
        Xunit.Assert.Equal(0, harness.Model.Calls);
    }

    [Xunit.Theory]
    [Xunit.InlineData("A")]
    [Xunit.InlineData("B")]
    public async Task Case_a_and_unrecognized_case_b_still_dispatch_model(string caseLetter)
    {
        using var harness = new StewardHarness(caseLetter);
        if (caseLetter == "A") harness.Seed("A");
        else SeedPlanner(harness, "unrecognized Planner contract reason");
        harness.Model.Reply("{\"kind\":\"no-action\",\"reason\":\"model handled\"}");
        var host = CreateHost(harness, new ConductorStewardDeterministicRoute());

        host.ServiceTick(harness.Kernel);
        await host.CurrentRound!;

        Xunit.Assert.Equal(1, harness.Model.Calls);
    }

    [Xunit.Fact]
    public async Task Superseded_deterministic_result_is_discarded_at_harvest()
    {
        using var harness = new StewardHarness("B");
        SeedPlanner(harness, PlanFailure);
        var host = CreateHost(harness, new ConductorStewardDeterministicRoute());
        host.ServiceTick(harness.Kernel);
        harness.Kernel.RecordTaskVerification(harness.Goal.Id, harness.Task.Id,
            new TaskVerificationRecord("test.exe", harness.Root, 1, "WORKER_RESULT: failed END_WORKER_RESULT",
                PlanFailure, DateTimeOffset.UtcNow.AddSeconds(2), WorkerResultPresent: true,
                DispatchStartedAt: DateTimeOffset.UtcNow.AddSeconds(1),
                CompletionVerdictRule: "planner-output-contract-rejected"));

        host.ServiceTick(harness.Kernel);

        Xunit.Assert.Empty(await harness.Intents.ListForGoalAsync(harness.Goal.Id.Value));
        Xunit.Assert.Contains("reason=trigger-superseded", File.ReadAllText(harness.ConductPath));
        Xunit.Assert.Equal(0, harness.Model.Calls);
    }

    [Xunit.Fact]
    public async Task Version_change_uses_same_harvest_version_as_model_result()
    {
        using var harness = new StewardHarness("B");
        SeedPlanner(harness, PlanFailure);
        long version = 7;
        var host = CreateHost(harness, new ConductorStewardDeterministicRoute(), version: _ => version);
        host.ServiceTick(harness.Kernel);
        version = 8;

        host.ServiceTick(harness.Kernel);

        var payload = Payload(Xunit.Assert.Single(await harness.Intents.ListForGoalAsync(harness.Goal.Id.Value)));
        Xunit.Assert.Equal(8, payload.ExpectedGoalStateVersion);
        Xunit.Assert.Contains("claimed_goal_version=7 harvest_goal_version=8", File.ReadAllText(harness.ConductPath));
        Xunit.Assert.Equal(0, harness.Model.Calls);
    }

    private static ConductorStewardHost CreateHost(StewardHarness harness,
        ConductorStewardDeterministicRoute route, ConductorStewardTriggerDetector? detector = null,
        Func<GoalId, long?>? version = null) => new(
        new ConductorStewardTriggerStore(Path.Combine(harness.Root, "steward-triggers.db")),
        detector ?? new ConductorStewardTriggerDetector(), harness.Model, harness.Intents,
        new AdjudicationEvidenceResolver(harness.Root), version ?? (_ => 7), _ => harness.Root,
        new GoalLifecycleEventWriter(Path.Combine(harness.Root, "lifecycle")),
        new ConductEventLogWriter(harness.ConductPath), deterministicRoute: route);

    private static void SeedPlanner(StewardHarness harness, string stderr)
    {
        var started = DateTimeOffset.UtcNow.AddMinutes(-1);
        harness.Kernel.RecordTaskDispatch(harness.Goal.Id, harness.Task.Id,
            new TaskDispatchRecord("test-worker", "test.exe", harness.Root, started,
                BaseCommit: StewardHarness.Sha));
        harness.Kernel.RecordTaskVerification(harness.Goal.Id, harness.Task.Id,
            new TaskVerificationRecord("test.exe", harness.Root, 1,
                "WORKER_RESULT: files: none END_WORKER_RESULT", stderr, started.AddSeconds(1),
                WorkerResultPresent: true, DispatchStartedAt: started,
                CompletionVerdictRule: "planner-output-contract-rejected"));
        harness.Kernel.ReportTaskProgress(harness.Goal.Id, harness.Task.Id, WorkTaskStatus.Failed, "failed");
    }

    private static AdjudicateOperatorIntentPayload Payload(OperatorIntentRecord intent) =>
        JsonSerializer.Deserialize<AdjudicateOperatorIntentPayload>(intent.PayloadJson,
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

    private sealed class FixedFiles : IConductorStewardTrackedFileLister
    {
        public IReadOnlyList<string> MatchingFiles(string worktree, string stem) => ["src/FooOne.cs", "src/FooTwo.cs"];
    }

    private sealed class FixedLane : IConductorStewardLaneSubstringResolver
    {
        public string RequiredSubstring(string worktree, string collection) => "GoalAcceptanceVerifierTests";
    }
}
