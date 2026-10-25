using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

using static ConductorDriverTests;

[Collection("IsolatedProcessSpawning")]
public sealed class ConductorDriverTestsDeferredNoChangeInterruption
{
    private static readonly string Candidate = new('b', 40);

    [Fact(Timeout = TestHangGuard.WholeTestMilliseconds)]
    [Trait("Category", "CrossTick")]
    public void InterruptedAttemptRerunsGreenAndDispatchesReviewer()
    {
        using var scenario = new Scenario();
        scenario.LaunchThenInterrupt();
        var interrupted = Assert.Single(scenario.Attempts());
        var runs = 0;
        var inline = new ConductorParallelAcceptanceAttemptCoordinator(
            scenario.AttemptRoot, TrunkBranchName.Default, utcNow: () => scenario.Now,
            runInline: true, acquireStableSlotLease: (_, _) => null);
        var driver = scenario.Driver(inline, request =>
        {
            runs++;
            return ConductorDriverTestsFindingEvidenceReuse.RetainedEvidenceWithExecutedClasses(
                scenario.Root, request, Candidate);
        });
        scenario.Now = scenario.Now.AddMinutes(1);

        driver.AdvanceOnce(scenario.Goal, ConductorAutonomyPolicy.Conservative);

        Assert.Equal(1, runs);
        var attempts = scenario.Attempts();
        Assert.Equal(2, attempts.Length);
        var rerun = Assert.Single(attempts, attempt => attempt.AttemptId != interrupted.AttemptId);
        Assert.Equal(ConductorParallelAcceptanceAttemptOutcome.Passed, rerun.Outcome);
        Assert.True(File.Exists(rerun.ResultPath));
        Assert.Contains(scenario.Goal.Timeline, evt => evt.Kind == ProgressKind.FindingEvidenceRunRecorded &&
            evt.Message.StartsWith("finding-evidence deferred-no-change outcome=green;", StringComparison.Ordinal));
        Assert.Equal(AgentRole.Reviewer, Assert.Single(scenario.Dispatches));
        Assert.Empty(scenario.Feedback);
        Assert.DoesNotContain(scenario.Goal.Timeline, evt => evt.Message.Contains("outcome=unusable", StringComparison.Ordinal));
        Assert.DoesNotContain(scenario.Goal.Timeline, evt => evt.Message.Contains("outcome=interrupted", StringComparison.Ordinal));
    }

    [Fact(Timeout = TestHangGuard.WholeTestMilliseconds)]
    [Trait("Category", "CrossTick")]
    public void SecondInterruptionEscalatesDurablyWithoutThirdLaunch()
    {
        using var scenario = new Scenario();
        var successor = scenario.LaunchThenInterrupt();
        var driver = scenario.Driver(successor);
        scenario.Now = scenario.Now.AddMinutes(1);

        var tick3 = driver.AdvanceOnce(scenario.Goal, ConductorAutonomyPolicy.Conservative);

        Assert.IsType<ConductorAdvanceOutcome.Held>(tick3.Outcome);
        Assert.Equal(2, scenario.Launches);
        Assert.Equal(2, scenario.Attempts().Length);
        scenario.Now = scenario.Now.AddMinutes(1);

        var tick4 = driver.AdvanceOnce(scenario.Goal, ConductorAutonomyPolicy.Conservative);

        var exhausted = Assert.IsType<ConductorAdvanceOutcome.Escalated>(tick4.Outcome);
        Assert.StartsWith("DEFERRED_NO_CHANGE_EVIDENCE_INTERRUPTED task=" + scenario.Developer.Id.Value +
            " candidate_sha=" + Candidate + " attempt=", exhausted.Reason, StringComparison.Ordinal);
        Assert.Contains("reruns=1/1", exhausted.Reason, StringComparison.Ordinal);
        var deadRerun = Assert.Single(scenario.Attempts(), attempt => attempt.TransientFailureCount == 2);
        Assert.Equal(ConductorParallelAcceptanceAttemptOutcome.ProcessDied, deadRerun.Outcome);
        Assert.Contains("attempt=" + deadRerun.AttemptId, exhausted.Reason, StringComparison.Ordinal);
        Assert.False(File.Exists(deadRerun.ResultPath));
        Assert.False(File.Exists(deadRerun.ExitCodePath));
        AssertInterruptedMarker(scenario);
        scenario.Now = scenario.Now.AddMinutes(1);

        var tick5 = driver.AdvanceOnce(scenario.Goal, ConductorAutonomyPolicy.Conservative);

        Assert.Equal(exhausted.Reason, Assert.IsType<ConductorAdvanceOutcome.Escalated>(tick5.Outcome).Reason);
        Assert.Equal(2, scenario.Launches);
        Assert.Equal(2, scenario.Attempts().Length);
        AssertInterruptedMarker(scenario);
        Assert.Empty(scenario.Feedback);
        Assert.Empty(scenario.Dispatches);
        Assert.DoesNotContain(scenario.Goal.Timeline, evt => evt.Message.Contains("outcome=unusable", StringComparison.Ordinal));
    }

