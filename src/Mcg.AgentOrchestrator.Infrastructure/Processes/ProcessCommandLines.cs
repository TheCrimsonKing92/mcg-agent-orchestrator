namespace Mcg.AgentOrchestrator.Infrastructure;

/// <summary>
/// Cross-platform process inspection shared by process-ownership consumers. Windows uses native
/// Toolhelp/PEB inspection; Linux reads <c>/proc</c>. Unavailable records remain in snapshots so
/// callers can fail closed instead of confusing an unreadable process with an absent process.
/// </summary>
internal static class ProcessCommandLines
{
    public static ProcessCommandLineSnapshot Snapshot() => ToSnapshot(ReadAllRecords());

    public static ProcessCommandLineSnapshot Snapshot(IEnumerable<int> pids) =>
        ToSnapshot(ReadRecords(pids.Distinct().ToArray()));

    public static ProcessCommandLineSnapshot SnapshotByNames(IEnumerable<string> processNames)
    {
        var names = processNames
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => Path.GetFileNameWithoutExtension(name)!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (names.Count == 0)
        {
            return ProcessCommandLineSnapshot.Empty;
        }

        if (OperatingSystem.IsWindows())
        {
            return ToSnapshot(WindowsNativeProcessInspection.ReadByNames(names));
        }

        var records = ReadAllRecords().Records
            .Where(pair => names.Contains(pair.Value.Name))
            .ToDictionary(pair => pair.Key, pair => pair.Value);
        return new ProcessCommandLineSnapshot(records);
    }

    public static Dictionary<int, string> Read(IEnumerable<int> pids)
    {
        var pidList = pids.Distinct().ToList();
        if (pidList.Count == 0)
        {
            return [];
        }

        return ReadRecords(pidList).Records
            .Where(pair => pair.Value.Status == ProcessInspectionStatus.Available &&
                !string.IsNullOrWhiteSpace(pair.Value.CommandLine))
            .ToDictionary(pair => pair.Key, pair => pair.Value.CommandLine!);
    }

    private static WindowsNativeProcessInspection.ProcessInspectionResult ReadAllRecords()
    {
        if (OperatingSystem.IsWindows())
        {
            return WindowsNativeProcessInspection.Read();
        }

        if (OperatingSystem.IsLinux())
        {
            return WindowsNativeProcessInspection.ProcessInspectionResult.Success(
                ReadLinuxRecords(EnumerateLinuxProcessIds()));
        }

        return WindowsNativeProcessInspection.ProcessInspectionResult.Success(
            new Dictionary<int, ProcessInspectionRecord>());
    }

    private static WindowsNativeProcessInspection.ProcessInspectionResult ReadRecords(IReadOnlyCollection<int> pids)
    {
        if (OperatingSystem.IsWindows())
        {
            return WindowsNativeProcessInspection.Read(pids);
        }

        if (OperatingSystem.IsLinux())
        {
            return WindowsNativeProcessInspection.ProcessInspectionResult.Success(ReadLinuxRecords(pids));
        }

        return WindowsNativeProcessInspection.ProcessInspectionResult.Success(
            new Dictionary<int, ProcessInspectionRecord>());
    }

    private static ProcessCommandLineSnapshot ToSnapshot(
        WindowsNativeProcessInspection.ProcessInspectionResult result) =>
        new(result.Records, result.Failure);

    private static IReadOnlyDictionary<int, ProcessInspectionRecord> ReadLinuxRecords(IEnumerable<int> pids)
    {
        var result = new Dictionary<int, ProcessInspectionRecord>();
        foreach (var pid in pids.Distinct())
        {
            string? commandLine = null;
            var status = ProcessInspectionStatus.Available;
            try
            {
                var path = $"/proc/{pid}/cmdline";
                if (!File.Exists(path))
                {
                    status = ProcessInspectionStatus.Exited;
                }
                else
                {
                    commandLine = File.ReadAllText(path).Replace('\0', ' ').Trim();
                }
            }
            catch (UnauthorizedAccessException)
            {
                status = ProcessInspectionStatus.AccessDenied;
            }
            catch (IOException)
            {
                status = ProcessInspectionStatus.NativeFailure;
            }
            catch
            {
                status = ProcessInspectionStatus.NativeFailure;
            }

            string name;
            string? executablePath = null;
            DateTimeOffset? startedAt = null;
            try
            {
                using var process = System.Diagnostics.Process.GetProcessById(pid);
                name = process.ProcessName;
                try { executablePath = process.MainModule?.FileName; } catch { }
                try { startedAt = new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero); } catch { }
            }
            catch
            {
                name = string.Empty;
                status = ProcessInspectionStatus.Exited;
            }

            result[pid] = new ProcessInspectionRecord(
                pid,
                ProcessParentIdResolver.TryGetParentProcessId(pid) ?? 0,
                name,
                executablePath,
                startedAt,
                commandLine,
                status);
        }

        return result;
    }

    private static IReadOnlyList<int> EnumerateLinuxProcessIds()
    {
        try
        {
            return Directory
                .EnumerateDirectories("/proc")
                .Select(Path.GetFileName)
                .Where(name => int.TryParse(name, out _))
                .Select(int.Parse)
                .ToList();
        }
        catch
        {
            return [];
        }
    }
}

public sealed class ProcessCommandLineSnapshot
{
    private readonly IReadOnlyDictionary<int, ProcessInspectionRecord> _records;
    private readonly Action<int>? _onRead;

    internal ProcessCommandLineSnapshot(IReadOnlyDictionary<int, string> commandLines, Action<int>? onRead = null)
        : this(commandLines.ToDictionary(
            pair => pair.Key,
            pair => new ProcessInspectionRecord(
                pair.Key,
                0,
                string.Empty,
                null,
                null,
                pair.Value,
                ProcessInspectionStatus.Available)), onRead)
    {
    }

    internal ProcessCommandLineSnapshot(
        IReadOnlyDictionary<int, ProcessInspectionRecord> records,
        Action<int>? onRead = null)
        : this(records, failure: null, onRead)
    {
    }

    internal ProcessCommandLineSnapshot(
        IReadOnlyDictionary<int, ProcessInspectionRecord> records,
        ProcessInspectionFailure? failure,
        Action<int>? onRead = null)
    {
        _records = records;
        Failure = failure;
        _onRead = onRead;
    }

    public static ProcessCommandLineSnapshot Empty { get; } = new(new Dictionary<int, string>());

    public IReadOnlyDictionary<int, ProcessInspectionRecord> Records => _records;

    public ProcessInspectionFailure? Failure { get; }

    public bool TryGetRecord(int processId, out ProcessInspectionRecord record) =>
        _records.TryGetValue(processId, out record!);

    public IReadOnlyDictionary<int, string> Read(IEnumerable<int> pids)
    {
        var result = new Dictionary<int, string>();
        var distinctPids = pids.Distinct().ToArray();
        _onRead?.Invoke(distinctPids.Length);
        foreach (var pid in distinctPids)
        {
            if (_records.TryGetValue(pid, out var record) &&
                record.Status == ProcessInspectionStatus.Available &&
                !string.IsNullOrWhiteSpace(record.CommandLine))
            {
                result[pid] = record.CommandLine;
            }
        }

        return result;
    }
}
