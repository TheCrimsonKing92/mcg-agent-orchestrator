using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class ScoutDispatchCompletionTestsContractDiagnostic : WorkerDispatchTestSupport
{
    [Xunit.Fact]
    public void MissingResearchSectionAppearsInTaskFailedMessageBeforeCommand()
    {
        var research = ResearcherContractFixture().ReplaceLineEndings("\n");
        research = research[..research.IndexOf("## Likely seams and risks", StringComparison.Ordinal)];

        var (planner, captured, failureMessage) = RefreshScout(research);

        Xunit.Assert.Equal(WorkTaskStatus.Failed, planner.Status);
        Xunit.Assert.NotNull(failureMessage);
        Xunit.Assert.Contains("rule=researcher-output-contract-rejected", failureMessage, StringComparison.Ordinal);
        const string diagnostic = "missing required section 'likely seams and risks'";
        Xunit.Assert.Contains(diagnostic, failureMessage, StringComparison.Ordinal);
        Xunit.Assert.Contains("Command: scout.exe", failureMessage, StringComparison.Ordinal);
        Xunit.Assert.True(failureMessage.IndexOf(diagnostic, StringComparison.Ordinal) <
            failureMessage.IndexOf("Command: scout.exe", StringComparison.Ordinal));
        Xunit.Assert.Equal(
            "Dispatch failed: rule=researcher-output-contract-rejected: Researcher output contract failed: " +
            "missing required section 'likely seams and risks'. Retry Planner for contract repair. Command: scout.exe",
            failureMessage);
        Xunit.Assert.DoesNotContain(ResearcherOutputContract.DurableResearchBeginMarker, captured, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void QualifiedResearchHeadingCompletesWithDurableResearchReceipt()
    {
        var research = ResearcherContractFixture().Replace(
            "## Likely seams and risks", "## Likely seams and risks (inference)", StringComparison.Ordinal);

        var (planner, captured, failureMessage) = RefreshScout(research);

        Xunit.Assert.Equal(WorkTaskStatus.Completed, planner.Status);
        Xunit.Assert.Null(failureMessage);
        Xunit.Assert.Contains(ResearcherOutputContract.DurableResearchBeginMarker, captured, StringComparison.Ordinal);
        Xunit.Assert.Contains(ResearcherOutputContract.DurableResearchEndMarker, captured, StringComparison.Ordinal);
        Xunit.Assert.Contains("## Likely seams and risks (inference)", captured, StringComparison.Ordinal);
    }

    private static (TaskSpec Planner, string Captured, string? FailureMessage) RefreshScout(string research)
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
        File.WriteAllText(stdout, string.Join(Environment.NewLine,
            research, PlannerContractPlanFixture(),
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
            new TaskProcessRecord(4247, "scout.exe", root, stdout, stderr, exit, now, null, null));

        new BackgroundDispatchRunner(isStillRunning: _ => false)
            .RefreshLatestProcess(kernel, goal.Id, planner.Id);

        var failure = goal.Timeline.SingleOrDefault(item => item.Kind == ProgressKind.TaskFailed);
        return (planner, File.ReadAllText(stdout), failure?.Message);
    }
}
