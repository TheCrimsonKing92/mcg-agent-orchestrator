using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using static ConductorDriverTests;

// Parallel-safe: scenario-owned kernel, files, clock and inline coordinator; no child processes.
public sealed class PreTesterOutOfDiffRerunScenarioTests
{
    private const string Candidate = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string OutsideClass = "RemoteWorkerBuildCheckFallbackTests";
    private const string InsideClass = "CandidateChangedTests";
    private const string InsidePath = "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/" + InsideClass + ".cs";
    private const string Incident = OutsideClass + ".Run_RemoteFailure_ReturnsIdenticalLocalVerdict(fault: \"digest\", reason: \"remote-receipt-digest-mismatch\")";
    private const string Regression = "RunGoalServiceProcessContractTests.RunGoalServiceRealAdvanceStepCompletesTwoEchoTasksSequentially";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OutOfDiffFlakeWithHistoryAndGreenRerunIsNonBlocking(bool priorRed)
    {
        using var scenario = new Scenario([Incident]);
        if (priorRed) scenario.SeedRed();
        scenario.Advance();

        Assert.Equal(priorRed ? 1 : 2, scenario.Requests.Count);
        Assert.Equal("Infrastructure.Tests:" + OutsideClass, scenario.Requests[^1]);
        var attempts = scenario.Attempts();
        Assert.Equal(scenario.Requests.Count, attempts.Count);
        Assert.All(attempts, attempt => Assert.Equal(Candidate, attempt.BranchHeadSha));
        Assert.Single(attempts.Where(attempt => attempt.FocusedEvidenceRequest == "Infrastructure.Tests:" + OutsideClass));
        var marker = scenario.Marker();
        Assert.Equal("non-blocking", marker.Verdict);
        Assert.Equal("green", marker.Rerun);
        Assert.Equal(new[] { Incident }, marker.FailingTests);
        Assert.Equal(new[] { "f4d70bca" }, marker.FailureHistory[CrossGoalTestFailureHistory.TestKey(Incident)!]);
        Assert.Equal(new[] { AgentRole.Tester }, scenario.DispatchedRoles);
        var events = scenario.Goal.Timeline.ToList();
        Assert.True(events.FindIndex(evt => PreTesterOutOfDiffRerunMarker.Parse(evt.Message) is not null) <
            events.FindIndex(evt => evt.TaskId == scenario.Tester.Id && evt.Kind == ProgressKind.TaskDispatchRecorded));
        foreach (var task in new[] { scenario.Tester, scenario.Reviewer })
        {
            var line = Assert.Single(scenario.Lines(task).Where(line => line.StartsWith("evidence_nonblocking_failures:", StringComparison.Ordinal)));
            Assert.Contains(Incident, line, StringComparison.Ordinal);
            Assert.Contains("source_receipt=" + marker.SourceReceipt, line, StringComparison.Ordinal);
            Assert.Contains("rerun_receipt=" + marker.RerunReceipt, line, StringComparison.Ordinal);
            Assert.Contains("f4d70bca", line, StringComparison.Ordinal);
            Assert.EndsWith("These failures are outside the candidate diff, passed a same-candidate rerun, and have failed on other goals; they do not block this candidate.", line);
        }
        Assert.DoesNotContain(scenario.Lines(scenario.Reviewer), line => line.StartsWith("evidence_index:", StringComparison.Ordinal));
        scenario.CompleteTester();
        Assert.Null(scenario.Route());
    }

    [Fact]
    public void RegressionWithoutCrossGoalHistoryStaysBlocking()
    {
        using var scenario = new Scenario([Regression], history: false);
        scenario.Advance();
        Assert.Single(scenario.Requests);
        AssertNoRerun(scenario);
        scenario.CompleteTester();
        var route = Assert.IsType<AttributedFailure>(scenario.Route());
        Assert.Equal("pre-tester-receipt", route.Source);
        Assert.Equal(new[] { Regression }, route.FailingTests);
    }

