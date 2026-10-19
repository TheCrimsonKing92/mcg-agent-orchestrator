using Mcg.AgentOrchestrator.Core;

// Parallel-safe: in-memory providers and kernels, no external calls or shared state.
public sealed class PlannerEvidenceApiBatchTests
{
    private const string Batch = """
        PLANNER_EVIDENCE_REQUEST: {"criterion_index":1,"evidence_key":"history","availability":"never-recorded","needed":"Retrieve evidence","reason":"Missing receipt"}
        PLANNER_EVIDENCE_REQUEST: {"criterion_index":1,"evidence_key":"tests","availability":"post-implementation","owner":"acceptance","tests":["CandidateTests"],"needed":"Execute candidate tests","reason":"Candidate required"}
        PLANNER_EVIDENCE_REQUEST: {"criterion_index":1,"evidence_key":"live","availability":"post-implementation","owner":"operator","needed":"Observe live behavior","reason":"Operator check"}
        """;

    [Fact]
    public async Task ApiBatch_RecordsFutureSiblingsBeforeBlockingOnHistory()
    {
        var (kernel, goal, planner, agents) = Create();
        var provider = new ControlledProvider();
        var runner = new AgentTaskRunner(kernel, agents, new InMemoryModelProviderRegistry([provider]));
        await runner.RunAsync(goal.Id, planner.Id, TestContext.Current.CancellationToken);
        Assert.Equal(1, provider.Calls);
        Assert.Equal(WorkTaskStatus.WaitingForHuman, planner.Status);
        Assert.Single(kernel.GetPendingBlockingHumanInput(goal.Id));
        Assert.Equal(2, kernel.HumanInputRequests.Count);
        Assert.Single(goal.GateTestObligations);
        Assert.All(kernel.HumanInputRequests, item => Assert.Equal(goal.AuthoritativeBrief.Version, item.PlannerIdentity!.BriefVersion));
        Assert.Equal(goal.AuthoritativeBrief.Version, Assert.Single(goal.GateTestObligations).PlannerIdentity!.BriefVersion);
    }

    [Fact]
    public async Task ApiCompletion_CannotBorrowBriefRevisedWhileProviderWasRunning()
    {
        var (kernel, goal, planner, agents) = Create();
        var provider = new ControlledProvider(() => kernel.ReviseGoalBrief(goal.Id, "New authoritative planning brief"));
        var runner = new AgentTaskRunner(kernel, agents, new InMemoryModelProviderRegistry([provider]));
        await runner.RunAsync(goal.Id, planner.Id, TestContext.Current.CancellationToken);
        Assert.Equal(1, provider.Calls);
        Assert.Equal(2, goal.AuthoritativeBrief.Version);
        Assert.Empty(kernel.HumanInputRequests);
        Assert.Empty(goal.GateTestObligations);
        Assert.Equal(WorkTaskStatus.Failed, planner.Status);
        Assert.Contains(goal.Timeline, item => item.Message.Contains("obsolete dispatch brief", StringComparison.Ordinal));
    }

    private static (AgentOrchestratorKernel, Goal, TaskSpec, IReadOnlyList<AgentDefinition>) Create()
    {
        var kernel = new AgentOrchestratorKernel();
        var planner = new TaskSpec(TaskId.New(), "Plan the implementation", AgentRole.Planner);
        var goal = kernel.CreateGoal("Independent evidence requirements", [planner, new(TaskId.New(), "Implement", AgentRole.Developer)]);
        kernel.SetGoalRefinedSpec(goal.Id, new RefinedSpec("Preserve the evidence requirements", ["Acceptance executes candidate checks"], VerificationClass.TestVerifiable, [], []));
        var agents = DefaultAgents();
        kernel.ActivateGoal(goal.Id, agents);
        return (kernel, goal, planner, agents);
    }

    private sealed class ControlledProvider(Action? duringCall = null) : IModelProvider
    {
        public string ProviderName => "OpenAI";
        internal int Calls { get; private set; }
        public Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            duringCall?.Invoke();
            return Task.FromResult(new ModelResponse(Batch, new ModelUsage(1, 1), "stop"));
        }
    }
}
