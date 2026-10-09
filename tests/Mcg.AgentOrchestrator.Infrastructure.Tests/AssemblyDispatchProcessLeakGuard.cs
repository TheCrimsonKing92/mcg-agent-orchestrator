using System.Diagnostics;
using System.Reflection;
using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit.v3;

[assembly: DispatchProcessLeakGuard]

// The assembly attribute's After exception is attached to the test, before temp cleanup.
internal sealed class DispatchProcessLeakGuardAttribute : BeforeAfterTestAttribute
{
    public override void Before(MethodInfo methodUnderTest, IXunitTest test) =>
        DispatchProcessLeakGuard.Begin(test.UniqueID);

    public override void After(MethodInfo methodUnderTest, IXunitTest test) =>
        DispatchProcessLeakGuard.End(test.UniqueID);
}

internal sealed record DispatchProcessLeak(int ProcessId, DateTimeOffset StartedAt, string DispatchFile);

internal static class DispatchProcessLeakGuard
{
    private sealed class Scope
    {
        internal DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;
        internal bool Overlapped { get; set; }
    }

    private static readonly object Gate = new();
    private static readonly Dictionary<string, Scope> Scopes = [];
    private static readonly Regex DispatchArgument = new(
        @"(?:^|\s)__dispatch-run\s+(?:""(?<path>[^""]+)""|(?<path>\S+))",
        RegexOptions.CultureInvariant);

    internal static bool HasScope(string testId)
    {
        lock (Gate) return Scopes.ContainsKey(testId);
    }

    internal static void Begin(string testId)
    {
        lock (Gate)
        {
            var scope = new Scope { Overlapped = Scopes.Count != 0 };
            foreach (var active in Scopes.Values) active.Overlapped = true;
            Scopes.Add(testId, scope);
        }
    }

    internal static void End(string testId)
    {
        Scope scope;
        DateTimeOffset endedAt;
        lock (Gate)
        {
            // Contract tests call this actual After hook themselves and catch its failure.
            if (!Scopes.Remove(testId, out scope!)) return;
            endedAt = DateTimeOffset.UtcNow;
        }

        var snapshot = ProcessCommandLines.SnapshotByNames(["dotnet"]);
        if (snapshot.Failure is not null || !OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
        {
            Console.Error.WriteLine($"dispatch-leak-guard unavailable test={testId}: process command-line enumeration failed.");
            return;
        }

        var leaks = FindLeaks(scope.StartedAt, endedAt, Environment.ProcessId, snapshot)
            .Where(IsStillSameHost).ToArray();
        if (leaks.Length != 0 && scope.Overlapped)
        {
            Console.Error.WriteLine($"dispatch-leak-guard ambiguous test={testId}: another test overlapped; process ownership cannot be attributed.");
            return;
        }
        if (leaks.Length != 0)
            throw new Xunit.Sdk.XunitException(
                "Test returned while dispatch hosts it started are still running:" + Environment.NewLine +
                string.Join(Environment.NewLine, leaks.Select(leak =>
                    $"pid={leak.ProcessId} dispatch={leak.DispatchFile}")));
    }

    internal static IReadOnlyList<DispatchProcessLeak> FindLeaks(
        DateTimeOffset startedAt, DateTimeOffset endedAt, int ownerPid, ProcessCommandLineSnapshot snapshot)
    {
        if (snapshot.Failure is not null) return [];
        var leaks = new List<DispatchProcessLeak>();
        foreach (var record in snapshot.Records.Values)
        {
            if (record.ParentProcessId != ownerPid || record.Status != ProcessInspectionStatus.Available ||
                record.StartedAt is not { } processStart || processStart < startedAt || processStart > endedAt ||
                !string.Equals(record.Name, "dotnet", StringComparison.OrdinalIgnoreCase) ||
                record.CommandLine is null) continue;
            var argument = DispatchArgument.Match(record.CommandLine);
            if (argument.Success)
                leaks.Add(new(record.ProcessId, processStart, argument.Groups["path"].Value));
        }
        return leaks;
    }

    private static bool IsStillSameHost(DispatchProcessLeak leak)
    {
        try
        {
            using var process = Process.GetProcessById(leak.ProcessId);
            return !process.HasExited &&
                new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero) == leak.StartedAt;
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }
}
