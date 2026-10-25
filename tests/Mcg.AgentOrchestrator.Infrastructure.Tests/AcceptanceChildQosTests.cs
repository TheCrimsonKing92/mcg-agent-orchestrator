using System.Diagnostics;
using System.Runtime.InteropServices;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: async-flow-scoped switch overrides; injected members, setters and clock.
// The read-only Windows contract test observes only this test host's own process.
public sealed class AcceptanceChildQosTests
{
    [Fact]
    public void Policy_MissingNullAndRoundTrip_PreservesOptOutDefault()
    {
        var policy = ConductorAutonomyPolicy.Conservative;
        Assert.False(policy.AcceptanceChildrenOptOutExecutionThrottling);
        var json = policy.ToJson();
        const string line = "  \"acceptanceChildrenOptOutExecutionThrottling\": false,";
        Assert.Contains(line, json, StringComparison.Ordinal);
        Assert.False(ConductorAutonomyPolicy.ParseJson(json.Replace(line, "", StringComparison.Ordinal))
            .AcceptanceChildrenOptOutExecutionThrottling);
        Assert.False(ConductorAutonomyPolicy.ParseJson(json.Replace(line,
            "  \"acceptanceChildrenOptOutExecutionThrottling\": null,", StringComparison.Ordinal))
            .AcceptanceChildrenOptOutExecutionThrottling);
        Assert.True(ConductorAutonomyPolicy.ParseJson((policy with
        {
            AcceptanceChildrenOptOutExecutionThrottling = true
        }).ToJson()).AcceptanceChildrenOptOutExecutionThrottling);
        var lines = json.Split('\n');
        var index = Array.FindIndex(lines, value => value.Contains("\"acceptanceAttemptBelowNormalPriority\"", StringComparison.Ordinal));
        Assert.True(index >= 0);
        Assert.Equal(line, lines[index + 1].TrimEnd('\r'));
        var error = Assert.Throws<FormatException>(() => ConductorAutonomyPolicy.ParseJson(
            json.Replace(line, "  \"acceptanceChildrenOptOutExecutionThrottling\": 1,", StringComparison.Ordinal)));
        Assert.Contains("acceptanceChildrenOptOutExecutionThrottling must be a boolean", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OwnedAttempt_PolicySwitch_ControlsSelfOptOut(bool enabled)
    {
        using var scope = AcceptanceChildQos.PushForTests(false);
        var calls = new List<(int, uint, uint)>();
        ConductorParallelAcceptanceAttemptCoordinator.ApplyOwnedProcessExecutionQos(Attempt(enabled),
            (pid, control, state) => { calls.Add((pid, control, state)); return true; });
        Assert.Equal(enabled, AcceptanceChildQos.Enabled);
        Assert.Equal(enabled ? [(Environment.ProcessId, 1u, 0u)] : Array.Empty<(int, uint, uint)>(), calls);
    }

    [Fact]
    public void Sweep_TwoPasses_SetsRootImmediatelyAndEveryPidOnce()
    {
        using var scope = AcceptanceChildQos.PushForTests(true);
        var calls = new List<(int, uint, uint)>();
        var pass = 0;
        using var sweep = AcceptanceChildQos.StartSweep(100, false,
            _ => ++pass == 1 ? [100, 101, 102] : [100, 101, 102, 103],
            (pid, control, state) => { calls.Add((pid, control, state)); return true; });
        Assert.NotNull(sweep);
        Assert.Equal([(100, 1u, 0u)], calls);
        sweep.RunPass();
        sweep.RunPass();
        Assert.Equal([(100, 1u, 0u), (101, 1u, 0u), (102, 1u, 0u), (103, 1u, 0u)], calls);
        Assert.Equal(2, pass);
    }

    [Fact]
    public void Sweep_Disabled_DoesNotEnumerateOrSet()
    {
        using var scope = AcceptanceChildQos.PushForTests(false);
        var calls = 0;
        using var sweep = AcceptanceChildQos.StartSweep(100, false,
            _ => throw new InvalidOperationException("Disabled sweep enumerated."),
            (_, _, _) => { calls++; return true; });
        Assert.Null(sweep);
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Sweep_FailedChild_IsNotRetriedAndDoesNotStopOthers(bool throws)
    {
        using var scope = AcceptanceChildQos.PushForTests(true);
        var calls = new List<int>();
        using var sweep = AcceptanceChildQos.StartSweep(100, false, _ => [100, 101, 102],
            (pid, _, _) =>
            {
                calls.Add(pid);
                if (pid == 101 && throws) throw new InvalidOperationException();
                return pid != 101;
            });
        Assert.NotNull(sweep);
        sweep.RunPass();
        sweep.RunPass();
        Assert.Equal([100, 101, 102], calls);
    }

    [Fact]
    public void Sweep_BoundedAndDisposed_DoesNotTouchExtraMembers()
    {
        using var scope = AcceptanceChildQos.PushForTests(true);
        var calls = new List<int>();
        using var sweep = AcceptanceChildQos.StartSweep(100, false,
            _ => Enumerable.Range(101, 300).ToArray(), (pid, _, _) => { calls.Add(pid); return true; });
        Assert.NotNull(sweep);
        sweep.RunPass();
        Assert.Equal(Enumerable.Range(100, 257), calls);
        sweep.Dispose();
        sweep.RunPass();
        Assert.Equal(257, calls.Count);
    }

    [Fact]
    public void NativeSetter_Constants_DescribeOnlyExecutionSpeedOptOut()
    {
        Assert.Equal(4, ProcessExecutionThrottlingOptOut.ProcessPowerThrottlingClass);
        Assert.Equal(1u, ProcessExecutionThrottlingOptOut.StateVersion);
        Assert.Equal(1u, ProcessExecutionThrottlingOptOut.ControlMaskExecutionSpeed);
        Assert.Equal(0u, ProcessExecutionThrottlingOptOut.StateMaskHighQos);
        Assert.Equal(0x0200u, ProcessExecutionThrottlingOptOut.ProcessSetInformationAccess);
        Assert.Equal(12, Marshal.SizeOf<ProcessExecutionThrottlingOptOut.PowerThrottlingState>());
    }

    public static IEnumerable<object[]> NativeSamples()
    {
        (uint Value, string Name, bool Below)[] priorities =
            [(0x40, "Idle", true), (0x4000, "BelowNormal", true), (0x20, "Normal", false),
             (0x8000, "AboveNormal", false), (0x80, "High", false)];
        foreach (var priority in priorities)
        foreach (var masks in new[] { (0u, 0u), (1u, 1u), (1u, 0u) })
        foreach (var memory in new[] { 1, 5 })
            yield return [priority.Value, priority.Name, priority.Below, masks.Item1, masks.Item2, memory];
    }

    [Theory]
    [MemberData(nameof(NativeSamples))]
    public void FromNative_Values_ClassifiesScheduling(uint priority, string name, bool below,
        uint control, uint state, int memory)
    {
        var sample = ProcessSchedulingProbe.FromNative(100, "child", priority, control, state, memory);
        Assert.Equal(name, sample.PriorityClass);
        Assert.Equal(below, sample.IsBelowNormal);
        Assert.Equal(control == 1 && state == 1, sample.IsExplicitEco);
        Assert.Equal(control == 1 && state == 0, sample.IsOptedOut);
        Assert.Equal(memory == 1, sample.IsBackgroundMemory);
    }

    [Fact]
    public void Probe_WindowsSelfAndInvalidPid_ReadsStateAndReportsFailure()
    {
        if (!OperatingSystem.IsWindows()) return;
        var sample = ProcessSchedulingProbe.Read(Environment.ProcessId);
        using var current = Process.GetCurrentProcess();
        Assert.Null(sample.UnavailableReason);
        Assert.Equal(current.PriorityClass.ToString(), sample.PriorityClass);
        Assert.NotNull(sample.ControlMask);
        Assert.NotNull(sample.StateMask);
        Assert.NotNull(sample.MemoryPriority);
        Assert.InRange(sample.MemoryPriority.Value, 0, 5);
        var invalid = ProcessSchedulingProbe.Read(0);
        Assert.NotNull(invalid.UnavailableReason);
        Assert.Null(invalid.ControlMask);
        Assert.Null(invalid.StateMask);
    }

    [Fact]
    public void Sampler_ThrottledTicks_CountsDistinctSchedulingStates()
    {
        var clock = new ManualClock();
        var reads = new List<int>();
        var sampler = new LaneSchedulingSampler(100, _ => [100, 101, 102, 103], pid =>
        {
            reads.Add(pid);
            return pid switch
            {
                100 => ProcessSchedulingProbe.FromNative(pid, "root", 0x20, 1, 0, 5),
                101 => ProcessSchedulingProbe.FromNative(pid, "slow.exe", 0x4000, 0, 0, 5),
                102 => ProcessSchedulingProbe.FromNative(pid, "eco", 0x20, 1, 1, 5),
                _ => ProcessSchedulingProbe.FromNative(pid, "memory", 0x20, 0, 0, 1)
            };
        }, clock);
        sampler.Tick();
        clock.Seconds = 10;
        sampler.Tick();
        Assert.Equal(4, reads.Count);
        clock.Seconds = 31;
        sampler.Tick();
        Assert.Equal([100, 101, 102, 103, 100, 101, 102, 103], reads);
        Assert.Equal(new LaneSchedulingSummary(2, 4, "Normal", "1/0", 1, 1, 1, 1, "slow.exe:BelowNormal"), sampler.Summary());
    }

    [Fact]
    public void Sampler_NormalMembers_HasZeroRestrictions()
    {
        var sampler = new LaneSchedulingSampler(100, _ => [100, 101, 102, 103],
            pid => ProcessSchedulingProbe.FromNative(pid, "normal", 0x20, 0, 0, 5));
        sampler.Tick();
        Assert.Equal(new LaneSchedulingSummary(1, 4, "Normal", "0/0", 0, 0, 0, 0, null), sampler.Summary());
    }

    [Fact]
    public void Sampler_FailedEnumeration_ReportsNoJobMembers()
    {
        var sampler = new LaneSchedulingSampler(100, _ => null,
            _ => throw new InvalidOperationException("No members should be read."));
        sampler.Tick();
        Assert.Equal(new LaneSchedulingSummary(0, 0, null, null, 0, 0, 0, 0, null, "no-job-members"), sampler.Summary());
    }

    [Fact]
    public async Task Heartbeat_ThirdTickCancellation_PreservesWriteThenTickOrder()
    {
        using var cancellation = new CancellationTokenSource();
        var events = new List<string>();
        var ticks = 0;
        await GateLaneHeartbeatLoop.RunAsync(first => events.Add(first ? "write(true)" : "write(false)"),
            () => { events.Add("tick"); if (++ticks == 3) cancellation.Cancel(); },
            cancellation.Token, TimeSpan.FromMilliseconds(1)).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(["write(true)", "write(false)", "tick", "write(false)", "tick", "write(false)", "tick"], events);
        await GateLaneHeartbeatLoop.RunAsync(_ => events.Add("after-cancel"), () => events.Add("after-cancel"),
            cancellation.Token, TimeSpan.FromMilliseconds(1));
        Assert.Equal(7, events.Count);
    }

    private static ConductorParallelAcceptanceAttempt Attempt(bool enabled) => new(
        "attempt", "goal", "goal", 0, null, null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch,
        Environment.ProcessId, default, "stdout", "stderr", "exit", "heartbeat", "result", "metadata",
        PolicyJson: (ConductorAutonomyPolicy.Conservative with
        { AcceptanceChildrenOptOutExecutionThrottling = enabled }).ToJson());

    private sealed class ManualClock : TimeProvider
    {
        internal int Seconds { get; set; }
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddSeconds(Seconds);
    }
}