    [Fact]
    public void CompletedUnmatchedClassesStillRetryDeveloperAsUnusable()
    {
        using var scenario = new Scenario(testSource: "public abstract class DeferredAlphaTests {}");
        var inline = new ConductorParallelAcceptanceAttemptCoordinator(
            scenario.AttemptRoot, TrunkBranchName.Default, utcNow: () => scenario.Now,
            runInline: true, acquireStableSlotLease: (_, _) => null);
        var runs = 0;
        var driver = scenario.Driver(inline, request =>
        {
            runs++;
            return ConductorDriverTestsFindingEvidenceReuse.RetainedEvidenceWithExecutedClasses(
                scenario.Root, request, Candidate, ["OtherAlphaTests", "AlphaDeferredAlphaTests"]);
        });

        driver.AdvanceOnce(scenario.Goal, ConductorAutonomyPolicy.Conservative);

        Assert.Equal(1, runs);
        Assert.Contains(scenario.Goal.Timeline, evt => evt.Kind == ProgressKind.FindingEvidenceRunRecorded &&
            evt.Message.StartsWith("finding-evidence deferred-no-change outcome=unusable;", StringComparison.Ordinal));
        Assert.StartsWith("DEFERRED_NO_CHANGE_EVIDENCE_UNUSABLE", Assert.Single(scenario.Feedback), StringComparison.Ordinal);
        Assert.Equal(AgentRole.Developer, Assert.Single(scenario.Dispatches));
    }

    private static void AssertInterruptedMarker(Scenario scenario)
    {
        var marker = Assert.Single(scenario.Goal.Timeline, evt =>
            evt.Kind == ProgressKind.FindingEvidenceRunRecorded &&
            evt.Message.StartsWith("finding-evidence deferred-no-change outcome=interrupted;", StringComparison.Ordinal));
        Assert.True(DeferredNoChangeEvidenceIndexLines.TryParse(marker.Message, out var entry));
        Assert.Null(entry.ResultPath);
        Assert.Empty(entry.FailingTests);
    }

    private sealed class Scenario : IDisposable
    {
        public string Root { get; } = ConductorDriverTests.CreateTempDirectory();
        public string AttemptRoot => Path.Combine(Root, "attempts");
        public AgentOrchestratorKernel Kernel { get; }
        public Goal Goal { get; }
        public TaskSpec Developer { get; }
        public TaskSpec Tester { get; }
        public CandidateIdentity Identity { get; } = new("deferred-candidate", "base", "manifest");
        public DateTimeOffset Now { get; set; } = new(2026, 10, 25, 7, 39, 0, TimeSpan.Zero);
        public int Launches { get; private set; }
        public List<string> Feedback { get; } = [];
        public List<AgentRole> Dispatches { get; } = [];

