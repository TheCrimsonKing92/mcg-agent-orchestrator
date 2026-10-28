using System.Security.Cryptography;
using System.Text.Json;
using System.Xml.Linq;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

internal sealed class FreshFindingEvidenceFixture : IDisposable
{
    internal const string Candidate = "70e934810b0d94e45d596acd7b287cf798697691";
    internal const string FindingId = "lane-guard-candidate-execution";
    internal const string ExactTest = "SplitCollectionHasParallelSharedFixtureAndExactMembers";
    internal const string TestClass = "IsolatedProcessSpawningRemainderChainTests";
    internal const string Project = "Infrastructure.Tests";
    internal static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-27T18:28:26Z");
    internal readonly CandidateIdentity Identity = new("patch", "base", "manifest");
    internal readonly string Root = Path.Combine(Path.GetTempPath(), "mcg-fresh-finding", Guid.NewGuid().ToString("N"));
    internal AgentOrchestratorKernel Kernel;
    internal Goal Goal => Kernel.Goals.Single();
    internal TaskSpec Reviewer => Goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
    internal TaskSpec Tester => Goal.Tasks.Single(task => task.RequiredRole == AgentRole.Tester);
    internal int Launches;
    internal readonly List<RetryCause> Causes = [];
    internal readonly AcceptanceGateEngineSettings Settings = new()
    {
        MtpInvocations = [new() { Project = "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj" }]
    };

    internal FreshFindingEvidenceFixture(bool twoRequests = false, bool retryReview = true, bool sourceFinding = false,
        bool compositeRequest = false, bool negativeControl = false, bool unresolvedTesterFinding = false)
    {
        Directory.CreateDirectory(Root);
        Kernel = new AgentOrchestratorKernel(new FixedClock(Now.AddMinutes(5)));
        var tester = new TaskSpec(TaskId.New(), "Supply executed evidence", AgentRole.Tester);
        var reviewer = new TaskSpec(TaskId.New(), "Review exact candidate", AgentRole.Reviewer);
        var goal = Kernel.CreateGoal("Resume held review", [tester, reviewer]);
        Kernel.ActivateGoal(goal.Id, ConductorDriverTests.DefaultAgents());
        var findings = new List<ReviewFinding> { Finding(FindingId, TestClass + "." + ExactTest) };
        if (twoRequests) findings.Add(Finding("second-request", "OtherTests.ExactGuard"));
        if (compositeRequest)
        {
            findings[0] = Finding(FindingId, TestClass);
            findings[1] = Finding("second-request", "OtherTests");
        }
        if (sourceFinding) findings[0] = findings[0] with { Category = FindingCategory.Correctness };
        if (negativeControl) findings[0] = findings[0] with
        {
            EvidenceRequest = findings[0].EvidenceRequest! with
            {
                NegativeControl = FindingEvidenceNegativeControl.RevertSrc,
                RevertPaths = ["src/Guard.cs"]
            }
        };
        Record(tester, findings.Select(item => item with
            { State = unresolvedTesterFinding ? ReviewFindingState.Open : ReviewFindingState.Resolved }).ToArray(),
            Now.AddMinutes(-4), Now.AddMinutes(-3), unresolvedTesterFinding ? "needs-work" : "pass");
        var old = Receipt(FindingId, ["OldGuard"], "finding-evidence-ae20831594d6ef6afe9a1bed");
        Attach(old, FindingId);
        var reviewFindings = compositeRequest
            ? [findings[0] with { EvidenceRequest = new FindingEvidenceRequest(findings.SelectMany(item => item.EvidenceRequest!.Selections).ToArray()) }]
            : findings.ToArray();
        Record(reviewer, reviewFindings, Now.AddMinutes(-1), Now, "needs-work");
        if (retryReview) Kernel.RetryTask(goal.Id, reviewer.Id, "No source or context change", RetryCause.UnchangedContextRepeat);
    }

    private static ReviewFinding Finding(string id, string selection) => new(id, ReviewFindingState.Open,
        new ReviewFindingLocation("tests/LaneTests.cs", id), "Exact executed guard evidence is missing",
        FindingSeverity.Blocking, FindingCategory.TestEvidence,
        new FindingEvidenceRequest([new(Project, selection)]));

