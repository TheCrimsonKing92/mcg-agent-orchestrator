using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

using static ConductorDriverTests;

[Xunit.Collection("IsolatedProcessSpawning")]
public sealed class ConductorDriverTestsPreTesterDeferredEvidenceExitNotes
{
    private const string CandidateSha = "bbb2222";
    private const string BaseSha = "aaa1111";
    private const string Prefix = "pre-tester-deferred-evidence skipped: ";

    [Theory]
    [InlineData("deferred naming DeferredAlphaTests")]
    [InlineData("deferred — DeferredAlphaTests")]
    public void NewlyParsedDeclarationStartsFocusedRunBeforeTester(string testsField)
    {
        using var scenario = new Scenario(testsField, "Mcg.AgentOrchestrator.Infrastructure.Tests",
            "DeferredAlphaTests");
        var order = new List<string>();
        var requests = new List<string>();
        var driver = scenario.Driver(request =>
        {
            order.Add("focused");
            requests.Add(request);
            return ConductorDriverTestsFindingEvidenceReuse.RetainedEvidenceWithExecutedClasses(
                scenario.Root, request, CandidateSha);
        }, role => order.Add(role.ToString()));

        driver.AdvanceOnce(scenario.Goal, ConductorAutonomyPolicy.Conservative);

        Assert.Equal(["focused", "Tester"], order);
        Assert.Contains("DeferredAlphaTests", Assert.Single(requests), StringComparison.Ordinal);
        Assert.Empty(scenario.ExitNotes());
    }

    [Fact]
    public void PriorCompletedOutcomeRecordsOneNoteAndDoesNotRun()
    {
        using var scenario = new Scenario("deferred naming DeferredAlphaTests",
            "Mcg.AgentOrchestrator.Infrastructure.Tests", "DeferredAlphaTests");
        scenario.Kernel.RecordFindingEvidenceRun(scenario.Goal.Id, scenario.Tester.Id,
            PreTesterEvidenceIndexLines.FormatMarker(new PreTesterEvidenceEntry(
                "green", CandidateSha, "prior-receipt",
                ["Mcg.AgentOrchestrator.Infrastructure.Tests:DeferredAlphaTests"], [], null, [])));

        scenario.AssertSkipped("exit=prior-outcome-not-started prior=green " +
                               "parsed=DeferredAlphaTests not_run=DeferredAlphaTests");
    }

    [Fact]
    public void NoResolvedClassRecordsOneNoteAndDoesNotRun()
    {
        using var scenario = new Scenario("deferred naming MissingClass");

        scenario.AssertSkipped("exit=no-resolved-classes prior=<none> " +
                               "parsed=MissingClass not_run=MissingClass");
    }

    [Fact]
    public void NoNormalizedClassRecordsOneNoteAndDoesNotRun()
    {
        using var scenario = new Scenario("deferred naming DeferredAlphaTests",
            "Unconfigured.Tests", "DeferredAlphaTests");

        scenario.AssertSkipped("exit=no-normalized-classes prior=<none> " +
                               "parsed=DeferredAlphaTests not_run=DeferredAlphaTests");
    }

    private sealed class Scenario : IDisposable
    {
        public string Root { get; } = ConductorDriverTests.CreateTempDirectory();
        public AgentOrchestratorKernel Kernel { get; }
        public Goal Goal { get; }
        public TaskSpec Developer { get; }
        public TaskSpec Tester { get; }

        public Scenario(string testsField, string? projectName = null, string? className = null)
        {
            (Kernel, Goal) = SoftwareGoal("Pre-Tester deferred evidence exit notes");
            Developer = Goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
            Tester = Goal.Tasks.Single(task => task.RequiredRole == AgentRole.Tester);
            foreach (var task in Goal.Tasks.TakeWhile(task => task.Id != Developer.Id))
                PassVerification(Kernel, Goal, task);
            DispatchTask(Kernel, Goal, Developer, baseCommit: BaseSha);
            var output = string.Join(Environment.NewLine,
                "WORKER_RESULT:", "files: tests/DeferredAlphaTests.cs", "commands: none",
                "tests: " + testsField, "commit: none", "blockers: none",
                "assigned_scope_complete: true", "model_fit: test/model - adequate - fixture",
                "skills: none", "confidence: high", "END_WORKER_RESULT");
            Kernel.RecordTaskVerification(Goal.Id, Developer.Id, new TaskVerificationRecord(
                "test.exe", "C:\\tmp", 0, output, "", DateTimeOffset.UtcNow,
                WorkerResultPresent: true, HasCommittedChanges: true));
            var worktree = GoalWorktrees.WorktreePath(Root, Goal.Id);
            Directory.CreateDirectory(worktree);
            File.WriteAllText(Path.Combine(worktree, ".git"), "gitdir: fixture");
            if (projectName is null || className is null) return;
            var project = Path.Combine(worktree, "tests", projectName);
            Directory.CreateDirectory(project);
            File.WriteAllText(Path.Combine(project, projectName + ".csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\" />");
            File.WriteAllText(Path.Combine(project, className + ".cs"),
                "public class " + className + " {}");
        }

        public IReadOnlyList<string> ExitNotes() => Goal.Timeline
            .Where(evt => evt.TaskId == Developer.Id && evt.Kind == ProgressKind.TaskNote &&
                          evt.Message.StartsWith(Prefix, StringComparison.Ordinal))
            .Select(evt => evt.Message).ToArray();

        public void AssertSkipped(string expected)
        {
            var runs = 0;
            var roles = new List<AgentRole>();
            var driver = Driver(_ =>
            {
                runs++;
                throw new Xunit.Sdk.XunitException("Skipped pre-Tester evidence must not run.");
            }, roles.Add);

            driver.AdvanceOnce(Goal, ConductorAutonomyPolicy.Conservative);

            Assert.Equal(Prefix + expected, Assert.Single(ExitNotes()));
            Assert.Equal(0, runs);
            Assert.Equal([AgentRole.Tester], roles);
        }

        public ConductorDriver Driver(Func<string, FocusedEvidenceRunResult> run,
            Action<AgentRole> dispatched)
        {
            var driver = MakeDriver(
                getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
                getPreReviewEvidenceContext: _ => NoPreReviewContext(CandidateSha),
                focusedEvidenceAttemptCoordinator: new ConductorParallelAcceptanceAttemptCoordinator(
                    Path.Combine(Root, "attempts"), runInline: true,
                    acquireStableSlotLease: (_, _) => null),
                runFocusedEvidence: (_, request) => run(request),
                dispatchAndStart: goal =>
                {
                    dispatched(goal.Tasks.First(task => task.Status == WorkTaskStatus.Assigned).RequiredRole);
                    return DispatchStartOutcome.Started();
                },
                recordTaskNote: (goalId, taskId, message) =>
                    Kernel.RecordTaskNote(goalId, taskId, message),
                recordFindingEvidenceRequest: (goalId, taskId, message) =>
                    Kernel.RecordFindingEvidenceRequest(goalId, taskId, message),
                recordFindingEvidenceRun: (goalId, taskId, message) =>
                    Kernel.RecordFindingEvidenceRun(goalId, taskId, message),
                executionDirectory: Root);
            driver.OverrideDeveloperCompletionStructuralPreflightForTests(
                _ => new DeveloperCompletionStructuralFindings(false, "clean"),
                TimeSpan.FromSeconds(5));
            return driver;
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
