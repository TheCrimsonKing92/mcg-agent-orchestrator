using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using static ConductorDriverTests;

[Xunit.Collection("IsolatedProcessSpawning")]
public sealed class ConductorDriverTestsStaleFindingNoChangeRecheck
{
    private static readonly string Reviewed = new('a', 40);
    private static readonly string Candidate = new('b', 40);

    [Xunit.Fact]
    public void StaleReviewerVerdictIsRecheckedAfterGreenDeferredEvidenceAndTester()
    {
        using var scenario = new Scenario(Candidate);
        var driver = scenario.Driver();

        scenario.RunEvidenceAndTester(driver);

        Xunit.Assert.Null(UnchangedCandidateRule.Evaluate(scenario.Goal, scenario.Reviewer, scenario.Identity));
        driver.AdvanceOnce(scenario.Goal, ConductorAutonomyPolicy.Conservative);
        Xunit.Assert.Equal(new[] { AgentRole.Tester, AgentRole.Reviewer }, scenario.Dispatches);
    }

    [Xunit.Fact]
    public void SameCommitReviewerVerdictRemainsHeldAfterGreenDeferredEvidenceAndTester()
    {
        using var scenario = new Scenario(Reviewed);
        var driver = scenario.Driver();

        scenario.RunEvidenceAndTester(driver);

        var result = driver.AdvanceOnce(scenario.Goal, ConductorAutonomyPolicy.Conservative);
        var hold = Xunit.Assert.IsType<UnchangedCandidateHoldReason>(
            Xunit.Assert.IsType<ConductorAdvanceOutcome.Held>(result.Outcome).TypedReason);
        Xunit.Assert.Equal(AgentRole.Reviewer, hold.Role);
        Xunit.Assert.Equal("needs-work", hold.PriorVerdict);
        Xunit.Assert.Equal(new[] { AgentRole.Tester }, scenario.Dispatches);
    }

    [Xunit.Theory]
    [Xunit.InlineData(true)]
    [Xunit.InlineData(false)]
    public void HistoricalDeferredRoundUsesItsOwnDispatchBase(bool stale)
    {
        using var scenario = new Scenario(stale ? Candidate : Reviewed);
        var deferred = scenario.Developer.LastVerification!;
        scenario.Clock.Advance();
        scenario.Kernel.RetryTask(scenario.Goal.Id, scenario.Developer.Id, "Next Developer round.");
        // The latest round has the opposite base and must not replace the historical round's base.
        scenario.RecordDispatch(scenario.Developer, stale ? Reviewed : Candidate);
        scenario.Clock.Advance();
        scenario.Kernel.RecordTaskVerification(scenario.Goal.Id, scenario.Developer.Id,
            new TaskVerificationRecord("fixture", "C:\\tmp", 0, "ok", "", scenario.Clock.UtcNow));
        Xunit.Assert.NotSame(deferred, scenario.Developer.LastVerification);

        var hold = UnchangedCandidateRule.Evaluate(scenario.Goal, scenario.Reviewer, scenario.Identity);

        if (stale) Xunit.Assert.Null(hold);
        else Xunit.Assert.IsType<UnchangedCandidateHoldReason>(hold);
    }

    private sealed class Scenario : IDisposable
    {
        public string Root { get; } = ConductorDriverTests.CreateTempDirectory();
        public AgentOrchestratorKernel Kernel { get; }
        public Goal Goal { get; }
        public TaskSpec Developer { get; }
        public TaskSpec Tester { get; }
        public TaskSpec Reviewer { get; }
        public TestClock Clock { get; } = new();
        public CandidateIdentity Identity { get; } = new("stale-finding-candidate", "base", "manifest");
        public List<AgentRole> Dispatches { get; } = [];
        private readonly string _candidate;

