using System.Diagnostics;

namespace Mcg.AgentOrchestrator.Infrastructure;

/// <summary>
/// Cross-platform, best-effort lookup of process command lines by pid, shared by the build-daemon
/// reaper and the worktree lock-holder diagnostic. Windows uses <c>wmic</c>; Linux reads
/// <c>/proc/&lt;pid&gt;/cmdline</c>. Only pids whose command line could be read are returned —
/// callers must treat a missing entry as "unknown", never as "matches".
/// </summary>
internal static class ProcessCommandLines
{
    public static ProcessCommandLineSnapshot Snapshot() =>
        new(ReadAll());

    public static Dictionary<int, string> Read(IEnumerable<int> pids)
    {
        var pidList = pids.Distinct().ToList();
        if (pidList.Count == 0)
        {
            return [];
        }

        if (OperatingSystem.IsWindows())
        {
            return ReadWindows(pidList);
        }

        if (OperatingSystem.IsLinux())
        {
            return ReadLinux(pidList);
        }

        return [];
    }

    private static Dictionary<int, string> ReadAll()
    {
        if (OperatingSystem.IsWindows())
        {
            return ReadWindowsAll();
        }

        if (OperatingSystem.IsLinux())
        {
            return ReadLinuxAll();
        }

        return [];
    }

    private static Dictionary<int, string> ReadWindowsAll()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "wmic",
                Arguments = "process get ProcessId,CommandLine /format:list",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process is null)
            {
                return [];
            }

            var outputTask = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(3000))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return [];
            }

            var output = outputTask.GetAwaiter().GetResult();
            return GoalWorktrees.ParseWmicListOutput(output);
        }
        catch
        {
            return [];
        }
    }

    private static Dictionary<int, string> ReadWindows(List<int> pids)
    {
        try
        {
            var filter = string.Join(" OR ", pids.Select(pid => $"ProcessId={pid}"));
            var psi = new ProcessStartInfo
            {
                FileName = "wmic",
                Arguments = $"process where \"({filter})\" get ProcessId,CommandLine /format:list",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process is null)
            {
                return [];
            }

            var outputTask = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(3000))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return [];
            }

            var output = outputTask.GetAwaiter().GetResult();
            return GoalWorktrees.ParseWmicListOutput(output);
        }
        catch
        {
            return [];
        }
    }

    private static Dictionary<int, string> ReadLinux(List<int> pids)
    {
        var result = new Dictionary<int, string>();
        foreach (var pid in pids)
        {
            try
            {
                var path = $"/proc/{pid}/cmdline";
                if (!File.Exists(path))
                {
                    continue;
                }

                // /proc/<pid>/cmdline is NUL-separated argv; join into a readable command line.
                var raw = File.ReadAllText(path);
                var commandLine = raw.Replace('\0', ' ').Trim();
                if (!string.IsNullOrWhiteSpace(commandLine))
                {
                    result[pid] = commandLine;
                }
            }
            catch
            {
                // Best-effort; an unreadable /proc entry is treated as "unknown" by callers.
            }
        }

        return result;
    }

    private static Dictionary<int, string> ReadLinuxAll()
    {
        try
        {
            var pids = Directory
                .EnumerateDirectories("/proc")
                .Select(Path.GetFileName)
                .Where(name => int.TryParse(name, out _))
                .Select(int.Parse)
                .ToList();

            return ReadLinux(pids);
        }
        catch
        {
            return [];
        }
    }
}

public sealed class ProcessCommandLineSnapshot
{
    private readonly IReadOnlyDictionary<int, string> _commandLines;
    private readonly Action<int>? _onRead;

    internal ProcessCommandLineSnapshot(IReadOnlyDictionary<int, string> commandLines, Action<int>? onRead = null)
    {
        _commandLines = commandLines;
        _onRead = onRead;
    }

    public static ProcessCommandLineSnapshot Empty { get; } = new(new Dictionary<int, string>());

    public IReadOnlyDictionary<int, string> Read(IEnumerable<int> pids)
    {
        var result = new Dictionary<int, string>();
        var distinctPids = pids.Distinct().ToArray();
        _onRead?.Invoke(distinctPids.Length);
        foreach (var pid in distinctPids)
        {
            if (_commandLines.TryGetValue(pid, out var commandLine))
            {
                result[pid] = commandLine;
            }
        }

        return result;
    }
}
