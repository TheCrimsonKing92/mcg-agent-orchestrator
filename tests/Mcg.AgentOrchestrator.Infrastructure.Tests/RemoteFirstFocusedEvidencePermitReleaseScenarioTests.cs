using System.Xml.Linq;
using Mcg.AgentOrchestrator.Infrastructure;
using Check = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.AcceptanceManifestCheck;

[Xunit.Collection("IsolatedProcessSpawning")]
public sealed class RemoteFirstFocusedEvidencePermitReleaseScenarioTests : RemoteFirstFocusedEvidenceFixture
{
    [Xunit.Theory(Timeout = TestHangGuard.WholeTestMilliseconds)]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task RemoteSubmit_ReleasesPermitAndFallbackReacquires(bool transportFailure)
    {
        using var scenario = CreateScenario();
        using var lease = Acquire(scenario);
        Assert.True(lease.IsExecutionLockHeld);
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var proceed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fake = scenario.CompletedExecutor();
        fake.Submit = async (request, token) =>
        {
            entered.TrySetResult(lease.IsExecutionLockHeld);
            await proceed.Task.WaitAsync(token);
            return transportFailure ? new(null, "transport-unavailable") : scenario.Completion(request);
        };
        var builds = 0;
        var reacquired = false;
        var runner = new LocalRunner(() =>
        {
            builds++;
            var acquisition = DotnetBuildEnvironmentManager.TryAcquireLeaseExecutionLock(lease.Environment, TimeSpan.Zero);
            if (acquisition is DotnetBuildLeaseAcquisition.Acquired acquired) acquired.Lease.Dispose();
            Assert.IsType<DotnetBuildLeaseAcquisition.SlotsBusy>(acquisition);
            reacquired = true;
        });
        var run = RunOwnedAsync(scenario, lease, runner);
        bool heldOnSubmit;
        bool heldDuringRemote;
        try
        {
            await Event(entered.Task, "remote SubmitAsync entered");
            heldOnSubmit = await entered.Task;
            heldDuringRemote = lease.IsExecutionLockHeld;
        }
        finally { proceed.TrySetResult(); }
        var result = await run;
        Assert.False(heldOnSubmit);
        Assert.False(heldDuringRemote);
        Assert.True(result.Passed, result.Summary);
        Assert.True(Assert.Single(result.Checks).Passed);
        Assert.Single(fake.Requests);
        Assert.Equal(transportFailure ? 1 : 0, builds);
        Assert.Equal(transportFailure, reacquired);
        Assert.Equal(transportFailure ? 1 : 0, runner.Tests);
    }

    [Xunit.Fact(Timeout = TestHangGuard.WholeTestMilliseconds)]
    public async Task ModeOff_KeepsPermitLikeUnconfiguredBaseline()
    {
        async Task<bool> Observe(bool configured)
        {
            using var scenario = CreateScenario();
            if (configured) scenario.Configure("off");
            else File.Delete(scenario.ConfigurationPath);
            var fake = scenario.CompletedExecutor();
            using var lease = Acquire(scenario);
            bool? held = null;
            var runner = new LocalRunner(() => held = lease.IsExecutionLockHeld);
            var result = await RunOwnedAsync(scenario, lease, runner);
            Assert.True(result.Passed, result.Summary);
            Assert.True(Assert.Single(result.Checks).Passed);
            Assert.Equal(1, runner.Tests);
            Assert.Empty(fake.Requests);
            return Assert.IsType<bool>(held);
        }
        var baseline = await Observe(false);
        Assert.True(baseline);
        Assert.Equal(baseline, await Observe(true));
    }

    [Xunit.Fact(Timeout = TestHangGuard.WholeTestMilliseconds)]
    public async Task TwoClaims_ReleaseOnceBeforeFirstRequest()
    {
        using var scenario = CreateScenario();
        scenario.Configure(slots: 2);
        var fake = scenario.CompletedExecutor();
        var calls = 0;
        var requestsAtRelease = -1;
        var result = await DirectAsync(scenario, [FocusedCheck(), FocusedCheck("BetaTests")], () =>
        {
            calls++;
            requestsAtRelease = fake.Requests.Count;
        });
        Assert.Equal(1, calls);
        Assert.Equal(0, requestsAtRelease);
        Assert.Equal(2, fake.Requests.Count);
        Assert.Equal(2, result.Results.Count);
        Assert.All(result.Results, check => Assert.True(check.Passed));
    }

    [Xunit.Theory(Timeout = TestHangGuard.WholeTestMilliseconds)]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task NoClaim_NeverRequestsRelease(bool noIdleSlot)
    {
        using var scenario = CreateScenario();
        using var claim = noIdleSlot ? RemoteExecutorOccupancy.TryClaimExclusive(
            AcceptancePartitionVerdictCache.ResolveHostStateRoot(scenario.Root), "fixture-executor") : null;
        if (noIdleSlot) Assert.NotNull(claim);
        var fake = scenario.CompletedExecutor();
        var calls = 0;
        var check = noIdleSlot ? FocusedCheck() : FocusedCheck(keys: ["machine-local"], buildChange: true);
        var result = await DirectAsync(scenario, [check], () => calls++);
        Assert.Equal(0, calls);
        Assert.Empty(fake.Requests);
        Assert.Equal("local-passed", Assert.Single(result.Results).OutputTail);
    }

