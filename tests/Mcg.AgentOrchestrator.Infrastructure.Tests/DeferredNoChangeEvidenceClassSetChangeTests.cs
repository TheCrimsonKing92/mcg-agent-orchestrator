using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

using static ConductorDriverTests;

[Xunit.Collection("IsolatedProcessSpawning")]
public sealed class DeferredNoChangeEvidenceClassSetChangeTests
{
    private static readonly string Candidate = new('b', 40);
    private static readonly string[] Original = ["DeferredAlphaTests", "DeferredBetaTests"];
    private static readonly string[] Expanded = ["DeferredAlphaTests", "DeferredBetaTests", "DeferredDeltaTests"];

    [Xunit.Fact(Timeout = 30000)]
    [Xunit.Trait("Category", "CrossTick")]
    public void ClassSetChangeStartsOneRunThenSkips()
    {
        using var scenario = new Scenario();
        var driver = scenario.Driver();
        scenario.CompleteDeveloper(Original);
        driver.AdvanceOnce(scenario.Goal, ConductorAutonomyPolicy.Conservative);
        Xunit.Assert.Single(scenario.Requests);
        Xunit.Assert.Equal("green", scenario.Latest(Original)!.Outcome);

        scenario.CompleteDeveloper(Expanded);
        scenario.Dispatches.Clear();
        var result = driver.AdvanceOnce(scenario.Goal, ConductorAutonomyPolicy.Conservative);

        Xunit.Assert.False(result.WasEscalated);
        Xunit.Assert.Equal(2, scenario.Requests.Count);
        Xunit.Assert.Equal(Selections(Expanded), scenario.Requests[1].Split("; "));
        var receipt = scenario.Latest(Expanded)!;
        Xunit.Assert.Equal("green", receipt.Outcome);
        Xunit.Assert.Equal(Expanded, receipt.Declared);
        Xunit.Assert.Equal(Selections(Expanded), receipt.Selections);
        Xunit.Assert.Single(scenario.Goal.Timeline.Where(evt =>
            evt.Kind == ProgressKind.FindingEvidenceRequestRecorded &&
            evt.Message.Contains("declared=" + string.Join(',', Expanded), StringComparison.Ordinal)));
        Xunit.Assert.False(ConductorDriver.HasPendingDeferredNoChangeEvidence(scenario.Goal));
        driver.AdvanceOnce(scenario.Goal, ConductorAutonomyPolicy.Conservative);
        Xunit.Assert.Equal(2, scenario.Requests.Count);
        Xunit.Assert.Contains(AgentRole.Tester, scenario.Dispatches);

        scenario.CompleteDeveloper(Expanded.Reverse().ToArray());
        result = driver.AdvanceOnce(scenario.Goal, ConductorAutonomyPolicy.Conservative);
        Xunit.Assert.False(result.WasEscalated);
        Xunit.Assert.Equal(2, scenario.Requests.Count);
    }

    [Xunit.Theory]
    [Xunit.InlineData("green", false)]
    [Xunit.InlineData("red", false)]
    [Xunit.InlineData("unusable", false)]
    [Xunit.InlineData("started", false)]
    [Xunit.InlineData("green", true)]
    [Xunit.InlineData("red", true)]
    [Xunit.InlineData("unusable", true)]
    public void OtherDeclarationStartsOneRun(string outcome, bool legacy)
    {
        using var scenario = new Scenario();
        scenario.CompleteDeveloper(Original);
        scenario.Record(outcome, Original, legacy: legacy);
        scenario.CompleteDeveloper(Expanded);

        var result = scenario.Driver().AdvanceOnce(scenario.Goal, ConductorAutonomyPolicy.Conservative);

        Xunit.Assert.False(result.WasEscalated);
        Xunit.Assert.Equal(Selections(Expanded), Xunit.Assert.Single(scenario.Requests).Split("; "));
        Xunit.Assert.Equal("green", scenario.Latest(Expanded)!.Outcome);
    }

