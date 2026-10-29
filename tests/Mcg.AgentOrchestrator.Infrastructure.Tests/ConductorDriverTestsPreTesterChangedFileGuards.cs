using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

using static ConductorDriverTests;

// Parallel-safe: files, kernel, and coordinators are scenario-owned; process launch is seamed.
public sealed class ConductorDriverTestsPreTesterChangedFileGuards
{
    private const string CandidateSha = "bbb2222";
    private const string ChangedPath = "src/X/Y.cs";
    private const string CoreSelection = "Core.Tests:DeclaredCoreTests";
    private const string GuardClass = "ObservedSourceTests";
    private const string GuardSelection = "Infrastructure.Tests:" + GuardClass;
    private const string FailureIdentity = GuardClass + ".ReadsChangedSource";

    [Fact]
    public void SelectedGuardRed_RetriesDeveloperWithDetailBeforeTesterOrGate()
    {
        using var scenario = new Scenario();
        var trx = Path.Combine(scenario.Root, "guard-failure.trx");
        ConductorDriverTestsActionableRedFailureDetail.WriteFailureTrx(trx, FailureIdentity);
        var requests = new List<string>();
        var driver = scenario.Driver(request =>
        {
            requests.Add(request);
            return CandidateOnlyRed(request, [FailureIdentity], trx);
        });

        driver.AdvanceOnce(scenario.Goal, ConductorAutonomyPolicy.Conservative);

        Assert.Equal([CoreSelection, GuardSelection], Selections(Assert.Single(requests)));
        Assert.Equal("actionable-red", scenario.Latest().Outcome);
        Assert.Equal([FailureIdentity], scenario.Latest().FailingTests);
        var feedback = Assert.Single(scenario.Feedback);
        Assert.StartsWith("ACTIONABLE_CANDIDATE_RED", feedback, StringComparison.Ordinal);
        Assert.Contains(FailureIdentity, feedback, StringComparison.Ordinal);
        Assert.Contains("fatal: synthetic too big", feedback, StringComparison.Ordinal);
        Assert.Equal([AgentRole.Developer], scenario.Roles);
        Assert.DoesNotContain(AgentRole.Tester, scenario.Roles);
        Assert.Equal(0, scenario.GateRuns);
    }