    private void Record(TaskSpec task, IReadOnlyList<ReviewFinding> findings, DateTimeOffset dispatchAt,
        DateTimeOffset completedAt, string verdict)
    {
        var context = task.RequiredRole == AgentRole.Reviewer
            ? FindingEvidenceReviewContext.Capture(Goal, task, Candidate, dispatchAt) : null;
        Kernel.RecordTaskDispatch(Goal.Id, task.Id, new TaskDispatchRecord("fixture", "fixture", Root, dispatchAt,
            BaseCommit: Candidate, CandidateIdentity: Identity, FindingEvidenceReviewContext: context));
        var output = $"WORKER_RESULT:\nfiles: none\ntests: deferred - {TestClass}\nblockers: none\n" +
            $"verdict: {verdict}\nfindings: {JsonSerializer.Serialize(findings)}\ntouched_anchors: []\nEND_WORKER_RESULT";
        Kernel.RecordTaskVerification(Goal.Id, task.Id, new TaskVerificationRecord("fixture", Root, 0, output, "", completedAt,
            WorkerResultPresent: true, ReviewedCommit: Candidate, CandidateIdentity: Identity, DispatchStartedAt: dispatchAt));
    }

    internal FindingEvidenceReceipt Receipt(string findingId = FindingId, string[]? methods = null,
        string id = "finding-evidence-332750923bcdeeab8ae826d8", string[]? redMethods = null)
    {
        var record = Tester.LastVerification!;
        Assert.True(WorkerResultBlockers.TryFindReviewFindingRound(record, out var round, out var diagnostic), diagnostic);
        var finding = record.MergedReviewFindings!.Single(item => item.StableId == findingId);
        var request = finding.EvidenceRequest!;
        var roundId = ConductorDriver.BuildFindingRoundFingerprint(Tester.Id, record.CompletedAt, round);
        var basis = ConductorDriver.BuildFindingEvidenceExecutionBasisIdentity(Settings);
        var requestId = ConductorDriver.BuildFindingEvidenceIdentity(request);
        methods ??= findingId == FindingId ? ["One", "Two", "Three", "Four", "Five", ExactTest] : ["ExactGuard"];
        var testClass = findingId == FindingId ? TestClass : "OtherTests";
        var arms = new List<FindingEvidenceArmReceipt> { ArmReceipt(methods, testClass, red: false) };
        if (request.NegativeControl is not null) arms.Add(ArmReceipt(redMethods ?? methods, testClass, red: true));
        var disposition = new FindingEvidenceRequestDisposition(findingId, requestId, "executed-green",
            Decision: FindingEvidenceRequestDecision.Executed, CandidateSha: Candidate, FindingRoundFingerprint: roundId,
            SourceReceiptIds: [id], ExecutionBasisIdentity: basis);
        return new(id, Candidate, request, true, true, "six exact tests passed", arms,
            [disposition], roundId, basis,
            NegativeControlOutcome: request.NegativeControl is null ? null : FindingEvidenceNegativeControlOutcome.Demonstrated);
    }

    private FindingEvidenceArmReceipt ArmReceipt(string[] methods, string testClass, bool red)
    {
        XNamespace trx = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
        var path = Path.Combine(Root, Guid.NewGuid().ToString("N") + ".trx");
        new XDocument(new XElement(trx + "TestRun",
            new XElement(trx + "TestDefinitions", methods.Select((method, index) => new XElement(trx + "UnitTest",
                new XAttribute("id", index), new XAttribute("name", method),
                new XElement(trx + "TestMethod", new XAttribute("className", testClass), new XAttribute("name", method))))),
            new XElement(trx + "Results", methods.Select((method, index) => new XElement(trx + "UnitTestResult",
                new XAttribute("testId", index), new XAttribute("testName", testClass + "." + method), new XAttribute("outcome", red ? "Failed" : "Passed")))),
            new XElement(trx + "ResultSummary", new XElement(trx + "Counters",
                new XAttribute("total", methods.Length), new XAttribute("executed", methods.Length),
                new XAttribute("passed", red ? 0 : methods.Length), new XAttribute("failed", red ? methods.Length : 0))))).Save(path);
        var bytes = File.ReadAllBytes(path);
        return new(red ? FindingEvidenceArm.SourceReverted : FindingEvidenceArm.Candidate, Candidate,
                 red ? FindingEvidenceArmDisposition.Red : FindingEvidenceArmDisposition.Green, true, !red, "executed",
                 ExecutedTestCount: methods.Length, TestResultPaths: [path],
                 ReceiptArtifacts: [new("trx", path, Convert.ToHexStringLower(SHA256.HashData(bytes)), bytes.Length)]);
    }

    internal void Attach(FindingEvidenceReceipt receipt, string findingId = FindingId) =>
        Kernel.RecordFindingEvidenceOutcome(Goal.Id, Tester.Id, findingId,
            new FindingEvidenceOutcome(true, receipt.ReceiptId, ResultReason: FindingEvidenceOutcomeReason.ValidEvidence,
                RequestedSelectionIdentity: ConductorDriver.BuildFindingEvidenceIdentity(receipt.Request)), receipt);

