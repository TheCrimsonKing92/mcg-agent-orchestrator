using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class ScoutDispatchCompletionTests : WorkerDispatchTestSupport
{
    [Xunit.Theory]
    [Xunit.InlineData(false, false)]
    [Xunit.InlineData(false, true)]
    [Xunit.InlineData(true, false)]
    public void SuccessfulScoutPersistsPlanAndResearchForNextTask(bool externalPlan, bool reorderPlanSections)
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
        var planPath = Path.Combine(root, "scout-plan.md");
        if (externalPlan)
        {
            File.WriteAllText(planPath, PlannerContractPlanFixture());
        }
        var plan = PlannerContractPlanFixture();
        if (reorderPlanSections)
        {
            var mapping = plan.IndexOf("## Acceptance criteria mapping", StringComparison.Ordinal);
            plan = plan[mapping..] + Environment.NewLine + plan[..mapping];
        }
        File.WriteAllText(stdout, string.Join(Environment.NewLine,
            ResearcherContractFixture(),
            externalPlan ? $"Plan file: `{planPath}`" : plan,
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
        Xunit.Assert.DoesNotContain("## Acceptance criteria mapping",
            File.ReadAllText(Path.Combine(contextDirectory, "research-notes.md")), StringComparison.Ordinal);
        Xunit.Assert.DoesNotContain("CURRENT-SOURCE-RESEARCH-9182",
            File.ReadAllText(Path.Combine(contextDirectory, "planner-plan.md")), StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void SampledScoutPersistsResearchFromSelectedPlannerCandidate()
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
        const string workerResult = "\nWORKER_RESULT:\nfiles: none\ncommands: source inspection\ntests: not-run - Scout is read-only\ncommit: none\nblockers: none\nmodel_fit: Anthropic/claude-opus-5-5 - adequate - scouting - mapped source and criteria\nskills: criterion-ownership-planning\nconfidence: high\nEND_WORKER_RESULT";
        var primary = ResearcherContractFixture() + Environment.NewLine + PlannerContractPlanFixture() + workerResult;
        var selectedResearch = ResearcherContractFixture().Replace(
            "CURRENT-SOURCE-RESEARCH-9182", "SELECTED-SOURCE-RESEARCH-2741", StringComparison.Ordinal);
        var selectedPlan = PlannerContractPlanFixture().Replace(
            "1. Map the requested behavior to captured output, map completion to a deterministic gate, and map downstream use to the generated context artifact with exact-content assertions.",
            "1. disposition=planned; plan=Developer owns `BackgroundDispatchRunner` and verifies the selected research artifact through `ScoutDispatchCompletionTests` with TEST-VERIFIABLE evidence and a stop condition for missing output.",
            StringComparison.Ordinal);
        var sample = selectedResearch + Environment.NewLine + selectedPlan + workerResult;
        var selection = PlannerCandidateSelector.Select(
            [new(0, primary), new(1, sample)], root, goal.RefinedSpec!.AcceptanceCriteria);
        Xunit.Assert.Equal(1, selection.Receipt.SelectedCandidateIndex);

        File.WriteAllText(stdout, primary);
        File.WriteAllText(stderr, string.Empty);
        File.WriteAllText(exit, "0");
        var sampleArtifacts = Xunit.Assert.Single(PlannerSampleDispatcher.CreateArtifacts(stdout, 2));
        File.WriteAllText(sampleArtifacts.StandardOutputPath, sample);
        DispatchExitArtifacts.Write(sampleArtifacts.ExitCodePath,
            DispatchExitArtifacts.Native(0, "selected sample completed", DateTimeOffset.UtcNow));
        var now = DateTimeOffset.UtcNow;
        kernel.RecordTaskDispatch(goal.Id, planner.Id,
            new TaskDispatchRecord("scout-worker", "scout.exe", root, now, PlannerSampleCount: 2));
        kernel.RecordTaskProcessStarted(goal.Id, planner.Id,
            new TaskProcessRecord(4246, "scout.exe", root, stdout, stderr, exit, now, null, null));

        new BackgroundDispatchRunner(isStillRunning: _ => false)
            .RefreshLatestProcess(kernel, goal.Id, planner.Id);

        Xunit.Assert.Equal(WorkTaskStatus.Completed, planner.Status);
        Xunit.Assert.Equal(1, planner.LastVerification!.PlannerCandidateDivergence!.SelectedCandidateIndex);
        var contextDirectory = new WorkerArtifactWriter().Write(goal, developer, root);
        var researchNotes = File.ReadAllText(Path.Combine(contextDirectory, "research-notes.md"));
        Xunit.Assert.Contains("SELECTED-SOURCE-RESEARCH-2741", researchNotes, StringComparison.Ordinal);
        Xunit.Assert.DoesNotContain("CURRENT-SOURCE-RESEARCH-9182", researchNotes, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void ScoutWithValidPlanAndInvalidResearchFailsWithResearchContractDiagnostic()
    {
        var kernel = new AgentOrchestratorKernel();
        var planner = new TaskSpec(TaskId.New(), "Inspect source and plan.", AgentRole.Planner);
        var goal = kernel.CreateGoal("Update seed.txt.", [planner]);
        kernel.SetGoalRefinedSpec(goal.Id, new RefinedSpec(
            "Update seed.txt.", ["Map the requested behavior."],
            VerificationClass.TestVerifiable, [], []));
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);

        var root = CreateTempDirectory();
        File.WriteAllText(Path.Combine(root, "seed.txt"), "seed");
        var stdout = Path.Combine(root, "scout.out.log");
        var stderr = Path.Combine(root, "scout.err.log");
        var exit = Path.Combine(root, "scout.exit.txt");
        File.WriteAllText(stdout, PlannerContractPlanFixture());
        File.WriteAllText(stderr, string.Empty);
        File.WriteAllText(exit, "0");
        var now = DateTimeOffset.UtcNow;
        kernel.RecordTaskDispatch(goal.Id, planner.Id,
            new TaskDispatchRecord("scout-worker", "scout.exe", root, now));
        kernel.RecordTaskProcessStarted(goal.Id, planner.Id,
            new TaskProcessRecord(4247, "scout.exe", root, stdout, stderr, exit, now, null, null));

        new BackgroundDispatchRunner(isStillRunning: _ => false)
            .RefreshLatestProcess(kernel, goal.Id, planner.Id);

        Xunit.Assert.Equal(WorkTaskStatus.Failed, planner.Status);
        Xunit.Assert.Contains("researcher-output-contract-rejected", planner.LastVerification!.StandardError,
            StringComparison.Ordinal);
        Xunit.Assert.Contains("Retry Planner", planner.LastVerification.StandardError, StringComparison.Ordinal);
        Xunit.Assert.DoesNotContain(ResearcherOutputContract.DurableResearchBeginMarker,
            File.ReadAllText(stdout), StringComparison.Ordinal);
    }
}
