using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

using static ConductorDriverTests;

public sealed partial class ConductorDriverTestsAcceptanceCoordination
{
    [Xunit.Fact]
    public void ConductorDriverInlineLandingPassesStableSlotIndexWhenLeaseAvailable()
    {
        var (kernel, goal) = SimpleGoal("Inline landing stable slot");
        PassVerification(kernel, goal, goal.Tasks.Single());
        var stableSlotLease = Assert.IsType<DotnetBuildLeaseAcquisition.Acquired>(
            DotnetBuildEnvironmentManager.TryAcquireStableSlotExecutionLock(0, TimeSpan.Zero)).Lease;
        int? capturedSlotIndex = null;
        DotnetBuildEnvironmentLease? capturedLease = null;
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            runAcceptanceVerificationWithLease: (_, slotIndex, lease, _) =>
            {
                capturedSlotIndex = slotIndex;
                capturedLease = lease;
                return AcceptanceVerificationSummary.PassedWithNoUnmetCriteria;
            },
            tryAcquireLandingStableSlotLease: _ => new DotnetBuildLeaseAcquisition.Acquired(stableSlotLease),
            classifyRisk: _ => ChangeRiskTier.DocsOnly);

        _ = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        Assert.Equal(0, capturedSlotIndex);
        Assert.Same(stableSlotLease, capturedLease);
        using var reacquiredLease = Assert.IsType<DotnetBuildLeaseAcquisition.Acquired>(
            DotnetBuildEnvironmentManager.TryAcquireStableSlotExecutionLock(0, TimeSpan.Zero)).Lease;
    }

    [Xunit.Fact]
    public void ConductorDriverInlineLandingRunsSerialFallbackWhenStableSlotUnavailable()
    {
        var (kernel, goal) = SimpleGoal("Inline landing serial fallback");
        PassVerification(kernel, goal, goal.Tasks.Single());
        var verifierRan = false;
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            runAcceptanceVerificationWithLease: (_, slotIndex, lease, _) =>
            {
                verifierRan = true;
                Assert.Null(slotIndex);
                Assert.Null(lease);
                return AcceptanceVerificationSummary.PassedWithNoUnmetCriteria;
            },
            tryAcquireLandingStableSlotLease: _ => new DotnetBuildLeaseAcquisition.SlotsBusy("test", []),
            classifyRisk: _ => ChangeRiskTier.DocsOnly);

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        Assert.True(verifierRan);
        Assert.IsType<ConductorAdvanceOutcome.Executed>(result.Outcome);
    }

    [Xunit.Fact]
    public void ConductorDriverInlineLandingRecordsDegradationWhenStableSlotUnavailable()
    {
        var events = RunDegradedLanding(new DotnetBuildLeaseAcquisition.SlotsBusy(
            "inline-landing",
            [new DotnetBuildStableSlotWait(1, 4242)]));

        Assert.Contains("landing-stable-slot-degraded", events);
        Assert.Contains("reason=slots-busy", events);
        Assert.Contains("slot-1 pid 4242", events);
    }

    [Xunit.Fact]
    public void ConductorDriverInlineLandingRecordsBuildLockReasonWhenStableSlotBlocked()
    {
        var events = RunDegradedLanding(new DotnetBuildLeaseAcquisition.BuildLockBlocked(
            "inline-landing",
            new BuildLockAttribution("locked.dll", [], "test")));

        Assert.Contains("landing-stable-slot-degraded", events);
        Assert.Contains("reason=build-lock-blocked", events);
        Assert.Contains("path=locked.dll", events);
    }

    [Xunit.Fact]
    public void ConductorDriverInlineLandingRecordsNoDegradationWhenLeaseAcquired()
    {
        var executionDirectory = CreateTempDirectory();
        var (kernel, goal) = SimpleGoal("Inline landing leased without degradation");
        PassVerification(kernel, goal, goal.Tasks.Single());
        var stableSlotLease = Assert.IsType<DotnetBuildLeaseAcquisition.Acquired>(
            DotnetBuildEnvironmentManager.TryAcquireStableSlotExecutionLock(0, TimeSpan.Zero)).Lease;
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            runAcceptanceVerificationWithLease: (_, _, _, _) =>
                AcceptanceVerificationSummary.PassedWithNoUnmetCriteria,
            tryAcquireLandingStableSlotLease: _ => new DotnetBuildLeaseAcquisition.Acquired(stableSlotLease),
            classifyRisk: _ => ChangeRiskTier.DocsOnly,
            executionDirectory: executionDirectory);
        var eventPath = LandingEventPath(executionDirectory);

        _ = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        var events = File.Exists(eventPath) ? File.ReadAllText(eventPath) : string.Empty;
        Assert.DoesNotContain("landing-stable-slot-degraded", events);
        using var reacquiredLease = Assert.IsType<DotnetBuildLeaseAcquisition.Acquired>(
            DotnetBuildEnvironmentManager.TryAcquireStableSlotExecutionLock(0, TimeSpan.Zero)).Lease;
    }

    [Xunit.Fact]
    public void ConductorDriverInlineLandingRequestsBoundedStableSlotWait()
    {
        var capturedTimeout = TimeSpan.Zero;

        var result = ConductorDriver.TryAcquireInlineLandingStableSlotLease(timeout =>
        {
            capturedTimeout = timeout;
            return new DotnetBuildLeaseAcquisition.SlotsBusy("test", []);
        });

        Assert.IsType<DotnetBuildLeaseAcquisition.SlotsBusy>(result);
        Assert.Equal(ConductorDriver.InlineLandingStableSlotLeaseTimeout, capturedTimeout);
        Assert.InRange(capturedTimeout, TimeSpan.FromMilliseconds(1), TimeSpan.FromSeconds(5));
    }

    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public void ConductorDriverInlineLandingReleasesStableSlotWhenVerifierThrows(bool cancellation)
    {
        var (kernel, goal) = SimpleGoal("Inline landing exceptional release");
        PassVerification(kernel, goal, goal.Tasks.Single());
        var stableSlotLease = Assert.IsType<DotnetBuildLeaseAcquisition.Acquired>(
            DotnetBuildEnvironmentManager.TryAcquireStableSlotExecutionLock(0, TimeSpan.Zero)).Lease;
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            runAcceptanceVerificationWithLease: (_, _, _, _) => throw (cancellation
                ? new OperationCanceledException("injected cancellation")
                : new InvalidOperationException("injected failure")),
            tryAcquireLandingStableSlotLease: _ => new DotnetBuildLeaseAcquisition.Acquired(stableSlotLease),
            classifyRisk: _ => ChangeRiskTier.DocsOnly);

        if (cancellation)
        {
            Assert.Throws<OperationCanceledException>(() =>
                driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative));
        }
        else
        {
            Assert.Throws<InvalidOperationException>(() =>
                driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative));
        }

        using var reacquiredLease = Assert.IsType<DotnetBuildLeaseAcquisition.Acquired>(
            DotnetBuildEnvironmentManager.TryAcquireStableSlotExecutionLock(0, TimeSpan.Zero)).Lease;
    }

    private static string RunDegradedLanding(DotnetBuildLeaseAcquisition acquisition)
    {
        var executionDirectory = CreateTempDirectory();
        var (kernel, goal) = SimpleGoal("Inline landing degradation record");
        PassVerification(kernel, goal, goal.Tasks.Single());
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            runAcceptanceVerificationWithLease: (_, _, _, _) =>
                AcceptanceVerificationSummary.PassedWithNoUnmetCriteria,
            tryAcquireLandingStableSlotLease: _ => acquisition,
            classifyRisk: _ => ChangeRiskTier.DocsOnly,
            executionDirectory: executionDirectory);
        var eventPath = LandingEventPath(executionDirectory);

        _ = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        Assert.True(File.Exists(eventPath), $"Expected degradation event at {eventPath}.");
        return File.ReadAllText(eventPath);
    }

    private static string LandingEventPath(string executionDirectory) => Path.Combine(
        executionDirectory,
        ".orchestrator",
        "logs",
        ConductEventLogWriter.CurrentFileName);
}
