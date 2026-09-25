using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class ScoutDispatchCompletionTests : WorkerDispatchTestSupport
{
    [Xunit.Fact]
    public void SuccessfulScoutPersistsPlanAndResearchForNextTask()
    {
        var kernel = new AgentOrchestratorKernel();
        var planner = new TaskSpec(TaskId.New(), "Inspect source and plan.", AgentRole.Planner);
        var developer = new TaskSpec(TaskId.New(), "Implement the plan.", AgentRole.Developer);
        var goal = kernel.CreateGoal("Update seed.txt.", [planner, developer]);
        kernel.SetGoalRefinedSpec(goal.Id, new RefinedSpec(
            "Update seed.txt.", ["Map the requested behavior."],
            VerificationClass.TestVerifiable, [], []));
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);

        var root = CreateTempDirectory();
        File.WriteAllText(Path.Combine(root, "seed.txt"), "seed");
        var stdout = Path.Combine(root, "scout.out.log");
        var stderr = Path.Combine(root, "scout.err.log");
        var exit = Path.Combine(root, "scout.exit.txt");
        File.WriteAllText(stdout, string.Join(Environment.NewLine,
            PlannerContractPlanFixture(),
            ResearcherContractFixture(),
            "WORKER_RESULT:",
            "files: none",
            "commands: source inspection",
            "tests: not-run - Scout is read-only",
            "commit: none",
            "blockers: none",
            "model_fit: Anthropic/claude-opus-5-5 - adequate - scouting - mapped source and criteria",
            "skills: criterion-ownership-planning",
            "confidence: high",
            "END_WORKER_RESULT"));
        File.WriteAllText(stderr, string.Empty);
        File.WriteAllText(exit, "0");
        var now = DateTimeOffset.UtcNow;
        kernel.RecordTaskDispatch(goal.Id, planner.Id,
            new TaskDispatchRecord("scout-worker", "scout.exe", root, now));
        kernel.RecordTaskProcessStarted(goal.Id, planner.Id,
            new TaskProcessRecord(4245, "scout.exe", root, stdout, stderr, exit, now, null, null));

        new BackgroundDispatchRunner(isStillRunning: _ => false)
            .RefreshLatestProcess(kernel, goal.Id, planner.Id);

        Xunit.Assert.Equal(WorkTaskStatus.Completed, planner.Status);
        var captured = File.ReadAllText(stdout);
        Xunit.Assert.Contains(PlannerOutputContract.DurablePlanBeginMarker, captured, StringComparison.Ordinal);
        Xunit.Assert.Contains(ResearcherOutputContract.DurableResearchBeginMarker, captured, StringComparison.Ordinal);
        var contextDirectory = new WorkerArtifactWriter().Write(goal, developer, root);
        Xunit.Assert.Contains("CURRENT-SOURCE-RESEARCH-9182",
            File.ReadAllText(Path.Combine(contextDirectory, "research-notes.md")), StringComparison.Ordinal);
    }
}
