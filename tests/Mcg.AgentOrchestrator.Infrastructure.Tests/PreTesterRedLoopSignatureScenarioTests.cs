using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using System.Xml.Linq;

using static ConductorDriverTests;

// Parallel-safe: scenario-owned files and coordinators; dispatch and evidence execution are seamed.
public sealed class PreTesterRedLoopSignatureScenarioTests
{
    private const string FailureClass = "TesterRedLabelledInconclusiveScenarioTests";
    private const string Failure = FailureClass + ".RefreshedRedReceiptRoutesDeveloperAfterSecondInconclusiveRound";
    private const string OtherClass = "SecondFailureScenarioTests";
    private const string OtherFailure = OtherClass + ".FailsOnCandidate";
    private const string FirstMessage = "Assert.IsType() Failure: Value is null";
    private const string NewMessage = "Assert.Single() Failure: The collection contained 2 items";
    private const string Project = "Mcg.AgentOrchestrator.Infrastructure.Tests";
    private static readonly string[] ChangedPaths =
        [$"tests/{Project}/{FailureClass}.cs", $"tests/{Project}/{OtherClass}.cs"];

    [Fact(Timeout = TestHangGuard.WholeTestMilliseconds)]
    [Trait("Category", "CrossTick")]
    public void IncidentReplay_ChangedAssertionRetriesDeveloperInsteadOfEscalating()
    {
        using var scenario = Replay("changed");

        Assert.IsType<ConductorAdvanceOutcome.Executed>(scenario.LastResult!.Outcome);
        Assert.Empty(scenario.Escalations);
        Assert.DoesNotContain(scenario.Goal.Timeline, evt => evt.Kind == ProgressKind.GoalPolicyDecision &&
            evt.Message.Contains("PRE_TESTER_RED_LOOP:", StringComparison.Ordinal));
        var retries = scenario.DeveloperRetries();
        Assert.Equal(2, retries.Length);
        Assert.All(retries, evt => Assert.StartsWith("ACTIONABLE_CANDIDATE_RED ", evt.Message, StringComparison.Ordinal));
        Assert.Contains("candidate_sha=026d75415", retries[1].Message, StringComparison.Ordinal);
        Assert.Equal([AgentRole.Developer, AgentRole.Developer], scenario.Dispatched);
        Assert.Equal(2, scenario.Runs);
        var lines = scenario.RedLines();
        Assert.Equal(3, lines.Length);
        Assert.Equal(FirstMessage + " @ " + FailureClass + ".cs:40",
            PreTesterFailureSignature.ReadRun(lines[^2]).Signatures[Failure]);
        Assert.Equal(NewMessage + " @ " + FailureClass + ".cs:280",
            PreTesterFailureSignature.ReadRun(lines[^1]).Signatures[Failure]);
    }

    [Theory(Timeout = TestHangGuard.WholeTestMilliseconds)]
    [Trait("Category", "CrossTick")]
    [InlineData("same", false, "6,1,1", "repeat")]
    [InlineData("missing", false, "6,1,1", "repeat")]
    [InlineData("unmatched", false, "6,1,1", "repeat")]
    [InlineData("grew", true, "6,1,2", "grew")]
    public void Controls_MatchNoTrxBaseline(string control, bool grew, string counts, string trip)
    {
        using var baseline = Replay("baseline", grew);
        using var actual = Replay(control, grew);

        Assert.IsType<ConductorAdvanceOutcome.Escalated>(baseline.LastResult!.Outcome);
        Assert.Single(baseline.Escalations);
        Assert.Equal(counts, EscalationField(baseline.Escalations[0], "failing_counts"));
        Assert.Equal(trip, EscalationField(baseline.Escalations[0], "trip"));
        Assert.Equal(2, baseline.Runs);
        Assert.Single(baseline.DeveloperRetries());
        Assert.Equal([AgentRole.Developer], baseline.Dispatched);
        Assert.All(baseline.RedLines(), line => Assert.Empty(PreTesterFailureSignature.ReadRun(line).Signatures));

        Assert.Equal(baseline.LastResult.Outcome.GetType(), actual.LastResult!.Outcome.GetType());
        Assert.Equal(baseline.Escalations.Count, actual.Escalations.Count);
        Assert.Equal(EscalationField(baseline.Escalations[0], "failing_counts"),
            EscalationField(actual.Escalations[0], "failing_counts"));
        Assert.Equal(EscalationField(baseline.Escalations[0], "trip"),
            EscalationField(actual.Escalations[0], "trip"));
        Assert.Equal(baseline.Runs, actual.Runs);
        Assert.Equal(baseline.DeveloperRetries().Length, actual.DeveloperRetries().Length);
        Assert.Equal(baseline.Dispatched, actual.Dispatched);
        var lines = actual.RedLines();
        Assert.Equal(FirstMessage + " @ " + FailureClass + ".cs:40",
            PreTesterFailureSignature.ReadRun(lines[^2]).Signatures[Failure]);
        if (control is "missing" or "unmatched")
            Assert.Empty(PreTesterFailureSignature.ReadRun(lines[^1]).Signatures);
        else
            Assert.Equal(grew ? 2 : 1, PreTesterFailureSignature.ReadRun(lines[^1]).Signatures.Count);
    }

