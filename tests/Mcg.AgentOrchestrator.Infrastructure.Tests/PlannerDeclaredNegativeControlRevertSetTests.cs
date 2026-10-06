using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each test owns its kernel and unique temporary directory; no processes or shared state.
public sealed class PlannerDeclaredNegativeControlRevertSetTests
{
    [Fact]
    public void UndeclaredBrief_UsesDurablePlannerReceiptAndCanonicalPathOrder()
    {
        using var fixture = new Fixture();
        fixture.CompletePlanner(0, Plan("negative-control-revert: tests/Mcg.AgentOrchestrator.Infrastructure.Tests/ProbeSupport.cs, config/probe-policy.json"));

        Assert.Equal(new[] { "config/probe-policy.json", "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/ProbeSupport.cs" },
            NegativeControlRevertSetResolver.Resolve(fixture.Goal));
    }

    [Fact]
    public void DeclaredBrief_WinsOverPlannerDeclaration()
    {
        using var fixture = new Fixture("negative-control-revert: config/a.json");
        fixture.CompletePlanner(0, Plan("negative-control-revert: config/b.json"));

        Assert.Equal(new[] { "config/a.json" }, NegativeControlRevertSetResolver.Resolve(fixture.Goal));
    }

    [Theory]
    [InlineData("negative-control-revert:")]
    [InlineData("negative-control-revert: src/A.cs")]
    public void RejectedBrief_DoesNotFallBackToValidPlannerDeclaration(string brief)
    {
        using var fixture = new Fixture(brief);
        fixture.CompletePlanner(0, Plan("negative-control-revert: config/b.json"));

        Assert.Null(NegativeControlRevertSetResolver.Resolve(fixture.Goal));
    }

    [Theory]
    [InlineData("negative-control-revert: config/a.json\nnegative-control-revert: config/b.json")]
    [InlineData("negative-control-revert: src/A.cs")]
    [InlineData("negative-control-revert: tests/*.cs")]
    public void RejectedPlannerDeclaration_ReturnsNull(string declaration)
    {
        using var fixture = new Fixture();
        fixture.CompletePlanner(0, Plan(declaration));

        Assert.Null(NegativeControlRevertSetResolver.Resolve(fixture.Goal));
    }

    [Fact]
    public void PlannerPlanWithoutDeclaration_ReturnsNull()
    {
        using var fixture = new Fixture();
        fixture.CompletePlanner(0, Plan());

        Assert.Null(NegativeControlRevertSetResolver.Resolve(fixture.Goal));
    }

    [Fact]
    public void GoalWithoutPlannerTask_ReturnsNull()
    {
        using var fixture = new Fixture(plannerCount: 0);

        Assert.Null(NegativeControlRevertSetResolver.Resolve(fixture.Goal));
    }

    [Fact]
    public void CapturedOutputWithoutDurablePlan_ReturnsNull()
    {
        using var fixture = new Fixture();
        fixture.CompleteCapturedOutput(0, "Planner complete without a durable plan.");

        Assert.Null(NegativeControlRevertSetResolver.Resolve(fixture.Goal));
    }

    [Fact]
    public void NewestResolvablePlanWithoutDeclaration_DoesNotUseOlderDeclaration()
    {
        using var fixture = new Fixture(plannerCount: 2);
        fixture.CompletePlanner(0, Plan("negative-control-revert: config/a.json"));
        fixture.CompletePlanner(1, Plan());

        Assert.Null(NegativeControlRevertSetResolver.Resolve(fixture.Goal));
    }

    [Fact]
    public void NewestPlannerWithoutResolvablePlan_UsesOlderResolvablePlan()
    {
        using var fixture = new Fixture(plannerCount: 2);
        fixture.CompletePlanner(0, Plan("negative-control-revert: config/a.json"));
        fixture.CompleteCapturedOutput(1, "No durable receipt.");

        Assert.Equal(new[] { "config/a.json" }, NegativeControlRevertSetResolver.Resolve(fixture.Goal));
    }

