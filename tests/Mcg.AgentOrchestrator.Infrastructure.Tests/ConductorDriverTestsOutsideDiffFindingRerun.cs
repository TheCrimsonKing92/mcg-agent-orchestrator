using System.Text.Json;
using System.Xml.Linq;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using static ConductorDriverTests;

// Parallel-safe: kernel, source tree, attempt artifacts and SQLite store belong to each fixture.
public sealed class ConductorDriverTestsOutsideDiffFindingRerun
{
    private const string FirstSha = "abc1234abcdef1234abcdef1234abcdef1234abcd12";
    private const string LaterSha = "def5678abcdef1234abcdef1234abcdef1234abcd12";
    private const string Identity = "Fixture.OutsideDiffTests.Case(train: False)";
    private const string OtherIdentity = "Fixture.OtherTests.Runs";
    private const string TestPath = "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/OutsideDiffTests.cs";
    private const string Reason = "outside-diff-red-rerun-green";

    [Fact]
    public void GreenRerunResolvesWithoutDeveloper()
    {
        using var fixture = new Fixture();
        fixture.ReportAndSeed();
        Assert.Equal(WorkTaskStatus.Failed, fixture.Tester.Status);
        fixture.Advance();
        Assert.Equal("Infrastructure.Tests:Fixture.OutsideDiffTests", Assert.Single(fixture.Requests));
        Assert.Single(fixture.RequestMarkers);
        Assert.Empty(fixture.Retried);
        Assert.Equal(ReviewFindingState.Open, fixture.Finding.State);
        fixture.Advance();

        Assert.Equal(ReviewFindingState.Resolved, fixture.Finding.State);
        Assert.Equal(Reason, fixture.Finding.EvidenceOutcome!.DecisionReason);
        Assert.Equal(FindingEvidenceOutcomeReason.ValidEvidence, fixture.Finding.EvidenceOutcome.ResultReason);
        Assert.Equal(WorkTaskStatus.Completed, fixture.Tester.Status);
        Assert.True(WorkerResultBlockers.TryFindReviewFindingRound(fixture.Tester.LastVerification,
            out var closureRound, out var closureDiagnostic), closureDiagnostic);
        Assert.Equal(ReviewFindingState.Resolved, Assert.Single(closureRound.Findings).State);
        Assert.Empty(fixture.Retried);
        Assert.Single(fixture.Requests);
        var closure = Assert.Single(fixture.Goal.Timeline.Where(evt => evt.Kind == ProgressKind.TaskNote &&
            evt.Message.StartsWith("FINDING_RECEIPT_CLOSURE", StringComparison.Ordinal)));
        Assert.Contains("decision=" + Reason, closure.Message);
        Assert.Contains(FirstSha, closure.Message);
        Assert.Contains("outside-finding", closure.Message);
        var notice = Assert.Single(fixture.Notices);
        Assert.Contains(Identity, notice.Body);
        Assert.Contains("original-red", notice.Body);
        Assert.Contains(fixture.Finding.EvidenceOutcome.ReceiptId!, notice.Body);
        Assert.Contains("/Baseline", notice.Body);

        fixture.ReloadAndRecreateDriver();
        Assert.Equal(ReviewFindingState.Resolved, fixture.Finding.State);
        fixture.ReportLaterSuccessfulTester();
        Assert.Null(fixture.Tester.LastVerification!.ReviewFindingContractViolation);
        Assert.Equal(WorkTaskStatus.Completed, fixture.Tester.Status);
        Assert.Empty(ReviewFindings.GetOpenBlockingFindings(fixture.Tester.LastVerification.MergedReviewFindings!, []));
        Assert.Single(fixture.Notices);
    }

    [Theory]
    [InlineData("red")]
    [InlineData("actionable-red")]
    public void PreTesterRedWithoutFindingReceiptRerunsAndResolves(string outcome)
    {
        using var fixture = new Fixture(seedReceipt: false);
        fixture.ReportAndSeed();
        fixture.SeedPreTesterRed(outcome);
        Assert.Empty(fixture.Tester.VerificationHistory.SelectMany(record => record.FindingEvidenceReceipts ?? []));
        fixture.ReloadAndRecreateDriver();

        fixture.Advance();
        Assert.Equal("Infrastructure.Tests:Fixture.OutsideDiffTests", Assert.Single(fixture.Requests));
        Assert.Single(fixture.RequestMarkers);
        Assert.Empty(fixture.Retried);
        fixture.AdvanceToDecision();

        Assert.Equal(ReviewFindingState.Resolved, fixture.Finding.State);
        Assert.Equal(Reason, fixture.Finding.EvidenceOutcome!.DecisionReason);
        Assert.Equal(WorkTaskStatus.Completed, fixture.Tester.Status);
        Assert.Contains("pre-tester-original-red", Assert.Single(fixture.Notices).Body);
        Assert.Empty(fixture.Retried);
    }

