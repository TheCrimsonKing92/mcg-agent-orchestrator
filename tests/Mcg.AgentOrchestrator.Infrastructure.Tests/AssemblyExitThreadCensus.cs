using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;

internal static class AssemblyExitThreadCensus
{
    private const int MaximumReportedThreads = 16;
    private static IReadOnlySet<int> _baselineThreadIds = new HashSet<int>();

    [ModuleInitializer]
    internal static void Install()
    {
        try
        {
            _baselineThreadIds = CaptureCurrentThreadIds();
            AppDomain.CurrentDomain.ProcessExit += (_, _) => ReportNewThreadsOnFailedExit();
        }
        catch (Exception ex)
        {
            WriteUnavailable(ex);
        }
    }

    private static IReadOnlySet<int> CaptureCurrentThreadIds()
    {
        using var process = Process.GetCurrentProcess();
        return process.Threads
            .Cast<ProcessThread>()
            .Select(thread => thread.Id)
            .ToHashSet();
    }

    private static void ReportNewThreadsOnFailedExit()
    {
        try
        {
            if (!ShouldReportForExitCode(Environment.ExitCode))
            {
                return;
            }

            using var process = Process.GetCurrentProcess();
            var census = ProjectNewestThreadsForReport(
                process.Threads
                .Cast<ProcessThread>()
                .Where(thread => !_baselineThreadIds.Contains(thread.Id)),
                GetThreadStartTimeOrMinValue,
                DescribeThread);
            if (census.TotalCount == 0)
            {
                return;
            }

            Console.Error.WriteLine(BuildCensusDiagnostic(Environment.ExitCode, census));
        }
        catch (Exception ex)
        {
            WriteUnavailable(ex);
        }
    }

    internal static bool ShouldReportForExitCode(int exitCode) => exitCode == 1;

    internal static ThreadCensusProjection ProjectNewestThreadsForReport<T>(
        IEnumerable<T> threads,
        Func<T, DateTime> getStartTime,
        Func<T, string> describe)
    {
        var newestFirst = threads
            .Select(thread => (Thread: thread, StartTime: getStartTime(thread)))
            .OrderByDescending(item => item.StartTime)
            .ToArray();
        var descriptions = newestFirst
            .Take(MaximumReportedThreads)
            .Select(item => describe(item.Thread))
            .ToArray();
        return new ThreadCensusProjection(newestFirst.Length, descriptions);
    }

    internal static string BuildCensusDiagnostic(int exitCode, ThreadCensusProjection census) =>
        $"test-host-exit-thread-census exit_code={exitCode.ToString(CultureInfo.InvariantCulture)} " +
        $"new_os_threads={census.TotalCount.ToString(CultureInfo.InvariantCulture)} " +
        $"reported_os_threads={census.Threads.Count.ToString(CultureInfo.InvariantCulture)} " +
        $"truncated={census.Truncated.ToString().ToLowerInvariant()} " +
        $"threads={string.Join(';', census.Threads)}";

    private static DateTime GetThreadStartTimeOrMinValue(ProcessThread thread)
    {
        try
        {
            return thread.StartTime;
        }
        catch
        {
            return DateTime.MinValue;
        }
    }

    private static string DescribeThread(ProcessThread thread)
    {
        try
        {
            var waitReason = thread.ThreadState == System.Diagnostics.ThreadState.Wait
                ? thread.WaitReason.ToString()
                : "not-waiting";
            return
                $"id:{thread.Id.ToString(CultureInfo.InvariantCulture)}," +
                $"started:{thread.StartTime.ToUniversalTime():O}," +
                $"cpu_ms:{thread.TotalProcessorTime.TotalMilliseconds.ToString("F0", CultureInfo.InvariantCulture)}," +
                $"state:{thread.ThreadState},wait:{waitReason}";
        }
        catch (Exception ex)
        {
            return $"id:{thread.Id.ToString(CultureInfo.InvariantCulture)},detail_unavailable:{ex.GetType().Name}";
        }
    }

    private static void WriteUnavailable(Exception ex)
    {
        try
        {
            Console.Error.WriteLine($"test-host-exit-thread-census unavailable exception_type={ex.GetType().Name}");
        }
        catch
        {
            // Diagnostics must never change the test-host exit path.
        }
    }

    internal sealed record ThreadCensusProjection(int TotalCount, IReadOnlyList<string> Threads)
    {
        internal bool Truncated => TotalCount > Threads.Count;
    }
}
