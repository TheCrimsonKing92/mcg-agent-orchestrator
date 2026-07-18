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

    public static BuildLockAttribution Attribute(string path, string? ownershipHint = null, string? phase = null, string? operation = null)
    {
        if (AttributeForTests?.Invoke(path, ownershipHint) is { } testAttribution)
        {
            var enriched = Enrich(testAttribution, phase, operation);
            EmitReceipt(enriched);
            return enriched;
        }

        var fullPath = TryFullPath(path);
        if (OperatingSystem.IsWindows() &&
            !DisableRestartManagerForTests &&
            TryAttributeWithRestartManager(fullPath, ownershipHint) is { } restartManagerAttribution)
        {
            var enriched = Enrich(restartManagerAttribution, phase, operation);
            EmitReceipt(enriched);
            return enriched;
        }

        if (OperatingSystem.IsWindows() && TryAttributeWithHandle(fullPath, ownershipHint) is { } handleAttribution)
        {
            var enriched = Enrich(handleAttribution, phase, operation);
            EmitReceipt(enriched);
            return enriched;
        }

        var fallback = Enrich(AttributeFromProcessSnapshot(fullPath, ownershipHint), phase, operation);
        EmitReceipt(fallback);
        return fallback;
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

    private static BuildLockAttribution? TryAttributeWithHandle(string path, string? ownershipHint)
    {
        var handle = ResolveHandleExecutable();
        if (handle is null)
        {
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
            ConfigureHandleProbeForTests?.Invoke(process.StartInfo, path);
            if (!process.Start())
            {
                return null;
            }

            try { process.StandardInput.Close(); } catch { }
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(HandleProbeTimeoutForTests ?? HandleProbeTimeout))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                try { process.WaitForExit(1000); } catch { }
                _ = Task.WhenAny(outputTask, Task.Delay(TimeSpan.FromSeconds(1)));
                _ = Task.WhenAny(errorTask, Task.Delay(TimeSpan.FromSeconds(1)));
                return new BuildLockAttribution(
                    path,
                    [new BuildLockHolder(null, "unknown-probe-timeout", null, false)],
                    "handle64-timeout");
            }

            var output = outputTask.GetAwaiter().GetResult() + Environment.NewLine + errorTask.GetAwaiter().GetResult();
            var holders = ParseHandleOutput(output, ownershipHint);
            return holders.Count == 0 ? null : new BuildLockAttribution(path, holders, "handle64");
        }
        catch
        {
            return null;
        }
    }

    private static BuildLockAttribution AttributeFromProcessSnapshot(string path, string? ownershipHint)
    {
        var snapshot = ProcessCommandLines.Snapshot();
        var holders = snapshot.Read(Process.GetProcesses().Select(process => process.Id))
            .Where(pair => IsOrchestratorOwned(pair.Value, ownershipHint))
            .Select(pair => new BuildLockHolder(pair.Key, TryProcessName(pair.Key), pair.Value, true, TryProcessStartTime(pair.Key)))
            .Take(8)
            .ToArray();
        return new BuildLockAttribution(path, holders, "process-snapshot");
    }

    private static List<BuildLockHolder> ParseHandleOutput(string output, string? ownershipHint)
    {
        var commandLines = new Dictionary<int, string>();
        var holders = new List<BuildLockHolder>();
        foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var pidMatch = HandlePidPattern.Match(line);
            if (!pidMatch.Success ||
                !int.TryParse(pidMatch.Groups["pid"].Value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var pid))
            {
                continue;
            }

            if (commandLines.Count == 0)
            {
                commandLines = ProcessCommandLines.Read([pid]);
            }
            else if (!commandLines.ContainsKey(pid))
            {
                foreach (var item in ProcessCommandLines.Read([pid]))
                {
                    commandLines[item.Key] = item.Value;
                }
            }

            commandLines.TryGetValue(pid, out var commandLine);
            var nameMatch = HandleNamePattern.Match(line);
            var processName = nameMatch.Success ? nameMatch.Groups["name"].Value : TryProcessName(pid);
            holders.Add(new BuildLockHolder(pid, processName, commandLine, IsOrchestratorOwned(commandLine, ownershipHint), TryProcessStartTime(pid)));
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

    private static string? ResolveHandleExecutable()
    {
        if (!string.IsNullOrWhiteSpace(HandleExecutableForTests))
        {
            return HandleExecutableForTests;
        }

        var explicitPath = Environment.GetEnvironmentVariable("MCG_HANDLE64");
        if (!string.IsNullOrWhiteSpace(explicitPath) && File.Exists(explicitPath))
        {
            return explicitPath;
        }

        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator))
        {
            foreach (var name in new[] { "handle64.exe", "handle.exe" })
            {
                var candidate = Path.Combine(directory.Trim(), name);
                if (File.Exists(candidate))
                {
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
                    return candidate;
                }
            }
        }

        return null;
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