    [Fact]
    public void NewestResolvableVerification_WinsWithinPlannerTask()
    {
        using var fixture = new Fixture();
        fixture.CompletePlanner(0, Plan("negative-control-revert: config/a.json"));
        fixture.CompletePlanner(0, Plan("negative-control-revert: config/b.json"));

        Assert.Equal(new[] { "config/b.json" }, NegativeControlRevertSetResolver.Resolve(fixture.Goal));
    }

    [Fact]
    public void TrailingUnresolvableVerification_UsesEarlierDurablePlan()
    {
        using var fixture = new Fixture();
        fixture.CompletePlanner(0, Plan("negative-control-revert: config/a.json"));
        fixture.CompleteCapturedOutput(0, "No durable receipt.");

        Assert.Equal(new[] { "config/a.json" }, NegativeControlRevertSetResolver.Resolve(fixture.Goal));
    }

    [Fact]
    public void IncompleteNewestPlanner_DoesNotDisplaceCompletedPlanner()
    {
        using var fixture = new Fixture(plannerCount: 2);
        fixture.CompletePlanner(0, Plan("negative-control-revert: config/a.json"));
        Assert.NotEqual(WorkTaskStatus.Completed, fixture.Goal.Tasks[1].Status);

        Assert.Equal(new[] { "config/a.json" }, NegativeControlRevertSetResolver.Resolve(fixture.Goal));
    }

    private static string Plan(string? declaration = null) =>
        WorkerDispatchTestSupport.PlannerContractPlanFixture() +
        (declaration is null ? string.Empty : "\n" + declaration);

    private sealed class Fixture : IDisposable
    {
        private readonly AgentOrchestratorKernel _kernel = new();
        private readonly string _root = Path.Combine(Path.GetTempPath(),
            "planner-revert-set-" + Guid.NewGuid().ToString("N"));
        private int _verificationNumber;

        public Fixture(string brief = "Resolve a Planner-proposed revert set.", int plannerCount = 1)
        {
            Directory.CreateDirectory(_root);
            File.WriteAllText(Path.Combine(_root, "seed.txt"), "Planner fixture evidence.");
            var tasks = Enumerable.Range(0, plannerCount)
                .Select(index => new TaskSpec(TaskId.New(), $"Plan increment {index}.", AgentRole.Planner))
                .Append(new TaskSpec(TaskId.New(), "Implement the increment.", AgentRole.Developer)).ToArray();
            Goal = _kernel.CreateGoal(brief, tasks);
            _kernel.ActivateGoal(Goal.Id, AgentCatalog.Default().Agents);
        }

        public Goal Goal { get; }

        public void CompletePlanner(int taskIndex, string plan)
        {
            var verification = CompleteCapturedOutput(taskIndex,
                PlannerOutputContract.BuildIngestedReceipt("plan.md", plan));
            Assert.True(WorkerArtifactWriter.TryResolveDurablePlannerPlan(verification, out var resolved, out var diagnostic),
                diagnostic);
            Assert.Equal(plan.ReplaceLineEndings("\n"), resolved.ReplaceLineEndings("\n"));
        }

        public TaskVerificationRecord CompleteCapturedOutput(int taskIndex, string output)
        {
            var path = Path.Combine(_root, $"planner-{++_verificationNumber}.out.log");
            File.WriteAllText(path, output);
            var task = Goal.Tasks[taskIndex];
            if (task.LastDispatch is null)
                _kernel.RecordTaskDispatch(Goal.Id, task.Id,
                    new TaskDispatchRecord("planner-fixture", "plan", _root, DateTimeOffset.UtcNow));
            var verification = new TaskVerificationRecord("plan", _root, 0, "Planner complete.",
                string.Empty, DateTimeOffset.UtcNow, StandardOutputPath: path);
            _kernel.RecordTaskVerification(Goal.Id, task.Id, verification);
            Assert.Equal(WorkTaskStatus.Completed, task.Status);
            return verification;
        }

        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
