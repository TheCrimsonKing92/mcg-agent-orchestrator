using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed record BuildLockHolder(
    int? ProcessId,
    string? ProcessName,
    string? CommandLine,
    bool IsOrchestratorOwned,
    DateTimeOffset? ProcessStartTime = null);

public sealed record BuildLockAttribution(
    string Path,
    IReadOnlyList<BuildLockHolder> Holders,
    string Source,
    string? Phase = null,
    string? Operation = null,
    TimeSpan? ProbeElapsed = null);

public sealed class BuildLockBlockedException : IOException
{
    public BuildLockBlockedException(BuildLockAttribution attribution)
        : base($"Build artifact lock blocked progress at {attribution.Path}; holder={FormatHolder(attribution.Holders.FirstOrDefault())}.")
    {
        Attribution = attribution;
    }

    public BuildLockAttribution Attribution { get; }

    private static string FormatHolder(BuildLockHolder? holder)
    {
        if (holder is null)
        {
            return "unknown";
        }

        var pid = holder.ProcessId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown";
        var name = string.IsNullOrWhiteSpace(holder.ProcessName) ? "unknown" : holder.ProcessName;
        return $"pid={pid} name={name}";
    }
}

internal enum LockAttributionDiagnosticBranch
{
    InjectedAttribution,
    RestartManager,
    HandleProbe,
    ProcessSnapshot
}

internal enum LockAttributionDiagnosticClassification
{
    InjectedAttribution,
    InjectedEmpty,
    RestartManagerAttribution,
    HandleAttribution,
    HandleTimeoutUnknown,
    ProcessSnapshotAttribution,
    ProcessSnapshotEmpty
}

internal sealed record LockAttributionDiagnosticEvent(string Stage, string Outcome);

internal sealed record LockAttributionTestHookSnapshot(
    string? AttributeOverride,
    string? HandleExecutable,
    TimeSpan? HandleProbeTimeout,
    string? ConfigureHandleProbe,
    bool DisableRestartManager);

internal sealed record LockAttributionDiagnosticReceipt(
    int ProcessId,
    LockAttributionTestHookSnapshot Hooks,
    LockAttributionDiagnosticBranch Branch,
    string Source,
    int HolderCount,
    LockAttributionDiagnosticClassification Classification,
    IReadOnlyList<LockAttributionDiagnosticEvent> Events)
{
    internal string Format() =>
        $"LOCK_ATTRIBUTION_DIAGNOSTIC processId={ProcessId} " +
        $"attributeOverride={Quote(Hooks.AttributeOverride)} " +
        $"handleExecutable={Quote(Hooks.HandleExecutable)} " +
        $"handleProbeTimeout={Quote(Hooks.HandleProbeTimeout?.ToString("c", System.Globalization.CultureInfo.InvariantCulture))} " +
        $"configureHandleProbe={Quote(Hooks.ConfigureHandleProbe)} " +
        $"disableRestartManager={Hooks.DisableRestartManager.ToString().ToLowerInvariant()} " +
        $"branch={Branch} source={Quote(Source)} holderCount={HolderCount} classification={Classification} " +
        $"events={Quote(string.Join('|', Events.Select(item => $"{item.Stage}:{item.Outcome}")))}";

    private static string Quote(string? value) =>
        $"\"{(value ?? "null").Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal).Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal)}\"";
}

internal sealed class LockAttributionDiagnosticCollector
{
    private readonly object _gate = new();
    private readonly List<LockAttributionDiagnosticEvent> _events = [];
    private LockAttributionTestHookSnapshot? _hooks;
    private LockAttributionDiagnosticReceipt? _receipt;

    internal LockAttributionDiagnosticReceipt Receipt
    {
        get
        {
            lock (_gate)
            {
                return _receipt ?? throw new InvalidOperationException("The LockAttribution diagnostic invocation has not completed.");
            }
        }
    }

    internal void Start(LockAttributionTestHookSnapshot hooks)
    {
        lock (_gate)
        {
            if (_hooks is not null || _receipt is not null)
            {
                throw new InvalidOperationException("A LockAttribution diagnostic collector can own only one invocation.");
            }

            _hooks = hooks;
            _events.Add(new LockAttributionDiagnosticEvent("invocation", "started"));
        }
    }