    internal void ReexecuteTesterAfterVerdict(bool needsClosure = false)
    {
        // A new worker round requests new execution; the old conductor-owned outcome
        // is not worker-authored input or evidence for that execution.
        var findings = Tester.LastVerification!.MergedReviewFindings!
            .Select(finding => finding with { EvidenceOutcome = null,
                State = needsClosure ? ReviewFindingState.Open : finding.State }).ToArray();
        Kernel.RetryTask(Goal.Id, Tester.Id, "Run the exact requested guard", RetryCause.NewTestFinding, invalidateDownstream: false);
        Record(Tester, findings, Now.AddMinutes(1), Now.AddMinutes(2), needsClosure ? "needs-work" : "pass");
        Assert.Null(Tester.LastVerification!.ReviewFindingContractViolation);
        Assert.NotNull(Tester.LastVerification.MergedReviewFindings);
    }

    internal void RecordCompositeReuse(FindingEvidenceReceipt first, FindingEvidenceReceipt second)
    {
        Attach(first);
        Attach(second, "second-request");
        var required = Reviewer.VerificationHistory.Last().MergedReviewFindings!.Single();
        Kernel.RetryTask(Goal.Id, Tester.Id, "Supply the complete recorded composite", RetryCause.NewTestFinding, invalidateDownstream: false);
        Record(Tester, [required with { State = ReviewFindingState.Resolved }], Now.AddMinutes(1), Now.AddMinutes(2), "pass");
        var requestId = ConductorDriver.BuildFindingEvidenceIdentity(required.EvidenceRequest!);
        var outcome = new FindingEvidenceOutcome(true, second.ReceiptId, ResultReason: FindingEvidenceOutcomeReason.ValidEvidence,
            RequestedSelectionIdentity: requestId, DecisionReason: "reused-covered-green:composite",
            SourceReceiptIds: [first.ReceiptId, second.ReceiptId]);
        foreach (var receipt in new[] { first, second })
            Kernel.RecordFindingEvidenceOutcome(Goal.Id, Tester.Id, FindingId, outcome, receipt);
        Assert.True(WorkerResultBlockers.TryFindReviewFindingRound(Tester.LastVerification, out var round, out _));
        Kernel.RecordFindingEvidenceRequestDispositions(Goal.Id, Tester.Id,
            [new(FindingId, requestId, "covered-by-receipt", "composite", FindingEvidenceRequestDecision.CoveredByReceipt,
                Candidate, ConductorDriver.BuildFindingRoundFingerprint(Tester, round),
                SourceReceiptIds: [first.ReceiptId, second.ReceiptId],
                ExecutionBasisIdentity: ConductorDriver.BuildFindingEvidenceExecutionBasisIdentity(Settings))]);
    }

    internal bool Select(out FindingEvidenceReviewContinuation? continuation, out string reason) =>
        FindingEvidenceReceiptSelector.TrySelectReviewContinuation(Goal, Reviewer, Candidate, Identity,
            request => request, ConductorDriver.BuildFindingEvidenceExecutionBasisIdentity(Settings), out continuation, out reason);

    internal ConductorDriver Driver()
    {
        var driver = ConductorDriverTests.MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            getPreReviewEvidenceContext: _ => ConductorDriverTests.NoPreReviewContext(Candidate),
            getFindingEvidenceEngineSettings: _ => Settings,
            retryTaskWithCause: (goalId, taskId, message, round, cause) =>
            {
                Causes.Add(cause);
                return Kernel.RetryTaskAutomatically(goalId, taskId, message, cause, retryRoundKind: round);
            },
            dispatchAndStart: _ =>
            {
                if (Reviewer.Status is not (WorkTaskStatus.Pending or WorkTaskStatus.Assigned))
                    return DispatchStartOutcome.EmptyBatch("No ready Reviewer task");
                Launches++;
                Kernel.RecordTaskDispatch(Goal.Id, Reviewer.Id, new TaskDispatchRecord("fixture", "review", Root, Now.AddMinutes(10),
                    CandidateIdentity: Identity, FindingEvidenceReviewContext: FindingEvidenceReviewContext.Capture(Goal, Reviewer, Candidate, Now.AddMinutes(10)),
                    FindingEvidenceReviewContinuation: Reviewer.FindingEvidenceReviewContinuation));
                return DispatchStartOutcome.Started();
            });
        driver.OverrideCandidateIdentityResolverForTests(_ => Identity);
        driver.BeginTick(Kernel, 1);
        return driver;
    }

    internal void Reload() => Kernel = AgentOrchestratorKernel.FromSnapshot(
        JsonSerializer.Deserialize<OrchestratorSnapshot>(JsonSerializer.Serialize(Kernel.ExportSnapshot()))!, new FixedClock(Now.AddMinutes(5)));

    public void Dispose() => Directory.Delete(Root, recursive: true);
    private sealed record FixedClock(DateTimeOffset UtcNow) : IClock;
}
