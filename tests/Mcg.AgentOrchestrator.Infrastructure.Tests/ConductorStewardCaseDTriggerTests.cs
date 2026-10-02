using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each fixture owns its filesystem and SQLite stores; time and git head are injected.
public sealed class ConductorStewardCaseDTriggerTests
{
    [Xunit.Theory]
    [Xunit.InlineData(true)]
    [Xunit.InlineData(false)]
    public void Gate_reopen_without_commits_emits_D_with_failure_evidence(bool rejection)
    {
        using var harness = new StewardCaseDHarness();
        harness.Seed(rejection: rejection);

        Assert.Null(harness.Goal.LatestAcceptanceFailure);
        Assert.Equal(StewardCaseDHarness.Sha, harness.Goal.RetainedAcceptanceFailure?.BranchHeadSha);
        var trigger = Assert.Single(harness.Detector.Detect(harness.Goal));

        Assert.Equal(ConductorStewardTriggerKind.DeveloperGateReopenNoCommit, trigger.Kind);
        Assert.Equal("D", trigger.CaseLetter);
        Assert.Equal(harness.Task.Id.Value, trigger.TaskId);
        Assert.Equal(StewardCaseDHarness.Sha, trigger.CandidateSha);
        Assert.Contains("failed-lane", trigger.Evidence, StringComparison.Ordinal);
        Assert.Contains(StewardCaseDHarness.TestIdentity, trigger.Evidence, StringComparison.Ordinal);
        Assert.Contains("Assert.Equal expected 1 actual 2", trigger.Evidence, StringComparison.Ordinal);
        Assert.Contains(StewardCaseDHarness.GenuineReason, trigger.Evidence, StringComparison.Ordinal);
        Assert.Contains("src/Changed.cs", trigger.Evidence, StringComparison.Ordinal);
        Assert.Contains("goalId=other-goal", trigger.Evidence, StringComparison.Ordinal);
        Assert.Contains("insideChangedPaths=False", trigger.Evidence, StringComparison.Ordinal);
        Assert.Contains($"failing-test={StewardCaseDHarness.TestIdentity}", trigger.EvidenceReferences);
        Assert.Equal(StewardCaseDHarness.WorkerResult, trigger.WorkerResult);
        Assert.Empty(harness.Detector.Detect(harness.Goal, (_, _, _) => false));
    }

    [Xunit.Theory]
    [Xunit.InlineData("moved-head")]
    [Xunit.InlineData("unrelated-retry")]
    [Xunit.InlineData("committed-result")]
    [Xunit.InlineData("no-blocker")]
    [Xunit.InlineData("result-has-commit")]
    public void Ineligible_no_commit_round_does_not_emit_D(string invalidCase)
    {
        using var harness = new StewardCaseDHarness();
        harness.Seed(rejection: false, acceptanceRetry: invalidCase != "unrelated-retry",
            resultCommit: invalidCase == "committed-result" ? StewardCaseDHarness.OtherSha : null,
            workerResult: invalidCase switch
            {
                "no-blocker" => StewardCaseDHarness.WorkerResult.Replace("exact-blocker - cause undetermined", "none"),
                "result-has-commit" => StewardCaseDHarness.WorkerResult.Replace("commit: none", $"commit: {StewardCaseDHarness.OtherSha}"),
                _ => null
            });
        if (invalidCase == "moved-head") harness.Head = StewardCaseDHarness.OtherSha;

        Assert.Empty(harness.Detector.Detect(harness.Goal));
    }

    [Xunit.Fact]
    public void Confirmed_candidate_red_keeps_A_precedence_over_D()
    {
        using var harness = new StewardCaseDHarness();
        harness.Seed(confirmedRed: true);

        var trigger = Assert.Single(harness.Detector.Detect(harness.Goal));

        Assert.Equal(ConductorStewardTriggerKind.DeveloperNoChangeWithConfirmedRed, trigger.Kind);
        Assert.DoesNotContain(harness.Detector.Detect(harness.Goal), item =>
            item.Kind == ConductorStewardTriggerKind.DeveloperGateReopenNoCommit);
    }

    [Xunit.Fact]
    public void Missing_case_D_sources_preserve_route_only_detection()
    {
        using var harness = new StewardCaseDHarness();
        harness.Seed();

        Assert.Empty(new ConductorStewardTriggerDetector().Detect(harness.Goal));
    }
}

internal sealed class StewardCaseDHarness : IDisposable
{
    internal const string Sha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    internal const string OtherSha = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    internal const string TestIdentity = "OutsideTests.Fails";
    internal const string GenuineReason = "Failure has neither a recorded infrastructure signature nor a prior cross-goal occurrence.";
    internal const string WorkerResult = "WORKER_RESULT:\r\nfiles: none\r\ncommands: inspect\r\ntests: deferred - OutsideTests\r\ncommit: none\r\nblockers: exact-blocker - cause undetermined\r\nassigned_scope_complete: false\r\nEND_WORKER_RESULT";
    internal const long Version = 7;

