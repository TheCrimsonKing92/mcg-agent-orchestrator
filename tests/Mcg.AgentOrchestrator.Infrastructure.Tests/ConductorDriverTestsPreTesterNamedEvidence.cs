using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

using static ConductorDriverTests;

[Xunit.Collection("IsolatedProcessSpawning")]
public sealed class ConductorDriverTestsPreTesterNamedEvidence
{
    private const string CandidateSha = "bbb2222";
    private const string BaseSha = "aaa1111";

    [Fact]
    public void BriefNamedClassJoinsDeveloperDeclaredRunBeforeTester()
    {
        using var scenario = new Scenario("Verify BriefNamedRegressionTests");
        var order = new List<string>();
        var requests = new List<string>();
        var driver = scenario.Driver(request =>
        {
            order.Add("focused");
            requests.Add(request);
            return scenario.Green(request);
        }, role => order.Add(role.ToString()));

        driver.AdvanceOnce(scenario.Goal, ConductorAutonomyPolicy.Conservative);

        Assert.Equal(["focused", "Tester"], order);
        Assert.Equal(["DeferredAlphaTests", "BriefNamedRegressionTests"], Classes(Assert.Single(requests)));
        var brief = scenario.Kernel.BuildTaskBriefSource(
            scenario.Goal.Id, scenario.Tester.Id, targetHeadCommit: CandidateSha);
        var lines = brief.Segments.SelectMany(segment => segment.Lines);
        Assert.Contains(lines, line => line.Contains("state=executed-on-candidate", StringComparison.Ordinal) &&
                                       line.Contains("DeferredAlphaTests", StringComparison.Ordinal) &&
                                       line.Contains("BriefNamedRegressionTests", StringComparison.Ordinal));
    }

    [Fact]
    public void PlannerNamedClassFollowsDeveloperAndBriefNamedClasses()
    {
        using var scenario = new Scenario("Verify BriefNamedRegressionTests",
            "Run PlannerNamedNeighborTests after BriefNamedRegressionTests");
        var requests = new List<string>();
        var driver = scenario.Driver(request =>
        {
            requests.Add(request);
            return scenario.Green(request);
        }, _ => { });

        driver.AdvanceOnce(scenario.Goal, ConductorAutonomyPolicy.Conservative);

        Assert.Equal(["DeferredAlphaTests", "BriefNamedRegressionTests", "PlannerNamedNeighborTests"],
            Classes(Assert.Single(requests)));
    }

    [Fact]
    public void UnresolvedAmbiguousAndNonTestNamesAreDroppedSilently()
    {
        using var scenario = new Scenario(
            "MissingBriefTests DuplicateNamedTests BriefHelper BriefNamedRegressionTests.SomeMethod DeferredAlphaTests",
            "DuplicateNamedTests MissingPlannerTests BriefHelper BriefNamedRegressionTests PlannerNamedNeighborTests",
            "deferred - DeferredAlphaTests, MissingDeclaredTests");
        scenario.Declare("DuplicateNamedTests");
        scenario.Declare("DuplicateNamedTests", "Other.Tests");
        scenario.Declare("BriefHelper");
        var requests = new List<string>();
        var driver = scenario.Driver(request =>
        {
            requests.Add(request);
            return scenario.Green(request);
        }, _ => { });

        driver.AdvanceOnce(scenario.Goal, ConductorAutonomyPolicy.Conservative);

        Assert.Equal(["DeferredAlphaTests", "BriefNamedRegressionTests", "PlannerNamedNeighborTests"],
            Classes(Assert.Single(requests)));
        var receipt = PreTesterEvidenceIndexLines.Latest(scenario.Goal, scenario.Tester.Id, CandidateSha);
        Assert.NotNull(receipt);
        Assert.Equal(["MissingDeclaredTests"], receipt.NotRun);
    }

