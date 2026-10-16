using System.Reflection;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

// The collection isolates the process-wide occupied-width probe.
[Xunit.Collection(TestCollections.DotnetBuildSlots)]
public sealed class ConductorBatchLoopTestsLandingHeldAdmission : ConductorBatchLoopTests
{
    public ConductorBatchLoopTestsLandingHeldAdmission(ITestOutputHelper output) : base(output) { }

    [Fact]
    public void Tick_OperatorWait_ExcludesHeldGoalFromPlan()
    {
        var (goals, wait, receipt) = TickReceipt("operator");

        Assert.Contains("ready=3", receipt, StringComparison.Ordinal);
        Assert.Contains($"{goals[0].Id.Value[..8]}:LandingHeld(request={wait.Id.Value[..8]})", receipt);
        Assert.Equal(goals.Skip(1).Select(goal => goal.Id.Value[..8]).Order(StringComparer.Ordinal),
            Members(receipt).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Tick_GateSatisfiableWait_KeepsAllFourGoalsInPlan()
    {
        var (goals, _, receipt) = TickReceipt("acceptance-gate");

        Assert.Contains("ready=4", receipt, StringComparison.Ordinal);
        Assert.DoesNotContain("LandingHeld", receipt, StringComparison.Ordinal);
        Assert.Equal(goals.Select(goal => goal.Id.Value[..8]).Order(StringComparer.Ordinal),
            Members(receipt).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Tick_AdmissionState_ResetsBeforeGoalWalk()
    {
        var sentinelKernel = new AgentOrchestratorKernel();
        var parent = sentinelKernel.CreateGoal("Sentinel parent for per-tick caches");
        var child = sentinelKernel.CreateGoal("Change src/Mcg.AgentOrchestrator.Core/Application/Sentinel.cs",
            [new TaskSpec(TaskId.New(), "Sentinel task", AgentRole.Developer)], parent.Id);
        var seedCount = 0;
        var seeded = new List<(bool ShutdownLatched, bool ParentCached, int Admitted, int ObservedPaths)>();
        var snapshots = new List<(bool ShutdownLatched, bool ParentCached, int Admitted, int ObservedPaths)>();
        var sameGuards = false;
        SliceBatchParentExecutionGuard? parentGuard = null;
        SliceBatchAdmissionEvaluator? admissionGuard = null;

        TickReceipt("operator", duringAdmission: driver =>
        {
            seedCount++;
            PrivateField(driver, "_buildServerShutdownRanThisTick").SetValue(driver, true);
            var parentPresent = true;
            parentGuard = new SliceBatchParentExecutionGuard(() => parentPresent ? [child] : []);
            parentGuard.BeginTick();
            parentPresent = false;
            driver.SliceBatchParentExecutionGuard = parentGuard;
            admissionGuard = new SliceBatchAdmissionEvaluator(() => [child], _ => [], (_, _) => { });
            admissionGuard.RecordAdmitted(child);
            admissionGuard.Evaluate(child);
            driver.SliceBatchAdmissionEvaluator = admissionGuard;
            seeded.Add(CaptureTickState(driver, parent));
        }, duringGoalWalk: driver =>
        {
            // Capture outside assertions: projection callers can catch exceptions from GetFacts.
            sameGuards = ReferenceEquals(parentGuard, driver.SliceBatchParentExecutionGuard) &&
                ReferenceEquals(admissionGuard, driver.SliceBatchAdmissionEvaluator);
            snapshots.Add(CaptureTickState(driver, parent));
        });

        Assert.Equal(1, seedCount);
        Assert.Equal((true, true, 1, 1), Assert.Single(seeded));
        Assert.True(sameGuards);
        Assert.Equal((false, false, 0, 0), Assert.Single(snapshots));
    }

    private static (bool ShutdownLatched, bool ParentCached, int Admitted, int ObservedPaths)
        CaptureTickState(ConductorDriver driver, Goal parent)
    {
        var admission = driver.SliceBatchAdmissionEvaluator!;
        return ((bool)PrivateField(driver, "_buildServerShutdownRanThisTick").GetValue(driver)!,
            driver.SliceBatchParentExecutionGuard!.IsSliceBatchParent(parent),
            ((HashSet<GoalId>)PrivateField(admission, "_admittedThisTick").GetValue(admission)!).Count,
            ((Dictionary<GoalId, IReadOnlyList<string>?>)PrivateField(admission, "_observedPathsThisTick")
                .GetValue(admission)!).Count);
    }

    private static FieldInfo PrivateField(object target, string name) =>
        target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException($"Missing per-tick state field {name}.");

    private static string[] Members(string receipt) =>
        receipt.Split("members=", StringSplitOptions.None)[1].Split(' ')[0].Split(',');

    private static (Goal[] Goals, HumanInputRequest Wait, string Receipt) TickReceipt(string owner,
        Action<ConductorDriver>? duringAdmission = null, Action<ConductorDriver>? duringGoalWalk = null)
    {
        var kernel = new AgentOrchestratorKernel();
        var goals = Enumerable.Range(0, 4).Select(index =>
            CreateVerifiedSimpleGoal(kernel, $"Landing hold admission {index}")).ToArray();
        // Ensure a real AdvanceOnce/GetFacts call in the walk even when all Verified goals queue.
        if (duringGoalWalk is not null)
            GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, AgentCatalog.Default().Agents, "Per-tick reset walk probe");
        var wait = kernel.RequestHumanInputDeduplicated(goals[0].Id, null, "Supply prospective evidence",
            kind: HumanWaitKind.ProspectiveAcceptanceEvidence, evidenceOwner: owner,
            testNames: ["GateTests"]).Request;
        var paths = goals.ToDictionary(goal => goal.Id,
            goal => (IReadOnlyList<string>)[$"tests/Mcg.AgentOrchestrator.Core.Tests/{goal.Id.Value[..8]}.cs"]);
        ConductorDriver? driver = null;
        var running = false;
        var admissionObserved = false;
        var walkObserved = false;
        var projector = new GateReadyCandidateProjector(
            id =>
            {
                if (running && !admissionObserved)
                {
                    admissionObserved = true;
                    duringAdmission?.Invoke(driver!);
                }
                return new GateReadyCandidateRevisionPair(id.Value.PadRight(40, 'b')[..40], new string('a', 40));
            },
            id => new GateReadyLandingScopeObservation(true, paths[id]),
            (_, _, _) => new GateReadyMergeTreeObservation(true));
        driver = MakeDriver(
            getFacts: _ =>
            {
                if (admissionObserved && !walkObserved && driver is not null &&
                    PrivateField(driver, "_conductorTickKernel").GetValue(driver) is not null)
                {
                    walkObserved = true;
                    duringGoalWalk?.Invoke(driver);
                }
                return new GoalLifecycleFacts(WorkspaceExists: true);
            },
            runAcceptanceWithSlot: (_, _) => AcceptanceVerificationSummary.PassedWithNoUnmetCriteria,
            classifyRisk: _ => ChangeRiskTier.DocsOnly,
            getLandingFileScopes: goal => paths[goal.Id], isVerificationGateSatisfied: _ => true,
            gateReadyCandidateProjector: projector);
        // Prove each input is ready before the tick adds the pending-wait exclusion.
        Assert.Null(PrivateField(driver, "_cohortKernel").GetValue(driver));
        Assert.Null(PrivateField(driver, "_conductorTickKernel").GetValue(driver));
        Assert.All(goals, goal => Assert.IsType<GateReadyCandidateProjectionResult.Ready>(
            driver.ProjectGateReadyCandidate(goal, ConductorAutonomyPolicy.Conservative)));
        using var probe = GateLoadContextProbe.PushLiveGateOccupantProbe(() =>
            [new GateLoadContextProbe.LiveGateOccupant(4101, "cccccccccccccccccccccccccccccccc", 0, TimeSpan.Zero)]);
        var logPath = Path.Combine(Path.GetTempPath(), $"landing-held-{Guid.NewGuid():N}.jsonl");
        try
        {
            running = true;
            new ConductorBatchLoop(conductEventLogWriter: new ConductEventLogWriter(logPath))
                .Run(kernel, driver, ConductorAutonomyPolicy.Conservative with { AcceptanceWidth = 1 },
                    NoStopPath(), maxIterations: 1);
            var receipts = File.ReadAllLines(logPath)
                .Select(line => JsonSerializer.Deserialize<ConductEventRecord>(
                    line, new JsonSerializerOptions(JsonSerializerDefaults.Web))!)
                .Where(record => record.EventKind == "speculative-cohort-plan").ToArray();
            return (goals, wait, Assert.Single(receipts).Detail);
        }
        finally
        {
            if (File.Exists(logPath)) File.Delete(logPath);
        }
    }
}
