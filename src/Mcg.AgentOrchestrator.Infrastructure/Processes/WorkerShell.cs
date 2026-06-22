namespace Mcg.AgentOrchestrator.Infrastructure;

/// <summary>
/// Resolves the PowerShell host used to run dispatch wrappers and worker command templates.
/// Worker commands are PowerShell-dialect (e.g. <c>claude ... -p (Get-Content -Raw 'prompt.md')</c>),
/// so a PowerShell host is required. Prefers cross-platform PowerShell (<c>pwsh</c>) when it is on
/// PATH; falls back to Windows PowerShell (<c>powershell.exe</c>) on Windows. This keeps existing
/// Windows behavior identical (where <c>pwsh</c> is usually absent) while making the runtime
/// portable to Linux/macOS once <c>pwsh</c> is installed.
/// </summary>
public static class WorkerShell
{
    /// <summary>The resolved PowerShell host executable.</summary>
    public static string Executable { get; } = ResolveExecutable();

    private static string ResolveExecutable()
    {
        if (IsOnPath("pwsh"))
        {
            return "pwsh";
        }

        // Non-Windows without pwsh surfaces a clear "pwsh not found" failure at launch, which is the
        // correct signal (the worker command templates need a PowerShell host).
        return OperatingSystem.IsWindows() ? "powershell.exe" : "pwsh";
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

    private static bool IsOnPath(string executable)
    {
        var pathVariable = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathVariable))
        {
            return false;
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
                    if (File.Exists(Path.Combine(directory, candidate)))
                    {
                        return true;
                    }
                }
                catch
                {
                    // Ignore malformed PATH entries.
                }
            }
        }

        return false;
    }
}
