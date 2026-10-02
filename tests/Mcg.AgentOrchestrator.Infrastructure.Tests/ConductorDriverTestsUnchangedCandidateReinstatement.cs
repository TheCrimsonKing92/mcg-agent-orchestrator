using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;

// Parallel safe: all state and clock observations belong to this fixture; no I/O.
public sealed class ConductorDriverTestsUnchangedCandidateReinstatement
{
    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public void InheritedResetAfterFailedDeveloperRetryReinstatesPassingTesterAndReviewer(bool withSpec)
    {
        var scenario = new Scenario(withSpec: withSpec);
        var testerVerdict = scenario.Tester.LastVerification;
        var reviewerVerdict = scenario.Reviewer.LastVerification;
        scenario.FailDeveloperRetryAndClose();
        var driver = scenario.Driver();

        var result = driver.AdvanceOnce(scenario.Goal, ConductorAutonomyPolicy.Conservative);

        Xunit.Assert.IsType<ConductorAdvanceOutcome.Executed>(result.Outcome);
        AssertReinstated(scenario, scenario.Tester, testerVerdict!);
        AssertReinstated(scenario, scenario.Reviewer, reviewerVerdict!);
        Xunit.Assert.Equal(0, scenario.Dispatches);
        Xunit.Assert.Empty(scenario.Escalations);
        Xunit.Assert.DoesNotContain(scenario.Goal.Timeline, evt =>
            evt.Message.StartsWith("UNCHANGED_CANDIDATE", StringComparison.Ordinal));

        driver.AdvanceOnce(scenario.Goal, ConductorAutonomyPolicy.Conservative);
        Xunit.Assert.Equal(2, ReinstatementEvents(scenario.Goal).Count());
        Xunit.Assert.Equal(0, scenario.Dispatches);
        Xunit.Assert.DoesNotContain(scenario.Escalations, message =>
            message.Contains("UNCHANGED_CANDIDATE", StringComparison.Ordinal));
    }

    [Xunit.Fact]
    public void NeedsWorkReviewerVerdictStaysHeldAfterInheritedReset()
    {
        var scenario = new Scenario(reviewerNeedsWork: true);
        var testerVerdict = scenario.Tester.LastVerification!;
        scenario.FailDeveloperRetryAndClose();

        var result = scenario.Driver().AdvanceOnce(scenario.Goal, ConductorAutonomyPolicy.Conservative);

        AssertReinstated(scenario, scenario.Tester, testerVerdict);
        var held = Xunit.Assert.IsType<ConductorAdvanceOutcome.Held>(result.Outcome);
        var reason = Xunit.Assert.IsType<UnchangedCandidateHoldReason>(held.TypedReason);
        Xunit.Assert.Equal(AgentRole.Reviewer, reason.Role);
        Xunit.Assert.Equal("needs-work", reason.PriorVerdict);
        Xunit.Assert.Equal(WorkTaskStatus.Assigned, scenario.Reviewer.Status);
        Xunit.Assert.Null(scenario.Reviewer.LastVerification);
        Xunit.Assert.DoesNotContain(ReinstatementEvents(scenario.Goal), evt => evt.TaskId == scenario.Reviewer.Id);
        Xunit.Assert.Equal(0, scenario.Dispatches);
    }

    [Xunit.Fact]
    public void ChangedEffectiveCriteriaAfterVerdictsBlocksReinstatement()
    {
        var scenario = new Scenario(withSpec: true);
        scenario.FailDeveloperRetryAndClose();
        scenario.Kernel.WaiveAcceptanceCriterion(scenario.Goal.Id, "1", "Criterion removed by operator.");

        var result = scenario.Driver().AdvanceOnce(scenario.Goal, ConductorAutonomyPolicy.Conservative);

        Xunit.Assert.IsType<UnchangedCandidateHoldReason>(
            Xunit.Assert.IsType<ConductorAdvanceOutcome.Held>(result.Outcome).TypedReason);
        foreach (var task in new[] { scenario.Tester, scenario.Reviewer })
        {
            Xunit.Assert.Equal(WorkTaskStatus.Assigned, task.Status);
            Xunit.Assert.Null(task.LastVerification);
        }
        Xunit.Assert.Empty(ReinstatementEvents(scenario.Goal));
        Xunit.Assert.Equal(0, scenario.Dispatches);
    }