    internal void Record(string stage, string outcome)
    {
        lock (_gate)
        {
            if (_hooks is null || _receipt is not null)
            {
                throw new InvalidOperationException("LockAttribution diagnostic events require one active invocation.");
            }

            _events.Add(new LockAttributionDiagnosticEvent(stage, outcome));
        }
    }

    internal void Complete(
        LockAttributionDiagnosticBranch branch,
        BuildLockAttribution attribution,
        LockAttributionDiagnosticClassification classification)
    {
        lock (_gate)
        {
            if (_hooks is null || _receipt is not null)
            {
                throw new InvalidOperationException("LockAttribution diagnostics must complete exactly once.");
            }

            _events.Add(new LockAttributionDiagnosticEvent("attribution", "completed"));
            _receipt = new LockAttributionDiagnosticReceipt(
                Environment.ProcessId,
                _hooks,
                branch,
                attribution.Source,
                attribution.Holders.Count,
                classification,
                _events.ToArray());
        }
    }
}

internal static partial class LockAttribution
{
    private static readonly Regex HandlePidPattern = new(@"\bpid:\s*(?<pid>\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex HandleNamePattern = new(@"^(?<name>[^:\s]+)\s+pid:\s*\d+", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly TimeSpan HandleProbeTimeout = TimeSpan.FromSeconds(10);

    internal static Func<string, string?, BuildLockAttribution?>? AttributeForTests { get; set; }
    internal static string? HandleExecutableForTests { get; set; }
    internal static TimeSpan? HandleProbeTimeoutForTests { get; set; }
    internal static Action<ProcessStartInfo, string>? ConfigureHandleProbeForTests { get; set; }
    internal static bool DisableRestartManagerForTests { get; set; }
    internal static Func<ProcessCommandLineSnapshot>? ProcessCommandLineSnapshotForTests { get; set; }

    public static BuildLockAttribution Attribute(string path, string? ownershipHint = null, string? phase = null, string? operation = null) =>
        AttributeCore(path, ownershipHint, phase, operation, diagnostics: null);

    internal static BuildLockAttribution AttributeWithDiagnosticsForTests(
        string path,
        string? ownershipHint,
        string? phase,
        string? operation,
        LockAttributionDiagnosticCollector diagnostics) =>
        AttributeCore(path, ownershipHint, phase, operation, diagnostics);

    private static BuildLockAttribution AttributeCore(
        string path,
        string? ownershipHint,
        string? phase,
        string? operation,
        LockAttributionDiagnosticCollector? diagnostics)
    {
        var testHooks = TestHookState.Capture();
        diagnostics?.Start(testHooks.ToDiagnosticSnapshot());

        if (testHooks.Attribute is not null)
        {
            var testAttribution = testHooks.Attribute(path, ownershipHint);
            diagnostics?.Record("attribute-override", testAttribution is null ? "returned-null" : "returned-attribution");
            if (testAttribution is not null)
            {
                return CompleteAttribution(
                    testAttribution,
                    phase,
                    operation,
                    LockAttributionDiagnosticBranch.InjectedAttribution,
                    diagnostics);
            }
        }
        else
        {
            diagnostics?.Record("attribute-override", "not-installed");
        }

        var fullPath = TryFullPath(path);
        if (OperatingSystem.IsWindows() && !testHooks.DisableRestartManager)
        {
            var restartManagerAttribution = TryAttributeWithRestartManager(fullPath, ownershipHint);
            diagnostics?.Record("restart-manager", restartManagerAttribution is null ? "no-attribution" : "returned-attribution");
            if (restartManagerAttribution is not null)
            {
                return CompleteAttribution(
                    restartManagerAttribution,
                    phase,
                    operation,
                    LockAttributionDiagnosticBranch.RestartManager,
                    diagnostics);
            }
        }
        else
        {
            diagnostics?.Record("restart-manager", OperatingSystem.IsWindows() ? "disabled-for-tests" : "non-windows");
        }

        if (OperatingSystem.IsWindows())
        {
            var handleAttribution = TryAttributeWithHandle(fullPath, ownershipHint, testHooks, diagnostics);
            if (handleAttribution is not null)
            {
                return CompleteAttribution(
                    handleAttribution,
                    phase,
                    operation,
                    LockAttributionDiagnosticBranch.HandleProbe,
                    diagnostics);
            }
        }
        else
        {
            diagnostics?.Record("handle-probe", "non-windows");
        }

        return CompleteAttribution(
            AttributeFromProcessSnapshot(fullPath, ownershipHint),
            phase,
            operation,
            LockAttributionDiagnosticBranch.ProcessSnapshot,
            diagnostics);
    }

    public static string? TryExtractLockedPath(string output)
    {
        var quoted = Regex.Match(output, @"'(?<path>[A-Za-z]:\\[^']+)'");
        if (quoted.Success)
        {
            return IsLeaseLockPath(quoted.Groups["path"].Value) ? null : quoted.Groups["path"].Value;
        }

        var bare = Regex.Match(output, @"(?<path>[A-Za-z]:\\[^\r\n:]+?\.(?:dll|exe|pdb|json|trx|cache|lock))", RegexOptions.IgnoreCase);
        if (!bare.Success)
        {
            return null;
        }

        var path = bare.Groups["path"].Value.Trim();
        return IsLeaseLockPath(path) ? null : path;
    }

    public static bool IsLeaseLockPath(string path) =>
        Path.GetFileName(path).Equals("lease.execution.lock", StringComparison.OrdinalIgnoreCase);

    public static void EmitReceipt(BuildLockAttribution attribution)
    {
        if (attribution.Holders.Count == 0)
        {
            Console.WriteLine(
                $"LOCK path=\"{attribution.Path}\" holderPid=unknown holderName=unknown source={attribution.Source} " +
                $"phase=\"{Escape(attribution.Phase ?? "unknown")}\" operation=\"{Escape(attribution.Operation ?? "unknown")}\"");
            return;
        }

        foreach (var holder in attribution.Holders)
        {
            Console.WriteLine(
                $"LOCK path=\"{attribution.Path}\" holderPid={holder.ProcessId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown"} " +
                $"holderName=\"{Escape(holder.ProcessName ?? "unknown")}\" owned={holder.IsOrchestratorOwned.ToString().ToLowerInvariant()} " +
                $"source={attribution.Source} phase=\"{Escape(attribution.Phase ?? "unknown")}\" operation=\"{Escape(attribution.Operation ?? "unknown")}\" " +
                $"holderStartTime=\"{Escape(holder.ProcessStartTime?.ToString("O", System.Globalization.CultureInfo.InvariantCulture) ?? "unknown")}\" " +
                $"commandLine=\"{Escape(holder.CommandLine ?? string.Empty)}\"");
        }
    }

