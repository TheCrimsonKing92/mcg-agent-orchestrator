using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

using static ConductorDriverTests;

// Parallel-safe: files and evidence coordinators are scenario-owned; process launch is seamed.
public sealed class ConductorDriverTestsPreTesterRedLoopOperatorRetry
{
    private const string BaseSha = "aaa1111";
    private const string ChangedCorePath = "tests/Mcg.AgentOrchestrator.Core.Tests/DeclaredCoreTests.cs";
    private const string EscalationPrefix =
        "PRE_TESTER_RED_LOOP: three consecutive candidate RED runs without Tester dispatch; ";
    private static string Failure => PreTesterAlwaysRunGuardTestClasses.Entries[0].TestClass + ".FailsOnCandidate";

    [Fact]
    public void OperatorAnswerRetriesDeveloperAfterOneNewRed()
    {
        using var scenario = new Scenario();
        scenario.SeedThreeReds();
        scenario.RecordLoopEscalation();
        scenario.RetryDeveloper("The conductor stopped this goal with PRE_TESTER_RED_LOOP: retry");
        scenario.DeliverDeveloper("bbb2222");

        var result = scenario.RunRed();

        AssertRetried(scenario, result, 1);
        Assert.Equal(1, scenario.Runs);
        Assert.Equal("actionable-red", scenario.Latest().Outcome);
        Assert.Equal([Failure], scenario.Latest().FailingTests);
        Assert.Empty(scenario.Escalations);
        Assert.Empty(scenario.Goal.Timeline.Where(evt =>
            evt.TaskId == scenario.Tester.Id && evt.Kind == ProgressKind.TaskDispatchRecorded));
    }

    // The timeout is only a hang detector; assertions measure recorded rounds, never elapsed time.
    [Fact(Timeout = 30_000)]
    [Trait("Category", "CrossTick")]
    public void ThreeNewRedsAfterOperatorAnswerEscalateAgain()
    {
        using var scenario = new Scenario();
        scenario.SeedThreeReds();
        scenario.RecordLoopEscalation();
        scenario.RetryDeveloper("Retry with the correction");
        scenario.DeliverDeveloper("bbb2222");
        AssertRetried(scenario, scenario.RunRed(), 1);
        scenario.DeliverDeveloper("ccc3333");
        AssertRetried(scenario, scenario.RunRed(), 2);
        scenario.DeliverDeveloper("ddd4444");

        var result = scenario.RunRed();

        Assert.IsType<ConductorAdvanceOutcome.Escalated>(result.Outcome);
        var reason = Assert.Single(scenario.Escalations);
        Assert.StartsWith(EscalationPrefix + "failing_sets=", reason, StringComparison.Ordinal);
        foreach (var sha in new[] { "bbb2222", "ccc3333", "ddd4444" })
            Assert.Contains("candidate_sha=" + sha, reason, StringComparison.Ordinal);
        foreach (var sha in new[] { "111aaaa", "222bbbb", "333cccc" })
            Assert.DoesNotContain("candidate_sha=" + sha, reason, StringComparison.Ordinal);
        Assert.Equal(3, scenario.Runs);
        Assert.Equal(2, scenario.Feedback.Count);
        Assert.Equal([AgentRole.Developer, AgentRole.Developer], scenario.Dispatched);
    }