    [Xunit.Fact]
    public void DirectUnchangedContextRepeatTesterRetryStaysHeld()
    {
        var scenario = new Scenario();
        scenario.Kernel.RetryTask(scenario.Goal.Id, scenario.Tester.Id,
            "Repeat the same Tester input.", RetryCause.UnchangedContextRepeat);

        var result = scenario.Driver().AdvanceOnce(scenario.Goal, ConductorAutonomyPolicy.Conservative);

        var held = Xunit.Assert.IsType<ConductorAdvanceOutcome.Held>(result.Outcome);
        var reason = Xunit.Assert.IsType<UnchangedCandidateHoldReason>(held.TypedReason);
        Xunit.Assert.Equal(AgentRole.Tester, reason.Role);
        Xunit.Assert.Equal("passed", reason.PriorVerdict);
        Xunit.Assert.Equal(WorkTaskStatus.Assigned, scenario.Tester.Status);
        Xunit.Assert.Null(scenario.Tester.LastVerification);
        Xunit.Assert.Empty(ReinstatementEvents(scenario.Goal));
        Xunit.Assert.Equal(0, scenario.Dispatches);
    }

    [Xunit.Fact]
    public void RepeatedUpstreamRetryKeepsOriginalVerdictProvenance()
    {
        var scenario = new Scenario();
        var testerVerdict = scenario.Tester.LastVerification!;
        var reviewerVerdict = scenario.Reviewer.LastVerification!;
        var driver = scenario.Driver();
        for (var round = 0; round < 2; round++)
        {
            scenario.FailDeveloperRetryAndClose();
            Xunit.Assert.IsType<ConductorAdvanceOutcome.Executed>(
                driver.AdvanceOnce(scenario.Goal, ConductorAutonomyPolicy.Conservative).Outcome);
            Xunit.Assert.Same(testerVerdict, scenario.Tester.LastVerification);
            Xunit.Assert.Same(reviewerVerdict, scenario.Reviewer.LastVerification);
        }
        Xunit.Assert.Equal(4, ReinstatementEvents(scenario.Goal).Count());
        Xunit.Assert.Single(scenario.Tester.VerificationHistory);
        Xunit.Assert.Single(scenario.Reviewer.VerificationHistory);
        Xunit.Assert.Equal(0, scenario.Dispatches);
    }

    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public void ChangedCandidateOrNewInputKeepsDispatchBehavior(bool newInput)
    {
        var scenario = new Scenario();
        scenario.FailDeveloperRetryAndClose();
        var driver = scenario.Driver();
        if (newInput)
        {
            var request = scenario.Kernel.RequestHumanInput(scenario.Goal.Id, null,
                "Which new test finding should be checked?");
            scenario.Kernel.SubmitHumanInput(request.Id, "Read the new test finding.");
            Xunit.Assert.True(scenario.Tester.LatestRetryInherited);
        }
        else
            driver.OverrideCandidateIdentityResolverForTests(_ => new CandidateIdentity("changed", "base", "manifest"));

        var result = driver.AdvanceOnce(scenario.Goal, ConductorAutonomyPolicy.Conservative);

        Xunit.Assert.IsType<ConductorAdvanceOutcome.Executed>(result.Outcome);
        Xunit.Assert.Equal(1, scenario.Dispatches);
        Xunit.Assert.Null(scenario.Tester.LastVerification);
        Xunit.Assert.Empty(ReinstatementEvents(scenario.Goal));
    }

    private static IEnumerable<ProgressEvent> ReinstatementEvents(Goal goal) => goal.Timeline.Where(evt =>
        evt.Kind == ProgressKind.TaskUpdated &&
        evt.Message.StartsWith("REINSTATED_UNCHANGED_CANDIDATE", StringComparison.Ordinal));

    private static void AssertReinstated(Scenario scenario, TaskSpec task, TaskVerificationRecord verdict)
    {
        Xunit.Assert.Equal(WorkTaskStatus.Completed, task.Status);
        Xunit.Assert.Same(verdict, task.LastVerification);
        Xunit.Assert.Single(task.VerificationHistory);
        var evt = Xunit.Assert.Single(ReinstatementEvents(scenario.Goal).Where(evt => evt.TaskId == task.Id));
        Xunit.Assert.Contains("task=" + task.Id.Value, evt.Message, StringComparison.Ordinal);
        Xunit.Assert.Contains("verdictTask=" + task.Id.Value, evt.Message, StringComparison.Ordinal);
        Xunit.Assert.Contains("verdictAt=" + verdict.CompletedAt.ToString("O"), evt.Message, StringComparison.Ordinal);
        Xunit.Assert.Contains("identity=" + scenario.Identity.Canonical, evt.Message, StringComparison.Ordinal);
    }

    private sealed class Scenario
    {
        private readonly TestClock _clock = new();
        public AgentOrchestratorKernel Kernel { get; }
        public Goal Goal { get; }
        public TaskSpec Developer { get; } = new(TaskId.New(), "Implement", AgentRole.Developer);
        public TaskSpec Tester { get; } = new(TaskId.New(), "Test", AgentRole.Tester);
        public TaskSpec Reviewer { get; } = new(TaskId.New(), "Review", AgentRole.Reviewer);
        public CandidateIdentity Identity { get; } = new("patch", "base", "manifest");
        public int Dispatches { get; private set; }
        public List<string> Escalations { get; } = [];
        private const string Candidate = "aaa111";