    private static BuildLockAttribution Enrich(BuildLockAttribution attribution, string? phase, string? operation) =>
        attribution with
        {
            Phase = string.IsNullOrWhiteSpace(attribution.Phase) ? phase : attribution.Phase,
            Operation = string.IsNullOrWhiteSpace(attribution.Operation) ? operation : attribution.Operation
        };

    private static BuildLockAttribution CompleteAttribution(
        BuildLockAttribution attribution,
        string? phase,
        string? operation,
        LockAttributionDiagnosticBranch branch,
        LockAttributionDiagnosticCollector? diagnostics)
    {
        var enriched = Enrich(attribution, phase, operation);
        EmitReceipt(enriched);
        diagnostics?.Complete(branch, enriched, Classify(branch, enriched));
        return enriched;
    }

    private static LockAttributionDiagnosticClassification Classify(
        LockAttributionDiagnosticBranch branch,
        BuildLockAttribution attribution) =>
        branch switch
        {
            LockAttributionDiagnosticBranch.InjectedAttribution when attribution.Holders.Count == 0 =>
                LockAttributionDiagnosticClassification.InjectedEmpty,
            LockAttributionDiagnosticBranch.InjectedAttribution =>
                LockAttributionDiagnosticClassification.InjectedAttribution,
            LockAttributionDiagnosticBranch.RestartManager =>
                LockAttributionDiagnosticClassification.RestartManagerAttribution,
            LockAttributionDiagnosticBranch.HandleProbe when
                string.Equals(attribution.Source, "handle64-timeout", StringComparison.Ordinal) =>
                LockAttributionDiagnosticClassification.HandleTimeoutUnknown,
            LockAttributionDiagnosticBranch.HandleProbe =>
                LockAttributionDiagnosticClassification.HandleAttribution,
            LockAttributionDiagnosticBranch.ProcessSnapshot when attribution.Holders.Count == 0 =>
                LockAttributionDiagnosticClassification.ProcessSnapshotEmpty,
            _ => LockAttributionDiagnosticClassification.ProcessSnapshotAttribution
        };