    [Fact]
    public void HistoryWithRedRerunRecordsBlocking()
    {
        using var scenario = new Scenario([Incident], rerunGreen: false);
        scenario.Advance();
        Assert.Equal(2, scenario.Requests.Count);
        var marker = scenario.Marker();
        Assert.Equal("blocking", marker.Verdict);
        Assert.Equal("red", marker.Rerun);
        foreach (var task in new[] { scenario.Tester, scenario.Reviewer })
            Assert.Contains(scenario.Lines(task), line => line ==
                $"evidence_out_of_diff_rerun: rerun_receipt={marker.RerunReceipt}; rerun=red; verdict=blocking");
        scenario.CompleteTester();
        Assert.NotNull(scenario.Route());
    }

    [Theory]
    [InlineData("mixed")]
    [InlineData("unresolved")]
    [InlineData("guard")]
    [InlineData("source-guard")]
    public void IneligibleFailureSetsDoNotRerun(string variant)
    {
        var failure = variant switch
        {
            "mixed" => InsideClass + ".Fails",
            "unresolved" => "UndeclaredTests.Fails",
            "guard" => PreTesterAlwaysRunGuardTestClasses.Entries[0].TestClass + ".Fails",
            _ => "CandidateTreeSourceGuardTests.Fails"
        };
        using var scenario = new Scenario([Incident, failure], declareFailures: variant != "unresolved");
        // A retained receipt can claim execution even when the declaring source is unavailable now.
        if (variant == "unresolved") scenario.SeedRed();
        scenario.Advance();
        Assert.Equal(variant == "unresolved" ? 0 : 1, scenario.Requests.Count);
        AssertNoRerun(scenario);
    }

    [Fact]
    public void RepeatedAdvancesRerunOnce()
    {
        using var scenario = new Scenario([Incident]);
        scenario.Advance();
        scenario.CompleteTester();
        scenario.Kernel.RetryTask(scenario.Goal.Id, scenario.Tester.Id, "fixture retry");
        scenario.Advance();
        Assert.Equal(2, scenario.Requests.Count);
        Assert.Single(scenario.Goal.Timeline.Where(evt => PreTesterOutOfDiffRerunMarker.Parse(evt.Message) is not null));
        Assert.Equal(new[] { AgentRole.Tester, AgentRole.Tester }, scenario.DispatchedRoles);
        Assert.Equal("non-blocking", scenario.Marker().Verdict);
    }

