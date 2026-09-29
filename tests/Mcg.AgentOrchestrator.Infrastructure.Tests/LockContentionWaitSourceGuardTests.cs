public sealed class LockContentionWaitSourceGuardTests
{
    private static readonly (string File, string Method)[] GuardedMethods =
    [
        ("DotnetBuildEnvironmentManagerTestsLeasePermitsStaleRecovery.cs",
            "DotnetBuildEnvironmentManagerSerializesSameGoalLeaseExecution"),
        ("PostLandingCanaryTests.cs", "PortableFileLeaseSerializesConcurrentCanaries"),
        ("PostLandingCanaryTests.cs", "QueuedLandingShasRunFifoWithOneReceiptEach")
    ];

    [Xunit.Fact]
    public void ContentionTestsObserveTheirWaitsWithoutShortTimeWindows()
    {
        var root = InfrastructureTestSupport.FindRepositoryRoot();
        var failures = new List<string>();
        foreach (var (file, method) in GuardedMethods)
        {
            var path = Path.Combine(root, "tests", "Mcg.AgentOrchestrator.Infrastructure.Tests", file);
            Xunit.Assert.True(File.Exists(path), $"Guarded source file missing: {path}");
            var body = RealTimeWindowSourceGuardTests.MethodBody(File.ReadAllText(path), method);
            Xunit.Assert.False(string.IsNullOrEmpty(body), $"Guarded test method missing: {file}::{method}");
            failures.AddRange(RealTimeWindowSourceGuardTests.ForbiddenCalls(body)
                .Select(call => $"{file}::{method}: {call}"));
        }

        Xunit.Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [Xunit.Fact]
    public void GuardCatchesFormerShortWindowsAndAllowsEventGates()
    {
        foreach (var call in new[]
        {
            "Thread.Sleep(50)", "SpinWait.SpinUntil(() => true)", "Stopwatch.StartNew()",
            "Task.Delay(200)", "Task.Delay(250)", "Task.Delay(150)",
            "WaitAsync(TimeSpan.FromSeconds(5))", "Wait(TimeSpan.FromMilliseconds(100))"
        })
            Xunit.Assert.NotEmpty(RealTimeWindowSourceGuardTests.ForbiddenCalls(call));

        Xunit.Assert.Empty(RealTimeWindowSourceGuardTests.ForbiddenCalls(
            "resume.Wait(TestHangGuard.Bound)"));
        Xunit.Assert.Empty(RealTimeWindowSourceGuardTests.ForbiddenCalls(
            "Task.Delay(interval, token)"));
    }
}