    private static BuildLockAttribution? TryAttributeWithRestartManager(string path, string? ownershipHint)
    {
        uint session = 0;
        var sessionKey = Guid.NewGuid().ToString("N");
        var result = RmStartSession(out session, 0, sessionKey);
        if (result != 0)
        {
            return null;
        }

        try
        {
            string[] resources = [path];
            result = RmRegisterResources(session, (uint)resources.Length, resources, 0, null, 0, null);
            if (result != 0)
            {
                return null;
            }

            uint needed = 0;
            uint count = 0;
            uint rebootReasons;
            result = RmGetList(session, out needed, ref count, null, out rebootReasons);
            if (result != ErrorMoreData || needed == 0)
            {
                return null;
            }

            var processInfo = new RmProcessInfo[needed];
            count = needed;
            result = RmGetList(session, out needed, ref count, processInfo, out rebootReasons);
            if (result != 0)
            {
                return null;
            }

            var pids = processInfo
                .Take((int)count)
                .Select(info => TryConvertProcessId(info.Process.ProcessId))
                .Where(pid => pid.HasValue)
                .Select(pid => pid!.Value)
                .Distinct()
                .Take(16)
                .ToArray();
            var commandLines = ProcessCommandLines.Read(pids);
            var holders = processInfo
                .Take((int)count)
                .Select(info =>
                {
                    var pid = TryConvertProcessId(info.Process.ProcessId);
                    if (!pid.HasValue)
                    {
                        return null;
                    }

                    commandLines.TryGetValue(pid.Value, out var commandLine);
                    var startTime = FileTimeToDateTimeOffset(info.Process.ProcessStartTime);
                    return new BuildLockHolder(
                        pid.Value,
                        string.IsNullOrWhiteSpace(info.ApplicationName) ? TryProcessName(pid.Value) : info.ApplicationName,
                        commandLine,
                        IsOrchestratorOwned(commandLine, ownershipHint),
                        startTime);
                })
                .Where(holder => holder is not null)
                .Select(holder => holder!)
                .GroupBy(holder => holder.ProcessId)
                .Select(group => group.First())
                .ToArray();
            return holders.Length == 0 ? null : new BuildLockAttribution(path, holders, "restart-manager");
        }
        catch
        {
            return null;
        }
        finally
        {
            _ = RmEndSession(session);
        }
    }

    private static BuildLockAttribution? TryAttributeWithHandle(
        string path,
        string? ownershipHint,
        TestHookState testHooks,
        LockAttributionDiagnosticCollector? diagnostics)
    {
        var handle = ResolveHandleExecutable(testHooks.HandleExecutable, diagnostics);
        if (handle is null)
        {
            diagnostics?.Record("handle-probe", "executable-not-found");
            return null;
        }

        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = handle,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            process.StartInfo.ArgumentList.Add("-accepteula");
            process.StartInfo.ArgumentList.Add("-nobanner");
            process.StartInfo.ArgumentList.Add(path);
            testHooks.ConfigureHandleProbe?.Invoke(process.StartInfo, path);
            diagnostics?.Record("handle-configure", testHooks.ConfigureHandleProbe is null ? "not-installed" : "invoked");
            bool started;
            try
            {
                started = process.Start();
            }
            catch (Exception exception)
            {
                diagnostics?.Record("handle-process-start", $"failed-{exception.GetType().Name}");
                return null;
            }

            if (!started)
            {
                diagnostics?.Record("handle-process-start", "returned-false");
                return null;
            }

            diagnostics?.Record("handle-process-start", "started");
            try { process.StandardInput.Close(); } catch { }
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(testHooks.HandleProbeTimeout ?? HandleProbeTimeout))
            {
                diagnostics?.Record("handle-wait", "timed-out");
                try
                {
                    process.Kill(entireProcessTree: true);
                    diagnostics?.Record("handle-kill", "requested");
                }
                catch (Exception exception)
                {
                    diagnostics?.Record("handle-kill", $"failed-{exception.GetType().Name}");
                }

                try
                {
                    diagnostics?.Record("handle-reap", process.WaitForExit(1000) ? "exited" : "still-running");
                }
                catch (Exception exception)
                {
                    diagnostics?.Record("handle-reap", $"failed-{exception.GetType().Name}");
                }

                _ = Task.WhenAny(outputTask, Task.Delay(TimeSpan.FromSeconds(1)));
                _ = Task.WhenAny(errorTask, Task.Delay(TimeSpan.FromSeconds(1)));
                return new BuildLockAttribution(
                    path,
                    [new BuildLockHolder(null, "unknown-probe-timeout", null, false)],
                    "handle64-timeout");
            }