    [Theory]
    [InlineData("not-run")]
    [InlineData("not-selected")]
    public void UnexecutedFailuresKeepPriorRedPath(string variant)
    {
        using var scenario = new Scenario([Incident]);
        scenario.SeedRed(notRun: variant == "not-run" ? [OutsideClass] : [], selected: variant != "not-selected");
        scenario.Advance();
        Assert.Empty(scenario.Requests);
        AssertNoRerun(scenario);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PendingOrTransientRerunHoldsUntilReceiptAndKeepsOriginalHistory(bool transient)
    {
        using var scenario = new Scenario([Incident]);
        scenario.SeedRed();
        var entry = Assert.IsType<PreTesterEvidenceEntry>(PreTesterEvidenceIndexLines.Latest(scenario.Goal, scenario.Tester.Id, Candidate));
        var historyReads = 0;
        var calls = 0;
        bool Reconcile(Goal goal, ConductorAutonomyPolicy policy, string request, string? sha, string source,
            ConductorFocusedEvidenceRequestContext? context, out FocusedEvidenceRunResult evidence,
            out ConductorParallelAcceptanceAttempt? attempt, out FailedGoalFindingObservation decision,
            out ConductorParallelAcceptanceAttemptDecisionKind? kind, ConductorParallelAcceptanceAttempt? existing = null)
        {
            calls++;
            Assert.Equal(Candidate, sha);
            Assert.Equal("Infrastructure.Tests:" + OutsideClass, request);
            evidence = calls == 3 ? scenario.Green(request) : null!;
            kind = calls == 1 ? ConductorParallelAcceptanceAttemptDecisionKind.Started :
                calls == 2 && !transient ? ConductorParallelAcceptanceAttemptDecisionKind.Running : ConductorParallelAcceptanceAttemptDecisionKind.Completed;
            var now = DateTimeOffset.Parse("2026-10-23T00:00:00Z");
            attempt = transient && calls == 2 ? new("attempt", goal.Id.Value, goal.Id.Value[..8], 0, sha, null, now, now, 0,
                ConductorParallelAcceptanceAttemptOutcome.InfrastructureDeferred, "", "", "", "", "", "") : null;
            decision = calls < 3 ? FailedGoalFindingObservation.Observed(FailedGoalFindingObservationKind.FindingEvidencePending,
                "Background pre-tester-out-of-diff-rerun focused evidence is running") : FailedGoalFindingObservation.None;
            return calls == 3;
        }
        var rerun = scenario.Rerun(Reconcile, (_, _) =>
        {
            Assert.Equal(1, ++historyReads);
            return new Dictionary<string, IReadOnlyList<string>> { [CrossGoalTestFailureHistory.TestKey(Incident)!] = new[] { "f4d70bca" } };
        });

        Assert.True(rerun.TryRun(scenario.Goal, scenario.Tester, entry, ConductorAutonomyPolicy.Permissive, out _, out _));
        Assert.True(rerun.TryRun(scenario.Goal, scenario.Tester, entry, ConductorAutonomyPolicy.Permissive, out _, out _));
        Assert.Null(PreTesterOutOfDiffRerunMarker.Latest(scenario.Goal, scenario.Tester.Id, Candidate, entry.ReceiptId));
        Assert.False(rerun.TryRun(scenario.Goal, scenario.Tester, entry, ConductorAutonomyPolicy.Permissive, out _, out _));
        Assert.False(rerun.TryRun(scenario.Goal, scenario.Tester, entry, ConductorAutonomyPolicy.Permissive, out _, out _));
        Assert.Equal(3, calls);
        Assert.Equal(1, historyReads);
        Assert.Single(scenario.Goal.Timeline.Where(evt => evt.Message.StartsWith(PreTesterOutOfDiffRerunMarker.RequestPrefix, StringComparison.Ordinal)));
        Assert.Equal("non-blocking", scenario.Marker().Verdict);
        Assert.Empty(scenario.DispatchedRoles);
    }

    private static void AssertNoRerun(Scenario scenario)
    {
        Assert.DoesNotContain(scenario.Goal.Timeline, evt => evt.Message.StartsWith(PreTesterOutOfDiffRerunMarker.Prefix, StringComparison.Ordinal) ||
            evt.Message.StartsWith(PreTesterOutOfDiffRerunMarker.RequestPrefix, StringComparison.Ordinal));
        foreach (var task in new[] { scenario.Tester, scenario.Reviewer })
            Assert.DoesNotContain(scenario.Lines(task), line => line.StartsWith("evidence_nonblocking_failures:", StringComparison.Ordinal) ||
                line.StartsWith("evidence_out_of_diff_rerun:", StringComparison.Ordinal));
    }

    private sealed class Scenario : IDisposable
    {
        private readonly string _root = ConductorDriverTests.CreateTempDirectory();
        private readonly FixtureClock _clock = new();
        private readonly string[] _failures;
        private readonly bool _history;
        private readonly bool _rerunGreen;
        private bool _seeded;
        public AgentOrchestratorKernel Kernel { get; }
        public Goal Goal { get; }
        public TaskSpec Developer { get; } = new(TaskId.New(), "Implement", AgentRole.Developer);
        public TaskSpec Tester { get; } = new(TaskId.New(), "Verify", AgentRole.Tester);
        public TaskSpec Reviewer { get; } = new(TaskId.New(), "Review", AgentRole.Reviewer);
        public List<string> Requests { get; } = [];
        public List<AgentRole> DispatchedRoles { get; } = [];

        public Scenario(string[] failures, bool history = true, bool rerunGreen = true, bool declareFailures = true)
        {
            _failures = failures;
            _history = history;
            _rerunGreen = rerunGreen;
            Kernel = new(_clock);
            Kernel.ConfigureCandidateIdentityResolver(_ => new CandidateIdentity("patch", "base", "manifest"));
            Goal = Kernel.CreateGoal("pre-Tester rerun scenario", [Developer, Tester, Reviewer]);
            Kernel.ActivateGoal(Goal.Id, DefaultAgents());
            Kernel.RecordTaskDispatch(Goal.Id, Developer.Id, new("fixture", "implement", _root, _clock.Next(), BaseCommit: "aaa1111"));
            var classes = failures.Select(acceptanceIdentity => AcceptanceTestSourceResolver.ExtractClassName(acceptanceIdentity)!).Append(InsideClass).Distinct().ToArray();
            var output = string.Join('\n', "WORKER_RESULT:", "files: " + InsidePath, "commands: none",
                "tests: deferred - " + string.Join(", ", classes), "commit: none", "blockers: none", "assigned_scope_complete: true",
                "model_fit: test/model - adequate - fixture", "skills: none", "confidence: high", "END_WORKER_RESULT");
            Kernel.RecordTaskVerification(Goal.Id, Developer.Id, new("implement", _root, 0, output, "", _clock.Next(),
                WorkerResultPresent: true, HasCommittedChanges: true));
            Assert.Equal(WorkTaskStatus.Completed, Developer.Status);
            var worktree = GoalWorktrees.WorktreePath(_root, Goal.Id);
            Directory.CreateDirectory(worktree);
            File.WriteAllText(Path.Combine(worktree, ".git"), "gitdir: fixture");
            foreach (var name in classes.Where(name => declareFailures || name != "UndeclaredTests")
                         .Concat(PreTesterAlwaysRunGuardTestClasses.Entries.Select(entry => entry.TestClass)).Distinct())
                Declare(worktree, name);
        }

        private static void Declare(string worktree, string name)
        {
            var project = Path.Combine(worktree, "tests", "Mcg.AgentOrchestrator.Infrastructure.Tests");
            Directory.CreateDirectory(project);
            File.WriteAllText(Path.Combine(project, "Mcg.AgentOrchestrator.Infrastructure.Tests.csproj"), "<Project />");
            File.WriteAllText(Path.Combine(project, name + ".cs"), "public class " + name + " {}");
        }

        public void SeedRed(string[]? notRun = null, bool selected = true)
        {
            _seeded = true;
            Kernel.RecordFindingEvidenceRun(Goal.Id, Tester.Id, PreTesterEvidenceIndexLines.FormatMarker(
                new("red", Candidate, "source-receipt", selected ? _failures.Select(identity =>
                    "Infrastructure.Tests:" + AcceptanceTestSourceResolver.ExtractClassName(identity)).Distinct().ToArray() : ["Infrastructure.Tests:" + InsideClass],
                    notRun ?? [], null, _failures)));
        }

        public PreTesterOutOfDiffRerunMarker Marker()
        {
            var entry = Assert.IsType<PreTesterEvidenceEntry>(PreTesterEvidenceIndexLines.Latest(Goal, Tester.Id, Candidate));
            return Assert.IsType<PreTesterOutOfDiffRerunMarker>(PreTesterOutOfDiffRerunMarker.Latest(Goal, Tester.Id, Candidate, entry.ReceiptId));
        }

        public IReadOnlyList<string> Lines(TaskSpec task) => PreTesterEvidenceIndexLines.ForTester(Goal, task, Candidate);
        public AttributedFailure? Route() => InconclusiveTesterRedRoute.ReadPreTesterRed(Goal, Tester, Candidate);

        public FocusedEvidenceRunResult Green(string request) =>
            ConductorDriverTestsFindingEvidenceReuse.RetainedEvidenceWithExecutedClasses(_root, request, Candidate);

        public PreTesterOutOfDiffRerun Rerun(PreTesterOutOfDiffRerun.ReconcileAttempt reconcile,
            Func<Goal, IReadOnlyList<string>, IReadOnlyDictionary<string, IReadOnlyList<string>>> history) =>
            new(GoalWorktrees.WorktreePath(_root, Goal.Id), _root, _ => [InsidePath], history, reconcile,
                (gid, tid, message) => Kernel.RecordFindingEvidenceRequest(gid, tid, message),
                (gid, tid, message) => Kernel.RecordFindingEvidenceRun(gid, tid, message),
                (_, _, _, _) => "rerun-batch", (_, _, _) => "rerun-receipt");

        public IReadOnlyList<ConductorParallelAcceptanceAttempt> Attempts() =>
            Directory.GetFiles(Path.Combine(_root, "attempts", Goal.Id.Value), "*.attempt.json")
                .Select(path => Assert.IsType<ConductorParallelAcceptanceAttempt>(JsonSerializer.Deserialize<ConductorParallelAcceptanceAttempt>(
                    File.ReadAllText(path), new JsonSerializerOptions(JsonSerializerDefaults.Web)))).ToArray();

        public void CompleteTester()
        {
            var dispatch = Assert.IsType<TaskDispatchRecord>(Tester.LastDispatch);
            Kernel.RecordDispatchExecutionResult(Goal.Id, Tester.Id, new(dispatch.Command, dispatch.WorkingDirectory, 0,
                "WORKER_RESULT:\nfiles: none\ntests: inconclusive - retained receipt\nblockers: none\nEND_WORKER_RESULT", "", _clock.Next(),
                WorkerResultPresent: true, DispatchStartedAt: dispatch.DispatchedAt));
            Assert.Equal(WorkTaskStatus.Failed, Tester.Status);
            Assert.Equal(DispatchOutcomeKind.VerificationInconclusive, DispatchFailureClassifier.Classify(Tester, Tester.LastVerification!).Kind);
        }

        public void Advance()
        {
            var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(Path.Combine(_root, "attempts"),
                TrunkBranchName.Default, runInline: true, acquireStableSlotLease: (_, _) => null);
            var driver = MakeDriver(getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
                getPreReviewEvidenceContext: _ => NoPreReviewContext(Candidate), getLandingFileScopes: _ => [InsidePath], executionDirectory: _root,
                focusedEvidenceAttemptCoordinator: coordinator,
                runFocusedEvidence: (_, request) =>
                {
                    Requests.Add(request);
                    return (Requests.Count > 1 || _seeded) && _rerunGreen
                        ? ConductorDriverTestsFindingEvidenceReuse.RetainedEvidenceWithExecutedClasses(_root, request, Candidate)
                        : Red(request);
                },
                dispatchAndStart: _ =>
                {
                    Assert.Equal(WorkTaskStatus.Assigned, Tester.Status);
                    DispatchedRoles.Add(Tester.RequiredRole);
                    Kernel.RecordTaskDispatch(Goal.Id, Tester.Id, new("fixture", "verify", _root, _clock.Next()));
                    return DispatchStartOutcome.Started();
                },
                retryTaskWithCause: (gid, tid, message, round, cause) => Kernel.RetryTask(gid, tid, message, retryRoundKind: round, retryCause: cause),
                recordTaskNote: (gid, tid, message) => Kernel.RecordTaskNote(gid, tid, message),
                recordFindingEvidenceRequest: (gid, tid, message) => Kernel.RecordFindingEvidenceRequest(gid, tid, message),
                recordFindingEvidenceRun: (gid, tid, message) => Kernel.RecordFindingEvidenceRun(gid, tid, message),
                emptyOutputBackoffDelay: _ => { });
            driver.PreTesterFailureHistory = (_, identities) => _history
                ? identities.Select(CrossGoalTestFailureHistory.TestKey).OfType<string>().Distinct().ToDictionary(key => key,
                    _ => (IReadOnlyList<string>)new[] { "f4d70bca000000000000000000000000" })
                : new Dictionary<string, IReadOnlyList<string>>();
            driver.OverrideDeveloperCompletionStructuralPreflightForTests(_ => new DeveloperCompletionStructuralFindings(false, "clean"), TimeSpan.FromSeconds(5));
            Assert.IsType<ConductorAdvanceOutcome.Executed>(driver.AdvanceOnce(Goal, ConductorAutonomyPolicy.Permissive).Outcome);
        }

        private FocusedEvidenceRunResult Red(string request)
        {
            var checks = _failures.Select(identity => new AcceptanceCheckResult(identity, false, 1, "candidate failed",
                FailingTestIdentities: [identity], ExecutedTestCount: 1)).ToArray();
            return new(request, true, false, "test failure", checks,
                Arms: [new(FindingEvidenceArm.Candidate, Candidate, FindingEvidenceArmDisposition.Red, true, false, "candidate failed", checks)]);
        }

        public void Dispose() => Directory.Delete(_root, recursive: true);
    }

    private sealed class FixtureClock : IClock
    {
        public DateTimeOffset UtcNow { get; private set; } = new(2026, 10, 23, 0, 0, 0, TimeSpan.Zero);
        public DateTimeOffset Next() => UtcNow = UtcNow.AddMinutes(1);
    }
}
