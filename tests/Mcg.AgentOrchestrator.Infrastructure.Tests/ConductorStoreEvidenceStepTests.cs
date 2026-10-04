using System.Security.Cryptography;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Each test uses its own store, event log and injected clock.
public sealed class ConductorStoreEvidenceStepTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const string EventLine = "{\"eventType\":\"Criterion\",\"text\":\"feasibility-0d8fe45d51913385 matching receipt\"}";

    [Fact]
    public async Task ResolvesOnceAndNextDispatchMaterializesMatchingEvent()
    {
        using var fixture = new WorkerStoreReferenceFixture();
        var source = fixture.WriteSource($"goal-events/{WorkerStoreReferenceFixture.EventGoalId}.jsonl", EventLine);
        var (kernel, goal, task) = PlannerGoal(fixture);
        var reference = Reference();
        var request = Request(kernel, goal, task, "receipt", reference);
        Assert.Equal(WorkTaskStatus.WaitingForHuman, task.Status);
        var intents = SqliteOperatorIntentStore.ForDirectories(fixture.StoreRoot, Path.Combine(fixture.Root, "logs"));
        var step = Step(fixture, intents);

        Assert.Contains(goal.Id, step.ServiceTick(kernel));
        step.ServiceTick(kernel);
        // Durable queue dedup also survives a fresh conductor instance.
        Step(fixture, intents).ServiceTick(kernel);

        var intent = Assert.Single(await intents.ListForGoalAsync(goal.Id.Value));
        Assert.Equal(OperatorIntentVerbs.Answer, intent.Verb);
        Assert.Equal(OperatorActorKind.Agent, intent.ActorKind);
        Assert.Equal("conductor-store-resolver", intent.Channel);
        Assert.Equal(fixture.Clock.UtcNow, intent.CreatedAt);
        var payload = JsonSerializer.Deserialize<AnswerOperatorIntentPayload>(intent.PayloadJson, Json)!;
        Assert.Equal(OperatorAnswerTargetKind.HumanInput, payload.TargetKind);
        Assert.Equal(request.Id.Value, payload.TargetId);
        Assert.Equal(goal.Id.Value, payload.GoalId);
        Assert.Equal(OperatorActorKind.Agent, payload.ActorKind);
        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(source))).ToLowerInvariant();
        Assert.Equal(reference.ToStoreRefLine() + Environment.NewLine +
            $"Provenance: kind=goal-events; locator={reference.Locator}; sha256={hash}", payload.Text);

        kernel.SubmitHumanInput(request.Id, payload.Text);
        WorkerProfileDispatcher.PrepareTask(kernel, goal, task,
            new WorkerProfile("codex-cli", "codex exec --sandbox {sandboxMode} --cd {workingDirectory}"),
            Path.Combine(fixture.Root, "prompts"), fixture.WorkingDirectory, fixture.Clock.UtcNow,
            providerName: "OpenAI", modelName: AgentCatalog.OpenAiSolSubscriptionModelAlias,
            citedPriorEvidenceResolver: CitedPriorEvidenceResolver.ForWorkspace(Path.Combine(fixture.StoreRoot, "state.db"), fixture.StoreRoot));

        var content = File.ReadAllText(Path.Combine(fixture.WorkingDirectory, ".orchestrator-context", goal.Id.Value,
            "store-refs", reference.Name + ".md"));
        Assert.Equal(EventLine, WorkerStoreReferenceFixture.Excerpt(content));
        Assert.Contains("Source sha256: " + hash, content);
    }

    [Fact]
    public async Task LeavesLegacyNeverRecordedHumanAnsweredAndUnresolvedRequestsOpen()
    {
        using var fixture = new WorkerStoreReferenceFixture();
        fixture.WriteSource($"goal-events/{WorkerStoreReferenceFixture.EventGoalId}.jsonl", EventLine);
        var (kernel, goal, task) = PlannerGoal(fixture);
        var noReference = Request(kernel, goal, task, "retrievable without reference", null);
        var neverRecordedDirective = AgentOutputDirectives.ParseHumanInputRequest(
            "PLANNER_EVIDENCE_REQUEST: {\"criterion_index\":1,\"evidence_key\":\"absent-fact\",\"availability\":\"never-recorded\",\"needed\":\"missing fact\",\"reason\":\"never recorded\"}", AgentRole.Planner).Directive!;
        var neverRecorded = kernel.RequestHumanInputDeduplicated(goal.Id, task.Id, neverRecordedDirective.Question,
            neverRecordedDirective.Kind, storeReference: neverRecordedDirective.StoreReference).Request;
        var humanAnswered = Request(kernel, goal, task, "human answer pending", Reference());
        var unresolved = Request(kernel, goal, task, "selector matches nothing", Reference("contains=absent"));
        var intents = SqliteOperatorIntentStore.ForDirectories(fixture.StoreRoot, Path.Combine(fixture.Root, "logs"));
        var human = await QueueAnswer(fixture, intents, goal, humanAnswered, OperatorActorKind.Human, "cli");
        var step = Step(fixture, intents);

        Assert.Empty(step.ServiceTick(kernel));
        Assert.Empty(step.ServiceTick(kernel));
        var queued = Assert.Single(await intents.ListForGoalAsync(goal.Id.Value));
        Assert.Equal(human.Id, queued.Id);
        Assert.All(new[] { noReference, neverRecorded, humanAnswered, unresolved }, request => Assert.False(request.IsCompleted));
        var log = Assert.Single(File.ReadAllLines(LogPath(fixture)));
        using var entry = JsonDocument.Parse(log);
        Assert.Equal($"STORE_EVIDENCE_UNRESOLVED goal={goal.Id.Value} request={unresolved.Id.Value} reason=no-match",
            entry.RootElement.GetProperty("detail").GetString());
    }

    [Theory]
    [InlineData(OperatorActorKind.Human, OperatorIntentStatus.Claimed, "cli")]
    [InlineData(OperatorActorKind.Agent, OperatorIntentStatus.Pending, "other-agent")]
    [InlineData(OperatorActorKind.Agent, OperatorIntentStatus.Rejected, "conductor-store-resolver")]
    public async Task ExistingAnswersPreventResubmission(OperatorActorKind actor, OperatorIntentStatus status, string channel)
    {
        using var fixture = new WorkerStoreReferenceFixture();
        fixture.WriteSource($"goal-events/{WorkerStoreReferenceFixture.EventGoalId}.jsonl", EventLine);
        var (kernel, goal, task) = PlannerGoal(fixture);
        var request = Request(kernel, goal, task, "receipt", Reference());
        var intents = SqliteOperatorIntentStore.ForDirectories(fixture.StoreRoot, Path.Combine(fixture.Root, "logs"));
        var original = await QueueAnswer(fixture, intents, goal, request, actor, channel);
        if (status != OperatorIntentStatus.Pending)
        {
            await intents.ClaimNextAsync(goal.Id.Value, "fixture");
            if (status == OperatorIntentStatus.Rejected)
                await intents.CompleteAsync(original.Id, "fixture", status, "fixture rejection", fixture.Clock.UtcNow);
        }
        Assert.Empty(Step(fixture, intents).ServiceTick(kernel));
        Assert.Equal(original.Id, Assert.Single(await intents.ListForGoalAsync(goal.Id.Value)).Id);
        Assert.False(File.Exists(LogPath(fixture)));
    }

    [Theory]
    [InlineData("state-db:state.db", "refused-kind")]
    [InlineData("operator-evidence:operator-evidence/../state.db", "parent-traversal")]
    [InlineData("goal-events:11111111111111111111111111111111#contains=receipt", "source-missing")]
    public async Task RefusedOrMissingStoresLogOnceAndRemainOpen(string value, string reason)
    {
        using var fixture = new WorkerStoreReferenceFixture();
        var (kernel, goal, task) = PlannerGoal(fixture);
        Assert.True(PlannerEvidenceStoreReference.TryParse("receipt", value, out var reference));
        var request = Request(kernel, goal, task, "receipt", reference);
        var intents = SqliteOperatorIntentStore.ForDirectories(fixture.StoreRoot, Path.Combine(fixture.Root, "logs"));
        var step = Step(fixture, intents);
        step.ServiceTick(kernel);
        step.ServiceTick(kernel);
        Step(fixture, intents).ServiceTick(kernel);
        Assert.Empty(await intents.ListForGoalAsync(goal.Id.Value));
        Assert.False(request.IsCompleted);
        Assert.Equal(WorkTaskStatus.WaitingForHuman, task.Status);
        using var entry = JsonDocument.Parse(Assert.Single(File.ReadAllLines(LogPath(fixture))));
        Assert.Equal($"STORE_EVIDENCE_UNRESOLVED goal={goal.Id.Value} request={request.Id.Value} reason={reason}",
            entry.RootElement.GetProperty("detail").GetString());
    }

    [Theory]
    [InlineData(HumanWaitKind.SpecClarification)]
    [InlineData(HumanWaitKind.ProspectiveAcceptanceEvidence)]
    public async Task OtherRequestKindsAreUntouchedEvenWithTypedReference(HumanWaitKind kind)
    {
        using var fixture = new WorkerStoreReferenceFixture();
        fixture.WriteSource($"goal-events/{WorkerStoreReferenceFixture.EventGoalId}.jsonl", EventLine);
        var (kernel, goal, task) = PlannerGoal(fixture);
        var request = kernel.RequestHumanInputDeduplicated(goal.Id, task.Id, "authority question", kind,
            storeReference: Reference()).Request;
        var intents = SqliteOperatorIntentStore.ForDirectories(fixture.StoreRoot, Path.Combine(fixture.Root, "logs"));
        Assert.Empty(Step(fixture, intents).ServiceTick(kernel));
        Assert.Empty(await intents.ListForGoalAsync(goal.Id.Value));
        Assert.False(request.IsCompleted);
        Assert.False(File.Exists(LogPath(fixture)));
    }

    private static string LogPath(WorkerStoreReferenceFixture fixture) => Path.Combine(fixture.Root, "logs", "conduct-events.log");
    private static ConductorStoreEvidenceStep Step(WorkerStoreReferenceFixture fixture, IOperatorIntentStore intents) =>
        new(intents, new ConductEventLogWriter(LogPath(fixture), utcNow: () => fixture.Clock.UtcNow), () => fixture.StoreRoot, fixture.Clock);

    private static (AgentOrchestratorKernel Kernel, Goal Goal, TaskSpec Task) PlannerGoal(WorkerStoreReferenceFixture fixture)
    {
        var kernel = new AgentOrchestratorKernel(fixture.Clock);
        var task = new TaskSpec(TaskId.New(), "Plan implementation.", AgentRole.Planner);
        var goal = kernel.CreateGoal("Retrieve prerequisite evidence.", [task]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        return (kernel, goal, task);
    }

    private static PlannerEvidenceStoreReference Reference(string selector = "contains=feasibility-0d8fe45d51913385")
    {
        Assert.True(PlannerEvidenceStoreReference.TryParse("Feasibility Receipt",
            $"goal-events:{WorkerStoreReferenceFixture.EventGoalId}#{selector}", out var reference));
        return reference!;
    }

    private static HumanInputRequest Request(AgentOrchestratorKernel kernel, Goal goal, TaskSpec task,
        string question, PlannerEvidenceStoreReference? reference) => kernel.RequestHumanInputDeduplicated(
            goal.Id, task.Id, question, HumanWaitKind.PlannerPrerequisiteEvidence, storeReference: reference).Request;

    private static Task<OperatorIntentRecord> QueueAnswer(WorkerStoreReferenceFixture fixture, IOperatorIntentStore intents,
        Goal goal, HumanInputRequest request, OperatorActorKind actor, string channel) => intents.EnqueueAsync(
            new OperatorIntentRecord(Guid.NewGuid().ToString("N"), "fixture-answer", OperatorIntentVerbs.Answer, goal.Id.Value, null,
                JsonSerializer.Serialize(new AnswerOperatorIntentPayload(OperatorAnswerTargetKind.HumanInput,
                    request.Id.Value, goal.Id.Value, "existing answer", actor), Json), [], "fixture", channel, "fixture",
                fixture.Clock.UtcNow, ActorKind: actor));
}
