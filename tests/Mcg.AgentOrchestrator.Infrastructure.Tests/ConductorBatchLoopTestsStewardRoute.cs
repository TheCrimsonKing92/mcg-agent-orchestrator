using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(TestCollections.DotnetBuildSlots)]
public sealed class ConductorBatchLoopTestsStewardRoute : ConductorBatchLoopTests
{
    public ConductorBatchLoopTestsStewardRoute(ITestOutputHelper output) : base(output) { }

    [Xunit.Theory]
    [Xunit.InlineData("A", "NewTestFinding", "reversible")]
    [Xunit.InlineData("B", "ContractClarification", "reversible-with-cost")]
    [Xunit.InlineData("C", "NewTestFinding", "reversible")]
    public async Task Three_recovery_triggers_submit_agent_adjudication_and_record_decision(
        string caseLetter, string cause, string reversibility)
    {
        using var harness = new StewardHarness(caseLetter);
        harness.Seed(caseLetter);
        var output = JsonSerializer.Serialize(new
        {
            kind = "route",
            targetTaskId = harness.Task.Id.Value,
            cause,
            text = "Named failure diagnosis",
            instruction = "Apply the named repair and check the evidence",
            evidenceReferences = new[] { "worker-output=receipt-1" },
            reversibility,
            precedent = "model-proposed-example"
        });
        harness.Model.Reply(output);
        var ticks = new List<BatchTickSummary>();
        var roundCompleted = false;
        void CompleteModelRound()
        {
            if (roundCompleted) return;
            Xunit.Assert.NotNull(harness.Host.CurrentRound);
            harness.Model.Started.Task.GetAwaiter().GetResult();
            harness.Host.CurrentRound!.GetAwaiter().GetResult();
            roundCompleted = true;
        }
        var stopPath = Path.Combine(harness.Root, "stop.signal");
        var summary = new ConductorBatchLoop(operatorIntents: harness.Coordinator)
            .WithSteward(harness.Host).Run(
                harness.Kernel, MakeDriver(), ConductorAutonomyPolicy.Conservative,
                stopPath, maxIterations: 2,
                watchInterval: TimeSpan.FromMilliseconds(1),
                sleepFunc: _ =>
                {
                    if (harness.Task.Status == WorkTaskStatus.Assigned) return true;
                    CompleteModelRound();
                    return false;
                },
                keepAliveWhenIdle: true, persistGoalTick: (_, _) => { },
                onTick: tick =>
                {
                    ticks.Add(tick);
                    if (harness.Task.Status == WorkTaskStatus.Assigned)
                        File.WriteAllText(stopPath, "stop");
                    else
                        CompleteModelRound();
                });
        Xunit.Assert.InRange(summary.Ticks, 1, 2);
        Xunit.Assert.Equal(1, harness.Model.Calls);
        var intent = (await harness.Intents.ListForGoalAsync(harness.Goal.Id.Value)).Single();
        Xunit.Assert.Equal(OperatorIntentVerbs.Adjudicate, intent.Verb);
        Xunit.Assert.Equal(OperatorActorKind.Agent, intent.ActorKind);
        Xunit.Assert.Equal(OperatorIntentAdjudication.StewardAssurance, intent.AuthenticationAssurance);
        Xunit.Assert.Equal(OperatorIntentStatus.Applied, intent.Status);
        Xunit.Assert.Equal(WorkTaskStatus.Assigned, harness.Task.Status);
        Xunit.Assert.Equal(Enum.Parse<RetryCause>(cause), harness.Task.PendingRetryCause);
        Xunit.Assert.Contains("Named failure diagnosis", harness.Task.AcceptedRetryFeedback?.Message);
        Xunit.Assert.Contains("Current refined acceptance criteria", harness.Task.AcceptedRetryFeedback?.Message);
        Xunit.Assert.Contains("Decision procedure", harness.Task.AcceptedRetryFeedback?.Message);
        var decision = await harness.Decisions.GetDecisionStateAsync($"adjudicate-{intent.Id}");
        Xunit.Assert.NotNull(decision?.Receipt);
        Xunit.Assert.Equal(reversibility == "reversible" ? DecisionReversibility.Reversible :
            DecisionReversibility.ReversibleWithCost, decision.Receipt.Reversibility);
        Xunit.Assert.Contains($"steward-case={caseLetter}", decision.Receipt.PrecedentRef);
        Xunit.Assert.Contains("trigger=", decision.Receipt.PrecedentRef);
        Xunit.Assert.Equal(EffectReceiptStatus.Applied, decision.Effect?.Status);
    }
}