    [Xunit.Fact(Timeout = TestHangGuard.WholeTestMilliseconds)]
    public async Task ReleaseThrows_PropagatesBeforeSubmitAndCleansUpClaim()
    {
        using var scenario = CreateScenario();
        var fake = scenario.CompletedExecutor();
        var failure = new InvalidOperationException("release failed");
        var calls = 0;
        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => DirectAsync(
            scenario, [FocusedCheck()], () => { calls++; throw failure; }));
        Assert.Same(failure, thrown);
        Assert.Equal(1, calls);
        Assert.Empty(fake.Requests);
        using var claim = RemoteExecutorOccupancy.TryClaimExclusive(
            AcceptancePartitionVerdictCache.ResolveHostStateRoot(scenario.Root), "fixture-executor");
        Assert.NotNull(claim);
    }

    private static async Task<RemoteFirstFocusedEvidence.Outcome> DirectAsync(
        Scenario scenario, IReadOnlyList<Check> checks, Action release)
    {
        await using var owner = AcceptanceExecutionOwners.CreateFocusedVerification(scenario.Root, scenario.Goal, null,
            options: new(RunId: "release-direct", ResultsPrefix: scenario.Prefix));
        return await RemoteFirstFocusedEvidence.RunAsync(scenario.Overrides, scenario.Clock, scenario.Root, checks,
            scenario.Goal, (IAcceptanceRunExecutionContext)owner, false,
            local => Task.FromResult<IReadOnlyList<AcceptanceCheckResult>>(local.Select(check =>
                new AcceptanceCheckResult(check.Name, true, 0, "local-passed")).ToArray()),
            TestContext.Current.CancellationToken, release);
    }

    private static async Task<FocusedEvidenceRunResult> RunOwnedAsync(
        Scenario scenario, DotnetBuildEnvironmentLease lease, LocalRunner runner)
    {
        var match = System.Text.RegularExpressions.Regex.Match(lease.Environment.ArtifactsPath, @"build-(?<slot>\d+)");
        Assert.True(match.Success, lease.Environment.ArtifactsPath);
        var slot = int.Parse(match.Groups["slot"].Value, System.Globalization.CultureInfo.InvariantCulture);
        await using var owner = AcceptanceExecutionOwners.CreateFocusedVerification(scenario.Root, scenario.Goal, slot,
            options: new(RunId: "release-attempt", ResultsPrefix: scenario.Prefix));
        return await new GoalAcceptanceVerifier(scenario.Overrides, runner.RunAsync).RunFocusedEvidenceOwnedAsync(
            scenario.Root, scenario.Goal, "Infrastructure.Tests: AlphaTests", owner, slot, lease);
    }

    private static DotnetBuildEnvironmentLease Acquire(Scenario scenario) =>
        DotnetBuildEnvironmentManager.AcquireFirstAvailableStableSlotExecutionLock(TimeSpan.FromSeconds(30),
            cancellationToken: TestContext.Current.CancellationToken,
            storageRoot: new(Path.Combine(scenario.Root, ".orchestrator", "build-storage")));

    private sealed class LocalRunner(Action onBuild)
    {
        internal int Tests;
        internal Task<GoalAcceptanceVerifier.CommandResult> RunAsync(string[] args, string directory, CancellationToken token)
        {
            if (args.Length > 1 && args[0] == "dotnet" && args[1] == "build")
            {
                onBuild();
                var output = Path.Combine(args[Array.IndexOf(args, "--artifacts-path") + 1],
                    "bin", "Mcg.AgentOrchestrator.Infrastructure.Tests", "debug");
                Directory.CreateDirectory(output);
                foreach (var extension in new[] { ".dll", ".exe" })
                    File.WriteAllText(Path.Combine(output, "Mcg.AgentOrchestrator.Infrastructure.Tests" + extension), "fixture");
            }
            if (args.Length > 1 && args[0] == "dotnet" && args[1].EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                Tests++;
                var directoryIndex = Array.IndexOf(args, "--results-directory");
                var fileIndex = Array.IndexOf(args, "--report-trx-filename");
                Assert.InRange(directoryIndex, 0, args.Length - 2);
                Assert.InRange(fileIndex, 0, args.Length - 2);
                Directory.CreateDirectory(args[directoryIndex + 1]);
                new XDocument(new XElement("TestRun",
                    new XElement("TestDefinitions", new XElement("UnitTest", new XAttribute("id", "test-0"),
                        new XAttribute("name", "AlphaTests.Executes"), new XElement("TestMethod",
                            new XAttribute("className", "AlphaTests"), new XAttribute("name", "Executes")))),
                    new XElement("Results", new XElement("UnitTestResult", new XAttribute("testId", "test-0"),
                        new XAttribute("testName", "AlphaTests.Executes"), new XAttribute("outcome", "Passed"))),
                    new XElement("ResultSummary", new XAttribute("outcome", "Completed"), new XElement("Counters",
                        new XAttribute("total", 1), new XAttribute("executed", 1), new XAttribute("passed", 1),
                        new XAttribute("failed", 0), new XAttribute("notExecuted", 0)))))
                    .Save(Path.Combine(args[directoryIndex + 1], args[fileIndex + 1]));
            }
            return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Build succeeded. Passed: 1"));
        }
    }
}