            diagnostics?.Record("handle-wait", $"exited-{process.ExitCode}");
            var output = outputTask.GetAwaiter().GetResult() + Environment.NewLine + errorTask.GetAwaiter().GetResult();
            var holders = ParseHandleOutput(output, ownershipHint);
            diagnostics?.Record("handle-parse", holders.Count == 0 ? "empty" : $"holders-{holders.Count}");
            return holders.Count == 0 ? null : new BuildLockAttribution(path, holders, "handle64");
        }
        catch (Exception exception)
        {
            diagnostics?.Record("handle-probe", $"failed-{exception.GetType().Name}");
            return null;
        }
    }

    private static BuildLockAttribution AttributeFromProcessSnapshot(string path, string? ownershipHint)
    {
        var snapshot = ProcessCommandLineSnapshotForTests?.Invoke() ?? ProcessCommandLines.Snapshot();
        var holders = snapshot.Read(snapshot.Records.Keys)
            .Where(pair => IsOrchestratorOwned(pair.Value, ownershipHint))
            .Select(pair => new BuildLockHolder(pair.Key, TryProcessName(pair.Key), pair.Value, true, TryProcessStartTime(pair.Key)))
            .Take(8)
            .ToArray();
        return new BuildLockAttribution(path, holders, "process-snapshot");
    }

    private static List<BuildLockHolder> ParseHandleOutput(string output, string? ownershipHint)
    {
        var parsed = output
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => (Line: line, Match: HandlePidPattern.Match(line)))
            .Where(item => item.Match.Success && int.TryParse(
                item.Match.Groups["pid"].Value,
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture,
                out _))
            .Select(item => (
                item.Line,
                Pid: int.Parse(item.Match.Groups["pid"].Value, System.Globalization.CultureInfo.InvariantCulture)))
            .ToArray();
        var commandLines = ProcessCommandLines.Read(parsed.Select(item => item.Pid));
        var holders = new List<BuildLockHolder>();
        foreach (var item in parsed)
        {
            commandLines.TryGetValue(item.Pid, out var commandLine);
            var nameMatch = HandleNamePattern.Match(item.Line);
            var processName = nameMatch.Success ? nameMatch.Groups["name"].Value : TryProcessName(item.Pid);
            holders.Add(new BuildLockHolder(item.Pid, processName, commandLine, IsOrchestratorOwned(commandLine, ownershipHint), TryProcessStartTime(item.Pid)));
        }

        return holders
            .GroupBy(holder => holder.ProcessId)
            .Select(group => group.First())
            .ToList();
    }

    private static bool IsOrchestratorOwned(string? commandLine, string? ownershipHint)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return false;
        }

        return (!string.IsNullOrWhiteSpace(ownershipHint) &&
                commandLine.Contains(ownershipHint, StringComparison.OrdinalIgnoreCase)) ||
            commandLine.Contains(".orchestrator-worktrees", StringComparison.OrdinalIgnoreCase) ||
            commandLine.Contains("mcg-dotnet-isolated", StringComparison.OrdinalIgnoreCase) ||
            commandLine.Contains("MCG_ORCHESTRATOR_REPOSITORY_ROOT", StringComparison.OrdinalIgnoreCase);
    }

    private static string? ResolveHandleExecutable(
        string? handleExecutableForTests,
        LockAttributionDiagnosticCollector? diagnostics)
    {
        if (!string.IsNullOrWhiteSpace(handleExecutableForTests))
        {
            diagnostics?.Record("handle-resolve", "test-override");
            return handleExecutableForTests;
        }

        var explicitPath = Environment.GetEnvironmentVariable("MCG_HANDLE64");
        if (!string.IsNullOrWhiteSpace(explicitPath) && File.Exists(explicitPath))
        {
            diagnostics?.Record("handle-resolve", "environment");
            return explicitPath;
        }

        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator))
        {
            foreach (var name in new[] { "handle64.exe", "handle.exe" })
            {
                var candidate = Path.Combine(directory.Trim(), name);
                if (File.Exists(candidate))
                {
                    diagnostics?.Record("handle-resolve", "path");
                    return candidate;
                }
            }
        }

        foreach (var directory in CommonHandleDirectories())
        {
            foreach (var name in new[] { "handle64.exe", "handle.exe" })
            {
                var candidate = Path.Combine(directory, name);
                if (File.Exists(candidate))
                {
                    diagnostics?.Record("handle-resolve", "common-directory");
                    return candidate;
                }
            }
        }

        return null;
    }

    private sealed record TestHookState(
        Func<string, string?, BuildLockAttribution?>? Attribute,
        string? HandleExecutable,
        TimeSpan? HandleProbeTimeout,
        Action<ProcessStartInfo, string>? ConfigureHandleProbe,
        bool DisableRestartManager)
    {
        internal static TestHookState Capture() => new(
            AttributeForTests,
            HandleExecutableForTests,
            HandleProbeTimeoutForTests,
            ConfigureHandleProbeForTests,
            DisableRestartManagerForTests);

        internal LockAttributionTestHookSnapshot ToDiagnosticSnapshot() => new(
            Describe(Attribute),
            HandleExecutable,
            HandleProbeTimeout,
            Describe(ConfigureHandleProbe),
            DisableRestartManager);

        private static string? Describe(Delegate? value)
        {
            if (value is null)
            {
                return null;
            }

            var declaringType = value.Method.DeclaringType?.FullName ?? "unknown";
            return $"{declaringType}::{value.Method.Name}";
        }
    }

    private static IEnumerable<string> CommonHandleDirectories()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var temp = Path.GetTempPath();
        string[] directories =
        [
            Path.Combine(userProfile, "Downloads"),
            Path.Combine(userProfile, "Downloads", "Handle"),
            Path.Combine(userProfile, "Downloads", "SysinternalsSuite"),
            Path.Combine(userProfile, "Desktop"),
            temp,
            @"C:\Sysinternals",
            @"C:\SysinternalsSuite",
            @"C:\Tools",
            @"C:\Tools\Sysinternals",
            Path.Combine(Environment.GetEnvironmentVariable("ChocolateyInstall") ?? string.Empty, "bin"),
            @"C:\ProgramData\chocolatey\bin",
            Path.Combine(userProfile, "scoop", "shims")
        ];
        return directories.Where(directory => !string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory));
    }

    private static string TryFullPath(string path)
    {
        try { return Path.GetFullPath(path); }
        catch { return path; }
    }

    private static string? TryProcessName(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.ProcessName;
        }
        catch
        {
            return null;
        }
    }

    private static DateTimeOffset? TryProcessStartTime(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
        }
        catch
        {
            return null;
        }
    }

    private static int? TryConvertProcessId(uint processId) =>
        processId is > 0 and <= int.MaxValue ? (int)processId : null;

    private static DateTimeOffset? FileTimeToDateTimeOffset(RmFileTime fileTime)
    {
        try
        {
            return new DateTimeOffset(DateTime.FromFileTimeUtc(fileTime.ToInt64()));
        }
        catch
        {
            return null;
        }
    }

    private static string Escape(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);

    private const int ErrorMoreData = 234;
    private const int CchRmMaxAppName = 255;
    private const int CchRmMaxSvcName = 63;

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmStartSession(out uint sessionHandle, int sessionFlags, string sessionKey);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmRegisterResources(
        uint sessionHandle,
        uint fileCount,
        string[]? fileNames,
        uint applicationCount,
        RmUniqueProcess[]? applications,
        uint serviceCount,
        string[]? serviceNames);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmGetList(
        uint sessionHandle,
        out uint processInfoNeeded,
        ref uint processInfo,
        [In, Out] RmProcessInfo[]? affectedApps,
        out uint rebootReasons);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmEndSession(uint sessionHandle);

    [StructLayout(LayoutKind.Sequential)]
    private struct RmUniqueProcess
    {
        public uint ProcessId;
        public RmFileTime ProcessStartTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RmFileTime
    {
        public uint LowDateTime;
        public uint HighDateTime;

        public readonly long ToInt64() => ((long)HighDateTime << 32) | LowDateTime;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RmProcessInfo
    {
        public RmUniqueProcess Process;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CchRmMaxAppName + 1)]
        public string ApplicationName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CchRmMaxSvcName + 1)]
        public string ServiceShortName;
        public int ApplicationType;
        public uint AppStatus;
        public uint TssSessionId;
        [MarshalAs(UnmanagedType.Bool)]
        public bool Restartable;
    }
}