    [Fact(Timeout = TestHangGuard.WholeTestMilliseconds)]
    [Trait("Category", "CrossTick")]
    public void FiveDistinctSignatures_StillTripHardCap()
    {
        using var scenario = new Scenario();
        Assert.Equal(5, PreTesterRedLoopRule.HardCap);
        for (var round = 1; round <= 5; round++)
        {
            scenario.DeliverDeveloper($"ca0000{round}");
            scenario.WriteTrx((Failure, "assertion " + round, round));
            var result = scenario.RunRed([Failure]);
            if (round < 5)
            {
                Assert.IsType<ConductorAdvanceOutcome.Executed>(result.Outcome);
                Assert.Empty(scenario.Escalations);
                Assert.Equal(round, scenario.DeveloperRetries().Length);
                Assert.Equal(Enumerable.Repeat(AgentRole.Developer, round), scenario.Dispatched);
                Assert.StartsWith("ACTIONABLE_CANDIDATE_RED ", scenario.DeveloperRetries()[^1].Message,
                    StringComparison.Ordinal);
            }
            else
                Assert.IsType<ConductorAdvanceOutcome.Escalated>(result.Outcome);
        }

        var escalation = Assert.Single(scenario.Escalations);
        Assert.Equal("1,1,1,1,1", EscalationField(escalation, "failing_counts"));
        Assert.Equal("cap", EscalationField(escalation, "trip"));
        Assert.Equal(5, scenario.Runs);
        Assert.Equal(4, scenario.DeveloperRetries().Length);
        Assert.Equal(5, scenario.RedLines().Select(line =>
            PreTesterFailureSignature.ReadRun(line).Signatures[Failure]).Distinct(StringComparer.Ordinal).Count());
    }

    private static Scenario Replay(string control, bool grew = false)
    {
        var scenario = new Scenario();
        try
        {
            scenario.SeedIncident();
            scenario.DeliverDeveloper("6107cd29d");
            if (control != "baseline") scenario.WriteTrx((Failure, FirstMessage, 40));
            Assert.IsType<ConductorAdvanceOutcome.Executed>(scenario.RunRed([Failure]).Outcome);
            Assert.Single(scenario.DeveloperRetries());
            scenario.DeliverDeveloper("026d75415");
            if (control == "same") scenario.WriteTrx((Failure, FirstMessage, 40));
            if (control == "changed") scenario.WriteTrx((Failure, NewMessage, 280));
            if (control == "unmatched") scenario.WriteTrx(("Unrelated.Test", NewMessage, 280));
            if (control == "grew") scenario.WriteTrx((Failure, NewMessage, 280), (OtherFailure, "new failure", 90));
            scenario.RunRed(grew ? [Failure, OtherFailure] : [Failure]);
            return scenario;
        }
        catch
        {
            scenario.Dispose();
            throw;
        }
    }