        public Scenario(string candidate)
        {
            _candidate = candidate;
            var (kernel, goal) = SoftwareGoal("Stale finding no-change recheck");
            Kernel = AgentOrchestratorKernel.FromSnapshot(kernel.ExportSnapshot(), Clock);
            Goal = Kernel.GetGoal(goal.Id);
            Developer = Goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
            Tester = Goal.Tasks.Single(task => task.RequiredRole == AgentRole.Tester);
            Reviewer = Goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
            foreach (var task in Goal.Tasks.TakeWhile(task => task.Id != Reviewer.Id))
            {
                RecordDispatch(task, Reviewed);
                Clock.Advance();
                Kernel.RecordTaskVerification(Goal.Id, task.Id,
                    new TaskVerificationRecord("fixture", "C:\\tmp", 0, "ok", "", Clock.UtcNow));
            }
            RecordDispatch(Reviewer, Reviewed);
            Clock.Advance();
            Kernel.RecordDispatchExecutionResult(Goal.Id, Reviewer.Id, new TaskVerificationRecord(
                "fixture", "C:\\tmp", 1,
                "WORKER_RESULT:\nfiles: none\ncommands: none\ntests: deferred - DeferredAlphaTests\n" +
                "commit: none\nblockers: candidate finding\nverdict: needs-work\n" +
                "model_fit: test/model - adequate - fixture\nskills: none\nconfidence: high\nEND_WORKER_RESULT",
                "", Clock.UtcNow, WorkerResultPresent: true, CandidateIdentity: Identity));
            Xunit.Assert.Equal(Reviewed, Reviewer.LastVerification!.ReviewedCommit);
            Xunit.Assert.Equal("needs-work", UnchangedCandidateVerdict.Derive(Goal, Reviewer, Reviewer.LastVerification));
            Clock.Advance();
            Kernel.RetryTask(Goal.Id, Developer.Id, "Repair the raised finding.");
            Xunit.Assert.Null(Reviewer.LastVerification);
            RecordDispatch(Developer, candidate);
            Clock.Advance();
            const string rationale = "NO_CHANGE: the candidate already contains the revision.";
            var output = rationale + "\nWORKER_RESULT:\nfiles: none\ncommands: none\n" +
                "tests: deferred - DeferredAlphaTests\ncommit: none\nblockers: none\n" +
                "assigned_scope_complete: true\nmodel_fit: test/model - adequate - fixture\n" +
                "skills: none\nconfidence: high\nEND_WORKER_RESULT";
            Kernel.RecordDispatchExecutionResult(Goal.Id, Developer.Id, new TaskVerificationRecord(
                "fixture", "C:\\tmp", 0, output,
                new DeferredNoChangeOutcome(candidate, ["DeferredAlphaTests"], rationale).FormatMarker(),
                Clock.UtcNow, WorkerResultPresent: true, DispatchStartedAt: Developer.LastDispatch!.DispatchedAt));
            Xunit.Assert.Equal("deferred-no-change-round", Developer.LastVerification!.CompletionVerdictRule);
            var worktree = GoalWorktrees.WorktreePath(Root, Goal.Id);
            Directory.CreateDirectory(worktree);
            File.WriteAllText(Path.Combine(worktree, ".git"), "gitdir: fixture");
            var project = Path.Combine(worktree, "tests", "Mcg.AgentOrchestrator.Infrastructure.Tests");
            Directory.CreateDirectory(project);
            File.WriteAllText(Path.Combine(project, "Mcg.AgentOrchestrator.Infrastructure.Tests.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\" />");
            File.WriteAllText(Path.Combine(project, "DeferredAlphaTests.cs"), "public class DeferredAlphaTests {}");
        }

        public void RecordDispatch(TaskSpec task, string baseCommit)
        {
            Clock.Advance();
            Kernel.RecordTaskDispatch(Goal.Id, task.Id,
                new TaskDispatchRecord("test-worker", "fixture", "C:\\tmp", Clock.UtcNow, BaseCommit: baseCommit));
        }

        public void RunEvidenceAndTester(ConductorDriver driver)
        {
            Xunit.Assert.True(ConductorDriver.HasPendingDeferredNoChangeEvidence(Goal));
            driver.AdvanceOnce(Goal, ConductorAutonomyPolicy.Conservative);
            Xunit.Assert.False(ConductorDriver.HasPendingDeferredNoChangeEvidence(Goal));
            Xunit.Assert.Contains(Goal.Timeline, evt => evt.Kind == ProgressKind.FindingEvidenceRunRecorded &&
                evt.Message.StartsWith("finding-evidence deferred-no-change outcome=green;", StringComparison.Ordinal));
            // AdvanceOnce may consume evidence and dispatch in one tick; otherwise the next tick does.
            if (Dispatches.Count == 0) driver.AdvanceOnce(Goal, ConductorAutonomyPolicy.Conservative);
            Xunit.Assert.Equal(new[] { AgentRole.Tester }, Dispatches);
            Clock.Advance();
            Kernel.RecordTaskVerification(Goal.Id, Tester.Id,
                new TaskVerificationRecord("fixture", "C:\\tmp", 0,
                    "WORKER_RESULT:\nfiles: none\ncommands: fixture\ntests: pass - fixture\ncommit: none\n" +
                    "blockers: none\nmodel_fit: test/model - adequate - fixture\nskills: none\nconfidence: high\nEND_WORKER_RESULT",
                    "", Clock.UtcNow, WorkerResultPresent: true, CandidateIdentity: Identity));
            Xunit.Assert.Equal(WorkTaskStatus.Completed, Tester.Status);
            Xunit.Assert.Empty(Tester.LastVerification!.FindingEvidenceReceipts ?? []);
        }

        public ConductorDriver Driver()
        {
            var driver = MakeDriver(
                getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
                getPreReviewEvidenceContext: _ => NoPreReviewContext(_candidate),
                focusedEvidenceAttemptCoordinator: new ConductorParallelAcceptanceAttemptCoordinator(
                    Path.Combine(Root, "attempts"), runInline: true, acquireStableSlotLease: (_, _) => null),
                runFocusedEvidence: (_, request) =>
                    ConductorDriverTestsFindingEvidenceReuse.RetainedEvidenceWithExecutedClasses(Root, request, _candidate),
                dispatchAndStart: goal =>
                {
                    var task = goal.Tasks.First(task => task.Status == WorkTaskStatus.Assigned);
                    Dispatches.Add(task.RequiredRole);
                    RecordDispatch(task, _candidate);
                    return DispatchStartOutcome.Started();
                },
                retryTaskWithCause: (goalId, taskId, message, round, cause) =>
                    Kernel.RetryTask(goalId, taskId, message, retryRoundKind: round, retryCause: cause),
                recordFindingEvidenceRequest: (goalId, taskId, message) =>
                    Kernel.RecordFindingEvidenceRequest(goalId, taskId, message),
                recordFindingEvidenceRun: (goalId, taskId, message) =>
                    Kernel.RecordFindingEvidenceRun(goalId, taskId, message),
                executionDirectory: Root);
            driver.OverrideDeveloperCompletionStructuralPreflightForTests(
                _ => new DeveloperCompletionStructuralFindings(false, "clean"), TimeSpan.FromSeconds(5));
            driver.OverrideCandidateIdentityResolverForTests(_ => Identity);
            return driver;
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }

    private sealed class TestClock : IClock
    {
        public DateTimeOffset UtcNow { get; private set; } = DateTimeOffset.Parse("2026-10-06T12:00:00Z");
        public void Advance() => UtcNow = UtcNow.AddSeconds(1);
    }
}
