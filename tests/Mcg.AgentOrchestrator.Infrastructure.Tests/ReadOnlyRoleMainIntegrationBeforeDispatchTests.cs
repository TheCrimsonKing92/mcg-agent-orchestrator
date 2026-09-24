using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

public class ReadOnlyRoleMainIntegrationBeforeDispatchTests : AcceptanceCohortWorkflowTests
{
    [Theory]
    [InlineData(AgentRole.Planner)]
    [InlineData(AgentRole.Researcher)]
    public void ReadOnlyDispatch_BranchBehindMain_IntegratesBeforeStartAndDispatchSeesIntegratedHead(AgentRole role)
    {
        var repo = CreateAcceptanceCohortRepository();
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal(
            $"Integrate before {role} dispatch",
            [new TaskSpec(TaskId.New(), "Inspect current main", role),
             new TaskSpec(TaskId.New(), "Implement change", AgentRole.Developer)]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        try
        {
            var worktree = GoalWorktrees.Ensure(repo, goal.Id);
            File.WriteAllText(Path.Combine(repo, "main.txt"), "main change");
            RunGit(repo, "add", "main.txt");
            RunGit(repo, "commit", "-m", "Main change");
            var mainHead = RunGitOutput(repo, "rev-parse", "main").Trim();
            var initialHead = RunGitOutput(worktree, "rev-parse", "HEAD").Trim();
            string? dispatchHead = null;
            var driver = CreateDriver(goal, repo, _ =>
                dispatchHead = RunGitOutput(worktree, "rev-parse", "HEAD").Trim());

            var advance = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

            Assert.IsType<ConductorAdvanceOutcome.Executed>(advance.Outcome);
            Assert.NotNull(dispatchHead);
            Assert.NotEqual(initialHead, dispatchHead);
            Assert.Equal(dispatchHead, RunGitOutput(worktree, "rev-parse", "HEAD").Trim());
            Assert.Equal(
                $"Integrate main into {GoalWorktrees.BranchName(goal.Id)} before {role} dispatch",
                RunGitOutput(worktree, "log", "-1", "--pretty=%s").Trim());
            Assert.Equal(2, RunGitOutput(worktree, "show", "-s", "--pretty=%P", "HEAD")
                .Split(' ', StringSplitOptions.RemoveEmptyEntries).Length);
            RunGitOutput(worktree, "merge-base", "--is-ancestor", mainHead, dispatchHead);
            Assert.Equal(string.Empty, RunGitOutput(worktree, "status", "--short").Trim());
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Fact]
    public void PlannerDispatch_BranchCurrentWithMain_CreatesNoCommit()
    {
        var repo = CreateAcceptanceCohortRepository();
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal(
            "Plan on current main",
            [new TaskSpec(TaskId.New(), "Plan change", AgentRole.Planner),
             new TaskSpec(TaskId.New(), "Implement change", AgentRole.Developer)]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        try
        {
            var worktree = GoalWorktrees.Ensure(repo, goal.Id);
            var initialHead = RunGitOutput(worktree, "rev-parse", "HEAD").Trim();
            var initialCount = RunGitOutput(worktree, "rev-list", "--count", "HEAD").Trim();
            string? dispatchHead = null;
            var driver = CreateDriver(goal, repo, _ =>
                dispatchHead = RunGitOutput(worktree, "rev-parse", "HEAD").Trim());

            var advance = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

            Assert.IsType<ConductorAdvanceOutcome.Executed>(advance.Outcome);
            Assert.Equal(initialHead, dispatchHead);
            Assert.Equal(initialHead, RunGitOutput(worktree, "rev-parse", "HEAD").Trim());
            Assert.Equal(initialCount, RunGitOutput(worktree, "rev-list", "--count", "HEAD").Trim());
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    internal static ConductorDriver CreateDriver(
        Goal goal,
        string repo,
        Action<Goal> onDispatch,
        Action<string>? onEscalation = null) => new(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            getRunningPaidWorkerCount: () => 0,
            createWorkspace: _ => "unused",
            dispatchAndStart: candidate =>
            {
                onDispatch(candidate);
                return DispatchStartOutcome.Started();
            },
            startRecordedDispatches: null,
            buildServerShutdown: null,
            runAcceptanceVerification: _ => AcceptanceVerificationSummary.PassedWithNoUnmetCriteria,
            runAdvisorySemanticAcceptance: null,
            retryTask: null,
            recordTaskNote: null,
            recordCriterionRetryFeedback: null,
            clearCriterionRetryFeedback: null,
            rebaseOntoMain: _ => new GoalWorktreeRebaseResult(
                GoalWorktreeRebaseStatus.AlreadyFastForwardable,
                GoalWorktrees.BranchName(goal.Id),
                "current",
                [],
                null),
            land: (candidate, _) => new LandingResult(
                candidate.Id.Value,
                candidate.Id.Value[..8],
                new LandingDecision.Promote(),
                LandingExecutor.IntegrationBranchName,
                MainAdvanced: true,
                "landed"),
            afterSuccessfulLanding: null,
            record: _ => { },
            cleanup: _ => new GoalWorktreeRemoveResult("clean", null, [], null),
            writeEscalation: (_, _, reason) => onEscalation?.Invoke(reason),
            classifyChangeRisk: _ => null,
            integrateMainBeforeReadOnlyDispatch: (candidate, role) =>
                ConductorDriver.IntegrateMainBeforeReadOnlyDispatch(repo, candidate, role));
}
