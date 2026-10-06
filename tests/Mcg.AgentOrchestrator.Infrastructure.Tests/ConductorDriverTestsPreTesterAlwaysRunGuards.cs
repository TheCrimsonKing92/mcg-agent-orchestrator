using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

using static ConductorDriverTests;

// Parallel-safe: all files and coordinator state belong to this scenario; process launch is seamed.
public sealed class ConductorDriverTestsPreTesterAlwaysRunGuards
{
    private const string CandidateSha = "bbb2222";
    private const string BaseSha = "aaa1111";
    private const string CoreSelection = "Core.Tests:DeclaredCoreTests";
    private const string ChangedCorePath = "tests/Mcg.AgentOrchestrator.Core.Tests/DeclaredCoreTests.cs";
    private static string[] GuardNames => PreTesterAlwaysRunGuardTestClasses.Entries
        .Select(entry => entry.TestClass).ToArray();
    private static string[] GuardSelections => GuardNames.Select(name => "Infrastructure.Tests:" + name).ToArray();

    [Fact]
    public void DeclaredCoreClassPrecedesAllSixInfrastructureGuards()
    {
        using var scenario = new Scenario();
        var roles = new List<AgentRole>();
        var launches = 0;
        var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(
            Path.Combine(scenario.Root, "background-attempts"),
            isProcessAlive: id => id == 7103,
            launchOwnedProcess: _ =>
            {
                launches++;
                return new ConductorParallelAcceptanceOwnedProcessLaunchResult(7103);
            },
            acquireStableSlotLease: (_, _) => null);
        var driver = scenario.Driver(
            _ => throw new Xunit.Sdk.XunitException("Background evidence must not execute inline."),
            roles.Add, coordinator: coordinator);

        driver.AdvanceOnce(scenario.Goal, ConductorAutonomyPolicy.Conservative);

        var started = scenario.Latest();
        Assert.Equal("started", started.Outcome);
        Assert.Equal(new[] { CoreSelection }.Concat(GuardSelections), started.Selections);
        Assert.Equal(10, started.Selections.Count);
        Assert.Empty(started.NotRun);
        Assert.Equal(1, launches);
        Assert.Empty(roles);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ListedFailureOutsideChangesRetriesDeveloperWithFailureDetail(bool hasChangedPaths)
    {
        using var scenario = new Scenario();
        var identity = GuardNames[0] + ".FailsOnCandidate";
        var trxPath = Path.Combine(scenario.Root, "guard-failure.trx");
        ConductorDriverTestsActionableRedFailureDetail.WriteFailureTrx(trxPath, identity);
        var roles = new List<AgentRole>();
        var feedback = new List<string>();
        var runs = 0;
        var driver = scenario.Driver(request =>
        {
            runs++;
            return CandidateOnlyRed(request, [identity], trxPath);
        }, roles.Add, feedback.Add, changedPaths: hasChangedPaths ? [ChangedCorePath] : []);

        driver.AdvanceOnce(scenario.Goal, ConductorAutonomyPolicy.Conservative);

        Assert.Equal(1, runs);
        Assert.Equal("actionable-red", scenario.Latest().Outcome);
        Assert.Equal([identity], scenario.Latest().FailingTests);
        Assert.Equal([AgentRole.Developer], roles);
        var message = Assert.Single(feedback);
        Assert.StartsWith("ACTIONABLE_CANDIDATE_RED", message, StringComparison.Ordinal);
        Assert.Contains(identity, message, StringComparison.Ordinal);
        Assert.Contains("fatal: synthetic too big", message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnlistedOutsideFailureKeepsEvenMixedFailuresPlainRed(bool alsoListedFailure)
    {
        using var scenario = new Scenario(tests: "deferred - DeclaredCoreTests, UnlistedOutsideTests");
        scenario.Declare("UnlistedOutsideTests");
        var failures = new List<string> { "UnlistedOutsideTests.FailsOnCandidate" };
        if (alsoListedFailure) failures.Add(GuardNames[0] + ".FailsOnCandidate");
        var roles = new List<AgentRole>();
        var feedback = new List<string>();
        var runs = 0;
        var driver = scenario.Driver(request =>
        {
            runs++;
            return CandidateOnlyRed(request, failures);
        }, roles.Add, feedback.Add);

        driver.AdvanceOnce(scenario.Goal, ConductorAutonomyPolicy.Conservative);

        Assert.Equal(1, runs);
        Assert.Equal("red", scenario.Latest().Outcome);
        Assert.Equal(failures, scenario.Latest().FailingTests);
        Assert.Equal([AgentRole.Tester], roles);
        Assert.Empty(feedback);
    }

    [Fact]
    public void ListedAndChangedSourceFailuresAreBothActionable()
    {
        using var scenario = new Scenario();
        var failures = new[] { GuardNames[0] + ".FailsOnCandidate", "DeclaredCoreTests.FailsOnCandidate" };
        var roles = new List<AgentRole>();
        var feedback = new List<string>();
        var driver = scenario.Driver(request => CandidateOnlyRed(request, failures), roles.Add, feedback.Add);

        driver.AdvanceOnce(scenario.Goal, ConductorAutonomyPolicy.Conservative);

        Assert.Equal("actionable-red", scenario.Latest().Outcome);
        Assert.Equal([AgentRole.Developer], roles);
        var message = Assert.Single(feedback);
        Assert.All(failures, identity => Assert.Contains(identity, message, StringComparison.Ordinal));
    }

    [Fact]
    public void MissingGuardIsOmittedWithoutNotRunAndEvidenceStillStarts()
    {
        var missing = GuardNames[2];
        using var scenario = new Scenario(missingGuard: missing);
        var requests = new List<string>();
        var roles = new List<AgentRole>();
        var driver = scenario.Driver(request =>
        {
            requests.Add(request);
            return scenario.Green(request);
        }, roles.Add);

        driver.AdvanceOnce(scenario.Goal, ConductorAutonomyPolicy.Conservative);

        var expected = new[] { CoreSelection }.Concat(GuardSelections.Where(selection =>
            selection != "Infrastructure.Tests:" + missing)).ToArray();
        Assert.Equal(expected, Selections(Assert.Single(requests)));
        var receipt = scenario.Latest();
        Assert.Equal("green", receipt.Outcome);
        Assert.Equal(expected, receipt.Selections);
        Assert.Empty(receipt.NotRun);
        Assert.DoesNotContain("Infrastructure.Tests:" + missing, receipt.Selections);
        Assert.Equal([AgentRole.Tester], roles);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LegacyStartedSelectionRemainsAuthoritativeOnReconciliation(bool includesOneGuard)
    {
        using var scenario = new Scenario();
        var startedSelections = includesOneGuard ? new[] { CoreSelection, GuardSelections[0] } : [CoreSelection];
        scenario.RecordStarted(startedSelections);
        var requests = new List<string>();
        var roles = new List<AgentRole>();
        var driver = scenario.Driver(request =>
        {
            requests.Add(request);
            return scenario.Green(request);
        }, roles.Add);

        var exception = Record.Exception(() => driver.AdvanceOnce(scenario.Goal, ConductorAutonomyPolicy.Conservative));

        Assert.Null(exception);
        Assert.Equal(startedSelections, Selections(Assert.Single(requests)));
        var receipt = scenario.Latest();
        Assert.Equal("green", receipt.Outcome);
        Assert.Equal(startedSelections, receipt.Selections);
        Assert.Empty(receipt.NotRun);
        Assert.Equal([AgentRole.Tester], roles);
    }

    [Theory]
    [InlineData("selection")]
    [InlineData("order")]
    [InlineData("not-run")]
    public void StartedReceiptStillRejectsUnrelatedDrift(string drift)
    {
        using var scenario = new Scenario();
        var selections = drift switch
        {
            "selection" => new[] { "Core.Tests:OtherCoreTests" },
            "order" => new[] { GuardSelections[0], CoreSelection },
            _ => new[] { CoreSelection }
        };
        scenario.RecordStarted(selections, drift == "not-run" ? ["MissingTests"] : []);
        var runs = 0;
        var driver = scenario.Driver(request =>
        {
            runs++;
            return scenario.Green(request);
        }, _ => { });

        var exception = Assert.Throws<InvalidDataException>(() =>
            driver.AdvanceOnce(scenario.Goal, ConductorAutonomyPolicy.Conservative));

        Assert.Equal("Started pre-Tester evidence selection changed before reconciliation.", exception.Message);
        Assert.Equal(0, runs);
        Assert.Equal("started", scenario.Latest().Outcome);
    }

    [Fact]
    public void AlreadyDeclaredGuardKeepsItsEarlierPositionWithoutDuplicate()
    {
        using var scenario = new Scenario(tests: "deferred - " + GuardNames[3] + ", DeclaredCoreTests");
        var requests = new List<string>();
        var driver = scenario.Driver(request =>
        {
            requests.Add(request);
            return scenario.Green(request);
        }, _ => { });

        driver.AdvanceOnce(scenario.Goal, ConductorAutonomyPolicy.Conservative);

        Assert.Equal(new[] { GuardSelections[3], CoreSelection }
            .Concat(GuardSelections.Where(selection => selection != GuardSelections[3])),
            Selections(Assert.Single(requests)));
        Assert.Equal("green", scenario.Latest().Outcome);
    }

    [Fact]
    public void SelectedGuardWithoutContentBoundTrxPreventsGreen()
    {
        using var scenario = new Scenario();
        var runs = 0;
        var driver = scenario.Driver(request =>
        {
            runs++;
            return ConductorDriverTestsFindingEvidenceReuse.RetainedEvidenceWithExecutedClasses(
                scenario.Root, request, CandidateSha, ["DeclaredCoreTests"]);
        }, _ => { });

        driver.AdvanceOnce(scenario.Goal, ConductorAutonomyPolicy.Conservative);

        Assert.Equal(1, runs);
        Assert.Equal("unusable", scenario.Latest().Outcome);
        Assert.Equal(new[] { CoreSelection }.Concat(GuardSelections), scenario.Latest().Selections);
    }

    private static string[] Selections(string request) =>
        request.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static FocusedEvidenceRunResult CandidateOnlyRed(
        string request, IReadOnlyList<string> failures, string? trxPath = null)
    {
        var red = CandidateRedFindingEvidence(request, CandidateSha, failures[0]);
        var candidate = red.Arms!.Single(arm => arm.Arm == FindingEvidenceArm.Candidate);
        var check = candidate.Checks.Single() with
        {
            FailingTestIdentities = failures,
            TestResultPaths = trxPath is null ? [] : [trxPath]
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
        private string Worktree => GoalWorktrees.WorktreePath(Root, Goal.Id);

        public Scenario(string? missingGuard = null, string tests = "deferred - DeclaredCoreTests")
        {
            (Kernel, Goal) = SoftwareGoal("Pre-Tester focused source evidence");
            Developer = Goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
            Tester = Goal.Tasks.Single(task => task.RequiredRole == AgentRole.Tester);
            foreach (var task in Goal.Tasks.TakeWhile(task => task.Id != Developer.Id))
                PassVerification(Kernel, Goal, task);
            DispatchTask(Kernel, Goal, Developer, baseCommit: BaseSha);
            var output = string.Join(Environment.NewLine,
                "WORKER_RESULT:", "files: " + ChangedCorePath, "commands: none",
                "tests: " + tests, "commit: none", "blockers: none",
                "assigned_scope_complete: true", "model_fit: test/model - adequate - fixture",
                "skills: none", "confidence: high", "END_WORKER_RESULT");
            Kernel.RecordTaskVerification(Goal.Id, Developer.Id, new TaskVerificationRecord(
                "test.exe", "C:\\tmp", 0, output, "", DateTimeOffset.UtcNow,
                WorkerResultPresent: true, HasCommittedChanges: true));
            Directory.CreateDirectory(Worktree);
            File.WriteAllText(Path.Combine(Worktree, ".git"), "gitdir: fixture");
            Declare("DeclaredCoreTests", "Mcg.AgentOrchestrator.Core.Tests");
            foreach (var name in GuardNames.Where(name => name != missingGuard)) Declare(name);
        }

        public void Declare(string name, string projectName = "Mcg.AgentOrchestrator.Infrastructure.Tests")
        {
            var project = Path.Combine(Worktree, "tests", projectName);
            Directory.CreateDirectory(project);
            File.WriteAllText(Path.Combine(project, projectName + ".csproj"), "<Project />");
            File.WriteAllText(Path.Combine(project, name + ".cs"), "public class " + name + " {}");
        }

        public PreTesterEvidenceEntry Latest() =>
            Assert.IsType<PreTesterEvidenceEntry>(PreTesterEvidenceIndexLines.Latest(Goal, Tester.Id, CandidateSha));

        public void RecordStarted(IReadOnlyList<string> selections, IReadOnlyList<string>? notRun = null) =>
            Kernel.RecordFindingEvidenceRequest(Goal.Id, Tester.Id,
                PreTesterEvidenceIndexLines.FormatMarker(new PreTesterEvidenceEntry(
                    "started", CandidateSha, "pre-change-receipt", selections, notRun ?? [], null, [])));

        public FocusedEvidenceRunResult Green(string request) =>
            ConductorDriverTestsFindingEvidenceReuse.RetainedEvidenceWithExecutedClasses(Root, request, CandidateSha);

        public ConductorDriver Driver(
            Func<string, FocusedEvidenceRunResult> run,
            Action<AgentRole> dispatched,
            Action<string>? feedback = null,
            ConductorParallelAcceptanceAttemptCoordinator? coordinator = null,
            IReadOnlyList<string>? changedPaths = null)
        {
            var driver = MakeDriver(
                getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
                getPreReviewEvidenceContext: _ => NoPreReviewContext(CandidateSha),
                getLandingFileScopes: _ => changedPaths ?? [ChangedCorePath],
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
                executionDirectory: Root);
            driver.OverrideDeveloperCompletionStructuralPreflightForTests(
                _ => new DeveloperCompletionStructuralFindings(false, "clean"), TimeSpan.FromSeconds(5));
            return driver;
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
