using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class ScoutPlannerBlockerPathTests : WorkerDispatchTestSupport
{
    [Xunit.Fact]
    public void ScoutPremiseInvalidUsesPlannerClarificationPath()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = GoalLifecycleCommands.CreateAndActivateGoal(
            kernel, AgentCatalog.Default().Agents,
            "Update one label in src/Mcg.AgentOrchestrator.App/Cli/CliCommandHelp.cs.");
        var planner = goal.Tasks[0];
        Xunit.Assert.True(ScoutRoundPolicy.IsScoutPlanner(goal, planner));
        var root = CreateTempDirectory();
        var stdout = """
            WORKER_RESULT:
            files: none
            commands: inspected source
            tests: not-run - read-only Scout
            commit: none
            blockers: premise-invalid - required API is absent; src/Mcg.AgentOrchestrator.Core/Domain/OrchestrationEnums.cs confirms it
            model_fit: Anthropic/claude-opus-5-5 - adequate - scouting - inspected the premise
            skills: research-evidence
            confidence: high
            END_WORKER_RESULT
            """;
        RecordCompletedProcess(kernel, goal, planner, root, stdout);

        new BackgroundDispatchRunner(isStillRunning: _ => false)
            .RefreshLatestProcess(kernel, goal.Id, planner.Id);

        var request = Xunit.Assert.Single(kernel.GetPendingHumanInput(goal.Id));
        Xunit.Assert.Equal(WorkTaskStatus.WaitingForHuman, planner.Status);
        Xunit.Assert.StartsWith("Planner reported premise-invalid:", request.Question, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void ScoutEvidenceRequestUsesPlannerProspectiveEvidencePath()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = GoalLifecycleCommands.CreateAndActivateGoal(
            kernel, AgentCatalog.Default().Agents,
            "Update one label in src/Mcg.AgentOrchestrator.App/Cli/CliCommandHelp.cs.");
        var planner = goal.Tasks[0];
        kernel.SetGoalRefinedSpec(goal.Id, new RefinedSpec(
            "Update the label.", ["Map the requested behavior."],
            VerificationClass.TestVerifiable, [], []));
        var root = CreateTempDirectory();
        File.WriteAllText(Path.Combine(root, "seed.txt"), "seed");
        var evidenceRequest = "PLANNER_EVIDENCE_REQUEST: {\"criterion_index\":1,\"evidence_key\":\"live-candidate-observation\",\"availability\":\"post-implementation\",\"owner\":\"operator\",\"needed\":\"live observation\",\"reason\":\"candidate does not exist yet\"}";
        var stdout = string.Join(Environment.NewLine,
            ResearcherContractFixture(),
            PlannerContractPlanFixture(),
            evidenceRequest,
            "WORKER_RESULT:",
            "files: none",
            "commands: inspected source",
            "tests: not-run - read-only Scout",
            "commit: none",
            "blockers: none",
            "model_fit: Anthropic/claude-opus-5-5 - adequate - scouting - mapped evidence",
            "skills: criterion-ownership-planning",
            "confidence: high",
            "END_WORKER_RESULT");
        RecordCompletedProcess(kernel, goal, planner, root, stdout);

        new BackgroundDispatchRunner(isStillRunning: _ => false)
            .RefreshLatestProcess(kernel, goal.Id, planner.Id);

        var request = Xunit.Assert.Single(kernel.GetPendingHumanInput(goal.Id));
        Xunit.Assert.Equal(HumanWaitKind.ProspectiveAcceptanceEvidence, request.Kind);
        Xunit.Assert.Equal("operator", request.EvidenceOwner);
        Xunit.Assert.Equal(WorkTaskStatus.Completed, planner.Status);
    }

    private static void RecordCompletedProcess(
        AgentOrchestratorKernel kernel, Goal goal, TaskSpec planner, string root, string output)
    {
        var stdout = Path.Combine(root, "scout.out.log");
        var stderr = Path.Combine(root, "scout.err.log");
        var exit = Path.Combine(root, "scout.exit.txt");
        File.WriteAllText(stdout, output);
        File.WriteAllText(stderr, string.Empty);
        File.WriteAllText(exit, "0");
        var now = DateTimeOffset.UtcNow;
        kernel.RecordTaskDispatch(goal.Id, planner.Id,
            new TaskDispatchRecord("scout-worker", "scout.exe", root, now));
        kernel.RecordTaskProcessStarted(goal.Id, planner.Id,
            new TaskProcessRecord(4246, "scout.exe", root, stdout, stderr, exit, now, null, null));
    }
}