    [Theory]
    [InlineData("missing-result")]
    [InlineData("stale-result")]
    [InlineData("unaccepted-result")]
    [InlineData("not-run")]
    [InlineData("unrecorded-identity")]
    [InlineData("missing-trx")]
    public void PreTesterRedWithoutUsableProvenanceKeepsDeveloperDispatch(string fault)
    {
        using var fixture = new Fixture(seedReceipt: false);
        fixture.ReportAndSeed();
        fixture.SeedPreTesterRed("red", fault);
        fixture.AdvanceToDecision();

        Assert.Empty(fixture.Requests);
        Assert.Empty(fixture.RequestMarkers);
        Assert.Equal(fixture.Developer.Id, Assert.Single(fixture.Retried));
        Assert.Equal(ReviewFindingState.Open, fixture.Finding.State);
        Assert.Empty(fixture.Notices);
    }

    [Fact]
    public void RedRerunWithGreenBaselineRoutesDeveloper()
    {
        using var fixture = new Fixture(candidateOutcome: "Failed");
        fixture.ReportAndSeed();
        fixture.AdvanceToDecision();
        Assert.Equal(fixture.Developer.Id, Assert.Single(fixture.Retried));
        Assert.Contains("ACTIONABLE_CANDIDATE_RED", Assert.Single(fixture.RetryMessages));
        Assert.Contains(fixture.Finding.EvidenceOutcome!.ReceiptId!, fixture.RetryMessages.Single());
        Assert.Equal(ReviewFindingState.Open, fixture.Finding.State);
        Assert.Single(fixture.Requests);
        Assert.Empty(fixture.Notices);
        Assert.DoesNotContain(fixture.Goal.Timeline, evt => evt.Message.StartsWith("FINDING_RECEIPT_CLOSURE", StringComparison.Ordinal));
    }

    [Fact]
    public void InDiffFindingKeepsTodaysDeveloperDispatch()
    {
        using var fixture = new Fixture(changed: [TestPath]);
        fixture.ReportAndSeed();
        fixture.AdvanceToDecision();
        Assert.Empty(fixture.Requests);
        Assert.Empty(fixture.RequestMarkers);
        Assert.Equal(fixture.Developer.Id, Assert.Single(fixture.Retried));
        Assert.Equal(RetryCause.NewSourceFinding, Assert.Single(fixture.RetryCauses));
        using var control = new Fixture(changed: [TestPath], seedReceipt: false);
        control.ReportAndSeed();
        control.AdvanceToDecision();
        Assert.Equal(control.RetryCauses, fixture.RetryCauses);
        Assert.Equal(control.RetryMessages.Single().Replace(control.Tester.Id.Value[..8], "tester"),
            fixture.RetryMessages.Single().Replace(fixture.Tester.Id.Value[..8], "tester"));
        using var outside = new Fixture();
        outside.ReportAndSeed();
        outside.AdvanceToDecision();
        Assert.Single(outside.Requests);
        Assert.Empty(outside.Retried);
    }

    [Fact]
    public void SecondOccurrenceIsNotAutoResolved()
    {
        using var fixture = new Fixture();
        fixture.ReportAndSeed();
        fixture.AdvanceToDecision();
        Assert.Equal(ReviewFindingState.Resolved, fixture.Finding.State);
        fixture.ReopenOnCandidate(LaterSha);
        fixture.AdvanceToDecision();
        Assert.Single(fixture.Requests);
        Assert.Single(fixture.RequestMarkers);
        Assert.Single(fixture.Notices);
        Assert.Equal(fixture.Developer.Id, Assert.Single(fixture.Retried));
        Assert.Equal(ReviewFindingState.Open, fixture.Finding.State);
    }

