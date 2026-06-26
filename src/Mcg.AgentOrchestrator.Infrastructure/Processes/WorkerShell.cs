namespace Mcg.AgentOrchestrator.Infrastructure;

/// <summary>
/// Resolves the PowerShell host used to run dispatch wrappers and worker command templates.
/// Worker commands are PowerShell-dialect (e.g. <c>claude ... -p (Get-Content -Raw 'prompt.md')</c>),
/// so a PowerShell host is required. On Windows, prefer a real filesystem executable and avoid the
/// WindowsApps package/alias path: Low-IL dispatches can be denied by package activation even when
/// ordinary file reads would be allowed. Non-Windows hosts still resolve <c>pwsh</c> from PATH.
/// </summary>
public static class WorkerShell
{
    /// <summary>The resolved PowerShell host executable.</summary>
    public static string Executable { get; } = ResolveExecutable();

    private static string ResolveExecutable()
    {
        if (OperatingSystem.IsWindows())
        {
            foreach (var candidate in WindowsPowerShellCandidates())
            {
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            var pathCandidate = FindOnPath("pwsh", skipWindowsApps: true) ??
                FindOnPath("powershell.exe", skipWindowsApps: true);
            if (!string.IsNullOrWhiteSpace(pathCandidate))
            {
                return pathCandidate;
            }

            // Last resort: keep the launch failure concrete if the Windows install is unusual.
            return "powershell.exe";
        }

        // Non-Windows without pwsh surfaces a clear "pwsh not found" failure at launch, which is the
        // correct signal (the worker command templates need a PowerShell host).
        return FindOnPath("pwsh", skipWindowsApps: false) ?? "pwsh";
    }

    private static IEnumerable<string> WindowsPowerShellCandidates()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrWhiteSpace(programFiles))
        {
            yield return Path.Combine(programFiles, "PowerShell", "7", "pwsh.exe");
        }

        var programW6432 = Environment.GetEnvironmentVariable("ProgramW6432");
        if (!string.IsNullOrWhiteSpace(programW6432))
        {
            yield return Path.Combine(programW6432, "PowerShell", "7", "pwsh.exe");
        }

        var systemRoot = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (!string.IsNullOrWhiteSpace(systemRoot))
        {
            yield return Path.Combine(systemRoot, "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
        }
    }

    /// <summary>
    /// Base arguments preceding the wrapper/command. <c>-NonInteractive -InputFormat None</c> make the
    /// host ignore stdin entirely: under a background/detached launch the worker's stdin is an inherited
    /// pipe that never reaches EOF, and a PowerShell host that reads stdin (the default) blocks at startup
    /// before ever running the command — the CLI-worker "startup hang" where the heartbeat shows childPid
    /// null and zero output until the watchdog reaps it. Ignoring stdin closes that race at the source
    /// (the post-Start StandardInput.Close in DispatchProcessHost is a racy band-aid by comparison).
    /// <c>-ExecutionPolicy Bypass</c> only applies on Windows; pwsh on Linux/macOS has no execution
    /// policy, so it is omitted there.
    /// </summary>
    public static IReadOnlyList<string> BaseArguments() =>
        OperatingSystem.IsWindows()
            ? ["-NoProfile", "-NonInteractive", "-InputFormat", "None", "-ExecutionPolicy", "Bypass", "-Command"]
            : ["-NoProfile", "-NonInteractive", "-InputFormat", "None", "-Command"];

    private static string? FindOnPath(string executable, bool skipWindowsApps)
    {
        var pathVariable = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathVariable))
        {
            return null;
        }

        var candidates = OperatingSystem.IsWindows()
            ? new[] { executable, executable + ".exe" }
            : [executable];

        foreach (var directory in pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var candidate in candidates)
            {
                try
                {
                    var path = Path.Combine(directory, candidate);
                    if (File.Exists(path) &&
                        (!skipWindowsApps || !IsWindowsAppsPath(path)))
                    {
                        return path;
                    }
                }
                catch
                {
                    // Ignore malformed PATH entries.
                }
            }
        }

        return null;
    }

    internal static bool IsWindowsAppsPath(string path)
    {
        var normalized = path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        return normalized.Contains(
            $"{Path.DirectorySeparatorChar}WindowsApps{Path.DirectorySeparatorChar}",
            StringComparison.OrdinalIgnoreCase) ||
            normalized.EndsWith(
                $"{Path.DirectorySeparatorChar}Microsoft{Path.DirectorySeparatorChar}WindowsApps",
                StringComparison.OrdinalIgnoreCase);
    }
}