    [Fact(Timeout = 30000)]
    [Trait("Category", "CrossTick")]
    public void StartedBackgroundAttemptWithBriefNamedClassReconcilesAcrossTicks()
    {
        using var scenario = new Scenario("Verify BriefNamedRegressionTests", "PlannerNamedNeighborTests");
        var roles = new List<AgentRole>();
        var launches = 0;
        var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(
            Path.Combine(scenario.Root, "background-attempts"),
            isProcessAlive: processId => processId == 7103,
            launchOwnedProcess: _ =>
            {
                launches++;
                return new ConductorParallelAcceptanceOwnedProcessLaunchResult(7103);
            },
            acquireStableSlotLease: (_, _) => null);
        var driver = scenario.Driver(
            _ => throw new Xunit.Sdk.XunitException("Background runner must not run inline."),
            roles.Add, coordinator: coordinator);

        driver.AdvanceOnce(scenario.Goal, ConductorAutonomyPolicy.Conservative);
        driver.AdvanceOnce(scenario.Goal, ConductorAutonomyPolicy.Conservative);

        Assert.Empty(roles);
        Assert.Equal(1, launches);
        Assert.Single(scenario.Goal.Timeline.Where(evt =>
            evt.TaskId == scenario.Tester.Id &&
            evt.Kind == ProgressKind.FindingEvidenceRequestRecorded &&
            evt.Message.StartsWith("finding-evidence pre-tester outcome=started;", StringComparison.Ordinal)));
        Assert.Equal(["DeferredAlphaTests", "BriefNamedRegressionTests", "PlannerNamedNeighborTests"],
            PreTesterEvidenceIndexLines.Latest(scenario.Goal, scenario.Tester.Id, CandidateSha)!
                .Selections.Select(selection => selection.Split(':')[1].Trim()).ToArray());
    }

    [Theory]
    [InlineData("pass - ran locally", BaseSha)]
    [InlineData("deferred - DeferredAlphaTests", CandidateSha)]
    public void GuardDeclineDispatchesTesterWithoutNamedEvidence(string tests, string baseSha)
    {
        using var scenario = new Scenario("Verify BriefNamedRegressionTests", "PlannerNamedNeighborTests",
            tests, baseSha);
        var roles = new List<AgentRole>();
        var runs = 0;
        var driver = scenario.Driver(request =>
        {
            runs++;
            return scenario.Green(request);
        }, roles.Add);

        driver.AdvanceOnce(scenario.Goal, ConductorAutonomyPolicy.Conservative);

        Assert.Equal(0, runs);
        Assert.Equal([AgentRole.Tester], roles);
    }

    [Fact(Timeout = 30000)]
    [Trait("Category", "CrossTick")]
    public void GreenReceiptWithBriefNamedClassAnswersLaterTesterRequest()
    {
        using var scenario = new Scenario("Verify BriefNamedRegressionTests");
        var outcomes = new List<FindingEvidenceOutcome>();
        var runs = 0;
        var driver = scenario.Driver(request =>
        {
            runs++;
            return scenario.Green(request);
        }, _ => { }, outcomes: outcomes);
        driver.AdvanceOnce(scenario.Goal, ConductorAutonomyPolicy.Conservative);
        var receipt = PreTesterEvidenceIndexLines.Latest(scenario.Goal, scenario.Tester.Id, CandidateSha);
        Assert.NotNull(receipt);
        Assert.Equal("green", receipt.Outcome);
        Assert.Contains(receipt.Selections, selection =>
            selection.EndsWith(":BriefNamedRegressionTests", StringComparison.Ordinal));
        FailReviewerNeedsWork(scenario.Kernel, scenario.Goal, scenario.Tester, "Need brief-named evidence",
            findings: [EvidenceFindingWithRequest("Need brief-named evidence", classes: ["BriefNamedRegressionTests"])],
            reviewedCommit: CandidateSha);

        driver.AdvanceOnce(scenario.Goal, ConductorAutonomyPolicy.Conservative);

        Assert.Equal(1, runs);
        Assert.Equal("pre-tester-covered", Assert.Single(outcomes).DecisionReason);
    }

    [Fact]
    public void CandidateRedBriefNamedClassRetriesDeveloper()
    {
        using var scenario = new Scenario("Verify BriefNamedRegressionTests");
        var roles = new List<AgentRole>();
        var feedback = new List<string>();
        var driver = scenario.Driver(request =>
        {
            Assert.Contains("BriefNamedRegressionTests", Classes(request));
            return CandidateRedFindingEvidence(request, CandidateSha,
                "BriefNamedRegressionTests.FailsOnCandidate");
        }, roles.Add, feedback.Add);

        driver.AdvanceOnce(scenario.Goal, ConductorAutonomyPolicy.Conservative);

        Assert.Equal([AgentRole.Developer], roles);
        Assert.StartsWith("ACTIONABLE_CANDIDATE_RED", Assert.Single(feedback), StringComparison.Ordinal);
        Assert.Contains("BriefNamedRegressionTests.FailsOnCandidate", feedback[0], StringComparison.Ordinal);
    }

