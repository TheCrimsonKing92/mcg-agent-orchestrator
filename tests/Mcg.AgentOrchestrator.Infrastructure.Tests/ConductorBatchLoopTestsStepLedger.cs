using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(TestCollections.DotnetBuildSlots)]
public sealed class ConductorBatchLoopTestsStepLedger(ITestOutputHelper output) : ConductorBatchLoopTests(output)
{
    [Xunit.Fact(DisplayName = "Prewalk reports all nine steps in fixed order from injected CPU")]
    public void PrewalkReportsInjectedStepCpu()
    {
        var root = CreateTempDirectory("mcg-step-ledger");
        try
        {
            var (kernel, _) = SimpleGoal();
            var cpu = TimeSpan.Zero;
            var advances = new Dictionary<string, int>
            {
                ["retire-until-goal-lessons"] = 11, ["workspace-intents"] = 13, ["steward"] = 17,
                ["author"] = 19, ["store-evidence"] = 23, ["judge-panel"] = 29,
                ["board-fill"] = 31, ["operator-intent-list"] = 37, ["eligibility"] = 41
            };
            var observed = new List<string>();
            var store = new SqliteOperatorIntentStore(Path.Combine(root, "intents.db"), Path.Combine(root, "logs"));
            var loop = new ConductorBatchLoop(operatorIntents: new OperatorIntentCoordinator(store), processCpuTime: () => cpu)
                .WithStepProbe(name => { observed.Add(name); cpu += TimeSpan.FromMilliseconds(advances[name]); });

            var tick = OneTick(loop, kernel, MakeDriver());

            var line = Phase(tick, "prewalk");
            Assert.Contains("prewalk_steps=retire-until-goal-lessons:11:1,workspace-intents:13:1,steward:17:1,author:19:1,store-evidence:23:1,judge-panel:29:1,board-fill:31:1,operator-intent-list:37:1,eligibility:41:1", line);
            Assert.EndsWith(" cpu_ms=221", line);
            // Emission order differs from call order; instrumentation cannot reorder calls.
            Assert.Equal(new[] { "retire-until-goal-lessons", "steward", "author", "store-evidence", "judge-panel", "board-fill", "workspace-intents", "operator-intent-list", "eligibility" }, observed);
            AssertBound(line, "prewalk_steps");
        }
        finally { TryDeleteDirectory(root); }
    }

    [Xunit.Fact(DisplayName = "Sweep reports twelve exclusive CPU steps including nested owned root reap")]
    public void SweepReportsExclusiveInjectedStepCpu()
    {
        var (kernel, _) = SimpleGoal();
        var cpu = TimeSpan.Zero;
        var sweepNames = new[] { "kernel-reload", "refresh-dispatches", "parked-reloads", "terminal-sweep-run", "owned-root-reap", "reconcile-remediation", "host-health", "maintenance", "state-log-check", "failure-clusters", "remote-git-mirror", "goal-refinement" };
        var loop = new ConductorBatchLoop(measuredSweep: _ =>
        {
            foreach (var name in sweepNames.Where(name => name != "owned-root-reap"))
                ConductorTickStepLedger.Measure(name, () =>
                {
                    if (name == "terminal-sweep-run")
                        ConductorTickStepLedger.Measure("owned-root-reap", () => cpu += TimeSpan.FromMilliseconds(7));
                    cpu += TimeSpan.FromMilliseconds(3);
                });
            cpu += TimeSpan.FromMilliseconds(5);
            return null;
        }, processCpuTime: () => cpu);

        var tick = OneTick(loop, kernel, MakeDriver());

        var sweep = Phase(tick, "sweep");
        Assert.Contains("sweep_steps=kernel-reload:3:1,refresh-dispatches:3:1,parked-reloads:3:1,terminal-sweep-run:3:1,owned-root-reap:7:1,reconcile-remediation:3:1,host-health:3:1,maintenance:3:1,state-log-check:3:1,failure-clusters:3:1,remote-git-mirror:3:1,goal-refinement:3:1", sweep);
        Assert.EndsWith(" cpu_ms=45", sweep);
        AssertBound(sweep, "sweep_steps");
        AssertBound(Phase(tick, "prewalk"), "prewalk_steps");
    }

