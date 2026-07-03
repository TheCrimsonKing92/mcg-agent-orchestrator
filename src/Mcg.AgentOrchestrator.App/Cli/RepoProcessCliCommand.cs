using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class RepoProcessCliCommand
{
    private const int MaxCommandLength = 420;

    public static void PrintInfo(IReadOnlyList<string> parts, TextWriter output)
    {
        var options = RepoProcessOptions.Parse(parts);
        if (!options.HasQuery)
        {
            throw new ArgumentException("Usage: repo-process-info [--id <pid>] [--parent-id <pid>] [--name <name>] [--command-contains <text>] [--newest <n>] [--conduct-loop] [--dispatch-host] [--include-children] [--locks]");
        }

        IReadOnlyList<ProcessSnapshot> snapshots;
        try
        {
            snapshots = BuildSnapshots();
        }
        catch (Exception ex)
        {
            PrintUnavailable(output, "enumerate", ex);
            return;
        }

        var byId = snapshots.ToDictionary(snapshot => snapshot.ProcessId);
        var childrenByParent = snapshots
            .Where(snapshot => snapshot.ParentProcessId > 0)
            .GroupBy(snapshot => snapshot.ParentProcessId)
            .ToDictionary(group => group.Key, group => group.OrderBy(snapshot => snapshot.ProcessId).ToList());
        var seen = new HashSet<int>();
        var selected = new List<ProcessSnapshot>();

        foreach (var processId in options.Ids)
        {
            if (byId.TryGetValue(processId, out var snapshot))
            {
                AddSelected(snapshot, options.IncludeChildren, childrenByParent, seen, selected);
            }
            else if (seen.Add(processId))
            {
                output.WriteLine($"PROCESS id={processId} status=missing");
            }
        }

        foreach (var parentId in options.ParentIds)
        {
            if (childrenByParent.TryGetValue(parentId, out var children))
            {
                foreach (var child in children)
                {
                    AddSelected(child, options.IncludeChildren, childrenByParent, seen, selected);
                }
            }
        }

        if (options.Names.Count > 0 || options.CommandContains.Count > 0 || options.LocksOnly)
        {
            var query = snapshots.Where(snapshot =>
                MatchesNames(snapshot, options.Names) &&
                MatchesCommand(snapshot, options.CommandContains) &&
                (!options.LocksOnly || IsLockHolder(snapshot)))
                .OrderByDescending(snapshot => snapshot.StartedAt ?? DateTimeOffset.MinValue)
                .ThenByDescending(snapshot => snapshot.ProcessId)
                .Take(options.Newest);
            foreach (var snapshot in query)
            {
                AddSelected(snapshot, options.IncludeChildren, childrenByParent, seen, selected);
            }
        }

        if (selected.Count == 0 && options.ShouldPrintEmptyMessage)
        {
            output.WriteLine(options.LocksOnly
                ? "No orchestrator lock-holders running; in-tree build lock is FREE."
                : "No matching repo processes found.");
            return;
        }

        foreach (var snapshot in selected)
        {
            var prefix = options.LocksOnly && IsLockHolder(snapshot)
                ? $"LOCK id={snapshot.ProcessId} kind={ClassifyKind(snapshot)}"
                : $"PROCESS id={snapshot.ProcessId}";
            output.WriteLine($"{prefix} parent={snapshot.ParentProcessId} name={snapshot.Name} created={FormatTime(snapshot.StartedAt)} path={snapshot.ExecutablePath ?? ""} command={ShortCommand(snapshot.CommandLine)}");
        }
    }

    public static void Stop(IReadOnlyList<string> parts, TextWriter output)
    {
        var options = RepoProcessStopOptions.Parse(parts);
        var snapshots = BuildSnapshots().ToDictionary(snapshot => snapshot.ProcessId);
        foreach (var processId in options.Ids)
        {
            if (!snapshots.TryGetValue(processId, out var snapshot))
            {
                output.WriteLine($"PROCESS id={processId} status=missing");
                continue;
            }

            var refused = false;
            foreach (var needle in options.CommandContains)
            {
                if (string.IsNullOrWhiteSpace(snapshot.CommandLine) ||
                    snapshot.CommandLine.IndexOf(needle, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    output.WriteLine($"PROCESS id={processId} status=refused reason=command-mismatch expected={needle}");
                    refused = true;
                    break;
                }
            }

            if (refused)
            {
                continue;
            }

            var stopped = WorkerProcessJobs.TryKillOrFallback(processId);
            output.WriteLine($"PROCESS id={processId} status={(stopped ? "stopped" : "not-stopped")}");
        }
    }

    private static IReadOnlyList<ProcessSnapshot> BuildSnapshots()
    {
        var raw = new List<(int Id, int ParentId, string Name, string? Path, DateTimeOffset? StartedAt)>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                raw.Add((process.Id, TryGetParentProcessId(process.Id), process.ProcessName, TryGetExecutablePath(process), TryGetStartTime(process)));
            }
        }

        var commandLines = ProcessCommandLines.Read(raw.Select(process => process.Id));
        return raw
            .Select(process => new ProcessSnapshot(
                process.Id,
                process.ParentId,
                process.Name,
                process.Path,
                process.StartedAt,
                commandLines.GetValueOrDefault(process.Id)))
            .ToList();
    }

    private static void AddSelected(
        ProcessSnapshot snapshot,
        bool includeChildren,
        IReadOnlyDictionary<int, List<ProcessSnapshot>> childrenByParent,
        HashSet<int> seen,
        List<ProcessSnapshot> selected)
    {
        if (!seen.Add(snapshot.ProcessId))
        {
            return;
        }

        selected.Add(snapshot);
        if (!includeChildren || !childrenByParent.TryGetValue(snapshot.ProcessId, out var children))
        {
            return;
        }

        foreach (var child in children)
        {
            AddSelected(child, includeChildren, childrenByParent, seen, selected);
        }
    }

    private static bool MatchesNames(ProcessSnapshot snapshot, IReadOnlyList<string> names) =>
        names.Count == 0 ||
        names.Any(name =>
            snapshot.Name.Equals(name, StringComparison.OrdinalIgnoreCase) ||
            snapshot.Name.Equals(Path.GetFileNameWithoutExtension(name), StringComparison.OrdinalIgnoreCase));

    private static bool MatchesCommand(ProcessSnapshot snapshot, IReadOnlyList<string> needles) =>
        needles.Count == 0 ||
        needles.All(needle => (snapshot.CommandLine ?? string.Empty).IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0);

    private static bool IsLockHolder(ProcessSnapshot snapshot)
    {
        var command = snapshot.CommandLine ?? string.Empty;
        return command.IndexOf("App.dll", StringComparison.OrdinalIgnoreCase) >= 0 ||
            command.IndexOf("__dispatch-run", StringComparison.OrdinalIgnoreCase) >= 0 ||
            command.IndexOf("DispatchProcessHost", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static string ClassifyKind(ProcessSnapshot snapshot)
    {
        var command = snapshot.CommandLine ?? string.Empty;
        if (command.IndexOf("__dispatch-run", StringComparison.OrdinalIgnoreCase) >= 0 ||
            command.IndexOf("DispatchProcessHost", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return "dispatch-host";
        }

        if (command.IndexOf("conduct", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return "conduct-loop";
        }

        if (command.IndexOf("serve-dashboard", StringComparison.OrdinalIgnoreCase) >= 0 ||
            command.IndexOf("-dashboard", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return "dashboard";
        }

        return "app-host";
    }

    private static void PrintUnavailable(TextWriter output, string operation, Exception ex)
    {
        output.WriteLine($"PROCESS_QUERY_UNAVAILABLE operation={operation} reason={ex.GetType().Name}: {ex.Message}");
        output.WriteLine("BACKLOG_CANDIDATE title=\"Repo process helper degraded\" body=\"Process helper could not inspect local processes through the orchestrator CLI; preserve this disposition instead of requesting operator approval.\"");
    }

    private static string ShortCommand(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return "";
        }

        var normalized = string.Join(' ', command.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return normalized.Length <= MaxCommandLength ? normalized : normalized[..MaxCommandLength] + "...";
    }

    private static string FormatTime(DateTimeOffset? value) => value?.ToString("o", CultureInfo.InvariantCulture) ?? "";

    private static DateTimeOffset? TryGetStartTime(Process process)
    {
        try { return process.StartTime; }
        catch { return null; }
    }

    private static string? TryGetExecutablePath(Process process)
    {
        try { return process.MainModule?.FileName; }
        catch { return null; }
    }

    private static int TryGetParentProcessId(int processId)
    {
        if (OperatingSystem.IsWindows())
        {
            return TryGetWindowsParentProcessId(processId);
        }

        if (OperatingSystem.IsLinux())
        {
            return TryGetLinuxParentProcessId(processId);
        }

        return 0;
    }

    private static int TryGetLinuxParentProcessId(int processId)
    {
        try
        {
            var stat = File.ReadAllText($"/proc/{processId}/stat");
            var lastParen = stat.LastIndexOf(')');
            if (lastParen < 0)
            {
                return 0;
            }

            var fields = stat[(lastParen + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return fields.Length >= 2 && int.TryParse(fields[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parentId)
                ? parentId
                : 0;
        }
        catch
        {
            return 0;
        }
    }

    private static int TryGetWindowsParentProcessId(int processId)
    {
        var snapshot = CreateToolhelp32Snapshot(0x00000002, 0);
        if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1))
        {
            return 0;
        }

        try
        {
            var entry = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>() };
            if (!Process32First(snapshot, ref entry))
            {
                return 0;
            }

            do
            {
                if (entry.th32ProcessID == (uint)processId)
                {
                    return (int)entry.th32ParentProcessID;
                }
            }
            while (Process32Next(snapshot, ref entry));
        }
        finally
        {
            CloseHandle(snapshot);
        }

        return 0;
    }

    private sealed record ProcessSnapshot(
        int ProcessId,
        int ParentProcessId,
        string Name,
        string? ExecutablePath,
        DateTimeOffset? StartedAt,
        string? CommandLine);

    private sealed record RepoProcessOptions(
        IReadOnlyList<int> Ids,
        IReadOnlyList<int> ParentIds,
        IReadOnlyList<string> Names,
        IReadOnlyList<string> CommandContains,
        int Newest,
        bool IncludeChildren,
        bool LocksOnly)
    {
        public bool HasQuery => Ids.Count > 0 || ParentIds.Count > 0 || Names.Count > 0 || CommandContains.Count > 0 || LocksOnly;
        public bool ShouldPrintEmptyMessage => Ids.Count == 0 && ParentIds.Count == 0;

        public static RepoProcessOptions Parse(IReadOnlyList<string> parts)
        {
            var ids = new List<int>();
            var parentIds = new List<int>();
            var names = new List<string>();
            var commandContains = new List<string>();
            var newest = 25;
            var includeChildren = false;
            var locksOnly = false;

            for (var i = 1; i < parts.Count; i++)
            {
                switch (parts[i].ToLowerInvariant())
                {
                    case "--id":
                        ids.Add(ParseIntValue(parts, ref i, "--id"));
                        break;
                    case "--parent-id":
                        parentIds.Add(ParseIntValue(parts, ref i, "--parent-id"));
                        break;
                    case "--name":
                        names.Add(ParseStringValue(parts, ref i, "--name"));
                        break;
                    case "--command-contains":
                        commandContains.Add(ParseStringValue(parts, ref i, "--command-contains"));
                        break;
                    case "--newest":
                        newest = ParseIntValue(parts, ref i, "--newest");
                        if (newest < 1)
                        {
                            throw new ArgumentException("--newest must be at least 1.");
                        }
                        break;
                    case "--conduct-loop":
                        commandContains.Add("conduct");
                        commandContains.Add("--loop");
                        break;
                    case "--dispatch-host":
                        commandContains.Add("__dispatch-run");
                        break;
                    case "--include-children":
                        includeChildren = true;
                        break;
                    case "--locks":
                        locksOnly = true;
                        break;
                    default:
                        throw new ArgumentException($"Unknown option '{parts[i]}'.");
                }
            }

            return new RepoProcessOptions(ids, parentIds, names, commandContains, newest, includeChildren, locksOnly);
        }
    }

    private sealed record RepoProcessStopOptions(IReadOnlyList<int> Ids, IReadOnlyList<string> CommandContains)
    {
        public static RepoProcessStopOptions Parse(IReadOnlyList<string> parts)
        {
            var ids = new List<int>();
            var commandContains = new List<string>();
            for (var i = 1; i < parts.Count; i++)
            {
                switch (parts[i].ToLowerInvariant())
                {
                    case "--id":
                        ids.Add(ParseIntValue(parts, ref i, "--id"));
                        break;
                    case "--command-contains":
                        commandContains.Add(ParseStringValue(parts, ref i, "--command-contains"));
                        break;
                    case "--force":
                        break;
                    default:
                        throw new ArgumentException($"Unknown option '{parts[i]}'.");
                }
            }

            if (ids.Count == 0)
            {
                throw new ArgumentException("Usage: repo-process-stop --id <pid> [--command-contains <text>] [--force]");
            }

            return new RepoProcessStopOptions(ids, commandContains);
        }
    }

    private static int ParseIntValue(IReadOnlyList<string> parts, ref int index, string flag)
    {
        var value = ParseStringValue(parts, ref index, flag);
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) || parsed <= 0)
        {
            throw new ArgumentException($"{flag} must be a positive integer.");
        }

        return parsed;
    }

    private static string ParseStringValue(IReadOnlyList<string> parts, ref int index, string flag)
    {
        if (index + 1 >= parts.Count || parts[index + 1].StartsWith("--", StringComparison.Ordinal))
        {
            throw new ArgumentException($"{flag} requires a value.");
        }

        index++;
        return parts[index];
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool Process32First(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool Process32Next(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESSENTRY32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }
}