    [Xunit.Fact(Timeout = 30000)]
    [Xunit.Trait("Category", "CrossTick")]
    public void ChangedDeclarationRedEscalatesOnRepeat()
    {
        using var scenario = new Scenario();
        scenario.CompleteDeveloper(Original);
        scenario.Record("green", Original);
        scenario.CompleteDeveloper(Expanded);
        var driver = scenario.Driver(request =>
            CandidateRedFindingEvidence(request, Candidate, "DeferredDeltaTests.FailsOnCandidate"));
        driver.AdvanceOnce(scenario.Goal, ConductorAutonomyPolicy.Conservative);
        Xunit.Assert.Single(scenario.Requests);
        Xunit.Assert.Equal("red", scenario.Latest(Expanded)!.Outcome);
        Xunit.Assert.Contains("DeferredDeltaTests.FailsOnCandidate",
            Xunit.Assert.Single(scenario.Feedback), StringComparison.Ordinal);

        scenario.CompleteDeveloper(Expanded);
        var result = driver.AdvanceOnce(scenario.Goal, ConductorAutonomyPolicy.Conservative);

        Xunit.Assert.True(result.WasEscalated);
        Xunit.Assert.Contains("DEFERRED_NO_CHANGE_REPEAT_RED", result.Outcome.ToString(), StringComparison.Ordinal);
        Xunit.Assert.Single(scenario.Requests);
    }

    [Xunit.Theory]
    [Xunit.InlineData("red", false)]
    [Xunit.InlineData("unusable", false)]
    [Xunit.InlineData("red", true)]
    [Xunit.InlineData("unusable", true)]
    public void MatchingFaultStillEscalates(string outcome, bool legacy)
    {
        using var scenario = new Scenario();
        scenario.CompleteDeveloper(Expanded);
        scenario.Record(outcome, Expanded, legacy: legacy);

        var result = scenario.Driver().AdvanceOnce(scenario.Goal, ConductorAutonomyPolicy.Conservative);

        Xunit.Assert.True(result.WasEscalated);
        Xunit.Assert.Contains("DEFERRED_NO_CHANGE_REPEAT_RED", result.Outcome.ToString(), StringComparison.Ordinal);
        Xunit.Assert.Empty(scenario.Requests);
    }

    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public void StartedSelectionDriftEscalatesWithoutThrowing(bool notRunChanged)
    {
        using var scenario = new Scenario();
        scenario.CompleteDeveloper(Expanded);
        scenario.Record("started", Expanded,
            selections: Selections(notRunChanged ? Expanded : Original),
            notRun: notRunChanged ? ["MissingTests"] : []);
        var driver = scenario.Driver();
        ConductorAdvanceResult? result = null;

        var exception = Xunit.Record.Exception(() =>
            result = driver.AdvanceOnce(scenario.Goal, ConductorAutonomyPolicy.Conservative));

        Xunit.Assert.Null(exception);
        Xunit.Assert.NotNull(result);
        Xunit.Assert.True(result.WasEscalated);
        Xunit.Assert.Contains("selection changed during its candidate evidence run", result.Outcome.ToString(),
            StringComparison.Ordinal);
        Xunit.Assert.Empty(scenario.Requests);
        Xunit.Assert.Empty(scenario.Feedback);
        Xunit.Assert.Equal("started", scenario.Latest(Expanded)!.Outcome);
    }

    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public void MatchingGreenSkipsRun(bool legacy)
    {
        using var scenario = new Scenario();
        scenario.CompleteDeveloper(Expanded);
        scenario.Record("green", Expanded, legacy: legacy);

        var result = scenario.Driver().AdvanceOnce(scenario.Goal, ConductorAutonomyPolicy.Conservative);

        Xunit.Assert.False(result.WasEscalated);
        Xunit.Assert.Empty(scenario.Requests);
        Xunit.Assert.Contains(AgentRole.Tester, scenario.Dispatches);
    }

    [Xunit.Fact]
    public void LookupUsesLatestMatchingDeclarationAndNormalizesSets()
    {
        using var scenario = new Scenario();
        scenario.Record("green", Original);
        scenario.Record("red", Expanded);
        scenario.Record("unusable", Original);
        scenario.Record("green", Expanded);

        Xunit.Assert.Equal("unusable", scenario.Latest(Original)!.Outcome);
        Xunit.Assert.Equal("green", scenario.Latest(Expanded)!.Outcome);
        Xunit.Assert.Equal("green", DeferredNoChangeEvidenceIndexLines.Latest(
            scenario.Goal, scenario.Developer.Id, Candidate,
            [" DeferredDeltaTests ", "DeferredBetaTests", "DeferredAlphaTests", "DeferredAlphaTests", ""])!.Outcome);
        Xunit.Assert.Null(DeferredNoChangeEvidenceIndexLines.Latest(
            scenario.Goal, scenario.Developer.Id, Candidate,
            ["deferredAlphaTests", "DeferredBetaTests", "DeferredDeltaTests"]));
        Xunit.Assert.Equal("green", DeferredNoChangeEvidenceIndexLines.Latest(
            scenario.Goal, scenario.Developer.Id, Candidate)!.Outcome);
    }

    private static string[] Selections(IEnumerable<string> classes) =>
        classes.Select(name => "Infrastructure.Tests:" + name).ToArray();

