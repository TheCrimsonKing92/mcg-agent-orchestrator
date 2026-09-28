using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

using static ConductorDriverTests;

[Xunit.Collection("IsolatedProcessSpawning")]
public sealed class ConductorDriverTestsPreTesterDeferredEvidenceDecline
{
    private const string CandidateSha = "bbb2222";
    private const string BaseSha = "aaa1111";
    private const string DeclinePrefix = "pre-tester-deferred-evidence declined: ";

    [Theory]
    [InlineData(false, "deferred - DeferredAlphaTests", CandidateSha, BaseSha,
        "runner-not-configured", CandidateSha, BaseSha)]
    [InlineData(true, null, CandidateSha, BaseSha,
        "tests-field-missing", CandidateSha, BaseSha)]
    [InlineData(true, "pass - ran", CandidateSha, BaseSha,
        "tests-status-not-deferred", CandidateSha, BaseSha)]
    [InlineData(true, "deferred - DeferredAlphaTests", "not-a-sha", BaseSha,
        "invalid-candidate", "not-a-sha", BaseSha)]
    [InlineData(true, "deferred - DeferredAlphaTests", CandidateSha, null,
        "invalid-base-commit", CandidateSha, "<none>")]
    [InlineData(true, "deferred - DeferredAlphaTests", CandidateSha, CandidateSha,
        "base-equals-candidate", CandidateSha, CandidateSha)]
    [InlineData(false, "deferred - DeferredAlphaTests", CandidateSha, null,
        "runner-not-configured", CandidateSha, "<none>")]
    public void DecliningGuardRecordsExactlyOneClauseWithCandidateAndBase(
        bool runnerConfigured, string? testsField, string candidateSha, string? baseCommit,
        string clause, string expectedCandidate, string expectedBase)
    {
        using var scenario = new Scenario(testsField, candidateSha, baseCommit);
        var driver = scenario.Driver(runnerConfigured);

        driver.AdvanceOnce(scenario.Goal, ConductorAutonomyPolicy.Conservative);

        var note = Assert.Single(scenario.Goal.Timeline.Where(evt =>
            evt.TaskId == scenario.Developer.Id && evt.Kind == ProgressKind.TaskNote &&
            evt.Message.StartsWith(DeclinePrefix, StringComparison.Ordinal)));
        Assert.Equal(
            $"{DeclinePrefix}clause={clause} candidate={expectedCandidate} base={expectedBase}",
            note.Message);
        Assert.Equal(0, scenario.FocusedRuns);
        Assert.Equal([AgentRole.Tester], scenario.DispatchedRoles);
    }

    [Fact]
    public void UnparsedVerificationDoesNotRecordDeclineNote()
    {
        using var scenario = new Scenario(null, CandidateSha, BaseSha, workerResultPresent: false);
        var driver = scenario.Driver(runnerConfigured: true);

        driver.AdvanceOnce(scenario.Goal, ConductorAutonomyPolicy.Conservative);

        Assert.DoesNotContain(scenario.Goal.Timeline, evt =>
            evt.TaskId == scenario.Developer.Id && evt.Kind == ProgressKind.TaskNote &&
            evt.Message.StartsWith(DeclinePrefix, StringComparison.Ordinal));
        Assert.Equal(0, scenario.FocusedRuns);
        Assert.Equal([AgentRole.Tester], scenario.DispatchedRoles);
    }

    private sealed class Scenario : IDisposable
    {
        public string Root { get; } = ConductorDriverTests.CreateTempDirectory();
        public AgentOrchestratorKernel Kernel { get; }
        public Goal Goal { get; }
        public TaskSpec Developer { get; }
        public List<AgentRole> DispatchedRoles { get; } = [];
        public int FocusedRuns { get; private set; }

        private readonly string _candidateSha;

        public Scenario(string? testsField, string candidateSha, string? baseCommit,
            bool workerResultPresent = true)
        {
            _candidateSha = candidateSha;
            (Kernel, Goal) = SoftwareGoal("Pre-Tester decline notes");
            Developer = Goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
            foreach (var task in Goal.Tasks.TakeWhile(task => task.Id != Developer.Id))
                PassVerification(Kernel, Goal, task);

            DispatchTask(Kernel, Goal, Developer, baseCommit: baseCommit);
            var lines = new List<string>
            {
                "WORKER_RESULT:", "files: tests/DeferredAlphaTests.cs", "commands: none"
            };
            if (testsField is not null) lines.Add("tests: " + testsField);
            lines.AddRange(["commit: none", "blockers: none", "assigned_scope_complete: true",
                "model_fit: test/model - adequate - fixture", "skills: none", "confidence: high",
                "END_WORKER_RESULT"]);
            Kernel.RecordTaskVerification(Goal.Id, Developer.Id, new TaskVerificationRecord(
                "test.exe", "C:\\tmp", 0, string.Join(Environment.NewLine, lines), "",
                DateTimeOffset.UtcNow, WorkerResultPresent: workerResultPresent,
                HasCommittedChanges: true));

            Directory.CreateDirectory(GoalWorktrees.WorktreePath(Root, Goal.Id));
        }

        public ConductorDriver Driver(bool runnerConfigured)
        {
            Func<Goal, string, FocusedEvidenceRunResult>? runner = runnerConfigured
                ? (_, _) =>
                {
                    FocusedRuns++;
                    throw new Xunit.Sdk.XunitException("Declining guard must not run focused evidence.");
                }
                : null;
            var driver = MakeDriver(
                getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
                getPreReviewEvidenceContext: _ => NoPreReviewContext(_candidateSha),
                runFocusedEvidence: runner,
                dispatchAndStart: goal =>
                {
                    DispatchedRoles.Add(goal.Tasks.First(task =>
                        task.Status == WorkTaskStatus.Assigned).RequiredRole);
                    return DispatchStartOutcome.Started();
                },
                recordTaskNote: (goalId, taskId, message) =>
                    Kernel.RecordTaskNote(goalId, taskId, message),
                executionDirectory: Root);
            driver.OverrideDeveloperCompletionStructuralPreflightForTests(
                _ => new DeveloperCompletionStructuralFindings(false, "clean"),
                TimeSpan.FromSeconds(5));
            return driver;
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
