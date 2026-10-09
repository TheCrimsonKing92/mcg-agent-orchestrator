using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

using static ConductorDriverTests;

// Parallel-safe: files and evidence coordinators are scenario-owned; process launch is seamed.
public sealed class ConductorDriverTestsPreTesterRedLoopConvergence
{
    private const string BaseSha = "aaa1111";
    private const string CandidateSha = "bbb2222";
    private const string ChangedCorePath = "tests/Mcg.AgentOrchestrator.Core.Tests/DeclaredCoreTests.cs";
    private const string EscalationPrefix =
        "PRE_TESTER_RED_LOOP: three consecutive candidate RED runs without Tester dispatch; failing_sets=";
    private static string Failure(int number) =>
        PreTesterAlwaysRunGuardTestClasses.Entries[0].TestClass + ".T" + number;

    [Fact]
    public void AdvanceOnce_ThreeShrinkingReds_RetriesDeveloperWithoutEscalation()
    {
        using var scenario = new Scenario();
        scenario.SeedReds(3, 2);

        var result = scenario.RunRed();

        Assert.IsType<ConductorAdvanceOutcome.Executed>(result.Outcome);
        Assert.Equal(1, scenario.Runs);
        Assert.Equal([AgentRole.Developer], scenario.Dispatched);
        var feedback = Assert.Single(scenario.Feedback);
        Assert.StartsWith("ACTIONABLE_CANDIDATE_RED ", feedback, StringComparison.Ordinal);
        Assert.Contains("candidate_sha=" + CandidateSha, feedback, StringComparison.Ordinal);
        Assert.Contains(Failure(1), feedback, StringComparison.Ordinal);
        Assert.Equal(WorkTaskStatus.Assigned, scenario.Developer.Status);
        Assert.Empty(scenario.Escalations);
        Assert.DoesNotContain(scenario.Goal.Timeline, evt =>
            evt.Kind == ProgressKind.GoalPolicyDecision &&
            evt.Message.Contains("PRE_TESTER_RED_LOOP:", StringComparison.Ordinal));
        var retry = Assert.Single(scenario.Goal.Timeline.Where(evt =>
            evt.TaskId == scenario.Developer.Id && evt.Kind == ProgressKind.TaskRetried));
        Assert.StartsWith("ACTIONABLE_CANDIDATE_RED ", retry.Message, StringComparison.Ordinal);
        AssertLiveRed(scenario);
    }

    [Fact]
    public void AdvanceOnce_FiveShrinkingReds_EscalatesCapWithEveryRun()
    {
        using var scenario = new Scenario();
        scenario.SeedReds(5, 4, 3, 2);

        var result = scenario.RunRed();

        AssertEscalated(scenario, result, "5,4,3,2,1", "cap");
    }

    [Fact]
    public void AdvanceOnce_NewestRedRepeats_EscalatesRepeatWithEveryCount()
    {
        using var scenario = new Scenario();
        scenario.SeedReds(2, 1);

        var result = scenario.RunRed();

        AssertEscalated(scenario, result, "2,1,1", "repeat");
    }

    private static void AssertLiveRed(Scenario scenario)
    {
        var latest = Assert.IsType<PreTesterEvidenceEntry>(
            PreTesterEvidenceIndexLines.Latest(scenario.Goal, scenario.Tester.Id, CandidateSha));
        Assert.Equal("actionable-red", latest.Outcome);
        Assert.Equal([Failure(1)], latest.FailingTests);
        Assert.DoesNotContain(scenario.Goal.Timeline, evt =>
            evt.TaskId == scenario.Tester.Id && evt.Kind == ProgressKind.TaskDispatchRecorded);
    }

