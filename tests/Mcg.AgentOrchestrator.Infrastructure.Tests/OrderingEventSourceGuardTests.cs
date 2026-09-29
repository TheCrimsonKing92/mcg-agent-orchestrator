using System.Text.RegularExpressions;

public sealed class OrderingEventSourceGuardTests
{
    private static readonly (string File, string Method)[] GuardedMethods =
    [
        ("WorkerProcessJobsTests.cs", "WorkerProcessJobsSuspendedChildRegistersNativeIdentityBeforeReadiness"),
        ("MergeTrainIdentityMaterializationTests.cs", "SameSelectionMaterializedInDifferentUtcSeconds_HasSameIdentity"),
        ("GoalAcceptanceVerifierTests.cs", "GoalAcceptanceVerifierNoHolderLockWaitExcludesProbeTimeAndBlocksAfterWindow")
    ];

    [Xunit.Fact]
    public void OrderingTestsUseEventsAndSuppliedTime()
    {
        var root = InfrastructureTestSupport.FindRepositoryRoot();
        var failures = new List<string>();
        foreach (var (file, method) in GuardedMethods)
        {
            var path = Path.Combine(root, "tests", "Mcg.AgentOrchestrator.Infrastructure.Tests", file);
            Xunit.Assert.True(File.Exists(path), $"Guarded source file missing: {path}");
            var body = RealTimeWindowSourceGuardTests.MethodBody(File.ReadAllText(path), method);
            Xunit.Assert.False(string.IsNullOrWhiteSpace(body), $"Guarded test method missing: {file}::{method}");
            failures.AddRange(ForbiddenCalls(body).Select(call => $"{file}::{method}: {call}"));
        }

        Xunit.Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [Xunit.Fact]
    public void GuardRejectsFormerTimingMechanisms()
    {
        foreach (var call in new[]
        {
            "Stopwatch.GetTimestamp()", "Thread.Sleep(120)", "SpinWait.SpinUntil(() => true)",
            "Task.Delay(10)", "DateTimeOffset.UtcNow", "DateTime.UtcNow",
            "File.GetLastWriteTimeUtc(path)", "Wait(TimeSpan.FromSeconds(5))",
            "WaitAsync(TimeSpan.FromSeconds(5))", "WaitForExitAsync(TimeSpan.FromSeconds(5))",
            "new CancellationTokenSource(TimeSpan.FromSeconds(5))"
        })
            Xunit.Assert.NotEmpty(ForbiddenCalls(call));

        Xunit.Assert.Empty(ForbiddenCalls("WaitAsync(TimeSpan.FromSeconds(30))"));
        Xunit.Assert.Empty(ForbiddenCalls("new CancellationTokenSource(TimeSpan.FromSeconds(30))"));
    }

    [Xunit.Fact]
    public void DefaultMergeTrainRebaseKeepsGitCliRunPath()
    {
        var root = InfrastructureTestSupport.FindRepositoryRoot();
        var path = Path.Combine(root, "src", "Mcg.AgentOrchestrator.Infrastructure", "Workspaces",
            "GoalWorktrees.MergeTrains.cs");
        var source = File.ReadAllText(path);
        Xunit.Assert.Matches(@"committerDate\s+is\s+null\s*\?\s*GitCli\.Run\s*\(", source);
    }

    internal static IReadOnlyList<string> ForbiddenCalls(string body)
    {
        var calls = new List<string>();
        foreach (Match match in Regex.Matches(body,
            @"\b(?:Stopwatch|Thread\.Sleep|SpinWait|Task\.Delay|DateTimeOffset\.UtcNow|DateTime\.UtcNow|File\.GetLastWriteTimeUtc)\b"))
            calls.Add(match.Value);
        calls.AddRange(RealTimeWindowSourceGuardTests.ForbiddenCalls(body)
            .Where(call => call.Contains("Wait", StringComparison.Ordinal)));
        foreach (Match match in Regex.Matches(body,
            @"\b(?:Wait|WaitAsync|WaitForExitAsync|CancellationTokenSource)\s*\(\s*TimeSpan\.From(?<unit>Ticks|Microseconds|Milliseconds|Seconds|Minutes|Hours)\s*\(\s*(?<value>\d+(?:\.\d+)?)\s*\)"))
        {
            var value = double.Parse(match.Groups["value"].Value, System.Globalization.CultureInfo.InvariantCulture);
            var seconds = match.Groups["unit"].Value switch
            {
                "Ticks" => value / TimeSpan.TicksPerSecond,
                "Microseconds" => value / 1_000_000,
                "Milliseconds" => value / 1000,
                "Minutes" => value * 60,
                "Hours" => value * 3600,
                _ => value
            };
            if (seconds < 30) calls.Add(match.Value);
        }
        foreach (Match match in Regex.Matches(body,
            @"\b(?:Wait|WaitAsync|WaitForExitAsync|CancellationTokenSource)\s*\(\s*(?<value>\d+)\s*\)"))
        {
            if (double.Parse(match.Groups["value"].Value, System.Globalization.CultureInfo.InvariantCulture) < 30_000)
                calls.Add(match.Value);
        }
        return calls;
    }
}
