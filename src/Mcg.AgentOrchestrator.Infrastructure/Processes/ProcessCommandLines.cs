namespace Mcg.AgentOrchestrator.Infrastructure;

/// <summary>
/// Cross-platform process inspection shared by process-ownership consumers. Windows uses native
/// Toolhelp/PEB inspection; Linux reads <c>/proc</c>. Unavailable records remain in snapshots so
/// callers can fail closed instead of confusing an unreadable process with an absent process.
/// </summary>
internal static class ProcessCommandLines
{
    public static ProcessCommandLineSnapshot Snapshot() => ToSnapshot(ReadAllRecords());

    public static ProcessCommandLineSnapshot SnapshotOperation()
    {
        if (OperatingSystem.IsWindows())
        {
            var operation = WindowsNativeProcessInspection.BeginOperation();
            return new ProcessCommandLineSnapshot(processIds => operation.ReadRequested(processIds));
        }

        return Snapshot();
    }

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

    public static ProcessCommandLineSnapshot Snapshot(ProcessInspectionQuery query)
    {
        if (OperatingSystem.IsWindows())
        {
            return ToSnapshot(WindowsNativeProcessInspection.BeginOperation().ReadCandidates(query));
        }

        // Keep the established /proc behavior intact. The caller applies the same final query
        // predicates after this snapshot; only Windows needs the native-read cost bound here.
        return Snapshot();
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
    private readonly Dictionary<int, ProcessInspectionRecord> _records;
    private readonly Action<int>? _onRead;
    private readonly Func<IReadOnlyCollection<int>, WindowsNativeProcessInspection.ProcessInspectionResult>? _readRecords;
    private ProcessInspectionFailure? _failure;
    private bool _hasLazyRead;

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
        _records = records.ToDictionary(pair => pair.Key, pair => pair.Value);
        _failure = failure;
        _onRead = onRead;
    }

    internal ProcessCommandLineSnapshot(
        Func<IReadOnlyCollection<int>, WindowsNativeProcessInspection.ProcessInspectionResult> readRecords)
    {
        _records = [];
        _readRecords = readRecords;
    }

    public static ProcessCommandLineSnapshot Empty { get; } = new(new Dictionary<int, string>());

    public IReadOnlyDictionary<int, ProcessInspectionRecord> Records => _readRecords is null
        ? _records
        : throw new InvalidOperationException(
            "A partial operation-backed process snapshot cannot be enumerated as a complete snapshot; request explicit process ids instead.");

    public ProcessInspectionFailure? Failure => _readRecords is not null && !_hasLazyRead
        ? throw new InvalidOperationException(
            "A partial operation-backed process snapshot has not inspected any requested process ids.")
        : _failure;

    public bool TryGetRecord(int processId, out ProcessInspectionRecord record)
    {
        EnsureRecords([processId]);
        return _records.TryGetValue(processId, out record!);
    }

    public IReadOnlyDictionary<int, string> Read(IEnumerable<int> pids)
    {
        var result = new Dictionary<int, string>();
        var distinctPids = pids.Distinct().ToArray();
        _onRead?.Invoke(distinctPids.Length);
        EnsureRecords(distinctPids);
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

    private void EnsureRecords(IReadOnlyCollection<int> processIds)
    {
        if (_readRecords is null)
        {
            return;
        }

        var missing = processIds.Where(processId => !_records.ContainsKey(processId)).Distinct().ToArray();
        if (missing.Length == 0)
        {
            return;
        }

        var inspected = _readRecords(missing);
        _hasLazyRead = true;
        _failure ??= inspected.Failure;
        foreach (var pair in inspected.Records)
        {
            _records[pair.Key] = pair.Value;
        }
    }
}