internal sealed class StewardHarness : IDisposable
{
    internal const string Sha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    internal StewardHarness(string caseLetter)
    {
        Root = Path.Combine(Path.GetTempPath(), $"mcg-steward-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Root);
        Kernel = new AgentOrchestratorKernel();
        var role = caseLetter == "B" ? AgentRole.Planner : AgentRole.Developer;
        Task = new TaskSpec(TaskId.New(), "Repair the named failure", role);
        Goal = Kernel.CreateGoal("Steward test goal", [Task]);
        Kernel.ActivateGoal(Goal.Id, AgentCatalog.Default().Agents);
        Kernel.SetGoalRefinedSpec(Goal.Id,
            new RefinedSpec("Repair the evidence", ["The named failing test must pass."],
                VerificationClass.TestVerifiable, [], []));
        Intents = new SqliteOperatorIntentStore(Path.Combine(Root, "operator-intents.db"), Path.Combine(Root, "logs"));
        Decisions = CollaborationItemStore.ForDirectory(Root);
        Coordinator = new OperatorIntentCoordinator(Intents, decisions: Decisions, goalStateVersionResolver: _ => 7);
        Model = new StewardFakeModel();
        ConductPath = Path.Combine(Root, "conduct-events.log");
        TrxPath = Path.Combine(Root, "acceptance.trx");
        Host = new ConductorStewardHost(
            new ConductorStewardTriggerStore(Path.Combine(Root, "steward-triggers.db")),
            new ConductorStewardTriggerDetector((_, _) => "GoalAcceptanceVerifier",
                _ => File.Exists(TrxPath) ? [TrxPath] : []), Model, Intents,
            new AdjudicationEvidenceResolver(Root), _ => 7, _ => Root,
            new GoalLifecycleEventWriter(Path.Combine(Root, "lifecycle")),
            new ConductEventLogWriter(ConductPath));
    }

    internal string Root { get; }
    internal string ConductPath { get; }
    internal string TrxPath { get; }
    internal AgentOrchestratorKernel Kernel { get; }
    internal Goal Goal { get; }
    internal TaskSpec Task { get; }
    internal SqliteOperatorIntentStore Intents { get; }
    internal CollaborationItemStore Decisions { get; }
    internal OperatorIntentCoordinator Coordinator { get; }
    internal StewardFakeModel Model { get; }
    internal ConductorStewardHost Host { get; }

    internal void Seed(string caseLetter)
    {
        Kernel.RecordTaskDispatch(Goal.Id, Task.Id,
            new TaskDispatchRecord("test-worker", "test.exe", Root, DateTimeOffset.UtcNow, BaseCommit: Sha));
        if (caseLetter == "C")
        {
            Kernel.ReportTaskProgress(Goal.Id, Task.Id, WorkTaskStatus.Completed, "done");
            Kernel.RecordTaskVerification(Goal.Id, Task.Id,
                new TaskVerificationRecord("test.exe", Root, 0, "WORKER_RESULT: pass END_WORKER_RESULT", "", DateTimeOffset.UtcNow));
            Xunit.Assert.True(Kernel.BeginGoalAcceptanceVerification(Goal.Id, "gate started"));
            const string guard = "AcceptanceGateEngine_disabled_collections_spanning_lanes_share_an_exclusive_resource";
            File.WriteAllText(TrxPath, $"""
                <TestRun><Results><UnitTestResult testName="{guard}" outcome="Failed"><Output><ErrorInfo>
                <Message>Disabled-collection test class 'ExampleTests' mapped to 2 acceptance lanes: [Remainder, DotnetBuildSlots].</Message>
                </ErrorInfo></Output></UnitTestResult></Results></TestRun>
                """);
            Xunit.Assert.True(Kernel.ReconcileGoalAcceptanceFailed(Goal.Id, [guard], "guard failed", Sha,
                checkAttributions: [new AcceptanceCheckAttribution(guard, AcceptanceFailureOrigin.Introduced,
                    "guard failed; assertion is in the TRX receipt")]));
            return;
        }
        var receipt = new FindingEvidenceReceipt("red-receipt", Sha,
            new FindingEvidenceRequest([]), true, false, "candidate RED",
            [new FindingEvidenceArmReceipt(FindingEvidenceArm.Candidate, Sha, FindingEvidenceArmDisposition.Red,
                true, false, "Assert.Equal expected 1 actual 2", FailingTestIdentities: ["ExampleTests.Fails"]),
             new FindingEvidenceArmReceipt(FindingEvidenceArm.Baseline, Sha, FindingEvidenceArmDisposition.Green,
                true, true, "baseline GREEN")]);
        var verification = new TaskVerificationRecord("test.exe", Root, 1,
            "WORKER_RESULT: files: none END_WORKER_RESULT",
            caseLetter == "A" ? "DISPATCH_REJECTED reason=no-change-evidence" :
                "rule=planner-output-contract-rejected cited a glob",
            DateTimeOffset.UtcNow, WorkerResultPresent: true,
            FindingEvidenceReceipts: caseLetter == "A" ? [receipt] : null,
            OrchestratorFailureReason: caseLetter == "A" ? "no-change-evidence" : null,
            CompletionVerdictRule: caseLetter == "B" ? "planner-output-contract-rejected" : null);
        Kernel.RecordTaskVerification(Goal.Id, Task.Id, verification);
        Kernel.ReportTaskProgress(Goal.Id, Task.Id, WorkTaskStatus.Failed, "failed");
    }

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); } catch { }
    }
}

internal sealed class StewardFakeModel : IConductorStewardModelRound
{
    private string? _output;
    private Exception? _failure;
    private bool _cancel;
    private TaskCompletionSource<string>? _held;
    internal int Calls { get; private set; }
    internal TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource<bool> SecondStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal void Reply(string output) => _output = output;
    internal void Fail(Exception exception) => _failure = exception;
    internal void Cancel() => _cancel = true;
    internal void Hold() => _held = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal void Release() => _held!.TrySetResult(_output ?? "{\"kind\":\"no-action\",\"reason\":\"done\"}");
    public Task<string> DispatchAsync(ConductorStewardTrigger trigger, string workingDirectory,
        CancellationToken cancellationToken)
    {
        Calls++;
        Started.TrySetResult(true);
        if (Calls == 2) SecondStarted.TrySetResult(true);
        if (_cancel) return Task.FromCanceled<string>(new CancellationToken(canceled: true));
        if (_failure is not null) return Task.FromException<string>(_failure);
        if (_held is not null) return _held.Task;
        return Task.FromResult(_output ?? throw new InvalidOperationException("Fake output was not set."));
    }
}