    [Xunit.Fact(DisplayName = "Uncalled steps are zero filled and ledger restores on loop exit")]
    public void UncalledStepsAndLoopExit()
    {
        var (kernel, _) = SimpleGoal();
        var probeCalls = 0;
        var loop = new ConductorBatchLoop(processCpuTime: () => TimeSpan.Zero).WithStepProbe(_ => probeCalls++);
        var tick = OneTick(loop, kernel, MakeDriver());

        Assert.Contains("operator-intent-list:0:0", Phase(tick, "prewalk"));
        Assert.Contains("sweep_steps=kernel-reload:0:0,refresh-dispatches:0:0,parked-reloads:0:0,terminal-sweep-run:0:0,owned-root-reap:0:0,reconcile-remediation:0:0,host-health:0:0,maintenance:0:0,state-log-check:0:0,failure-clusters:0:0,remote-git-mirror:0:0,goal-refinement:0:0", Phase(tick, "sweep"));
        Assert.Equal(8, probeCalls);
        var outsideCalls = 0;
        ConductorTickStepLedger.Measure("author", () => outsideCalls++);
        Assert.Equal(1, outsideCalls);
        Assert.Equal(8, probeCalls);
    }

    [Xunit.Fact(DisplayName = "Repeated fractional CPU samples truncate after accumulation and reset each tick")]
    public void FractionalCpuAccumulatesAndResets()
    {
        var cpu = TimeSpan.Zero;
        var first = new ConductorTickStepLedger(() => cpu);
        using (first.Activate())
            for (var i = 0; i < 3; i++)
                ConductorTickStepLedger.Measure("eligibility", () => cpu += TimeSpan.FromTicks(6000));
        Assert.EndsWith("eligibility:1:3", first.FormatSteps(ConductorTickStepLedger.PrewalkNames));
        var second = new ConductorTickStepLedger(() => cpu);
        using (second.Activate())
            ConductorTickStepLedger.Measure("eligibility", () => cpu += TimeSpan.FromTicks(6000));
        Assert.EndsWith("eligibility:0:1", second.FormatSteps(ConductorTickStepLedger.PrewalkNames));
    }

    [Xunit.Fact(DisplayName = "Failed step runs once and records CPU while preserving the thrown exception")]
    public void FailedStepPreservesException()
    {
        var cpu = TimeSpan.Zero;
        var ledger = new ConductorTickStepLedger(() => cpu);
        var failure = new InvalidOperationException("step failed");
        var calls = 0;
        using (ledger.Activate())
        {
            var thrown = Assert.Throws<InvalidOperationException>(() => ConductorTickStepLedger.Measure("author", () =>
            {
                calls++;
                cpu += TimeSpan.FromMilliseconds(9);
                throw failure;
            }));
            Assert.Same(failure, thrown);
        }
        Assert.Equal(1, calls);
        Assert.Contains("author:9:1", ledger.FormatSteps(ConductorTickStepLedger.PrewalkNames));
    }

    internal static ConductorDriver FixtureDriver() => MakeDriver();

    internal static BatchTickSummary OneTick(ConductorBatchLoop loop, AgentOrchestratorKernel kernel, ConductorDriver driver)
    {
        BatchTickSummary? captured = null;
        loop.Run(kernel, driver, ConductorAutonomyPolicy.Conservative, NoStopPath(), maxIterations: 1, onTick: tick => captured = tick);
        return Assert.IsType<BatchTickSummary>(captured);
    }

    internal static string Phase(BatchTickSummary tick, string phase) => Regex.Replace(
        Assert.Single(tick.ProgressLines!, line => line.StartsWith($"PHASE_TIMING tick={tick.Tick} phase={phase} ", StringComparison.Ordinal)), @" ts=\S+$", "");

    private static void AssertBound(string line, string token)
    {
        var values = Regex.Match(line, token + @"=(\S+)").Groups[1].Value;
        Assert.NotEmpty(values);
        var sum = values.Split(',').Sum(entry => long.Parse(entry.Split(':')[1], System.Globalization.CultureInfo.InvariantCulture));
        var total = long.Parse(Regex.Match(line, @" cpu_ms=(\d+)$").Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        Assert.True(sum <= total, $"Step sum {sum} exceeds phase CPU {total}: {line}");
    }
}
