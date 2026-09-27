using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(TestCollections.DotnetBuildSlots)]
public sealed class ConductorBatchLoopTestsStewardSafety
{
    [Xunit.Fact]
    public async Task Owner_retry_during_model_round_supersedes_steward_route_without_creating_a_decision_request()
    {
        using var harness = new StewardHarness("B");
        harness.Seed("B");
        harness.Model.Reply(Route(harness.Task.Id.Value));
        harness.Model.Hold();
        harness.Host.ServiceTick(harness.Kernel);
        await harness.Model.Started.Task;

        harness.Kernel.RetryTaskWithAuthoritativeFeedback(harness.Goal.Id, harness.Task.Id,
            "Owner supplied repair", RetryCause.ContractClarification);
        harness.Model.Release();
        await harness.Host.CurrentRound!;
        harness.Host.ServiceTick(harness.Kernel);

        Xunit.Assert.Empty(await harness.Intents.ListForGoalAsync(harness.Goal.Id.Value));
        Xunit.Assert.Empty(await harness.Decisions.ListDecisionRequestsAsync(harness.Goal.Id.Value));
        Xunit.Assert.Equal(WorkTaskStatus.Assigned, harness.Task.Status);
        Xunit.Assert.Equal("Owner supplied repair", harness.Task.AcceptedRetryFeedback?.Message);
        Xunit.Assert.Contains("trigger-superseded", File.ReadAllText(harness.ConductPath));
    }

    [Xunit.Fact]
    public async Task Scoped_conductor_leaves_another_goals_pending_trigger_for_its_own_conductor()
    {
        using var harness = new StewardHarness("B");
        var otherTask = new TaskSpec(TaskId.New(), "Other Planner", AgentRole.Planner);
        var otherGoal = harness.Kernel.CreateGoal("Other goal", [otherTask]);
        harness.Kernel.ActivateGoal(otherGoal.Id, AgentCatalog.Default().Agents);
        harness.Kernel.SetGoalRefinedSpec(otherGoal.Id,
            new RefinedSpec("Repair", ["Correct citations"], VerificationClass.TestVerifiable, [], []));
        harness.Kernel.RecordTaskDispatch(otherGoal.Id, otherTask.Id,
            new TaskDispatchRecord("test-worker", "test.exe", harness.Root, DateTimeOffset.UtcNow,
                BaseCommit: StewardHarness.Sha));
        harness.Kernel.RecordTaskVerification(otherGoal.Id, otherTask.Id,
            new TaskVerificationRecord("test.exe", harness.Root, 1,
                "WORKER_RESULT: failed END_WORKER_RESULT", "rule=planner-output-contract-rejected",
                DateTimeOffset.UtcNow, WorkerResultPresent: true,
                CompletionVerdictRule: "planner-output-contract-rejected"));
        harness.Kernel.ReportTaskProgress(otherGoal.Id, otherTask.Id, WorkTaskStatus.Failed, "failed");
        var trigger = new ConductorStewardTriggerDetector().Detect(otherGoal).Single();
        var store = new ConductorStewardTriggerStore(Path.Combine(harness.Root, "steward-triggers.db"));
        store.Observe(trigger);
        harness.Model.Reply("{\"kind\":\"no-action\",\"reason\":\"done\"}");

        harness.Host.ServiceTick(harness.Kernel, harness.Goal.Id.Value);
        Xunit.Assert.Null(harness.Host.CurrentRound);
        Xunit.Assert.Equal(0, harness.Model.Calls);
        Xunit.Assert.Equal("pending", store.Observe(trigger).Status);

        harness.Host.ServiceTick(harness.Kernel, otherGoal.Id.Value);
        await harness.Model.Started.Task;
        await harness.Host.CurrentRound!;
        harness.Host.ServiceTick(harness.Kernel, otherGoal.Id.Value);
        Xunit.Assert.Equal(1, harness.Model.Calls);
        Xunit.Assert.Equal("serviced", store.Observe(trigger).Status);
    }

    [Xunit.Fact]
    public async Task Incomplete_retry_template_is_no_action_without_an_owner_question()
    {
        using var harness = new StewardHarness("B");
        harness.Seed("B");
        harness.Model.Reply(JsonSerializer.Serialize(new
        {
            kind = "route", targetTaskId = harness.Task.Id.Value,
            cause = "ContractClarification", text = "Diagnosis without instruction",
            reversibility = "reversible", evidenceReferences = new[] { "worker-output=receipt-1" }
        }));
        harness.Host.ServiceTick(harness.Kernel);
        await harness.Model.Started.Task;
        await harness.Host.CurrentRound!;
        harness.Host.ServiceTick(harness.Kernel);

        Xunit.Assert.Empty(await harness.Intents.ListForGoalAsync(harness.Goal.Id.Value));
        Xunit.Assert.Null(harness.Goal.CurrentHold);
        Xunit.Assert.Contains("incomplete-template", File.ReadAllText(harness.ConductPath));
    }

    [Xunit.Fact]
    public async Task Canceled_model_round_records_timeout_and_consumes_the_trigger()
    {
        using var harness = new StewardHarness("B");
        harness.Seed("B");
        harness.Model.Cancel();
        harness.Host.ServiceTick(harness.Kernel);
        await harness.Model.Started.Task;
        await Xunit.Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await harness.Host.CurrentRound!);
        harness.Host.ServiceTick(harness.Kernel);
        harness.Host.ServiceTick(harness.Kernel);