    [Fact]
    public void GreenCandidateRedBaselineKeepsFindingOpen()
    {
        using var fixture = new Fixture(baselineOutcome: "Failed");
        fixture.ReportAndSeed();
        fixture.AdvanceToDecision();
        Assert.Equal(ReviewFindingState.Open, fixture.Finding.State);
        Assert.NotEqual(Reason, fixture.Finding.EvidenceOutcome!.DecisionReason);
        Assert.Empty(fixture.Notices);
        Assert.Equal(fixture.Developer.Id, Assert.Single(fixture.Retried));
        Assert.Contains("Candidate green; Baseline red", Assert.Single(fixture.RetryMessages));
        Assert.DoesNotContain(fixture.Goal.Timeline, evt => evt.Message.StartsWith("FINDING_RECEIPT_CLOSURE", StringComparison.Ordinal));
    }

    [Fact]
    public void BothRedRequireInheritedOperatorEvidence()
    {
        using var fixture = new Fixture(candidateOutcome: "Failed", baselineOutcome: "Failed");
        fixture.ReportAndSeed();
        fixture.AdvanceToDecision();
        Assert.Empty(fixture.Retried);
        Assert.Contains("inherited", Assert.Single(fixture.Escalations), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(ReviewFindingState.Open, fixture.Finding.State);
        Assert.Empty(fixture.Notices);
    }

    [Theory]
    [InlineData("NotExecuted", Identity)]
    [InlineData("Passed", "Fixture.OutsideDiffTests.Case(train: True)")]
    [InlineData("Passed", "Fixture.OutsideDiffTests.AnotherCase")]
    public void ClassGreenWithoutExactPassedIdentityCannotResolve(string outcome, string executedIdentity)
    {
        using var fixture = new Fixture(candidateOutcome: outcome, executedIdentities: [executedIdentity]);
        fixture.ReportAndSeed();
        fixture.AdvanceToDecision();
        Assert.Single(fixture.Requests);
        Assert.Equal(ReviewFindingState.Open, fixture.Finding.State);
        Assert.NotEqual(Reason, fixture.Finding.EvidenceOutcome!.DecisionReason);
        Assert.Empty(fixture.Notices);
        Assert.Equal(fixture.Tester.Id, Assert.Single(fixture.Retried));
        Assert.StartsWith("finding evidence-on-demand:", Assert.Single(fixture.RetryMessages));
    }

    [Fact]
    public void MixedInsideOutsideFindingDoesNotRerun()
    {
        using var fixture = new Fixture(changed: [TestPath], namedIdentities: [Identity, OtherIdentity]);
        fixture.ReportAndSeed();
        fixture.AdvanceToDecision();
        Assert.Empty(fixture.Requests);
        Assert.Equal(fixture.Developer.Id, Assert.Single(fixture.Retried));
    }

    [Fact]
    public void CrossClassFindingUsesOneRequestAndRequiresBothIdentities()
    {
        using var fixture = new Fixture(namedIdentities: [Identity, OtherIdentity]);
        fixture.ReportAndSeed();
        fixture.AdvanceToDecision();
        var request = Assert.Single(fixture.Requests);
        Assert.Equal("Infrastructure.Tests:Fixture.OtherTests; Infrastructure.Tests:Fixture.OutsideDiffTests", request);
        Assert.Single(fixture.RequestMarkers);
        Assert.Single(fixture.ReceiptMarkers);
        Assert.Equal(ReviewFindingState.Resolved, fixture.Finding.State);
        Assert.Empty(fixture.Retried);
        var notice = Assert.Single(fixture.Notices);
        Assert.Contains(Identity, notice.Body);
        Assert.Contains(OtherIdentity, notice.Body);
    }

    [Fact]
    public void CrossClassPartialGreenCannotResolveFinding()
    {
        using var fixture = new Fixture(namedIdentities: [Identity, OtherIdentity], executedIdentities: [Identity]);
        fixture.ReportAndSeed();
        fixture.AdvanceToDecision();
        Assert.Contains("Fixture.OtherTests", Assert.Single(fixture.Requests));
        Assert.Single(fixture.RequestMarkers);
        Assert.Equal(ReviewFindingState.Open, fixture.Finding.State);
        Assert.NotEqual(Reason, fixture.Finding.EvidenceOutcome!.DecisionReason);
        Assert.Empty(fixture.Notices);
        Assert.Equal(fixture.Tester.Id, Assert.Single(fixture.Retried));
    }

    [Fact]
    public void RelaunchReusesRecordedAttempt()
    {
        using var fixture = new Fixture();
        fixture.ReportAndSeed();
        fixture.Advance();
        Assert.Single(fixture.RequestMarkers);
        Assert.Equal(ReviewFindingState.Open, fixture.Finding.State);
        fixture.ReloadAndRecreateDriver();
        Assert.Equal(FirstSha, fixture.Developer.LastDispatch!.ResultCommit);
        fixture.AdvanceToDecision();
        Assert.Single(fixture.Requests);
        Assert.Single(fixture.RequestMarkers);
        Assert.Equal(ReviewFindingState.Resolved, fixture.Finding.State);
        Assert.Single(fixture.Notices);
    }

    [Theory]
    [InlineData("empty-diff")]
    [InlineData("unresolved-source")]
    [InlineData("unmatched-case")]
    [InlineData("stale-receipt")]
    [InlineData("no-receipt")]
    public void UncertainEligibilityKeepsDeveloperRoute(string uncertainty)
    {
        using var fixture = new Fixture(changed: uncertainty == "empty-diff" ? [] : null,
            seedReceipt: uncertainty != "no-receipt");
        fixture.ReportAndSeed(receiptSha: uncertainty == "stale-receipt" ? LaterSha : null,
            receiptIdentities: uncertainty == "unmatched-case" ? [Identity.Replace("False", "True")] : null);
        if (uncertainty == "unresolved-source") fixture.RemoveTestSources();
        fixture.AdvanceToDecision();
        Assert.Empty(fixture.Requests);
        Assert.Equal(fixture.Developer.Id, Assert.Single(fixture.Retried));
    }

    [Fact]
    public void BaselineMayExecuteDifferentIdentityInSameClass()
    {
        using var fixture = new Fixture(baselineIdentities: ["Fixture.OutsideDiffTests.OtherCase"]);
        fixture.ReportAndSeed();
        fixture.AdvanceToDecision();
        Assert.Equal(ReviewFindingState.Resolved, fixture.Finding.State);
        Assert.Single(fixture.Notices);
        Assert.Empty(fixture.Retried);
    }

    [Fact]
    public void BaselineMissingOneClassCannotResolveFinding()
    {
        using var fixture = new Fixture(namedIdentities: [Identity, OtherIdentity], baselineIdentities: [Identity]);
        fixture.ReportAndSeed();
        fixture.AdvanceToDecision();
        Assert.Contains("Fixture.OtherTests", Assert.Single(fixture.Requests));
        Assert.Equal(ReviewFindingState.Open, fixture.Finding.State);
        Assert.NotEqual(Reason, fixture.Finding.EvidenceOutcome!.DecisionReason);
        Assert.Empty(fixture.Notices);
        Assert.Equal(fixture.Tester.Id, Assert.Single(fixture.Retried));
    }

    [Fact]
    public void TamperedTrxCannotAuthorizeClosure()
    {
        using var fixture = new Fixture();
        var arm = ConductorDriver.CreateFindingEvidenceArmReceipt(fixture.RunEvidence("fixture").Arms!
            .Single(item => item.Arm == FindingEvidenceArm.Candidate));
        Assert.True(FindingEvidenceTrxCoverage.HasPassedIdentities(arm, [Identity]));
        File.AppendAllText(Assert.Single(arm.TestResultPaths!), " ");
        Assert.False(FindingEvidenceTrxCoverage.HasPassedIdentities(arm, [Identity]));
    }

    [Fact]
    public void UnresolvedRerunAllowsLaterCandidate()
    {
        using var fixture = new Fixture(candidateOutcome: "Passed", baselineOutcome: "Failed");
        fixture.ReportAndSeed();
        fixture.AdvanceToDecision();
        Assert.Equal(ReviewFindingState.Open, fixture.Finding.State);
        fixture.ReopenOnCandidate(LaterSha);
        fixture.AdvanceToDecision();
        Assert.Equal(2, fixture.Requests.Count);
        Assert.Equal(2, fixture.RequestMarkers.Count());
        Assert.Empty(fixture.Notices);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = ConductorDriverTests.CreateTempDirectory();
        private ConductorDriver _driver = null!;
        private readonly string _candidateOutcome;
        private readonly string _baselineOutcome;
        private readonly string[] _names;
        private readonly string[] _executed;
        private readonly string[] _baselineIdentities;
        private readonly IReadOnlyList<string> _changed;
        private readonly bool _seedReceipt;
        private int _tick;
        public string Sha { get; private set; } = FirstSha;
        public AgentOrchestratorKernel Kernel { get; private set; }
        public Goal Goal { get; private set; }
        public TaskSpec Tester => Goal.Tasks.Single(task => task.RequiredRole == AgentRole.Tester);
        public TaskSpec Developer => Goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
        public ReviewFinding Finding => Assert.Single(Tester.VerificationHistory.Last().MergedReviewFindings!);
        public List<string> Requests { get; } = [];
        public List<TaskId> Retried { get; } = [];
        public List<string> RetryMessages { get; } = [];
        public List<RetryCause> RetryCauses { get; } = [];
        public List<string> Escalations { get; } = [];
        public IEnumerable<ProgressEvent> RequestMarkers => Goal.Timeline.Where(evt =>
            evt.Kind == ProgressKind.FindingEvidenceRequestRecorded && evt.Message.StartsWith(Reason + "; phase=request;", StringComparison.Ordinal));
        public IEnumerable<ProgressEvent> ReceiptMarkers => Goal.Timeline.Where(evt =>
            evt.Kind == ProgressKind.FindingEvidenceRunRecorded && evt.Message.StartsWith(Reason + "; phase=receipt;", StringComparison.Ordinal));
        public IReadOnlyList<CollaborationItem> Notices => CollaborationItemStore.ForDirectory(Path.Combine(_root, ".orchestrator"))
            .ListAsync(Goal.Id.Value).GetAwaiter().GetResult().Where(item => item.Type == CollaborationItemType.Notice).ToArray();

        public Fixture(string candidateOutcome = "Passed", string baselineOutcome = "Passed",
            IReadOnlyList<string>? changed = null, string[]? namedIdentities = null,
            string[]? executedIdentities = null, bool seedReceipt = true, string[]? baselineIdentities = null)
        {
            _candidateOutcome = candidateOutcome;
            _baselineOutcome = baselineOutcome;
            _changed = changed ?? ["src/Changed.cs"];
            _names = namedIdentities ?? [Identity];
            _executed = executedIdentities ?? _names;
            _baselineIdentities = baselineIdentities ?? _names;
            _seedReceipt = seedReceipt;
            Directory.CreateDirectory(Path.Combine(_root, "tests/Mcg.AgentOrchestrator.Infrastructure.Tests"));
            Directory.CreateDirectory(Path.Combine(_root, ".orchestrator"));
            File.WriteAllText(Path.Combine(_root, TestPath), "namespace Fixture; public class OutsideDiffTests { public void Case() {} }");
            File.WriteAllText(Path.Combine(_root, "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/OtherTests.cs"),
                "namespace Fixture; public class OtherTests { public void Runs() {} }");
            (Kernel, Goal) = SoftwareGoal();
            foreach (var task in Goal.Tasks.TakeWhile(task => task.Id != Tester.Id))
                PassVerification(Kernel, Goal, task, hasCommittedChanges: task.RequiredRole == AgentRole.Developer);
            Kernel.RecordDispatchResultCommit(Goal.Id, Developer.Id, Sha);
            CreateDriver();
        }

        private void CreateDriver() => _driver = MakeDriver(
            executionDirectory: _root,
            getLandingFileScopes: _ => _changed,
            getPreReviewEvidenceContext: _ => NoPreReviewContext(Sha),
            focusedEvidenceAttemptCoordinator: new ConductorParallelAcceptanceAttemptCoordinator(
                Path.Combine(_root, "attempts"), TrunkBranchName.Default, runInline: true, acquireStableSlotLease: (_, _) => null),
            runFocusedEvidence: (_, request) => { Requests.Add(request); return RunEvidence(request); },
            retryTaskWithCause: (goalId, taskId, message, kind, cause) =>
            {
                Retried.Add(taskId);
                RetryMessages.Add(message);
                RetryCauses.Add(cause);
                return Kernel.RetryTask(goalId, taskId, message, retryRoundKind: kind, retryCause: cause);
            },
            writeEscalation: (_, _, message) => Escalations.Add(message),
            recordFindingEvidenceRequest: (goalId, taskId, message) => Kernel.RecordFindingEvidenceRequest(goalId, taskId, message),
            recordFindingEvidenceRun: (goalId, taskId, message) => Kernel.RecordFindingEvidenceRun(goalId, taskId, message),
            recordFindingEvidenceOutcome: (goalId, taskId, stableId, outcome, receipt) =>
                Kernel.RecordFindingEvidenceOutcome(goalId, taskId, stableId, outcome, receipt));

        public void ReportAndSeed(string? receiptSha = null, string[]? receiptIdentities = null)
        {
            var finding = new ReviewFinding("outside-finding", ReviewFindingState.Open,
                new ReviewFindingLocation(TestPath, "Case"), "Failed: " + string.Join("; ", _names), Category: FindingCategory.Correctness);
            Report([finding], "fail - " + string.Join(", ", _names), "needs-work");
            Assert.Null(Tester.LastVerification!.ReviewFindingContractViolation);
            if (!_seedReceipt) return;
            var sourceSha = receiptSha ?? Sha;
            var red = Arm(FindingEvidenceArm.Candidate, "Failed", receiptIdentities ?? _names, sourceSha);
            var selections = (receiptIdentities ?? _names).Select(name =>
                new FindingEvidenceSelection("Infrastructure.Tests", name[..name.LastIndexOf('.')])).Distinct().ToArray();
            var receipt = new FindingEvidenceReceipt("original-red-" + sourceSha[..7], sourceSha, new(selections), true, false,
                "original conductor red", [ConductorDriver.CreateFindingEvidenceArmReceipt(red)]);
            Kernel.RecordFindingEvidenceOutcome(Goal.Id, Tester.Id, finding.StableId,
                new FindingEvidenceOutcome(true, receipt.ReceiptId, ResultReason: FindingEvidenceOutcomeReason.CandidateRed), receipt);
        }

        public void SeedPreTesterRed(string outcome, string? fault = null)
        {
            var selection = "Infrastructure.Tests:Fixture.OutsideDiffTests";
            var red = Arm(FindingEvidenceArm.Candidate, "Failed",
                fault == "unrecorded-identity" ? ["Fixture.OutsideDiffTests.Case(train: True)"] : _names, Sha);
            var evidence = new FocusedEvidenceRunResult(selection, fault != "unaccepted-result", false,
                "pre-Tester original red", red.Checks, Arms: [red]);
            var artifact = new ConductorParallelAcceptanceRunArtifact("focused-evidence", null, null, null, null,
                null, fault == "stale-result" ? LaterSha : Sha, LaterSha, null, null,
                FocusedEvidence: evidence);
            var resultPath = Path.Combine(_root, "pre-tester.result.json");
            if (fault != "missing-result")
                File.WriteAllText(resultPath, JsonSerializer.Serialize(artifact, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            if (fault == "missing-trx")
                foreach (var path in red.Checks.SelectMany(check => check.TestResultPaths ?? [])) File.Delete(path);
            Kernel.RecordFindingEvidenceRun(Goal.Id, Tester.Id, PreTesterEvidenceIndexLines.FormatMarker(
                new(outcome, Sha, "pre-tester-original-red", [selection], fault == "not-run" ? [selection] : [],
                    resultPath, _names)));
        }

        private void Report(IReadOnlyList<ReviewFinding> findings, string tests, string verdict)
        {
            DispatchTask(Kernel, Goal, Tester, "test", reviewFindingTouchedAnchors: findings.Select(finding => finding.Location).ToArray(),
                baseCommit: Sha);
            var stdout = string.Join(Environment.NewLine, "WORKER_RESULT:", "files: none", "commands: inspect",
                "tests: " + tests, "commit: none", "blockers: none", "findings: " + JsonSerializer.Serialize(findings),
                "touched_anchors: []", "verdict: " + verdict, "model_fit: fixture/model - adequate - verification",
                "skills: none", "confidence: high", "END_WORKER_RESULT");
            Kernel.RecordDispatchExecutionResult(Goal.Id, Tester.Id, new TaskVerificationRecord(
                "test", "C:\\tmp", 0, stdout, "", DateTimeOffset.UtcNow, WorkerResultPresent: true));
        }

        public void ReportLaterSuccessfulTester()
        {
            Kernel.RetryTask(Goal.Id, Tester.Id, "later verification on same candidate", RetryCause.ContractClarification);
            Report([], "deferred - conductor receipt proves resolution", "pass");
        }

        public void ReopenOnCandidate(string sha)
        {
            // A later candidate needs a new Developer attempt, not a relabelled old result.
            if (Developer.LastVerification?.Succeeded == true)
                Kernel.RetryTask(Goal.Id, Developer.Id, "produce later candidate", RetryCause.ContractClarification);
            DispatchTask(Kernel, Goal, Developer, baseCommit: Sha);
            Kernel.RecordTaskVerification(Goal.Id, Developer.Id, new TaskVerificationRecord(
                "test.exe", "C:\\tmp", 0, "ok", "", DateTimeOffset.UtcNow, HasCommittedChanges: true));
            Kernel.RecordDispatchResultCommit(Goal.Id, Developer.Id, sha);
            Assert.Equal(Sha, Developer.LastDispatch!.BaseCommit);
            Assert.Equal(sha, Developer.LastDispatch.ResultCommit);
            Assert.True(Developer.LastVerification!.Succeeded);
            Sha = sha;
            Kernel.RetryTask(Goal.Id, Tester.Id, "verify later candidate", RetryCause.ContractClarification);
            ReportAndSeed();
        }

        public void ReloadAndRecreateDriver()
        {
            var goalId = Goal.Id;
            Kernel = AgentOrchestratorKernel.FromSnapshot(JsonSerializer.Deserialize<OrchestratorSnapshot>(
                JsonSerializer.Serialize(Kernel.ExportSnapshot()))!);
            Goal = Kernel.GetGoal(goalId);
            CreateDriver();
        }

        public void Advance()
        {
            _driver.BeginTick(Kernel, ++_tick);
            _driver.AdvanceOnce(Goal, ConductorAutonomyPolicy.Permissive);
        }

        public void AdvanceToDecision()
        {
            var retries = Retried.Count;
            var escalations = Escalations.Count;
            for (var i = 0; i < 8 && Tester.Status != WorkTaskStatus.Completed &&
                Retried.Count == retries && Escalations.Count == escalations; i++) Advance();
        }

        public FocusedEvidenceRunResult RunEvidence(string request)
        {
            var candidate = Arm(FindingEvidenceArm.Candidate, _candidateOutcome, _executed, Sha);
            var baseline = Arm(FindingEvidenceArm.Baseline, _baselineOutcome, _baselineIdentities, LaterSha);
            var passed = candidate.Passed && baseline.Passed;
            return new(request, true, passed, "candidate=" + _candidateOutcome + "; baseline=" + _baselineOutcome,
                candidate.Checks, Arms: [candidate, baseline],
                OutcomeReason: passed ? FindingEvidenceOutcomeReason.ValidEvidence : FindingEvidenceOutcomeReason.CandidateRed);
        }

        private FocusedEvidenceArmRunResult Arm(FindingEvidenceArm arm, string outcome, string[] identities, string sha)
        {
            var path = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".trx");
            XNamespace trx = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
            var tests = identities.Select((name, index) => new { Name = name, Id = "test-" + index,
                Method = AcceptanceTrxTestIdentityResolver.NormalizeSelector(name) }).ToArray();
            var executed = outcome == "NotExecuted" ? 0 : tests.Length;
            var failed = outcome == "Failed" ? tests.Length : 0;
            new XDocument(new XElement(trx + "TestRun",
                new XElement(trx + "TestDefinitions", tests.Select(test => new XElement(trx + "UnitTest",
                    new XAttribute("id", test.Id), new XElement(trx + "TestMethod",
                        new XAttribute("className", test.Method[..test.Method.LastIndexOf('.')]),
                        new XAttribute("name", test.Method[(test.Method.LastIndexOf('.') + 1)..]))))),
                new XElement(trx + "Results", tests.Select(test => new XElement(trx + "UnitTestResult",
                    new XAttribute("testId", test.Id), new XAttribute("testName", test.Name), new XAttribute("outcome", outcome)))),
                new XElement(trx + "ResultSummary", new XElement(trx + "Counters", new XAttribute("total", tests.Length),
                    new XAttribute("executed", executed), new XAttribute("passed", executed - failed), new XAttribute("failed", failed)))))
                .Save(path);
            var passed = outcome != "Failed";
            var check = new AcceptanceCheckResult("focused", passed, passed ? 0 : 1, "fixture result",
                TestResultPaths: [path], FailingTestIdentities: passed ? [] : identities, ExecutedTestCount: executed);
            return new(arm, sha, passed ? FindingEvidenceArmDisposition.Green : FindingEvidenceArmDisposition.Red,
                true, passed, outcome, [check]);
        }

        public void RemoveTestSources()
        {
            File.Delete(Path.Combine(_root, TestPath));
            File.Delete(Path.Combine(_root, "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/OtherTests.cs"));
        }

        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
