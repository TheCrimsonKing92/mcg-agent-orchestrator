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

    private static IEnumerable<string> WindowsPowerShellCandidates() =>
        WindowsPowerShellCandidates(
            Environment.GetEnvironmentVariable("LOCALAPPDATA"),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetEnvironmentVariable("ProgramW6432"));

    /// <summary>
    /// Candidate pwsh 7 paths in preference order, as a pure function of the four location inputs so the
    /// ordering is assertable without mutating the ambient environment (<see cref="Executable"/> resolves
    /// once per process and cannot be re-run).
    /// </summary>
    internal static IEnumerable<string> WindowsPowerShellCandidates(
        string? localAppDataVariable,
        string? localAppDataKnownFolder,
        string? programFiles,
        string? programW6432)
    {
        // Read the LOCALAPPDATA VARIABLE before falling back to the known-folder API. GetFolderPath expands
        // the REG_EXPAND_SZ literal "%USERPROFILE%\AppData\Local" against the CURRENT process environment, so
        // under the hermetic verification environment - which repoints USERPROFILE at an empty profile root -
        // it returns a directory that has never contained a PowerShell install. The variable, by contrast, is
        // set to the REAL per-user location by ConfigureHermeticVerificationEnvironment precisely so derived
        // paths keep working. Consulting it first is what makes the per-user pwsh 7 install findable inside
        // the gate; consulting only GetFolderPath is what silently handed every gate lane Windows PowerShell
        // 5.1, where ProcessStartInfo.ArgumentList does not exist and argument passing degrades to an
        // interactive REPL.
        foreach (var localAppData in new[] { localAppDataVariable, localAppDataKnownFolder })
        {
            if (!string.IsNullOrWhiteSpace(localAppData))
            {
                yield return Path.Combine(localAppData, "Programs", "PowerShell", "7", "pwsh.exe");
            }
        }

        if (!string.IsNullOrWhiteSpace(programFiles))
        {
            yield return Path.Combine(programFiles, "PowerShell", "7", "pwsh.exe");
        }

        if (!string.IsNullOrWhiteSpace(programW6432))
        {
            yield return Path.Combine(programW6432, "PowerShell", "7", "pwsh.exe");
        }

        // Windows PowerShell 5.1 is deliberately NOT a candidate here. It used to be the last entry, which
        // put it AHEAD of the PATH lookup in ResolveExecutable - so on any host where the three pwsh 7
        // candidates above miss, resolution silently selected a different MAJOR VERSION of the shell while a
        // perfectly good pwsh 7 sat on PATH. That is not a fallback, it is a downgrade, and it is invisible:
        // WorkerShellTests only asserts File.Exists on the result, which 5.1 satisfies.
        //
        // It bit the acceptance gate hard. The first candidate derives from
        // GetFolderPath(LocalApplicationData), and that known folder is stored as the REG_EXPAND_SZ literal
        // "%USERPROFILE%\AppData\Local", expanded against the CHILD's environment block. The hermetic
        // verification environment rewrites USERPROFILE, so the per-user pwsh 7 install stopped resolving and
        // every gate lane ran workers under 5.1 - where ProcessStartInfo.ArgumentList does not exist, so
        // argument-passing silently degraded to an interactive REPL. That known-folder trap is now handled by
        // reading the LOCALAPPDATA variable first, above.
        //
        // Removing 5.1 from this list was NOT on its own sufficient, and the reason is worth keeping: on a
        // host where pwsh 7 is installed per-user, PATH carries only
        // C:\WINDOWS\System32\WindowsPowerShell\v1.0, so FindOnPath("pwsh") misses and
        // FindOnPath("powershell.exe") lands on the very same 5.1 binary this list stopped naming. Dropping
        // the candidate changed which line selected 5.1, not whether it was selected. Only resolving the real
        // pwsh 7 path fixes it.
        //
        // 5.1 stays reachable through the PATH search and the final literal fallback in ResolveExecutable,
        // so nothing is lost on a genuinely 5.1-only host - it is simply no longer PREFERRED over pwsh 7.
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
