using System.Globalization;
using System.Text.RegularExpressions;

public sealed class ConductorLoopHandoffVerificationClockSourceGuardTests
{
    private static readonly (string File, string Method)[] GuardedMethods =
    [
        ("ConductorBatchLoopTestsSelfHandoff.cs", "ConductorLoopHandoffDoesNotRetryWhileSuccessorIsAlive"),
        ("ConductorBatchLoopTestsSelfHandoff.cs", "ConductorLoopHandoffSlowBootSuccessorSucceedsWithoutRetry"),
        ("ConductorLoopHandoffTests.cs", "AliveSuccessorWaitsPastLegacyTimeoutUntilLoopStart"),
        ("ConductorLoopHandoffTests.cs", "AliveSuccessorHardCeilingReportsAliveTimeout")
    ];

    [Xunit.Fact]
    public void HandoffVerificationTestsUseOnlyFakeTime()
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
    public void GuardDetectsRealTimeCallsAndShortHangBounds()
    {
        foreach (var call in new[]
        {
            "Stopwatch.StartNew()", "Thread.Sleep(x)", "SpinWait.SpinUntil(() => true)",
            "Task.Delay(interval)", "t.Join(5000)", "Wait(TimeSpan.FromSeconds(5))"
        })
            Xunit.Assert.NotEmpty(ForbiddenCalls(call));

        foreach (var call in new[]
        {
            "t.Join(TimeSpan.FromSeconds(30))", "string.Join(\",\", items)",
            "clock.PollWait(interval)", "WaitAsync(TimeSpan.FromSeconds(30))"
        })
            Xunit.Assert.Empty(ForbiddenCalls(call));
    }

    private static IReadOnlyList<string> ForbiddenCalls(string body)
    {
        var calls = new List<string>(RealTimeWindowSourceGuardTests.ForbiddenCalls(body));
        calls.AddRange(Regex.Matches(body, @"\bTask\.Delay\s*\(").Select(match => match.Value)
            .Where(call => !calls.Any(existing => existing.Contains("Task.Delay", StringComparison.Ordinal))));

        foreach (Match match in Regex.Matches(body,
            @"(?<!\bstring\.)\bJoin\s*\(\s*(?:(?<unit>TimeSpan\.From(?:Ticks|Microseconds|Milliseconds|Seconds|Minutes|Hours))\s*\(\s*(?<value>\d+(?:\.\d+)?)|(?<milliseconds>\d+))"))
        {
            var seconds = match.Groups["milliseconds"].Success
                ? double.Parse(match.Groups["milliseconds"].Value, CultureInfo.InvariantCulture) / 1000
                : double.Parse(match.Groups["value"].Value, CultureInfo.InvariantCulture) *
                  (match.Groups["unit"].Value.EndsWith("Ticks", StringComparison.Ordinal) ? .0000001 :
                   match.Groups["unit"].Value.EndsWith("Microseconds", StringComparison.Ordinal) ? .000001 :
                   match.Groups["unit"].Value.EndsWith("Milliseconds", StringComparison.Ordinal) ? .001 :
                   match.Groups["unit"].Value.EndsWith("Minutes", StringComparison.Ordinal) ? 60 :
                   match.Groups["unit"].Value.EndsWith("Hours", StringComparison.Ordinal) ? 3600 : 1);
            if (seconds < 30) calls.Add(match.Value);
        }

        return calls;
    }
}
