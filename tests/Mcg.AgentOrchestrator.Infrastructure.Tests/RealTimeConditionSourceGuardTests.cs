using System.Text.RegularExpressions;

public sealed class RealTimeConditionSourceGuardTests
{
    private static readonly (string File, string Method)[] GuardedMethods =
    [
        ("SqliteOrchestratorStateRepositoryTests.cs", "SlowWriteAndBlockedWriterEmitJsonlReceipts"),
        ("GoalWorktreeTestsRemoveCleanupDebt.cs", "GoalWorktreesRemoveRetriesAndSucceedsWhenTransientLockReleases"),
        ("LocalProcessVerifierTests.cs", "LocalProcessVerifierReportsConfiguredTimeoutAsStructuredEvidence"),
    ];

    [Xunit.Fact]
    public void GuardedMethodsDriveConditionsWithoutRealTime()
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
    public void GuardDetectsTimersAndShortLiteralBounds()
    {
        foreach (var call in new[]
        {
            "Stopwatch.StartNew()", "Thread.Sleep(50)", "SpinWait.SpinUntil(() => true)",
            "CancelAfter(timeout)", "Task.Delay(150)", "Task.Delay(TimeSpan.FromSeconds(3))",
            "WaitAsync(TimeSpan.FromSeconds(5))", "Join(5000)"
        })
            Xunit.Assert.NotEmpty(ForbiddenCalls(call));

        Xunit.Assert.Empty(ForbiddenCalls("TestHangGuard.WaitAsync(task, \"initial snapshot\")"));
        Xunit.Assert.Empty(ForbiddenCalls("WaitAsync(TimeSpan.FromSeconds(30))"));
        Xunit.Assert.Empty(ForbiddenCalls("string.Join(Environment.NewLine, failures)"));
    }

    private static IReadOnlyList<string> ForbiddenCalls(string body)
    {
        var calls = RealTimeWindowSourceGuardTests.ForbiddenCalls(body).ToList();
        foreach (Match match in Regex.Matches(body, @"\bCancelAfter\s*\("))
            calls.Add(match.Value);
        foreach (Match match in Regex.Matches(body,
            @"\bJoin\s*\(\s*(?:\d+(?:\.\d+)?|TimeSpan\.From\w+\s*\(\s*\d+(?:\.\d+)?)"))
        {
            if (RealTimeWindowSourceGuardTests.ForbiddenCalls(
                    match.Value.Replace("Join", "Wait", StringComparison.Ordinal)).Count > 0)
                calls.Add(match.Value);
        }
        return calls;
    }
}
