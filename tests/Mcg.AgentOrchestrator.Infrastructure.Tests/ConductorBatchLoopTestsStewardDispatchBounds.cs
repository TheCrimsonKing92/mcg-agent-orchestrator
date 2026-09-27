using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(TestCollections.DotnetBuildSlots)]
public sealed class ConductorBatchLoopTestsStewardDispatchBounds
{
    [Xunit.Fact]
    public void Unrelated_rejection_does_not_dispatch()
    {
        using var harness = new StewardHarness("B");
        harness.Kernel.RecordTaskDispatch(harness.Goal.Id, harness.Task.Id,
            new TaskDispatchRecord("test-worker", "test.exe", harness.Root, DateTimeOffset.UtcNow,
                BaseCommit: StewardHarness.Sha));
        harness.Kernel.RecordTaskVerification(harness.Goal.Id, harness.Task.Id,
            new TaskVerificationRecord("test.exe", harness.Root, 1, "WORKER_RESULT: failed",
                "rule=other-contract-rejected", DateTimeOffset.UtcNow, WorkerResultPresent: true,
                CompletionVerdictRule: "other-contract-rejected"));
        harness.Kernel.ReportTaskProgress(harness.Goal.Id, harness.Task.Id, WorkTaskStatus.Failed, "unrelated rejection");
        harness.Host.ServiceTick(harness.Kernel);
        Xunit.Assert.Null(harness.Host.CurrentRound);
        Xunit.Assert.Equal(0, harness.Model.Calls);
    }

    [Xunit.Fact]
    public async Task Same_trigger_identity_is_dispatched_once_and_survives_store_reopen()
    {
        using var harness = new StewardHarness("B");
        harness.Seed("B");
        harness.Model.Reply("{\"kind\":\"no-action\",\"reason\":\"insufficient evidence\"}");
        harness.Host.ServiceTick(harness.Kernel);
        await harness.Model.Started.Task;
        await harness.Host.CurrentRound!;
        harness.Host.ServiceTick(harness.Kernel);
        harness.Host.ServiceTick(harness.Kernel);
        Xunit.Assert.Equal(1, harness.Model.Calls);
        var reopened = new ConductorStewardTriggerStore(Path.Combine(harness.Root, "steward-triggers.db"));
        var trigger = new ConductorStewardTriggerDetector().Detect(harness.Goal).Single();
        Xunit.Assert.Equal("serviced", reopened.Observe(trigger).Status);
        Xunit.Assert.Equal(1, File.ReadAllLines(harness.ConductPath)
            .Count(line => line.Contains("\"eventKind\":\"steward\"", StringComparison.Ordinal)));
    }

    [Xunit.Theory]
    [Xunit.InlineData("not JSON", "unparseable-output")]
    [Xunit.InlineData("{\"kind\":\"no-action\",\"reason\":\"insufficient evidence\"}", "insufficient evidence")]
    public async Task Unusable_round_is_serviced_once_without_an_owner_question(string output, string reason)
    {
        using var harness = new StewardHarness("B");
        harness.Seed("B");
        harness.Model.Reply(output);
        harness.Host.ServiceTick(harness.Kernel);
        await harness.Model.Started.Task;
        await harness.Host.CurrentRound!;
        harness.Host.ServiceTick(harness.Kernel);
        harness.Host.ServiceTick(harness.Kernel);
        Xunit.Assert.Equal(1, harness.Model.Calls);
        Xunit.Assert.Null(harness.Goal.CurrentHold);
        Xunit.Assert.Empty(await harness.Intents.ListForGoalAsync(harness.Goal.Id.Value));
        Xunit.Assert.Contains(reason, File.ReadAllText(harness.ConductPath));
    }

    [Xunit.Fact]
    public async Task Model_failure_is_no_action_and_consumes_the_trigger()
    {
        using var harness = new StewardHarness("B");
        harness.Seed("B");
        harness.Model.Fail(new InvalidOperationException("model unavailable"));
        harness.Host.ServiceTick(harness.Kernel);
        await harness.Model.Started.Task;
        await Xunit.Assert.ThrowsAsync<InvalidOperationException>(async () => await harness.Host.CurrentRound!);
        harness.Host.ServiceTick(harness.Kernel);
        harness.Host.ServiceTick(harness.Kernel);
        Xunit.Assert.Equal(1, harness.Model.Calls);
        Xunit.Assert.Null(harness.Goal.CurrentHold);
        Xunit.Assert.Empty(await harness.Intents.ListForGoalAsync(harness.Goal.Id.Value));
        Xunit.Assert.Contains("model-failure", File.ReadAllText(harness.ConductPath));
    }