    private static string EscalationField(string message, string key) =>
        message.Split(';', StringSplitOptions.TrimEntries).Last(part =>
            part.StartsWith(key + "=", StringComparison.Ordinal))[(key.Length + 1)..];

    private sealed class Scenario : IDisposable
    {
        private string Root { get; } = ConductorDriverTests.CreateTempDirectory();
        private ReplayClock Clock { get; } = new();
        public AgentOrchestratorKernel Kernel { get; }
        public Goal Goal { get; }
        public TaskSpec Developer { get; }
        public TaskSpec Tester { get; }
        public string CandidateSha { get; private set; } = "c1fdebb3a";
        public int Runs { get; private set; }
        public List<string> Escalations { get; } = [];
        public List<AgentRole> Dispatched { get; } = [];
        public ConductorAdvanceResult? LastResult { get; private set; }
        private string Worktree => GoalWorktrees.WorktreePath(Root, Goal.Id);
        private string TrxPath => Path.Combine(Root, CandidateSha + ".trx");

        public Scenario()
        {
            Kernel = new AgentOrchestratorKernel(Clock);
            Goal = GoalLifecycleCommands.CreateAndActivateGoal(Kernel, DefaultAgents(), "Pre-Tester failure signature replay");
            Developer = Goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
            Tester = Goal.Tasks.Single(task => task.RequiredRole == AgentRole.Tester);
            foreach (var task in Goal.Tasks.TakeWhile(task => task.Id != Developer.Id))
                PassVerification(Kernel, Goal, task);
            Directory.CreateDirectory(Worktree);
            File.WriteAllText(Path.Combine(Worktree, ".git"), "gitdir: fixture");
            Declare(FailureClass, Project);
            Declare(OtherClass, Project);
            foreach (var entry in PreTesterAlwaysRunGuardTestClasses.Entries) Declare(entry.TestClass, Project);
        }

        private void Declare(string name, string projectName)
        {
            var project = Path.Combine(Worktree, "tests", projectName);
            Directory.CreateDirectory(project);
            File.WriteAllText(Path.Combine(project, projectName + ".csproj"), "<Project />");
            File.WriteAllText(Path.Combine(project, name + ".cs"), "public class " + name + " {}");
        }

        public void SeedIncident()
        {
            var controls = new[] { "outside-selection", "not-run", "green", "different-candidate" }
                .Select(control => FailureClass +
                    $".DeclinedReceiptRecoveryEqualsDisabledRouteBaseline(control: \"{control}\")");
            var failures = controls.Concat([FailureClass + ".UnusableReceiptRetryEqualsDisabledRouteBaseline", Failure]).ToArray();
            Kernel.RecordFindingEvidenceRun(Goal.Id, Tester.Id,
                PreTesterEvidenceIndexLines.FormatMarker(new PreTesterEvidenceEntry(
                    "actionable-red", "c1fdebb3a", "incident-0801", [], [], null, failures)));
            var seed = Assert.Single(RedLines());
            Assert.Equal(6, PreTesterFailureSignature.ReadRun(seed).FailingTests.Count);
            Assert.Empty(PreTesterFailureSignature.ReadRun(seed).Signatures);
            Assert.Equal(new DateTimeOffset(2026, 10, 18, 8, 1, 19, TimeSpan.Zero),
                Goal.Timeline.Last().OccurredAt);
        }

        public void DeliverDeveloper(string sha)
        {
            Clock.UtcNow = Clock.UtcNow.AddMinutes(20);
            CandidateSha = sha;
            DispatchTask(Kernel, Goal, Developer, baseCommit: "aaa1111");
            var output = string.Join(Environment.NewLine,
                "WORKER_RESULT:", "files: " + string.Join(", ", ChangedPaths), "commands: none",
                "tests: deferred - " + FailureClass + ", " + OtherClass, "commit: none", "blockers: none",
                "assigned_scope_complete: true", "model_fit: test/model - adequate - fixture",
                "skills: none", "confidence: high", "END_WORKER_RESULT");
            Kernel.RecordTaskVerification(Goal.Id, Developer.Id, new TaskVerificationRecord(
                "test.exe", Root, 0, output, "", Clock.UtcNow,
                WorkerResultPresent: true, HasCommittedChanges: true));
        }