    [Fact]
    public void MixedUnrelatedFailure_RemainsPlainRed()
    {
        using var scenario = new Scenario(tests: "deferred - DeclaredCoreTests, OutsideTests");
        scenario.Declare("OutsideTests");
        var driver = scenario.Driver(request => CandidateOnlyRed(request,
            [FailureIdentity, "OutsideTests.Fails"]));

        driver.AdvanceOnce(scenario.Goal, ConductorAutonomyPolicy.Conservative);

        Assert.Equal("red", scenario.Latest().Outcome);
        Assert.Empty(scenario.Feedback);
        Assert.Equal([AgentRole.Tester], scenario.Roles);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StartedReceipt_ReconcilesItsOriginalGuardSelection(bool includesGuard)
    {
        using var scenario = new Scenario();
        string[] original = includesGuard ? [CoreSelection, GuardSelection] : [CoreSelection];
        scenario.RecordStarted(original);
        var requests = new List<string>();
        var driver = scenario.Driver(request =>
        {
            requests.Add(request);
            return scenario.Green(request);
        });

        var error = Record.Exception(() => driver.AdvanceOnce(scenario.Goal, ConductorAutonomyPolicy.Conservative));

        Assert.Null(error);
        Assert.Equal(original, Selections(Assert.Single(requests)));
        Assert.Equal(original, scenario.Latest().Selections);
        Assert.Equal("green", scenario.Latest().Outcome);
        Assert.Equal([AgentRole.Tester], scenario.Roles);
    }

    [Fact]
    public void NewBackgroundAttempt_RecordsTheSelectedGuardBeforeDispatch()
    {
        using var scenario = new Scenario();
        var launches = 0;
        var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(
            Path.Combine(scenario.Root, "background-attempts"), TrunkBranchName.Default,
            isProcessAlive: pid => pid == 7103,
            launchOwnedProcess: _ =>
            {
                launches++;
                return new ConductorParallelAcceptanceOwnedProcessLaunchResult(7103);
            }, acquireStableSlotLease: (_, _) => null);
        var driver = scenario.Driver(_ => throw new Xunit.Sdk.XunitException("Unexpected inline evidence."), coordinator);

        driver.AdvanceOnce(scenario.Goal, ConductorAutonomyPolicy.Conservative);

        Assert.Equal("started", scenario.Latest().Outcome);
        Assert.Equal([CoreSelection, GuardSelection], scenario.Latest().Selections);
        Assert.Equal(1, launches);
        Assert.Empty(scenario.Roles);
        Assert.Equal(0, scenario.GateRuns);
    }

    [Fact]
    public void AlreadyDeclaredGuard_IsExecutedOnce()
    {
        using var scenario = new Scenario(tests: "deferred - DeclaredCoreTests, " + GuardClass);
        var requests = new List<string>();
        var driver = scenario.Driver(request =>
        {
            requests.Add(request);
            return scenario.Green(request);
        });

        driver.AdvanceOnce(scenario.Goal, ConductorAutonomyPolicy.Conservative);

        Assert.Equal([CoreSelection, GuardSelection], Selections(Assert.Single(requests)));
        Assert.Equal("green", scenario.Latest().Outcome);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnresolvedGuard_ReportsNotRunWithoutChangingLegacyIdentity(bool started)
    {
        using var scenario = new Scenario();
        File.Move(Path.Combine(scenario.Worktree, "tests", "Mcg.AgentOrchestrator.Infrastructure.Tests", GuardClass + ".cs"),
            Path.Combine(scenario.Worktree, "tests", "Mcg.AgentOrchestrator.Infrastructure.Tests", "Different.cs"));
        if (started) scenario.RecordStarted([CoreSelection]);
        var driver = scenario.Driver(scenario.Green);

        driver.AdvanceOnce(scenario.Goal, ConductorAutonomyPolicy.Conservative);

        Assert.Equal([CoreSelection], scenario.Latest().Selections);
        Assert.Equal(started ? [] : new[] { GuardClass }, scenario.Latest().NotRun);
        Assert.Equal("green", scenario.Latest().Outcome);
    }

    private static string[] Selections(string request) =>
        request.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static FocusedEvidenceRunResult CandidateOnlyRed(
        string request, IReadOnlyList<string> failures, string? trx = null)
    {
        var red = CandidateRedFindingEvidence(request, CandidateSha, failures[0]);
        var candidate = red.Arms!.Single(arm => arm.Arm == FindingEvidenceArm.Candidate);
        var check = candidate.Checks.Single() with
        {
            FailingTestIdentities = failures,
            TestResultPaths = trx is null ? [] : [trx]
        };
        return red with { Checks = [check], Arms = [candidate with { Checks = [check] }] };
    }

    private sealed class Scenario : IDisposable
    {
        public string Root { get; } = ConductorDriverTests.CreateTempDirectory();
        public AgentOrchestratorKernel Kernel { get; }
        public Goal Goal { get; }
        public TaskSpec Developer { get; }
        public TaskSpec Tester { get; }
        public string Worktree => GoalWorktrees.WorktreePath(Root, Goal.Id);
        public List<AgentRole> Roles { get; } = [];
        public List<string> Feedback { get; } = [];
        public int GateRuns { get; private set; }

        public Scenario(string tests = "deferred - DeclaredCoreTests")
        {
            (Kernel, Goal) = SoftwareGoal("Pre-Tester changed file evidence");
            Developer = Goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
            Tester = Goal.Tasks.Single(task => task.RequiredRole == AgentRole.Tester);
            foreach (var task in Goal.Tasks.TakeWhile(task => task.Id != Developer.Id))
                PassVerification(Kernel, Goal, task);
            DispatchTask(Kernel, Goal, Developer, baseCommit: "aaa1111");
            var output = string.Join(Environment.NewLine,
                "WORKER_RESULT:", "files: " + ChangedPath, "commands: none",
                "tests: " + tests, "commit: none", "blockers: none",
                "assigned_scope_complete: true", "model_fit: test/model - adequate - fixture",
                "skills: none", "confidence: high", "END_WORKER_RESULT");
            Kernel.RecordTaskVerification(Goal.Id, Developer.Id, new TaskVerificationRecord(
                "test.exe", "C:\\tmp", 0, output, "", DateTimeOffset.UtcNow,
                WorkerResultPresent: true, HasCommittedChanges: true));
            Directory.CreateDirectory(Worktree);
            File.WriteAllText(Path.Combine(Worktree, ".git"), "gitdir: fixture");
            Declare("DeclaredCoreTests", "Mcg.AgentOrchestrator.Core.Tests");
            Declare(GuardClass, methods: """
                [Xunit.Fact] public void ReadsChangedSource() {
                    var path = Path.Combine(FindRoot(), "src", "X", "Y.cs");
                    var source = File.ReadAllText(path);
                }
                """);
        }

        public void Declare(string name, string projectName = "Mcg.AgentOrchestrator.Infrastructure.Tests", string methods = "")
        {
            var project = Path.Combine(Worktree, "tests", projectName);
            Directory.CreateDirectory(project);
            File.WriteAllText(Path.Combine(project, projectName + ".csproj"), "<Project />");
            File.WriteAllText(Path.Combine(project, name + ".cs"), "public class " + name + " { " + methods + " }");
        }

        public PreTesterEvidenceEntry Latest() =>
            Assert.IsType<PreTesterEvidenceEntry>(PreTesterEvidenceIndexLines.Latest(Goal, Tester.Id, CandidateSha));

        public void RecordStarted(IReadOnlyList<string> selections) => Kernel.RecordFindingEvidenceRequest(Goal.Id, Tester.Id,
            PreTesterEvidenceIndexLines.FormatMarker(new PreTesterEvidenceEntry(
                "started", CandidateSha, "pre-change-receipt", selections, [], null, [])));

        public FocusedEvidenceRunResult Green(string request) =>
            ConductorDriverTestsFindingEvidenceReuse.RetainedEvidenceWithExecutedClasses(Root, request, CandidateSha);

        public ConductorDriver Driver(Func<string, FocusedEvidenceRunResult> run,
            ConductorParallelAcceptanceAttemptCoordinator? coordinator = null)
        {
            var driver = MakeDriver(
                getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
                getPreReviewEvidenceContext: _ => NoPreReviewContext(CandidateSha),
                getLandingFileScopes: _ => [ChangedPath],
                focusedEvidenceAttemptCoordinator: coordinator ?? new ConductorParallelAcceptanceAttemptCoordinator(
                    Path.Combine(Root, "attempts"), TrunkBranchName.Default, runInline: true, acquireStableSlotLease: (_, _) => null),
                runFocusedEvidence: (_, request) => run(request),
                runAcceptance: _ => { GateRuns++; return true; },
                runAcceptanceVerificationWithLease: (_, _, _, _) =>
                {
                    GateRuns++;
                    return AcceptanceVerificationSummary.PassedWithNoUnmetCriteria;
                },
                dispatchAndStart: goal =>
                {
                    Roles.Add(goal.Tasks.First(task => task.Status == WorkTaskStatus.Assigned).RequiredRole);
                    return DispatchStartOutcome.Started();
                },
                retryTaskWithCause: (goalId, taskId, message, round, cause) =>
                {
                    Feedback.Add(message);
                    return Kernel.RetryTask(goalId, taskId, message, retryRoundKind: round, retryCause: cause);
                },
                recordFindingEvidenceRequest: (goalId, taskId, message) => Kernel.RecordFindingEvidenceRequest(goalId, taskId, message),
                recordFindingEvidenceRun: (goalId, taskId, message) => Kernel.RecordFindingEvidenceRun(goalId, taskId, message),
                executionDirectory: Root);
            driver.OverrideDeveloperCompletionStructuralPreflightForTests(
                _ => new DeveloperCompletionStructuralFindings(false, "clean"), TimeSpan.FromSeconds(5));
            return driver;
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