    [Xunit.Fact]
    public async Task Only_one_model_round_is_in_flight_across_two_pending_triggers()
    {
        using var harness = new StewardHarness("B");
        harness.Seed("B");
        var otherTask = new TaskSpec(TaskId.New(), "Second Planner", AgentRole.Planner);
        var otherGoal = harness.Kernel.CreateGoal("Second goal", [otherTask]);
        harness.Kernel.ActivateGoal(otherGoal.Id, AgentCatalog.Default().Agents);
        harness.Kernel.SetGoalRefinedSpec(otherGoal.Id,
            new RefinedSpec("Repair", ["Fix the citation"], VerificationClass.TestVerifiable, [], []));
        harness.Kernel.RecordTaskDispatch(otherGoal.Id, otherTask.Id,
            new TaskDispatchRecord("test-worker", "test.exe", harness.Root, DateTimeOffset.UtcNow,
                BaseCommit: StewardHarness.Sha));
        harness.Kernel.RecordTaskVerification(otherGoal.Id, otherTask.Id,
            new TaskVerificationRecord("test.exe", harness.Root, 1, "WORKER_RESULT: failed",
                "rule=planner-output-contract-rejected", DateTimeOffset.UtcNow,
                WorkerResultPresent: true, CompletionVerdictRule: "planner-output-contract-rejected"));
        harness.Kernel.ReportTaskProgress(otherGoal.Id, otherTask.Id, WorkTaskStatus.Failed, "failed");
        harness.Model.Hold();
        harness.Host.ServiceTick(harness.Kernel);
        await harness.Model.Started.Task;
        harness.Host.ServiceTick(harness.Kernel);
        Xunit.Assert.Equal(1, harness.Model.Calls);
        harness.Model.Release();
        await harness.Host.CurrentRound!;
        harness.Host.ServiceTick(harness.Kernel);
        await harness.Model.SecondStarted.Task;
        Xunit.Assert.Equal(2, harness.Model.Calls);
    }

    [Xunit.Fact]
    public async Task Repeat_after_applied_route_becomes_question_without_second_dispatch()
    {
        using var harness = new StewardHarness("B");
        harness.Seed("B");
        harness.Model.Reply(JsonSerializer.Serialize(new
        {
            kind = "route", targetTaskId = harness.Task.Id.Value, cause = "ContractClarification",
            text = "Correct the citation", instruction = "Check all cited paths",
            reversibility = "reversible", evidenceReferences = new[] { "worker-output=receipt-1" }
        }));
        harness.Host.ServiceTick(harness.Kernel);
        await harness.Model.Started.Task;
        await harness.Host.CurrentRound!;
        harness.Host.ServiceTick(harness.Kernel);
        harness.Coordinator.ExecutePending(harness.Kernel, harness.Goal);
        harness.Coordinator.CompletePersisted([harness.Goal.Id]);
        harness.Host.ServiceTick(harness.Kernel);
        var first = new ConductorStewardTriggerDetector().Detect(harness.Goal);
        Xunit.Assert.Empty(first);
        // A later failed round has a new occurrence but the same trigger identity and candidate.
        harness.Kernel.RecordTaskDispatch(harness.Goal.Id, harness.Task.Id,
            new TaskDispatchRecord("test-worker", "test.exe", harness.Root,
                DateTimeOffset.UtcNow.AddMinutes(1), BaseCommit: StewardHarness.Sha));
        harness.Kernel.RecordTaskVerification(harness.Goal.Id, harness.Task.Id,
            new TaskVerificationRecord("test.exe", harness.Root, 1,
                "WORKER_RESULT: files: none END_WORKER_RESULT",
                "rule=planner-output-contract-rejected cited another glob", DateTimeOffset.UtcNow.AddMinutes(1),
                WorkerResultPresent: true, CompletionVerdictRule: "planner-output-contract-rejected"));
        harness.Kernel.ReportTaskProgress(harness.Goal.Id, harness.Task.Id, WorkTaskStatus.Failed, "failed again");
        harness.Host.ServiceTick(harness.Kernel);
        Xunit.Assert.Equal("steward-owner-question", harness.Goal.CurrentHold?.State);
        Xunit.Assert.Single(await harness.Intents.ListForGoalAsync(harness.Goal.Id.Value));
        // The persisted route bound is keyed by task and candidate, including this later failure.
        var store = new ConductorStewardTriggerStore(Path.Combine(harness.Root, "steward-triggers.db"));
        Xunit.Assert.True(store.HasAppliedRoute(new ConductorStewardTrigger(harness.Goal.Id.Value,
            harness.Task.Id.Value, StewardHarness.Sha, ConductorStewardTriggerKind.PlannerOutputContractRejected,
            DateTimeOffset.UtcNow, "repeat", "", [], [])));
        Xunit.Assert.Equal(1, harness.Model.Calls);
    }
}