        public void WriteTrx(params (string Name, string Message, int Line)[] failures) =>
            new XDocument(new XElement("TestRun", new XElement("Results", failures.Select(failure =>
                new XElement("UnitTestResult", new XAttribute("testName", failure.Name), new XAttribute("outcome", "Failed"),
                    new XElement("Output", new XElement("ErrorInfo", new XElement("Message", failure.Message),
                        new XElement("StackTrace", "at Fixture.Test() in C:\\fixture\\tests\\" + Project +
                            "\\" + FailureClass + ".cs:line " + failure.Line)))))))).Save(TrxPath);

        public string[] RedLines() => Goal.Timeline.Where(evt => evt.TaskId == Tester.Id &&
            evt.Kind == ProgressKind.FindingEvidenceRunRecorded &&
            evt.Message.StartsWith("finding-evidence pre-tester outcome=actionable-red;", StringComparison.Ordinal))
            .Select(evt => evt.Message).ToArray();

        public ProgressEvent[] DeveloperRetries() => Goal.Timeline.Where(evt =>
            evt.TaskId == Developer.Id && evt.Kind == ProgressKind.TaskRetried).ToArray();

        public ConductorAdvanceResult RunRed(IReadOnlyList<string> failures)
        {
            var driver = MakeDriver(
                getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
                getPreReviewEvidenceContext: _ => NoPreReviewContext(CandidateSha),
                getLandingFileScopes: _ => ChangedPaths,
                focusedEvidenceAttemptCoordinator: new ConductorParallelAcceptanceAttemptCoordinator(
                    Path.Combine(Root, "attempts"), TrunkBranchName.Default, runInline: true,
                    acquireStableSlotLease: (_, _) => null),
                runFocusedEvidence: (_, request) =>
                {
                    Runs++;
                    Assert.Contains(FailureClass, request, StringComparison.Ordinal);
                    var red = CandidateRedFindingEvidence(request, CandidateSha, failures[0]);
                    var candidate = red.Arms!.Single(arm => arm.Arm == FindingEvidenceArm.Candidate);
                    var check = candidate.Checks.Single() with
                    {
                        FailingTestIdentities = failures, TestResultPaths = [TrxPath]
                    };
                    return red with { Checks = [check], Arms = [candidate with { Checks = [check] }] };
                },
                dispatchAndStart: goal =>
                {
                    Dispatched.Add(goal.Tasks.First(task => task.Status == WorkTaskStatus.Assigned).RequiredRole);
                    return DispatchStartOutcome.Started();
                },
                retryTaskWithCause: (goalId, taskId, message, round, cause) =>
                    Kernel.RetryTask(goalId, taskId, message, retryRoundKind: round, retryCause: cause),
                recordFindingEvidenceRequest: (goalId, taskId, message) =>
                    Kernel.RecordFindingEvidenceRequest(goalId, taskId, message),
                recordFindingEvidenceRun: (goalId, taskId, message) =>
                    Kernel.RecordFindingEvidenceRun(goalId, taskId, message),
                writeEscalation: (_, _, reason) => Escalations.Add(reason),
                executionDirectory: Root);
            driver.OverrideDeveloperCompletionStructuralPreflightForTests(
                _ => new DeveloperCompletionStructuralFindings(false, "clean"), TimeSpan.FromSeconds(5));
            LastResult = driver.AdvanceOnce(Goal, ConductorAutonomyPolicy.Conservative);
            var entry = Assert.IsType<PreTesterEvidenceEntry>(PreTesterEvidenceIndexLines.Latest(Goal, Tester.Id, CandidateSha));
            Assert.Equal("actionable-red", entry.Outcome);
            Assert.Equal(failures.Order(StringComparer.Ordinal), entry.FailingTests.Order(StringComparer.Ordinal));
            return LastResult;
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }

    private sealed class ReplayClock : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = new(2026, 10, 18, 8, 1, 19, TimeSpan.Zero);
    }
}