        Xunit.Assert.Equal(1, harness.Model.Calls);
        Xunit.Assert.Empty(await harness.Intents.ListForGoalAsync(harness.Goal.Id.Value));
        Xunit.Assert.Contains("reason=timeout", File.ReadAllText(harness.ConductPath));
    }

    [Xunit.Fact]
    public async Task Completed_round_survives_conductor_stop_without_a_second_model_dispatch()
    {
        using var harness = new StewardHarness("B");
        harness.Seed("B");
        harness.Model.Reply(Route(harness.Task.Id.Value));
        harness.Host.ServiceTick(harness.Kernel);
        await harness.Model.Started.Task;
        await harness.Host.CurrentRound!;
        harness.Host.Stop();

        var store = new ConductorStewardTriggerStore(Path.Combine(harness.Root, "steward-triggers.db"));
        var trigger = new ConductorStewardTriggerDetector().Detect(harness.Goal).Single();
        Xunit.Assert.Equal("ready", store.Observe(trigger).Status);
        var resumed = new ConductorStewardHost(store, new ConductorStewardTriggerDetector(),
            harness.Model, harness.Intents, new AdjudicationEvidenceResolver(harness.Root),
            _ => 7, _ => harness.Root,
            new GoalLifecycleEventWriter(Path.Combine(harness.Root, "lifecycle")),
            new ConductEventLogWriter(harness.ConductPath));
        resumed.ServiceTick(harness.Kernel);

        Xunit.Assert.Equal(1, harness.Model.Calls);
        Xunit.Assert.Single(await harness.Intents.ListForGoalAsync(harness.Goal.Id.Value));
        Xunit.Assert.Equal("serviced", store.Observe(trigger).Status);
    }

    [Xunit.Fact]
    public void No_change_rejection_without_confirmed_red_does_not_dispatch()
    {
        using var harness = new StewardHarness("B");
        var task = new TaskSpec(TaskId.New(), "Developer", AgentRole.Developer);
        var goal = harness.Kernel.CreateGoal("No confirmed RED", [task]);
        harness.Kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        harness.Kernel.RecordTaskDispatch(goal.Id, task.Id,
            new TaskDispatchRecord("test-worker", "test.exe", harness.Root, DateTimeOffset.UtcNow,
                BaseCommit: StewardHarness.Sha));
        harness.Kernel.RecordTaskVerification(goal.Id, task.Id,
            new TaskVerificationRecord("test.exe", harness.Root, 1,
                "WORKER_RESULT: failed END_WORKER_RESULT", "DISPATCH_REJECTED reason=no-change-evidence",
                DateTimeOffset.UtcNow, WorkerResultPresent: true,
                OrchestratorFailureReason: "no-change-evidence"));
        harness.Kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Failed, "rejected");

        harness.Host.ServiceTick(harness.Kernel, goal.Id.Value);
        Xunit.Assert.Null(harness.Host.CurrentRound);
        Xunit.Assert.Equal(0, harness.Model.Calls);
    }

    [Xunit.Fact]
    public void Unmatched_acceptance_failure_does_not_dispatch()
    {
        using var harness = new StewardHarness("C");
        harness.Seed("C");
        File.WriteAllText(harness.TrxPath,
            "<TestRun><Results><UnitTestResult testName='UnrelatedCheck' outcome='Failed'><Output><ErrorInfo><Message>Other failure</Message></ErrorInfo></Output></UnitTestResult></Results></TestRun>");

        harness.Host.ServiceTick(harness.Kernel);
        Xunit.Assert.Null(harness.Host.CurrentRound);
        Xunit.Assert.Equal(0, harness.Model.Calls);
    }

    [Xunit.Fact]
    public void Reviewer_not_verifiable_failure_does_not_dispatch()
    {
        using var harness = new StewardHarness("B");
        var task = new TaskSpec(TaskId.New(), "Reviewer", AgentRole.Reviewer);
        var goal = harness.Kernel.CreateGoal("Reviewer finding", [task]);
        harness.Kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        harness.Kernel.RecordTaskDispatch(goal.Id, task.Id,
            new TaskDispatchRecord("test-worker", "test.exe", harness.Root, DateTimeOffset.UtcNow,
                BaseCommit: StewardHarness.Sha));
        harness.Kernel.RecordTaskVerification(goal.Id, task.Id,
            new TaskVerificationRecord("test.exe", harness.Root, 1,
                "WORKER_RESULT: not-verifiable END_WORKER_RESULT", "not-verifiable",
                DateTimeOffset.UtcNow, WorkerResultPresent: true,
                CompletionVerdictRule: "reviewer-not-verifiable"));
        harness.Kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Failed, "not verifiable");

        harness.Host.ServiceTick(harness.Kernel, goal.Id.Value);
        Xunit.Assert.Null(harness.Host.CurrentRound);
        Xunit.Assert.Equal(0, harness.Model.Calls);
    }

    private static string Route(string taskId) => JsonSerializer.Serialize(new
    {
        kind = "route", targetTaskId = taskId, cause = "ContractClarification",
        text = "Steward diagnosis", instruction = "Apply the repair",
        reversibility = "reversible", evidenceReferences = new[] { "worker-output=receipt-1" }
    });
}