        public Scenario(bool reviewerNeedsWork = false, bool withSpec = false)
        {
            Kernel = new AgentOrchestratorKernel(_clock);
            Goal = Kernel.CreateGoal("Restore prior unchanged-candidate verdicts", [Developer, Tester, Reviewer]);
            Kernel.ActivateGoal(Goal.Id, ConductorDriverTests.DefaultAgents());
            if (withSpec)
                Kernel.SetGoalRefinedSpec(Goal.Id, new RefinedSpec(
                    "Restore verdicts", ["Verify the candidate"], VerificationClass.TestVerifiable, [], []));
            foreach (var task in Goal.Tasks)
            {
                Kernel.RecordTaskDispatch(Goal.Id, task.Id, new TaskDispatchRecord(
                    "fixture", "worker", @"C:\repo", _clock.UtcNow, CandidateIdentity: Identity));
                Kernel.RecordDispatchResultCommit(Goal.Id, task.Id, Candidate);
                _clock.Advance();
                var output = task == Reviewer
                    ? "WORKER_RESULT:\nblockers: " + (reviewerNeedsWork ? "A correction remains." : "none") +
                      "\nfindings: []\ntouched_anchors: []\n" +
                      "criteria_verdicts: [{\"criterion_index\":0,\"verdict\":\"met\",\"evidence\":\"fixture\"}]\n" +
                      "verdict: " + (reviewerNeedsWork ? "needs-work" : "pass") + "\nEND_WORKER_RESULT"
                    : "ok";
                Kernel.RecordTaskVerification(Goal.Id, task.Id, new TaskVerificationRecord(
                    "worker", @"C:\repo", 0, output, "", _clock.UtcNow,
                    WorkerResultPresent: true, ReviewedCommit: Candidate, CandidateIdentity: Identity));
                // A retained non-passing Reviewer verdict can have been closed by the operator.
                if (reviewerNeedsWork && task == Reviewer)
                    Kernel.ReportTaskProgress(Goal.Id, task.Id, WorkTaskStatus.Completed, "Operator closed the review.");
                Xunit.Assert.Equal(WorkTaskStatus.Completed, task.Status);
                _clock.Advance();
            }
        }

        public void FailDeveloperRetryAndClose()
        {
            var hadKnownCommit = Developer.LastDispatch?.ResultCommit is not null;
            Kernel.RetryTask(Goal.Id, Developer.Id, "Retry upstream work.", RetryCause.NewSourceFinding);
            // The original commit first preserves downstream verifications until reconciliation.
            if (hadKnownCommit)
            {
                Xunit.Assert.NotNull(Tester.LastVerification);
                Xunit.Assert.NotNull(Reviewer.LastVerification);
            }
            _clock.Advance();
            Kernel.ReportTaskProgress(Goal.Id, Developer.Id, WorkTaskStatus.Failed, "Retry failed without a commit.");
            Xunit.Assert.Null(Developer.LastDispatch);
            foreach (var task in new[] { Tester, Reviewer })
            {
                Xunit.Assert.Null(task.LastVerification);
                Xunit.Assert.True(task.LatestRetryInherited);
                Xunit.Assert.Contains(Goal.Timeline, evt => evt.TaskId == task.Id &&
                    evt.Kind == ProgressKind.TaskRetried &&
                    evt.Message.Contains("did not prove candidate", StringComparison.Ordinal) &&
                    evt.Message.Contains("status Failed", StringComparison.Ordinal));
            }
            _clock.Advance();
            Kernel.ReportTaskProgress(Goal.Id, Developer.Id, WorkTaskStatus.Completed, "Operator closed the failed retry.");
            Xunit.Assert.Equal(GoalStatus.Active, Goal.Status);
            _clock.Advance();
        }

        public ConductorDriver Driver()
        {
            var driver = ConductorDriverTests.MakeDriver(
                getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
                dispatchAndStart: _ => { Dispatches++; return DispatchStartOutcome.Started(); },
                recordTaskNote: (goalId, taskId, note) => Kernel.RecordTaskNote(goalId, taskId, note),
                writeEscalation: (_, _, reason) => Escalations.Add(reason));
            driver.OverrideCandidateIdentityResolverForTests(_ => Identity, Kernel);
            return driver;
        }
    }

    private sealed class TestClock : IClock
    {
        public DateTimeOffset UtcNow { get; private set; } = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
        public void Advance() => UtcNow = UtcNow.AddSeconds(1);
    }
}
