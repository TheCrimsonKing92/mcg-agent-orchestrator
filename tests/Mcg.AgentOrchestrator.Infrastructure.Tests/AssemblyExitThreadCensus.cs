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
            var survivors = process.Threads
                .Cast<ProcessThread>()
                .Where(thread => !_baselineThreadIds.Contains(thread.Id))
                .Take(MaximumReportedThreads)
                .Select(DescribeThread)
                .ToArray();
            if (survivors.Length == 0)
            {
                return;
            }

            Console.Error.WriteLine(
                $"test-host-exit-thread-census exit_code={Environment.ExitCode.ToString(CultureInfo.InvariantCulture)} " +
                $"new_os_threads={survivors.Length.ToString(CultureInfo.InvariantCulture)} " +
                $"threads={string.Join(';', survivors)}");
        }
        catch (Exception ex)
        {
            WriteUnavailable(ex);
        }
    }

    internal static bool ShouldReportForExitCode(int exitCode) => exitCode == 1;

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
}
