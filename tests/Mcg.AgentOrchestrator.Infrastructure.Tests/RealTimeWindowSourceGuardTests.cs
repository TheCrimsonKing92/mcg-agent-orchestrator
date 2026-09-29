using System.Text.RegularExpressions;

public sealed class RealTimeWindowSourceGuardTests
{
    private static readonly (string File, string Method)[] GuardedMethods =
    [
        ("AcceptanceOutputCaptureTests.cs", "ConsumedSubBufferOutputIsPublishedWhileSourceRemainsOpen"),
        ("AcceptanceOutputCaptureTests.cs", "TerminalOnlyFlushControlWithholdsLiveBytesButPreservesFinalOutput"),
        ("StewardTests.cs", "StewardDispatcherTimeoutCancelsTimedOutTriageWork"),
        ("StewardTests.cs", "StewardDispatcherFailOpenRoutesRawWhenEngineTimesOut"),
        ("ConductorBatchLoopTestsParallelAcceptance.cs", "BatchLoopDocIntersectionAdmitsConcurrentlyWithEvidence")
    ];

    [Xunit.Fact]
    public void GuardedTestsUseEventsAndInjectedTimeInsteadOfShortWindows()
    {
        var root = InfrastructureTestSupport.FindRepositoryRoot();
        var failures = new List<string>();
        foreach (var (file, method) in GuardedMethods)
        {
            var path = Path.Combine(root, "tests", "Mcg.AgentOrchestrator.Infrastructure.Tests", file);
            Xunit.Assert.True(File.Exists(path), $"Guarded source file missing: {path}");
            var body = MethodBody(File.ReadAllText(path), method);
            Xunit.Assert.False(string.IsNullOrEmpty(body), $"Guarded test method missing: {file}::{method}");
            failures.AddRange(ForbiddenCalls(body).Select(call => $"{file}::{method}: {call}"));
        }

        Xunit.Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [Xunit.Fact]
    public void GuardDetectsFormerWindowsAndAllowsHangGuards()
    {
        foreach (var call in new[]
        {
            "Thread.Sleep(50)", "SpinWait.SpinUntil(() => true)", "Stopwatch.StartNew()",
            "Task.Delay(TimeSpan.FromSeconds(3))", "WaitAsync(TimeSpan.FromSeconds(5))",
            "Wait(TimeSpan.FromMilliseconds(100))", "WaitAsync(1000)"
        })
            Xunit.Assert.NotEmpty(ForbiddenCalls(call));

        Xunit.Assert.Empty(ForbiddenCalls("Wait(TestHangGuard.Bound)"));
        Xunit.Assert.Empty(ForbiddenCalls("WaitAsync(TimeSpan.FromSeconds(30))"));
    }

    internal static string MethodBody(string source, string method)
    {
        var declaration = Regex.Match(source,
            @"\b(?:public|private|internal)\s+(?:async\s+)?(?:void|Task(?:<[^>]+>)?)\s+" +
            Regex.Escape(method) + @"\s*\(");
        if (!declaration.Success) return string.Empty;
        var start = source.IndexOf('{', declaration.Index + declaration.Length);
        if (start < 0) return string.Empty;
        var depth = 0;
        for (var index = start; index < source.Length; index++)
        {
            if (source[index] == '{') depth++;
            if (source[index] == '}' && --depth == 0) return source[(start + 1)..index];
        }
        return string.Empty;
    }

    internal static IReadOnlyList<string> ForbiddenCalls(string body)
    {
        var calls = new List<string>();
        foreach (Match match in Regex.Matches(body, @"\b(?:Thread\.Sleep|SpinWait\b|Stopwatch\b)\s*(?:\.|\()"))
            calls.Add(match.Value);
        foreach (Match match in Regex.Matches(body,
            @"\bTask\.Delay\s*\(\s*(?:\d+(?:\.\d+)?|TimeSpan\.From\w+\s*\(\s*\d+(?:\.\d+)?)"))
            calls.Add(match.Value);
        foreach (Match match in Regex.Matches(body,
            @"\bWait(?:Async)?\s*\(\s*(?:(?<unit>TimeSpan\.From(?:Ticks|Microseconds|Milliseconds|Seconds|Minutes|Hours))\s*\(\s*(?<value>\d+(?:\.\d+)?)|(?<ticks>\d+))"))
        {
            var value = match.Groups["ticks"].Success
                ? double.Parse(match.Groups["ticks"].Value, System.Globalization.CultureInfo.InvariantCulture) / 1000
                : double.Parse(match.Groups["value"].Value, System.Globalization.CultureInfo.InvariantCulture) *
                  (match.Groups["unit"].Value.EndsWith("Ticks", StringComparison.Ordinal) ? .0000001 :
                   match.Groups["unit"].Value.EndsWith("Microseconds", StringComparison.Ordinal) ? .000001 :
                   match.Groups["unit"].Value.EndsWith("Milliseconds", StringComparison.Ordinal) ? .001 :
                   match.Groups["unit"].Value.EndsWith("Minutes", StringComparison.Ordinal) ? 60 :
                   match.Groups["unit"].Value.EndsWith("Hours", StringComparison.Ordinal) ? 3600 : 1);
            if (value < 30) calls.Add(match.Value);
        }
        return calls;
    }
}
