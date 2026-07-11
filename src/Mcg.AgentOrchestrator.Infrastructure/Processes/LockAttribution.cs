using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed record BuildLockHolder(
    int? ProcessId,
    string? ProcessName,
    string? CommandLine,
    bool IsOrchestratorOwned);

public sealed record BuildLockAttribution(
    string Path,
    IReadOnlyList<BuildLockHolder> Holders,
    string Source);

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

    internal static Func<string, string?, BuildLockAttribution?>? AttributeForTests { get; set; }

    public static BuildLockAttribution Attribute(string path, string? ownershipHint = null)
    {
        if (AttributeForTests?.Invoke(path, ownershipHint) is { } testAttribution)
        {
            EmitReceipt(testAttribution);
            return testAttribution;
        }

        var fullPath = TryFullPath(path);
        if (OperatingSystem.IsWindows() && TryAttributeWithHandle(fullPath, ownershipHint) is { } handleAttribution)
        {
            EmitReceipt(handleAttribution);
            return handleAttribution;
        }

        var fallback = AttributeFromProcessSnapshot(fullPath, ownershipHint);
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
            Console.WriteLine($"LOCK path=\"{attribution.Path}\" holderPid=unknown holderName=unknown source={attribution.Source}");
            return;
        }

        foreach (var holder in attribution.Holders)
        {
            Console.WriteLine(
                $"LOCK path=\"{attribution.Path}\" holderPid={holder.ProcessId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown"} " +
                $"holderName=\"{Escape(holder.ProcessName ?? "unknown")}\" owned={holder.IsOrchestratorOwned.ToString().ToLowerInvariant()} " +
                $"source={attribution.Source} commandLine=\"{Escape(holder.CommandLine ?? string.Empty)}\"");
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
                UseShellExecute = false,
                CreateNoWindow = true
            };
            process.StartInfo.ArgumentList.Add("-accepteula");
            process.StartInfo.ArgumentList.Add("-nobanner");
            process.StartInfo.ArgumentList.Add(path);
            if (!process.Start())
            {
                return null;
            }

            var output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(3000))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return null;
            }

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
            .Select(pair => new BuildLockHolder(pair.Key, TryProcessName(pair.Key), pair.Value, true))
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
            holders.Add(new BuildLockHolder(pid, processName, commandLine, IsOrchestratorOwned(commandLine, ownershipHint)));
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

    private static string Escape(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
}
