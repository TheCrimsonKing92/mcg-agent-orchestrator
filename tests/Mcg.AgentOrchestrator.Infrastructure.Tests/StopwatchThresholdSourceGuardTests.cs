using System.Text.RegularExpressions;

public sealed class StopwatchThresholdSourceGuardTests
{
    private static readonly (string File, string Method)[] GuardedMethods =
    [
        ("ConductorEvidenceAttemptLifecycleTests.cs", "AttemptWriterLease_WhenHeld_TimesOutWithTypedReceipt"),
        ("ConductorEvidenceAttemptLifecycleTests.cs", "LiveAttemptObservation_DoesNotEnterWriterLeaseBoundary"),
        ("ConductorEvidenceAttemptLifecycleTests.cs", "WriterLeaseBusyBeforeFirstMetadataWriteSignalsDeferredObservation"),
        ("SqliteOrchestratorStateRepositoryTests.cs", "ContendedWriteUsesOneElapsedBudget"),
        ("ConductorDriverTestsDeveloperCompletionStructuralPreflight.cs", "HungPrecheck_IsAbandonedAfterBoundAndTesterDispatchContinues")
    ];

    [Xunit.Fact]
    public void GuardedMethodsAssertMechanismsWithoutShortTimingWindows()
    {
        var root = InfrastructureTestSupport.FindRepositoryRoot();
        var failures = new List<string>();
        foreach (var (file, method) in GuardedMethods)
        {
            var path = Path.Combine(root, "tests", "Mcg.AgentOrchestrator.Infrastructure.Tests", file);
            Xunit.Assert.True(File.Exists(path), $"Guarded source file missing: {path}");
            var body = RealTimeWindowSourceGuardTests.MethodBody(File.ReadAllText(path), method);
            Xunit.Assert.False(string.IsNullOrEmpty(body), $"Guarded test method missing: {file}::{method}");
            failures.AddRange(ForbiddenCalls(body).Select(call => $"{file}::{method}: {call}"));
        }

        Xunit.Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [Xunit.Fact]
    public void GuardDetectsShortJoinBoundsAndFormerStopwatchChecks()
    {
        foreach (var call in new[]
        {
            "Stopwatch.StartNew()", "System.Diagnostics.Stopwatch.StartNew()",
            "Thread.Sleep(50)", "SpinWait.SpinUntil(() => true)",
            "Task.Delay(TimeSpan.FromSeconds(5))", "Wait(TimeSpan.FromSeconds(10))",
            "WaitAsync(1000)", "Join(TimeSpan.FromSeconds(5))", "Join(10000)"
        })
            Xunit.Assert.NotEmpty(ForbiddenCalls(call));

        Xunit.Assert.Empty(ForbiddenCalls("Join(TestHangGuard.Bound)"));
        Xunit.Assert.Empty(ForbiddenCalls("release.Wait()"));
        Xunit.Assert.Empty(ForbiddenCalls("string.Join(Environment.NewLine, failures)"));
    }

    private static IReadOnlyList<string> ForbiddenCalls(string body)
    {
        var calls = RealTimeWindowSourceGuardTests.ForbiddenCalls(body).ToList();
        foreach (Match match in Regex.Matches(body,
            @"\bJoin\s*\(\s*(?:\d+(?:\.\d+)?|TimeSpan\.From\w+\s*\(\s*\d+(?:\.\d+)?)"))
        {
            if (RealTimeWindowSourceGuardTests.ForbiddenCalls(match.Value.Replace("Join", "Wait", StringComparison.Ordinal)).Count > 0)
                calls.Add(match.Value);
        }
        return calls;
    }
}
