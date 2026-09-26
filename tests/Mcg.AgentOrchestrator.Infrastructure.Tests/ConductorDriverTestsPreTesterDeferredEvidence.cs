using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

using static ConductorDriverTests;

[Xunit.Collection("IsolatedProcessSpawning")]
public sealed class ConductorDriverTestsPreTesterDeferredEvidence
{
    private const string CandidateSha = "bbb2222";
    private const string BaseSha = "aaa1111";

    [Fact]
    public void ChangedDeferredClassesRunBeforeTesterAndGreenReceiptIsIndexed()
    {
        using var scenario = new Scenario("deferred - DeferredAlphaTests, DeferredBetaTests", BaseSha,
            ["DeferredAlphaTests", "DeferredBetaTests"]);
        var order = new List<string>();
        var requests = new List<string>();
        var driver = scenario.Driver(
            request =>
            {
                order.Add("focused");
                requests.Add(request);
                return ConductorDriverTestsFindingEvidenceReuse.RetainedEvidenceWithExecutedClasses(
                    scenario.Root, request, CandidateSha);
            },
            role => order.Add(role.ToString()));

        driver.AdvanceOnce(scenario.Goal, ConductorAutonomyPolicy.Conservative);

        Assert.Equal(["focused", "Tester"], order);
        var request = Assert.Single(requests);
        Assert.Equal(2, request.Split(';', StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.Contains("DeferredAlphaTests", request, StringComparison.Ordinal);
        Assert.Contains("DeferredBetaTests", request, StringComparison.Ordinal);
        var lines = PreTesterEvidenceIndexLines.ForTester(scenario.Goal, scenario.Tester, CandidateSha);
        Assert.Contains(lines, line => line.Contains("state=executed-on-candidate", StringComparison.Ordinal) &&
                                       line.Contains("candidate_sha=" + CandidateSha, StringComparison.Ordinal));
        var brief = scenario.Kernel.BuildTaskBriefSource(
            scenario.Goal.Id, scenario.Tester.Id, targetHeadCommit: CandidateSha);
        Assert.Contains("state=executed-on-candidate",
            string.Join(Environment.NewLine, brief.Segments.SelectMany(segment => segment.Lines)),
            StringComparison.Ordinal);
    }

    [Fact]
    public void ActionableRedRetriesDeveloperWithoutTesterDispatch()
    {
        using var scenario = new Scenario("deferred - DeferredAlphaTests", BaseSha, ["DeferredAlphaTests"]);
        var roles = new List<AgentRole>();
        var feedback = new List<string>();
        var driver = scenario.Driver(
            request => CandidateRedFindingEvidence(request, CandidateSha,
                "DeferredAlphaTests.FailsOnCandidate"),
            roles.Add,
            feedback.Add);

        driver.AdvanceOnce(scenario.Goal, ConductorAutonomyPolicy.Conservative);

        Assert.Equal([AgentRole.Developer], roles);
        Assert.DoesNotContain(AgentRole.Tester, roles);
        Assert.StartsWith("ACTIONABLE_CANDIDATE_RED", Assert.Single(feedback), StringComparison.Ordinal);
        Assert.Contains("DeferredAlphaTests.FailsOnCandidate", feedback[0], StringComparison.Ordinal);
    }

    [Fact]
    public void PartlyUnselectableDeclarationRunsResolvedClassAndReportsNotRun()
    {
        using var scenario = new Scenario("deferred - DeferredAlphaTests, MissingClass", BaseSha,
            ["DeferredAlphaTests"]);
        var requests = new List<string>();
        var roles = new List<AgentRole>();
        var driver = scenario.Driver(
            request =>
            {
                requests.Add(request);
                return ConductorDriverTestsFindingEvidenceReuse.RetainedEvidenceWithExecutedClasses(
                    scenario.Root, request, CandidateSha);
            },
            roles.Add);

        driver.AdvanceOnce(scenario.Goal, ConductorAutonomyPolicy.Conservative);

        Assert.Equal([AgentRole.Tester], roles);
        Assert.Contains("DeferredAlphaTests", Assert.Single(requests), StringComparison.Ordinal);
        Assert.DoesNotContain("MissingClass", requests[0], StringComparison.Ordinal);
        Assert.Contains(PreTesterEvidenceIndexLines.ForTester(scenario.Goal, scenario.Tester, CandidateSha),
            line => line.Contains("evidence_not_run: MissingClass", StringComparison.Ordinal));
    }

    [Fact]
    public void RedWithoutFailingTestIdentityContinuesToTesterWithOutcome()
    {
        using var scenario = new Scenario("deferred - DeferredAlphaTests", BaseSha, ["DeferredAlphaTests"]);
        var roles = new List<AgentRole>();
        var driver = scenario.Driver(
            request =>
            {
                var red = CandidateRedFindingEvidence(request, CandidateSha);
                var arm = red.Arms!.Single(item => item.Arm == FindingEvidenceArm.Candidate);
                var check = arm.Checks.Single() with { FailingTestIdentities = [] };
                return red with { Checks = [check], Arms = [arm with { Checks = [check] }] };
            },
            roles.Add);

        driver.AdvanceOnce(scenario.Goal, ConductorAutonomyPolicy.Conservative);

        Assert.Equal([AgentRole.Tester], roles);
        Assert.Contains(PreTesterEvidenceIndexLines.ForTester(scenario.Goal, scenario.Tester, CandidateSha),
            line => line.Contains("state=red", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("deferred - DeferredAlphaTests", CandidateSha, true)]
    [InlineData("deferred - no named classes", BaseSha, false)]
    [InlineData("deferred - MissingClass", BaseSha, false)]
    public void UnchangedOrUnselectableDeclarationKeepsTesterDispatch(
        string tests, string baseSha, bool createSource)
    {
        using var scenario = new Scenario(tests, baseSha,
            createSource ? ["DeferredAlphaTests"] : []);
        var roles = new List<AgentRole>();
        var runs = 0;
        var driver = scenario.Driver(
            request =>
            {
                runs++;
                return ConductorDriverTestsFindingEvidenceReuse.RetainedEvidenceWithExecutedClasses(
                    scenario.Root, request, CandidateSha);
            },
            roles.Add);

        driver.AdvanceOnce(scenario.Goal, ConductorAutonomyPolicy.Conservative);

        Assert.Equal(0, runs);
        Assert.Equal([AgentRole.Tester], roles);
    }

    private sealed class Scenario : IDisposable
    {
        public string Root { get; } = ConductorDriverTests.CreateTempDirectory();
        public AgentOrchestratorKernel Kernel { get; }
        public Goal Goal { get; }
        public TaskSpec Developer { get; }
        public TaskSpec Tester { get; }

        public Scenario(string tests, string baseSha, IReadOnlyList<string> classNames)
        {
            (Kernel, Goal) = SoftwareGoal("Pre-Tester deferred evidence");
            Developer = Goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
            Tester = Goal.Tasks.Single(task => task.RequiredRole == AgentRole.Tester);
            foreach (var task in Goal.Tasks.TakeWhile(task => task.Id != Developer.Id))
                PassVerification(Kernel, Goal, task);
            DispatchTask(Kernel, Goal, Developer, baseCommit: baseSha);
            var output = string.Join(Environment.NewLine,
                "WORKER_RESULT:", "files: tests/DeferredAlphaTests.cs", "commands: none",
                "tests: " + tests, "commit: none", "blockers: none",
                "assigned_scope_complete: true", "model_fit: test/model - adequate - fixture",
                "skills: none", "confidence: high", "END_WORKER_RESULT");
            Kernel.RecordTaskVerification(Goal.Id, Developer.Id, new TaskVerificationRecord(
                "test.exe", "C:\\tmp", 0, output, "", DateTimeOffset.UtcNow,
                WorkerResultPresent: true, HasCommittedChanges: true));
            var worktree = GoalWorktrees.WorktreePath(Root, Goal.Id);
            var project = Path.Combine(worktree, "tests", "Mcg.AgentOrchestrator.Infrastructure.Tests");
            Directory.CreateDirectory(project);
            File.WriteAllText(Path.Combine(project, "Mcg.AgentOrchestrator.Infrastructure.Tests.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\" />");
            foreach (var name in classNames)
                File.WriteAllText(Path.Combine(project, name + ".cs"), "public class " + name + " {}");
        }

        public ConductorDriver Driver(
            Func<string, FocusedEvidenceRunResult> run,
            Action<AgentRole> dispatched,
            Action<string>? feedback = null)
        {
            var driver = MakeDriver(
                getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
                getPreReviewEvidenceContext: _ => NoPreReviewContext(CandidateSha),
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