    private static string[] Classes(string request) => request
        .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(selection => selection.Split(':')[1].Trim()).ToArray();

    private sealed class Scenario : IDisposable
    {
        public string Root { get; } = ConductorDriverTests.CreateTempDirectory();
        public AgentOrchestratorKernel Kernel { get; } = new();
        public Goal Goal { get; }
        public TaskSpec Developer { get; }
        public TaskSpec Tester { get; }
        private string Worktree => GoalWorktrees.WorktreePath(Root, Goal.Id);

        public Scenario(string objective, string plannerOutput = "ok",
            string tests = "deferred - DeferredAlphaTests", string baseSha = BaseSha)
        {
            Goal = Kernel.CreateGoal(objective,
            [
                new TaskSpec(TaskId.New(), "Plan", AgentRole.Planner),
                new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer),
                new TaskSpec(TaskId.New(), "Test", AgentRole.Tester),
                new TaskSpec(TaskId.New(), "Review", AgentRole.Reviewer)
            ]);
            Kernel.ActivateGoal(Goal.Id, DefaultAgents());
            var planner = Goal.Tasks.Single(task => task.RequiredRole == AgentRole.Planner);
            Developer = Goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
            Tester = Goal.Tasks.Single(task => task.RequiredRole == AgentRole.Tester);
            DispatchTask(Kernel, Goal, planner);
            Kernel.RecordTaskVerification(Goal.Id, planner.Id, new TaskVerificationRecord(
                "test.exe", "C:\\tmp", 0, plannerOutput, "", DateTimeOffset.UtcNow));
            DispatchTask(Kernel, Goal, Developer, baseCommit: baseSha);
            var output = string.Join(Environment.NewLine,
                "WORKER_RESULT:", "files: tests/DeferredAlphaTests.cs", "commands: none",
                "tests: " + tests, "commit: none", "blockers: none",
                "assigned_scope_complete: true", "model_fit: test/model - adequate - fixture",
                "skills: none", "confidence: high", "END_WORKER_RESULT");
            Kernel.RecordTaskVerification(Goal.Id, Developer.Id, new TaskVerificationRecord(
                "test.exe", "C:\\tmp", 0, output, "", DateTimeOffset.UtcNow,
                WorkerResultPresent: true, HasCommittedChanges: true));
            Directory.CreateDirectory(Worktree);
            File.WriteAllText(Path.Combine(Worktree, ".git"), "gitdir: fixture");
            foreach (var name in new[] { "DeferredAlphaTests", "BriefNamedRegressionTests", "PlannerNamedNeighborTests" })
                Declare(name);
        }

        public void Declare(string name, string projectName = "Mcg.AgentOrchestrator.Infrastructure.Tests")
        {
            var project = Path.Combine(Worktree, "tests", projectName);
            Directory.CreateDirectory(project);
            File.WriteAllText(Path.Combine(project, projectName + ".csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
            File.WriteAllText(Path.Combine(project, name + ".cs"), "public class " + name + " {}");
        }

        public FocusedEvidenceRunResult Green(string request) =>
            ConductorDriverTestsFindingEvidenceReuse.RetainedEvidenceWithExecutedClasses(Root, request, CandidateSha);

        public ConductorDriver Driver(
            Func<string, FocusedEvidenceRunResult> run,
            Action<AgentRole> dispatched,
            Action<string>? feedback = null,
            ConductorParallelAcceptanceAttemptCoordinator? coordinator = null,
            List<FindingEvidenceOutcome>? outcomes = null)
        {
            var driver = MakeDriver(
                getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
                getPreReviewEvidenceContext: _ => NoPreReviewContext(CandidateSha),
                getLandingFileScopes: _ => [],
                focusedEvidenceAttemptCoordinator: coordinator ?? new ConductorParallelAcceptanceAttemptCoordinator(
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
                recordFindingEvidenceOutcome: (goalId, taskId, stableId, outcome, receipt) =>
                {
                    outcomes?.Add(outcome);
                    Kernel.RecordFindingEvidenceOutcome(goalId, taskId, stableId, outcome, receipt);
                },
                executionDirectory: Root);
            driver.OverrideDeveloperCompletionStructuralPreflightForTests(
                _ => new DeveloperCompletionStructuralFindings(false, "clean"), TimeSpan.FromSeconds(5));
            return driver;
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