    [Fact(Timeout = 30_000)]
    [Trait("Category", "CrossTick")]
    public void ConductorRetriesWithoutEscalationPreserveThreeRedGuard()
    {
        using var scenario = new Scenario();
        AssertRetried(scenario, scenario.RunRed(), 1);
        scenario.DeliverDeveloper("ccc3333");
        AssertRetried(scenario, scenario.RunRed(), 2);
        scenario.DeliverDeveloper("ddd4444");

        var result = scenario.RunRed();

        Assert.IsType<ConductorAdvanceOutcome.Escalated>(result.Outcome);
        Assert.StartsWith(EscalationPrefix + "failing_sets=", Assert.Single(scenario.Escalations),
            StringComparison.Ordinal);
        Assert.Equal(3, scenario.Runs);
        var retries = scenario.Goal.Timeline.Where(evt =>
            evt.TaskId == scenario.Developer.Id && evt.Kind == ProgressKind.TaskRetried).ToArray();
        Assert.Equal(2, retries.Length);
        Assert.All(retries, evt => Assert.StartsWith("ACTIONABLE_CANDIDATE_RED ", evt.Message,
            StringComparison.Ordinal));
        Assert.DoesNotContain(scenario.Goal.Timeline, evt =>
            evt.Kind == ProgressKind.GoalPolicyDecision && evt.Message.Contains("PRE_TESTER_RED_LOOP:",
                StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false, "The conductor stopped this goal with PRE_TESTER_RED_LOOP: retry")]
    [InlineData(true, "ACTIONABLE_CANDIDATE_RED candidate_sha=333cccc")]
    public void RetryWithoutQualifyingOperatorAnswerDoesNotReset(bool recordEscalation, string retry)
    {
        using var scenario = new Scenario();
        scenario.SeedThreeReds();
        if (recordEscalation) scenario.RecordLoopEscalation();
        scenario.RetryDeveloper(retry);
        scenario.DeliverDeveloper("bbb2222");

        var result = scenario.RunRed();

        Assert.IsType<ConductorAdvanceOutcome.Escalated>(result.Outcome);
        Assert.StartsWith(EscalationPrefix, Assert.Single(scenario.Escalations), StringComparison.Ordinal);
        Assert.Empty(scenario.Feedback);
        Assert.Empty(scenario.Dispatched);
        Assert.Equal(1, scenario.Runs);
    }

    [Fact]
    public void AnswerBeforeNewestEscalationDoesNotReset()
    {
        using var scenario = new Scenario();
        scenario.RecordLoopEscalation();
        scenario.RetryDeveloper("Answer the first escalation");
        scenario.DeliverDeveloper("bbb2222");
        scenario.SeedThreeReds();
        scenario.RecordLoopEscalation();

        var result = scenario.RunRed();

        Assert.IsType<ConductorAdvanceOutcome.Escalated>(result.Outcome);
        Assert.StartsWith(EscalationPrefix, Assert.Single(scenario.Escalations), StringComparison.Ordinal);
        Assert.Empty(scenario.Feedback);
        Assert.Empty(scenario.Dispatched);
    }

    private static void AssertRetried(Scenario scenario, ConductorAdvanceResult result, int round)
    {
        Assert.IsType<ConductorAdvanceOutcome.Executed>(result.Outcome);
        Assert.Empty(scenario.Escalations);
        Assert.Equal(Enumerable.Repeat(AgentRole.Developer, round), scenario.Dispatched);
        Assert.Equal(round, scenario.Feedback.Count);
        var feedback = scenario.Feedback[^1];
        Assert.StartsWith("ACTIONABLE_CANDIDATE_RED ", feedback, StringComparison.Ordinal);
        Assert.Contains("candidate_sha=" + scenario.CandidateSha, feedback, StringComparison.Ordinal);
        Assert.Contains(Failure, feedback, StringComparison.Ordinal);
        Assert.Equal(WorkTaskStatus.Assigned, scenario.Developer.Status);
    }

    private sealed class Scenario : IDisposable
    {
        private string Root { get; } = ConductorDriverTests.CreateTempDirectory();
        public AgentOrchestratorKernel Kernel { get; }
        public Goal Goal { get; }
        public TaskSpec Developer { get; }
        public TaskSpec Tester { get; }
        public string CandidateSha { get; private set; } = "bbb2222";
        public int Runs { get; private set; }
        public List<string> Feedback { get; } = [];
        public List<string> Escalations { get; } = [];
        public List<AgentRole> Dispatched { get; } = [];
        private string Worktree => GoalWorktrees.WorktreePath(Root, Goal.Id);

        public Scenario()
        {
            (Kernel, Goal) = SoftwareGoal("Pre-Tester RED loop operator answer");
            Developer = Goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
            Tester = Goal.Tasks.Single(task => task.RequiredRole == AgentRole.Tester);
            foreach (var task in Goal.Tasks.TakeWhile(task => task.Id != Developer.Id))
                PassVerification(Kernel, Goal, task);
            DeliverDeveloper(CandidateSha);
            Directory.CreateDirectory(Worktree);
            File.WriteAllText(Path.Combine(Worktree, ".git"), "gitdir: fixture");
            Declare("DeclaredCoreTests", "Mcg.AgentOrchestrator.Core.Tests");
            foreach (var entry in PreTesterAlwaysRunGuardTestClasses.Entries)
                Declare(entry.TestClass, "Mcg.AgentOrchestrator.Infrastructure.Tests");
        }

        private void Declare(string name, string projectName)
        {
            var project = Path.Combine(Worktree, "tests", projectName);
            Directory.CreateDirectory(project);
            File.WriteAllText(Path.Combine(project, projectName + ".csproj"), "<Project />");
            File.WriteAllText(Path.Combine(project, name + ".cs"), "public class " + name + " {}");
        }

        public void DeliverDeveloper(string sha)
        {
            CandidateSha = sha;
            DispatchTask(Kernel, Goal, Developer, baseCommit: BaseSha);
            var output = string.Join(Environment.NewLine,
                "WORKER_RESULT:", "files: " + ChangedCorePath, "commands: none",
                "tests: deferred - DeclaredCoreTests", "commit: none", "blockers: none",
                "assigned_scope_complete: true", "model_fit: test/model - adequate - fixture",
                "skills: none", "confidence: high", "END_WORKER_RESULT");
            Kernel.RecordTaskVerification(Goal.Id, Developer.Id, new TaskVerificationRecord(
                "test.exe", "C:\\tmp", 0, output, "", DateTimeOffset.UtcNow,
                WorkerResultPresent: true, HasCommittedChanges: true));
        }

        public void RetryDeveloper(string message) => Kernel.RetryTask(
            Goal.Id, Developer.Id, message, retryRoundKind: RetryRoundKind.Mechanical,
            retryCause: RetryCause.NewSourceFinding);

        public void RecordLoopEscalation() => Kernel.RecordGoalPolicyDecision(Goal.Id,
            "Batch loop tick 9: escalated at WorkspaceReady — " + EscalationPrefix + "failing_sets=old");

        public void SeedThreeReds()
        {
            foreach (var sha in new[] { "111aaaa", "222bbbb", "333cccc" })
                Kernel.RecordFindingEvidenceRun(Goal.Id, Tester.Id,
                    PreTesterEvidenceIndexLines.FormatMarker(new PreTesterEvidenceEntry(
                        "actionable-red", sha, "old-" + sha, ["Core.Tests:DeclaredCoreTests"], [], null, [Failure])));
        }

        public PreTesterEvidenceEntry Latest() => Assert.IsType<PreTesterEvidenceEntry>(
            PreTesterEvidenceIndexLines.Latest(Goal, Tester.Id, CandidateSha));

        public ConductorAdvanceResult RunRed()
        {
            var driver = MakeDriver(
                getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
                getPreReviewEvidenceContext: _ => NoPreReviewContext(CandidateSha),
                getLandingFileScopes: _ => [ChangedCorePath],
                focusedEvidenceAttemptCoordinator: new ConductorParallelAcceptanceAttemptCoordinator(
                    Path.Combine(Root, "attempts"), runInline: true, acquireStableSlotLease: (_, _) => null),
                runFocusedEvidence: (_, request) =>
                {
                    Runs++;
                    var red = CandidateRedFindingEvidence(request, CandidateSha, Failure);
                    var candidate = red.Arms!.Single(arm => arm.Arm == FindingEvidenceArm.Candidate);
                    return red with { Arms = [candidate] };
                },
                dispatchAndStart: goal =>
                {
                    Dispatched.Add(goal.Tasks.First(task => task.Status == WorkTaskStatus.Assigned).RequiredRole);
                    return DispatchStartOutcome.Started();
                },
                retryTaskWithCause: (goalId, taskId, message, round, cause) =>
                {
                    Feedback.Add(message);
                    return Kernel.RetryTask(goalId, taskId, message, retryRoundKind: round, retryCause: cause);
                },
                recordFindingEvidenceRequest: (goalId, taskId, message) =>
                    Kernel.RecordFindingEvidenceRequest(goalId, taskId, message),
                recordFindingEvidenceRun: (goalId, taskId, message) =>
                    Kernel.RecordFindingEvidenceRun(goalId, taskId, message),
                writeEscalation: (_, _, reason) => Escalations.Add(reason),
                executionDirectory: Root);
            driver.OverrideDeveloperCompletionStructuralPreflightForTests(
                _ => new DeveloperCompletionStructuralFindings(false, "clean"), TimeSpan.FromSeconds(5));
            return driver.AdvanceOnce(Goal, ConductorAutonomyPolicy.Conservative);
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