    private sealed class Scenario : IDisposable
    {
        public string Root { get; } = ConductorDriverTests.CreateTempDirectory();
        public AgentOrchestratorKernel Kernel { get; }
        public Goal Goal { get; }
        public TaskSpec Developer { get; }
        public TaskSpec Tester { get; }
        public List<string> Requests { get; } = [];
        public List<string> Feedback { get; } = [];
        public List<AgentRole> Dispatches { get; } = [];

        public Scenario()
        {
            (Kernel, Goal) = SoftwareGoal("Deferred no-change class set change");
            Developer = Goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
            Tester = Goal.Tasks.Single(task => task.RequiredRole == AgentRole.Tester);
            foreach (var task in Goal.Tasks.TakeWhile(task => task.Id != Developer.Id))
                PassVerification(Kernel, Goal, task);
            var worktree = GoalWorktrees.WorktreePath(Root, Goal.Id);
            Directory.CreateDirectory(worktree);
            File.WriteAllText(Path.Combine(worktree, ".git"), "gitdir: fixture");
            var project = Path.Combine(worktree, "tests", "Mcg.AgentOrchestrator.Infrastructure.Tests");
            Directory.CreateDirectory(project);
            File.WriteAllText(Path.Combine(project, "Mcg.AgentOrchestrator.Infrastructure.Tests.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\" />");
            foreach (var name in Expanded)
                File.WriteAllText(Path.Combine(project, name + ".cs"), "public class " + name + " {}");
        }

        public void CompleteDeveloper(IReadOnlyList<string> classes)
        {
            Kernel.RetryTask(Goal.Id, Developer.Id, "Repeat no-change with a corrected class declaration.");
            DispatchTask(Kernel, Goal, Developer, baseCommit: Candidate);
            var dispatch = Developer.LastDispatch!;
            const string rationale = "NO_CHANGE: the candidate already contains the revision.";
            var output = rationale + "\nWORKER_RESULT:\nfiles: none\ncommands: none\n" +
                "tests: deferred - " + string.Join(", ", classes) + "\ncommit: none\nblockers: none\n" +
                "assigned_scope_complete: true\nmodel_fit: test/model - adequate - fixture\n" +
                "skills: none\nconfidence: high\nEND_WORKER_RESULT";
            Kernel.RecordDispatchExecutionResult(Goal.Id, Developer.Id, new TaskVerificationRecord(
                dispatch.Command, dispatch.WorkingDirectory, 0, output,
                new DeferredNoChangeOutcome(Candidate, classes, rationale).FormatMarker(),
                DateTimeOffset.UtcNow, WorkerResultPresent: true, HasCommittedChanges: false));
            Xunit.Assert.Equal(WorkTaskStatus.Completed, Developer.Status);
            Xunit.Assert.Equal("deferred-no-change-round", Developer.LastVerification!.CompletionVerdictRule);
        }

        public DeferredNoChangeEvidenceEntry? Latest(IReadOnlyList<string> classes) =>
            DeferredNoChangeEvidenceIndexLines.Latest(Goal, Developer.Id, Candidate, classes);

        public void Record(string outcome, IReadOnlyList<string> classes, bool legacy = false,
            IReadOnlyList<string>? selections = null, IReadOnlyList<string>? notRun = null)
        {
            var marker = DeferredNoChangeEvidenceIndexLines.FormatMarker(new DeferredNoChangeEvidenceEntry(
                outcome, Developer.Id, Candidate, "fixture-receipt", selections ?? Selections(classes),
                notRun ?? [], null, [], Declared: legacy ? null : classes));
            if (outcome == "started") Kernel.RecordFindingEvidenceRequest(Goal.Id, Tester.Id, marker);
            else Kernel.RecordFindingEvidenceRun(Goal.Id, Tester.Id, marker);
        }

        public ConductorDriver Driver(Func<string, FocusedEvidenceRunResult>? run = null)
        {
            var driver = MakeDriver(
                getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
                getPreReviewEvidenceContext: _ => NoPreReviewContext(Candidate),
                focusedEvidenceAttemptCoordinator: new ConductorParallelAcceptanceAttemptCoordinator(
                    Path.Combine(Root, "attempts"), Mcg.AgentOrchestrator.Infrastructure.TrunkBranchName.Default, runInline: true, acquireStableSlotLease: (_, _) => null),
                runFocusedEvidence: (_, request) =>
                {
                    Requests.Add(request);
                    return run?.Invoke(request) ??
                        ConductorDriverTestsFindingEvidenceReuse.RetainedEvidenceWithExecutedClasses(Root, request, Candidate);
                },
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
            return driver;
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