        public Scenario(string testSource = "public class DeferredAlphaTests {}")
        {
            (Kernel, Goal) = SoftwareGoal("Deferred no-change interrupted evidence");
            Developer = Goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
            Tester = Goal.Tasks.Single(task => task.RequiredRole == AgentRole.Tester);
            foreach (var task in Goal.Tasks.TakeWhile(task => task.Id != Developer.Id))
                PassVerification(Kernel, Goal, task);
            Kernel.RetryTask(Goal.Id, Developer.Id, "Retry unchanged candidate after evidence.");
            DispatchTask(Kernel, Goal, Developer, baseCommit: Candidate);
            var output = "NO_CHANGE: the candidate already contains the revision.\n" +
                "WORKER_RESULT:\nfiles: none\ncommands: none\n" +
                "tests: deferred - DeferredAlphaTests\ncommit: none\nblockers: none\n" +
                "assigned_scope_complete: true\nmodel_fit: test/model - adequate - fixture\n" +
                "skills: none\nconfidence: high\nEND_WORKER_RESULT";
            Kernel.RecordDispatchExecutionResult(Goal.Id, Developer.Id, new TaskVerificationRecord(
                "test.exe", "C:\\tmp", 0, output,
                new DeferredNoChangeOutcome(Candidate, ["DeferredAlphaTests"],
                    "NO_CHANGE: the candidate already contains the revision.").FormatMarker(),
                Now, WorkerResultPresent: true, HasCommittedChanges: false));
            Assert.Equal(WorkTaskStatus.Completed, Developer.Status);
            DispatchTask(Kernel, Goal, Tester, baseCommit: Candidate);
            Kernel.RecordTaskVerification(Goal.Id, Tester.Id, new TaskVerificationRecord(
                "test.exe", "C:\\tmp", 0, "pass", "", Now,
                WorkerResultPresent: true, HasCommittedChanges: false,
                ReviewedCommit: Candidate, CandidateIdentity: Identity));
            var worktree = GoalWorktrees.WorktreePath(Root, Goal.Id);
            Directory.CreateDirectory(worktree);
            File.WriteAllText(Path.Combine(worktree, ".git"), "gitdir: fixture");
            var project = Path.Combine(worktree, "tests", "Mcg.AgentOrchestrator.Infrastructure.Tests");
            Directory.CreateDirectory(project);
            File.WriteAllText(Path.Combine(project, "Mcg.AgentOrchestrator.Infrastructure.Tests.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\" />");
            File.WriteAllText(Path.Combine(project, "DeferredAlphaTests.cs"), testSource);
        }

        public ConductorParallelAcceptanceAttemptCoordinator LaunchThenInterrupt()
        {
            var starting = Coordinator(generation: 1001, alive: true);
            var tick1 = Driver(starting).AdvanceOnce(Goal, ConductorAutonomyPolicy.Conservative);
            Assert.IsType<ConductorAdvanceOutcome.Held>(tick1.Outcome);
            Assert.Equal(1, Launches);
            var launched = Assert.Single(Attempts());
            Assert.Equal(1001, launched.ConductorGenerationId);
            Assert.Equal(ConductorParallelAcceptanceAttemptOutcome.Running, launched.Outcome);
            Assert.False(File.Exists(launched.ResultPath));
            Assert.False(File.Exists(launched.ExitCodePath));
            Now = Now.AddMinutes(1);
            var successor = Coordinator(generation: 1002, alive: false);

            var tick2 = Driver(successor).AdvanceOnce(Goal, ConductorAutonomyPolicy.Conservative);

            var held = Assert.IsType<ConductorAdvanceOutcome.Held>(tick2.Outcome);
            Assert.StartsWith("FOCUSED_EVIDENCE_INTERRUPTED attempt=" + launched.AttemptId + " rerun=1/1; ",
                held.Reason, StringComparison.Ordinal);
            var interrupted = Assert.Single(Attempts());
            Assert.Equal(ConductorParallelAcceptanceAttemptOutcome.ProcessDied, interrupted.Outcome);
            Assert.Equal(1, interrupted.TransientFailureCount);
            Assert.NotNull(interrupted.ReconciledAt);
            Assert.False(File.Exists(interrupted.ResultPath));
            Assert.False(File.Exists(interrupted.ExitCodePath));
            Assert.Equal(1, Launches);
            Assert.Empty(Feedback);
            Assert.Empty(Dispatches);
            Assert.DoesNotContain(Goal.Timeline, evt => evt.Message.Contains("outcome=unusable", StringComparison.Ordinal));
            return successor;
        }

        private ConductorParallelAcceptanceAttemptCoordinator Coordinator(int generation, bool alive) => new(
            AttemptRoot, TrunkBranchName.Default, utcNow: () => Now,
            isProcessAlive: _ => alive,
            launchOwnedProcess: _ =>
            {
                Launches++;
                return new ConductorParallelAcceptanceOwnedProcessLaunchResult(7300 + Launches);
            },
            recentHeartbeatGrace: TimeSpan.Zero, conductorGenerationId: generation);

        public ConductorParallelAcceptanceAttempt[] Attempts() =>
            Directory.EnumerateFiles(Path.Combine(AttemptRoot, Goal.Id.Value), "*.attempt.json")
                .Select(path => JsonSerializer.Deserialize<ConductorParallelAcceptanceAttempt>(
                    File.ReadAllText(path), new JsonSerializerOptions(JsonSerializerDefaults.Web))!)
                .OrderBy(attempt => attempt.Ordinal).ToArray();

        public ConductorDriver Driver(
            ConductorParallelAcceptanceAttemptCoordinator coordinator,
            Func<string, FocusedEvidenceRunResult>? run = null)
        {
            var driver = MakeDriver(
                getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
                getPreReviewEvidenceContext: _ => NoPreReviewContext(Candidate),
                focusedEvidenceAttemptCoordinator: coordinator,
                runFocusedEvidence: (_, request) => run is null
                    ? throw new InvalidOperationException("The external holder must not invoke an inline runner.")
                    : run(request),
                dispatchAndStart: goal =>
                {
                    Dispatches.Add(goal.Tasks.First(task => task.Status == WorkTaskStatus.Assigned).RequiredRole);
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
                executionDirectory: Root);
            driver.OverrideDeveloperCompletionStructuralPreflightForTests(
                _ => new DeveloperCompletionStructuralFindings(false, "clean"), TimeSpan.FromSeconds(5));
            driver.OverrideCandidateIdentityResolverForTests(_ => Identity);
            return driver;
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
