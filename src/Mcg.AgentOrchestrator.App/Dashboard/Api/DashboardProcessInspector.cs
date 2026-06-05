using System.Diagnostics;
using System.Globalization;
using Mcg.AgentOrchestrator.App.Dashboard.Rendering;

namespace Mcg.AgentOrchestrator.App.Dashboard.Api;

internal static class DashboardProcessInspector
{
    internal const string DashboardProcessName = "Mcg.AgentOrchestrator.App";

    public static DashboardProcessDiagnostic InspectCurrent()
    {
        var currentPath = TryGetMainModulePath(Process.GetCurrentProcess());
        var listeningPortsByPid = GetListeningTcpPortsByPid();
        return Build(
            DashboardProcessName,
            Environment.ProcessId,
            currentPath,
            EnumerateProcesses(DashboardProcessName),
            listeningPortsByPid);
    }

    internal static DashboardProcessDiagnostic Build(
        string processName,
        int currentProcessId,
        string? currentExecutablePath,
        IReadOnlyList<DashboardProcessSnapshot> processes,
        IReadOnlyDictionary<int, IReadOnlyList<int>>? listeningPortsByPid = null)
    {
        listeningPortsByPid ??= new Dictionary<int, IReadOnlyList<int>>();
        var currentListeningPorts = GetPortsForPid(listeningPortsByPid, currentProcessId);
        var siblings = processes
            .Where(process => process.ProcessId != currentProcessId)
            .Where(process => IsMatchingDashboardProcess(currentExecutablePath, process))
            .OrderBy(process => process.ProcessId)
            .Select(process => new DashboardSiblingProcessContext(
                process.ProcessId,
                process.ExecutablePath,
                process.StartedAt,
                GetPortsForPid(listeningPortsByPid, process.ProcessId),
                $"Stop-Process -Id {process.ProcessId.ToString(CultureInfo.InvariantCulture)}",
                process.Detail))
            .ToList();

        var message = siblings.Count == 0
            ? "No duplicate dashboard app processes were detected for this prototype app executable."
            : $"Detected {siblings.Count} sibling dashboard app process(es). Stop only exact known PIDs after confirming they are stale.";

        return new DashboardProcessDiagnostic(
            currentProcessId,
            processName,
            currentExecutablePath,
            currentListeningPorts,
            siblings,
            message);
    }

    internal static Dictionary<int, IReadOnlyList<int>> ParseNetstatListeningPorts(string output)
    {
        var portsByPid = new Dictionary<int, SortedSet<int>>();
        using var reader = new StringReader(output);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 5 ||
                !parts[0].Equals("TCP", StringComparison.OrdinalIgnoreCase) ||
                !parts[^2].Equals("LISTENING", StringComparison.OrdinalIgnoreCase) ||
                !int.TryParse(parts[^1], NumberStyles.None, CultureInfo.InvariantCulture, out var pid))
            {
                continue;
            }

            var localEndpoint = parts[1];
            if (!TryParsePort(localEndpoint, out var port))
            {
                continue;
            }

            if (!portsByPid.TryGetValue(pid, out var ports))
            {
                ports = [];
                portsByPid.Add(pid, ports);
            }

            ports.Add(port);
        }

        return portsByPid.ToDictionary(
            item => item.Key,
            item => (IReadOnlyList<int>)item.Value.ToList());
    }

    private static Dictionary<int, IReadOnlyList<int>> GetListeningTcpPortsByPid()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "netstat",
                Arguments = "-ano -p TCP",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });

            if (process is null)
            {
                return [];
            }

            var output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(1500))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                    // Process exited after the timeout check.
                }

                return [];
            }

            return ParseNetstatListeningPorts(output);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            return [];
        }
    }

    private static IReadOnlyList<int> GetPortsForPid(
        IReadOnlyDictionary<int, IReadOnlyList<int>> listeningPortsByPid,
        int pid)
    {
        return listeningPortsByPid.TryGetValue(pid, out var ports)
            ? ports
            : [];
    }

    private static bool TryParsePort(string localEndpoint, out int port)
    {
        var index = localEndpoint.LastIndexOf(':');
        if (index < 0 || index == localEndpoint.Length - 1)
        {
            port = 0;
            return false;
        }

        return int.TryParse(localEndpoint[(index + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out port);
    }

    private static bool IsMatchingDashboardProcess(string? currentExecutablePath, DashboardProcessSnapshot process)
    {
        if (string.IsNullOrWhiteSpace(currentExecutablePath) || string.IsNullOrWhiteSpace(process.ExecutablePath))
        {
            return true;
        }

        return string.Equals(currentExecutablePath, process.ExecutablePath, StringComparison.OrdinalIgnoreCase);
    }

    private static List<DashboardProcessSnapshot> EnumerateProcesses(string processName)
    {
        return Process.GetProcessesByName(processName)
            .Select(process =>
            {
                using (process)
                {
                    return new DashboardProcessSnapshot(
                        process.Id,
                        TryGetMainModulePath(process),
                        TryGetStartTime(process),
                        "same process name");
                }
            })
            .ToList();
    }

    private static string? TryGetMainModulePath(Process process)
    {
        try
        {
            return process.MainModule?.FileName;
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private static DateTimeOffset? TryGetStartTime(Process process)
    {
        try
        {
            return process.StartTime;
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }
}

internal sealed record DashboardProcessSnapshot(
    int ProcessId,
    string? ExecutablePath,
    DateTimeOffset? StartedAt,
    string Detail);
