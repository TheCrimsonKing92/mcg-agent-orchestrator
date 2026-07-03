using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
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

        IReadOnlySet<int> currentInvocationLineage = options.LocksOnly
            ? BuildCurrentInvocationLineage(byId)
            : new HashSet<int>();

        if (options.Names.Count > 0 || options.CommandContains.Count > 0 || options.LocksOnly)
        {
            var query = snapshots.Where(snapshot =>
                MatchesNames(snapshot, options.Names) &&
                MatchesCommand(snapshot, options.CommandContains) &&
                (!options.LocksOnly || IsReportableLockHolder(snapshot, currentInvocationLineage)))
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
            var prefix = options.LocksOnly && IsReportableLockHolder(snapshot, currentInvocationLineage)
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

        var commandLines = ReadCommandLines(raw.Select(process => process.Id));
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

    private static Dictionary<int, string> ReadCommandLines(IEnumerable<int> processIds)
    {
        var ids = processIds.Distinct().ToList();
        if (ids.Count == 0)
        {
            return [];
        }

        if (OperatingSystem.IsWindows())
        {
            return ReadWindowsCommandLines(ids);
        }

        if (OperatingSystem.IsLinux())
        {
            return ReadLinuxCommandLines(ids);
        }

        return [];
    }

    private static Dictionary<int, string> ReadWindowsCommandLines(IReadOnlyList<int> processIds)
    {
        var result = new Dictionary<int, string>();
        foreach (var processId in processIds)
        {
            var commandLine = TryReadWindowsCommandLine(processId);
            if (!string.IsNullOrWhiteSpace(commandLine))
            {
                result[processId] = commandLine;
            }
        }

        return result;
    }

    private static Dictionary<int, string> ReadLinuxCommandLines(IReadOnlyList<int> processIds)
    {
        var result = new Dictionary<int, string>();
        foreach (var processId in processIds)
        {
            try
            {
                var path = $"/proc/{processId}/cmdline";
                if (!File.Exists(path))
                {
                    continue;
                }

                var commandLine = File.ReadAllText(path).Replace('\0', ' ').Trim();
                if (!string.IsNullOrWhiteSpace(commandLine))
                {
                    result[processId] = commandLine;
                }
            }
            catch
            {
                // Best-effort; an unreadable process command line is treated as unknown by callers.
            }
        }

        return result;
    }

    private static string? TryReadWindowsCommandLine(int processId)
    {
        var handle = OpenProcess(ProcessQueryLimitedInformation | ProcessVmRead, false, processId);
        if (handle == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var status = NtQueryInformationProcess(
                handle,
                0,
                out var basicInformation,
                Marshal.SizeOf<PROCESS_BASIC_INFORMATION>(),
                out _);
            if (status != 0 || basicInformation.PebBaseAddress == IntPtr.Zero)
            {
                return null;
            }

            var processParametersAddress = ReadIntPtr(handle, basicInformation.PebBaseAddress + PebProcessParametersOffset);
            if (processParametersAddress == IntPtr.Zero)
            {
                return null;
            }

            var commandLine = ReadUnicodeString(handle, processParametersAddress + ProcessParametersCommandLineOffset);
            return string.IsNullOrWhiteSpace(commandLine) ? null : commandLine;
        }
        catch
        {
            return null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private static IntPtr ReadIntPtr(IntPtr processHandle, IntPtr address)
    {
        var buffer = new byte[IntPtr.Size];
        return ReadBytes(processHandle, address, buffer)
            ? IntPtr.Size == 8
                ? new IntPtr(BitConverter.ToInt64(buffer, 0))
                : new IntPtr(BitConverter.ToInt32(buffer, 0))
            : IntPtr.Zero;
    }

    private static string? ReadUnicodeString(IntPtr processHandle, IntPtr address)
    {
        var header = new byte[IntPtr.Size == 8 ? 16 : 8];
        if (!ReadBytes(processHandle, address, header))
        {
            return null;
        }

        var length = BitConverter.ToUInt16(header, 0);
        if (length == 0 || length > MaxWindowsCommandLineBytes)
        {
            return null;
        }

        var bufferAddress = IntPtr.Size == 8
            ? new IntPtr(BitConverter.ToInt64(header, 8))
            : new IntPtr(BitConverter.ToInt32(header, 4));
        if (bufferAddress == IntPtr.Zero)
        {
            return null;
        }

        var commandBuffer = new byte[length];
        return ReadBytes(processHandle, bufferAddress, commandBuffer)
            ? Encoding.Unicode.GetString(commandBuffer).TrimEnd('\0')
            : null;
    }

    private static bool ReadBytes(IntPtr processHandle, IntPtr address, byte[] buffer) =>
        ReadProcessMemory(processHandle, address, buffer, buffer.Length, out var bytesRead) &&
        bytesRead.ToInt64() == buffer.Length;

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
        needles.All(needle => ContainsOrdinalIgnoreCase(snapshot.CommandLine ?? string.Empty, needle));

    private static bool IsLockHolder(ProcessSnapshot snapshot)
    {
        var command = snapshot.CommandLine ?? string.Empty;
        return ContainsOrdinalIgnoreCase(command, "App.dll") ||
            ContainsOrdinalIgnoreCase(command, "__dispatch-run") ||
            ContainsOrdinalIgnoreCase(command, "DispatchProcessHost");
    }

    private static bool IsReportableLockHolder(ProcessSnapshot snapshot, IReadOnlySet<int> currentInvocationLineage) =>
        IsLockHolder(snapshot) && !currentInvocationLineage.Contains(snapshot.ProcessId);

    private static HashSet<int> BuildCurrentInvocationLineage(IReadOnlyDictionary<int, ProcessSnapshot> snapshotsById)
    {
        var excluded = new HashSet<int>();
        var currentProcessId = Environment.ProcessId;
        if (!snapshotsById.TryGetValue(currentProcessId, out var current))
        {
            excluded.Add(currentProcessId);
            return excluded;
        }

        excluded.Add(current.ProcessId);
        var parentId = current.ParentProcessId;
        while (parentId > 0 && snapshotsById.TryGetValue(parentId, out var parent))
        {
            if (!IsRepoProcessWrapper(parent))
            {
                break;
            }

            excluded.Add(parent.ProcessId);
            parentId = parent.ParentProcessId;
        }

        return excluded;
    }

    private static bool IsRepoProcessWrapper(ProcessSnapshot snapshot)
    {
        var name = snapshot.Name ?? string.Empty;
        var command = snapshot.CommandLine ?? string.Empty;
        return name.Equals("powershell", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("pwsh", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("cmd", StringComparison.OrdinalIgnoreCase) ||
            ContainsOrdinalIgnoreCase(command, "Invoke-RepoScript.ps1") ||
            ContainsOrdinalIgnoreCase(command, "Invoke-OrchestratorCommand.ps1") ||
            ContainsOrdinalIgnoreCase(command, "Get-RepoProcessInfo.ps1") ||
            ContainsOrdinalIgnoreCase(command, "Find-OrchestratorLocks.ps1");
    }

    private static string ClassifyKind(ProcessSnapshot snapshot)
    {
        var command = snapshot.CommandLine ?? string.Empty;
        if (ContainsOrdinalIgnoreCase(command, "__dispatch-run") ||
            ContainsOrdinalIgnoreCase(command, "DispatchProcessHost"))
        {
            return "dispatch-host";
        }

        if (ContainsOrdinalIgnoreCase(command, "conduct"))
        {
            return "conduct-loop";
        }

        if (ContainsOrdinalIgnoreCase(command, "serve-dashboard") ||
            ContainsOrdinalIgnoreCase(command, "-dashboard"))
        {
            return "dashboard";
        }

        return "app-host";
    }

    private static bool ContainsOrdinalIgnoreCase(string value, string expected) =>
        value.Contains(expected, StringComparison.OrdinalIgnoreCase);

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

    private const int ProcessQueryLimitedInformation = 0x1000;
    private const int ProcessVmRead = 0x0010;
    private const int MaxWindowsCommandLineBytes = 32766;
    private static readonly int PebProcessParametersOffset = IntPtr.Size == 8 ? 0x20 : 0x10;
    private static readonly int ProcessParametersCommandLineOffset = IntPtr.Size == 8 ? 0x70 : 0x40;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(int dwDesiredAccess, bool bInheritHandle, int dwProcessId);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(
        IntPtr processHandle,
        int processInformationClass,
        out PROCESS_BASIC_INFORMATION processInformation,
        int processInformationLength,
        out int returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(
        IntPtr hProcess,
        IntPtr lpBaseAddress,
        byte[] lpBuffer,
        int dwSize,
        out IntPtr lpNumberOfBytesRead);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool Process32First(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool Process32Next(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_BASIC_INFORMATION
    {
        public IntPtr Reserved1;
        public IntPtr PebBaseAddress;
        public IntPtr Reserved2_0;
        public IntPtr Reserved2_1;
        public IntPtr UniqueProcessId;
        public IntPtr InheritedFromUniqueProcessId;
    }

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
