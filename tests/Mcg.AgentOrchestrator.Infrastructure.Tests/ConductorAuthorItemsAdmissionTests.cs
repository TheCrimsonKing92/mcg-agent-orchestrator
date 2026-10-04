using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

public sealed class ConductorAuthorItemsAdmissionTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public void PlannerPrerequisiteEvidenceIsOfferedToAuthor()
    {
        var (kernel, goal, request) = Request(HumanWaitKind.PlannerPrerequisiteEvidence);
        Assert.Equal(WorkTaskStatus.WaitingForHuman, Assert.Single(goal.Tasks).Status);
        var item = Assert.Single(ConductorAuthorItems.Detect(goal, [], kernel.HumanInputRequests, []));
        Assert.Equal(OperatorAnswerTargetKind.HumanInput, item.TargetKind);
        Assert.Equal("planner-prerequisite-evidence", item.ForkKind);
        Assert.Equal(request.Id.Value, item.TargetId);
    }

    [Theory]
    [InlineData(HumanWaitKind.ProspectiveAcceptanceEvidence, false)]
    [InlineData(HumanWaitKind.PlannerPrerequisiteEvidence, true)]
    [InlineData(HumanWaitKind.SpecClarification, true)]
    public void ProspectiveOrStoreReferenceRequestsAreExcluded(HumanWaitKind kind, bool storeReference)
    {
        var (kernel, goal, _) = Request(kind, storeReference);
        Assert.Empty(ConductorAuthorItems.Detect(goal, [], kernel.HumanInputRequests, []));
    }

    [Theory]
    [InlineData(OperatorIntentStatus.Pending)]
    [InlineData(OperatorIntentStatus.Claimed)]
    public void QueuedHumanAnswerExcludesPlannerRequest(OperatorIntentStatus status)
    {
        var (kernel, goal, request) = Request(HumanWaitKind.PlannerPrerequisiteEvidence);
        var payload = new AnswerOperatorIntentPayload(OperatorAnswerTargetKind.HumanInput,
            request.Id.Value, goal.Id.Value, "Owner ruling", OperatorActorKind.Human);
        var intent = new OperatorIntentRecord(Guid.NewGuid().ToString("N"), "human-answer",
            OperatorIntentVerbs.Answer, goal.Id.Value, null,
            JsonSerializer.Serialize(payload, Json),
            [], "operator", "cli", "fixture", Now, Status: status, ActorKind: OperatorActorKind.Human);
        Assert.Empty(ConductorAuthorItems.Detect(goal, [], kernel.HumanInputRequests, [intent]));
    }

    [Fact]
    public void SpecClarificationRetainsWorkerForkKind()
    {
        var (kernel, goal, _) = Request(HumanWaitKind.SpecClarification);
        Assert.Equal("worker-spec-clarification",
            Assert.Single(ConductorAuthorItems.Detect(goal, [], kernel.HumanInputRequests, [])).ForkKind);
    }

    private static (AgentOrchestratorKernel Kernel, Goal Goal, HumanInputRequest Request) Request(
        HumanWaitKind kind, bool withStoreReference = false)
    {
        var kernel = new AgentOrchestratorKernel(new FixedClock());
        var task = new TaskSpec(TaskId.New(), "Plan scoped change", AgentRole.Planner);
        var goal = kernel.CreateGoal("Move the pinned behavior", [task]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        PlannerEvidenceStoreReference? reference = null;
        if (withStoreReference)
            Assert.True(PlannerEvidenceStoreReference.TryParse("receipt",
                "goal-events:11111111111111111111111111111111#contains=receipt", out reference));
        var request = kernel.RequestHumanInputDeduplicated(goal.Id, task.Id, "Which frozen path changes?", kind,
            storeReference: reference).Request;
        return (kernel, goal, request);
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => Now;
    }
}