    internal StewardCaseDHarness(AgentRole role = AgentRole.Developer)
    {
        Root = Path.Combine(Path.GetTempPath(), $"mcg-steward-d-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Root);
        Kernel = new AgentOrchestratorKernel(Clock);
        Task = new TaskSpec(TaskId.New(), "Inspect reopened gate failure", role);
        Goal = Kernel.CreateGoal("Steward case D", [Task]);
        Kernel.ActivateGoal(Goal.Id, AgentCatalog.Default().Agents);
        Kernel.SetGoalRefinedSpec(Goal.Id, new RefinedSpec("Recover unchanged candidate",
            ["The unchanged candidate passes a fresh gate."], VerificationClass.TestVerifiable, [], []));
        Index = new AcceptanceFailingTestIndex(Path.Combine(Root, AcceptanceFailingTestIndex.FileName));
        Sources = new ConductorStewardCaseDSources(_ => Head,
            (goal, sha) => GoalOperationJournal.Read(Root, goal.Id).Entries.LastOrDefault(entry =>
                entry.Operation == GoalOperationJournal.AcceptanceApparatusGenuineOperation && entry.BranchHeadSha == sha)?.Detail,
            (_, _) => ["src/Changed.cs"], _ => [TrxPath], Index);
        Detector = new ConductorStewardTriggerDetector(caseDSources: Sources);
        Intents = new SqliteOperatorIntentStore(Path.Combine(Root, "operator-intents.db"), Path.Combine(Root, "logs"));
        Decisions = CollaborationItemStore.ForDirectory(Root);
        Coordinator = CreateCoordinator();
        Triggers = new ConductorStewardTriggerStore(Path.Combine(Root, "steward-triggers.db"));
        Host = new ConductorStewardHost(Triggers, Detector, Model, Intents,
            new AdjudicationEvidenceResolver(Root), _ => Version, _ => Root,
            new GoalLifecycleEventWriter(Path.Combine(Root, "lifecycle")),
            new ConductEventLogWriter(ConductPath), utcNow: () => Clock.UtcNow, caseDSources: Sources);
    }

    internal string Root { get; }
    internal string Head { get; set; } = Sha;
    internal string TrxPath => Path.Combine(Root, "acceptance.trx");
    internal string ConductPath => Path.Combine(Root, "conduct-events.log");
    internal CaseDClock Clock { get; } = new();
    internal AgentOrchestratorKernel Kernel { get; }
    internal Goal Goal { get; }
    internal TaskSpec Task { get; }
    internal AcceptanceFailingTestIndex Index { get; }
    internal ConductorStewardCaseDSources Sources { get; }
    internal ConductorStewardTriggerDetector Detector { get; }
    internal SqliteOperatorIntentStore Intents { get; }
    internal CollaborationItemStore Decisions { get; }
    internal OperatorIntentCoordinator Coordinator { get; }
    internal ConductorStewardTriggerStore Triggers { get; }
    internal StewardFakeModel Model { get; } = new();
    internal ConductorStewardHost Host { get; }

    internal OperatorIntentCoordinator CreateCoordinator(
        Func<GoalId, string?>? headResolver = null, AcceptanceFailingTestIndex? index = null) =>
        new(Intents, utcNow: () => Clock.UtcNow, goalHeadResolver: headResolver ?? (_ => Head),
            decisions: Decisions, goalStateVersionResolver: _ => Version,
            evidenceResolver: new AdjudicationEvidenceResolver(Root), apparatusRegateIndex: index ?? Index);

    internal void Seed(bool rejection = true, bool acceptanceRetry = true,
        bool confirmedRed = false, string? resultCommit = null, string? workerResult = null)
    {
        Kernel.ReportTaskProgress(Goal.Id, Task.Id, WorkTaskStatus.Completed, "candidate ready");
        Kernel.RecordTaskVerification(Goal.Id, Task.Id,
            new TaskVerificationRecord("focused", Root, 0, "passed", "", Clock.UtcNow));
        Assert.True(Kernel.BeginGoalAcceptanceVerification(Goal.Id, "gate started"));
        Assert.True(Kernel.ReconcileGoalAcceptanceFailed(Goal.Id, ["failed-lane"], "gate failed", Sha,
            checkAttributions: [new AcceptanceCheckAttribution("failed-lane", AcceptanceFailureOrigin.Introduced, TestIdentity)]));
        GoalOperationJournal.AcceptanceApparatusGenuine(Root, Goal, Sha, OtherSha, GenuineReason);
        File.WriteAllText(TrxPath, $"""
            <TestRun><Results><UnitTestResult testName="{TestIdentity}" outcome="Failed">
            <Output><ErrorInfo><Message>Assert.Equal expected 1 actual 2</Message></ErrorInfo></Output>
            </UnitTestResult></Results></TestRun>
            """);
        Index.Append([new AcceptanceFailingTestIndexRecord(AcceptanceFailingTestIndex.ContractVersion,
            AcceptanceFailingTestIndexKinds.GateFailure, "other-goal", Clock.UtcNow,
            CandidateSha: OtherSha, TestIdentity: TestIdentity, InsideChangedPaths: false)], Clock.UtcNow);
        Clock.Advance();
        Kernel.RetryTaskAutomatically(Goal.Id, Task.Id,
            acceptanceRetry ? ConductorStewardTriggerDetector.AcceptanceRetryPrefix + " (attempt 1/2): gate failed" : "Unrelated repair",
            RetryCause.CriterionEvidenceOwnerMismatch);
        Clock.Advance();
        Kernel.RecordTaskDispatch(Goal.Id, Task.Id, new TaskDispatchRecord("test-worker", "test.exe", Root,
            Clock.UtcNow, BaseCommit: Sha, ResultCommit: resultCommit));
        Clock.Advance();
        Kernel.RecordTaskVerification(Goal.Id, Task.Id, new TaskVerificationRecord("test.exe", Root, 1,
            "diagnostic preamble\r\n" + (workerResult ?? WorkerResult) + "\r\ntrailing output",
            rejection ? "DISPATCH_REJECTED reason=no-change-evidence" : "", Clock.UtcNow,
            WorkerResultPresent: true, HasCommittedChanges: resultCommit is not null,
            FindingEvidenceReceipts: confirmedRed ? [RedReceipt()] : null,
            OrchestratorFailureReason: rejection ? "no-change-evidence" : null));
        Kernel.ReportTaskProgress(Goal.Id, Task.Id, WorkTaskStatus.Failed, "no repair found");
        Assert.Equal(WorkTaskStatus.Failed, Task.Status);
        Assert.False(Goal.IsTerminal);
    }

    internal static FindingEvidenceReceipt RedReceipt() => new("red-receipt", Sha,
        new FindingEvidenceRequest([]), true, false, "candidate RED",
        [new FindingEvidenceArmReceipt(FindingEvidenceArm.Candidate, Sha, FindingEvidenceArmDisposition.Red,
             true, false, "Assert.Equal expected 1 actual 2", FailingTestIdentities: [TestIdentity]),
         new FindingEvidenceArmReceipt(FindingEvidenceArm.Baseline, OtherSha, FindingEvidenceArmDisposition.Green,
             true, true, "baseline GREEN")]);

    internal AdjudicateOperatorIntentPayload ClosePayload(string caseLetter = "D") => new("close",
        ConductorStewardCaseDAdmission.ComposeText("Passing candidate receipt supports re-gate.", WorkerResult),
        [$"steward-case={caseLetter}", $"failing-test={TestIdentity}"], Version, Root,
        Precedent: $"steward-case={caseLetter} trigger={Goal.Id.Value}:{Task.Id.Value}:{Sha}:DeveloperGateReopenNoCommit",
        Precondition: AdjudicationPrecondition.Capture(Goal, Task));

    internal async Task<OperatorIntentRecord> EnqueueClose(string caseLetter = "D")
    {
        var id = Guid.NewGuid().ToString("N");
        return await Intents.EnqueueAsync(new OperatorIntentRecord(id, id, OperatorIntentVerbs.Adjudicate,
            Goal.Id.Value, Task.Id.Value, JsonSerializer.Serialize(ClosePayload(caseLetter), OperatorIntentJson.Options), [],
            "steward", "conductor-steward", OperatorIntentAdjudication.StewardAssurance, Clock.UtcNow,
            ActorKind: OperatorActorKind.Agent));
    }

    internal void SeedRegates(int count)
    {
        for (var i = 0; i < count; i++)
            Index.Append([new AcceptanceFailingTestIndexRecord(AcceptanceFailingTestIndex.ContractVersion,
                AcceptanceFailingTestIndexKinds.ApparatusRegate, Goal.Id.Value, Clock.UtcNow,
                CandidateSha: OtherSha, EvidenceKind: "previous-regate")], Clock.UtcNow);
    }

    public void Dispose()
    {
        Host.Stop();
        Directory.Delete(Root, recursive: true);
    }

    internal sealed class CaseDClock : IClock
    {
        public DateTimeOffset UtcNow { get; private set; } = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
        internal void Advance() => UtcNow = UtcNow.AddSeconds(1);
    }
}
