using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

using static ConductorDriverTests;

[Xunit.Collection("IsolatedProcessSpawning")]
public sealed class ConductorDriverTestsDeferredNoChangeEvidence
{
    private static readonly string Candidate = new('b', 40);

    [Xunit.Fact]
    public void PriorTesterPassDoesNotBlockOneEvidenceRunBeforeReviewer()
    {
        using var scenario = new Scenario(passTester: true);
        var order = new List<string>();
        var driver = scenario.Driver(
            request =>
            {
                order.Add("focused");
                return ConductorDriverTestsFindingEvidenceReuse.RetainedEvidenceWithExecutedClasses(
                    scenario.Root, request, Candidate);
            },
            role => order.Add(role.ToString()));

        driver.AdvanceOnce(scenario.Goal, ConductorAutonomyPolicy.Conservative);

        Xunit.Assert.Equal("focused", order[0]);
        Xunit.Assert.Contains(scenario.Goal.Timeline, evt =>
            evt.Kind == ProgressKind.FindingEvidenceRunRecorded &&
            evt.Message.StartsWith("finding-evidence deferred-no-change outcome=green;", StringComparison.Ordinal) &&
            evt.Message.Contains("candidate_sha=" + Candidate, StringComparison.Ordinal) &&
            evt.Message.Contains("DeferredAlphaTests", StringComparison.Ordinal));
        driver.AdvanceOnce(scenario.Goal, ConductorAutonomyPolicy.Conservative);
        Xunit.Assert.Equal(1, order.Count(item => item == "focused"));
        Xunit.Assert.DoesNotContain("Tester", order);
    }

    [Xunit.Fact]
    public void CandidateRedRoutesFailureIdentityBackToDeveloperOnce()
    {
        using var scenario = new Scenario(passTester: true);
        var requests = new List<string>();
        var feedback = new List<string>();
        var driver = scenario.Driver(
            request =>
            {
                requests.Add(request);
                return CandidateRedFindingEvidence(request, Candidate,
                    "DeferredAlphaTests.FailsOnCandidate");
            },
            _ => { }, feedback.Add);

        driver.AdvanceOnce(scenario.Goal, ConductorAutonomyPolicy.Conservative);

        Xunit.Assert.Single(requests);
        Xunit.Assert.Contains("DeferredAlphaTests.FailsOnCandidate", Assert.Single(feedback), StringComparison.Ordinal);
        Xunit.Assert.Contains(scenario.Goal.Timeline, evt =>
            evt.Kind == ProgressKind.FindingEvidenceRunRecorded &&
            evt.Message.StartsWith("finding-evidence deferred-no-change outcome=red;", StringComparison.Ordinal));
        driver.AdvanceOnce(scenario.Goal, ConductorAutonomyPolicy.Conservative);
        Xunit.Assert.Single(requests);
    }

    private sealed class Scenario : IDisposable
    {
        public string Root { get; } = ConductorDriverTests.CreateTempDirectory();
        public AgentOrchestratorKernel Kernel { get; }
        public Goal Goal { get; }
        public TaskSpec Developer { get; }
        public TaskSpec Tester { get; }

        public Scenario(bool passTester)
        {
            (Kernel, Goal) = SoftwareGoal("Deferred no-change candidate evidence");
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
                DateTimeOffset.UtcNow, WorkerResultPresent: true, HasCommittedChanges: false));
            Xunit.Assert.Equal(WorkTaskStatus.Completed, Developer.Status);
            if (passTester)
            {
                DispatchTask(Kernel, Goal, Tester, baseCommit: Candidate);
                Kernel.RecordTaskVerification(Goal.Id, Tester.Id, new TaskVerificationRecord(
                    "test.exe", "C:\\tmp", 0, "pass", "", DateTimeOffset.UtcNow,
                    HasCommittedChanges: false, ReviewedCommit: Candidate));
            }
            var worktree = GoalWorktrees.WorktreePath(Root, Goal.Id);
            Directory.CreateDirectory(worktree);
            File.WriteAllText(Path.Combine(worktree, ".git"), "gitdir: fixture");
            var project = Path.Combine(worktree, "tests", "Mcg.AgentOrchestrator.Infrastructure.Tests");
            Directory.CreateDirectory(project);
            File.WriteAllText(Path.Combine(project, "Mcg.AgentOrchestrator.Infrastructure.Tests.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\" />");
            File.WriteAllText(Path.Combine(project, "DeferredAlphaTests.cs"),
                "public class DeferredAlphaTests {}");
        }

        public ConductorDriver Driver(
            Func<string, FocusedEvidenceRunResult> run,
            Action<AgentRole> dispatched,
            Action<string>? feedback = null)
        {
            var driver = MakeDriver(
                getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
                getPreReviewEvidenceContext: _ => NoPreReviewContext(Candidate),
                focusedEvidenceAttemptCoordinator: new ConductorParallelAcceptanceAttemptCoordinator(
                    Path.Combine(Root, "attempts"), runInline: true, acquireStableSlotLease: (_, _) => null),
                runFocusedEvidence: (_, request) => run(request),
                dispatchAndStart: goal =>
                {
                    dispatched(goal.Tasks.First(task => task.Status == WorkTaskStatus.Assigned).RequiredRole);
                    return DispatchStartOutcome.Started();
                },
                retryTaskWithCause: (goalId, taskId, message, round, cause) =>
                {
                    feedback?.Invoke(message);
                    return Kernel.RetryTask(goalId, taskId, message, retryRoundKind: round, retryCause: cause);
                },
                recordFindingEvidenceRequest: (goalId, taskId, message) =>
                    Kernel.RecordFindingEvidenceRequest(goalId, taskId, message),
                recordFindingEvidenceRun: (goalId, taskId, message) =>
                    Kernel.RecordFindingEvidenceRun(goalId, taskId, message),
                executionDirectory: Root);
            driver.OverrideDeveloperCompletionStructuralPreflightForTests(
                _ => new DeveloperCompletionStructuralFindings(false, "clean"), TimeSpan.FromSeconds(5));
            return driver;
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