    private static void AssertEscalated(
        Scenario scenario, ConductorAdvanceResult result, string counts, string kind)
    {
        Assert.IsType<ConductorAdvanceOutcome.Escalated>(result.Outcome);
        Assert.Equal(1, scenario.Runs);
        Assert.Empty(scenario.Feedback);
        Assert.Empty(scenario.Dispatched);
        AssertLiveRed(scenario);
        var reason = Assert.Single(scenario.Escalations);
        Assert.StartsWith(EscalationPrefix, reason, StringComparison.Ordinal);
        Assert.Contains("failing_counts=" + counts, reason, StringComparison.Ordinal);
        Assert.EndsWith("trip=" + kind, reason, StringComparison.Ordinal);
        var runMessages = scenario.Goal.Timeline.Where(evt =>
            evt.TaskId == scenario.Tester.Id && evt.Kind == ProgressKind.FindingEvidenceRunRecorded)
            .Select(evt => evt.Message);
        Assert.Equal(EscalationPrefix + string.Join(" | ", runMessages) +
            "; failing_counts=" + counts + "; trip=" + kind, reason);
    }

    private sealed class Scenario : IDisposable
    {
        private string Root { get; } = ConductorDriverTests.CreateTempDirectory();
        public AgentOrchestratorKernel Kernel { get; }
        public Goal Goal { get; }
        public TaskSpec Developer { get; }
        public TaskSpec Tester { get; }
        public int Runs { get; private set; }
        public List<string> Feedback { get; } = [];
        public List<string> Escalations { get; } = [];
        public List<AgentRole> Dispatched { get; } = [];
        private string Worktree => GoalWorktrees.WorktreePath(Root, Goal.Id);

        public Scenario()
        {
            (Kernel, Goal) = SoftwareGoal("Pre-Tester RED loop convergence");
            Developer = Goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
            Tester = Goal.Tasks.Single(task => task.RequiredRole == AgentRole.Tester);
            foreach (var task in Goal.Tasks.TakeWhile(task => task.Id != Developer.Id))
                PassVerification(Kernel, Goal, task);
            DispatchTask(Kernel, Goal, Developer, baseCommit: BaseSha);
            var output = string.Join(Environment.NewLine,
                "WORKER_RESULT:", "files: " + ChangedCorePath, "commands: none",
                "tests: deferred - DeclaredCoreTests", "commit: none", "blockers: none",
                "assigned_scope_complete: true", "model_fit: test/model - adequate - fixture",
                "skills: none", "confidence: high", "END_WORKER_RESULT");
            Kernel.RecordTaskVerification(Goal.Id, Developer.Id, new TaskVerificationRecord(
                "test.exe", "C:\\tmp", 0, output, "", DateTimeOffset.UtcNow,
                WorkerResultPresent: true, HasCommittedChanges: true));
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

        public void SeedReds(params int[] counts)
        {
            for (var index = 0; index < counts.Length; index++)
            {
                var sha = (index + 1).ToString("x7");
                Kernel.RecordFindingEvidenceRun(Goal.Id, Tester.Id,
                    PreTesterEvidenceIndexLines.FormatMarker(new PreTesterEvidenceEntry(
                        "actionable-red", sha, "old-" + sha, ["Core.Tests:DeclaredCoreTests"], [], null,
                        Enumerable.Range(1, counts[index]).Select(Failure).ToArray())));
            }
        }

        public ConductorAdvanceResult RunRed()
        {
            var driver = MakeDriver(
                getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
                getPreReviewEvidenceContext: _ => NoPreReviewContext(CandidateSha),
                getLandingFileScopes: _ => [ChangedCorePath],
                focusedEvidenceAttemptCoordinator: new ConductorParallelAcceptanceAttemptCoordinator(
                    Path.Combine(Root, "attempts"), Mcg.AgentOrchestrator.Infrastructure.TrunkBranchName.Default, runInline: true, acquireStableSlotLease: (_, _) => null),
                runFocusedEvidence: (_, request) =>
                {
                    Runs++;
                    var red = CandidateRedFindingEvidence(request, CandidateSha, Failure(1));
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
