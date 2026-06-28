using System.Diagnostics;
using System.Text.Json;

namespace Mcg.AgentOrchestrator.Infrastructure;

/// <summary>
/// Native, cross-platform replacement for the old generated-PowerShell dispatch wrapper. Runs
/// detached as a hidden <c>__dispatch-run</c> subcommand of the App: it sets the build env, launches
/// the worker command through the resolved PowerShell host (<see cref="WorkerShell"/>), streams
/// stdout/stderr to log files, writes a periodic heartbeat, and always records the exit code so the
/// orchestrator can reconcile the dispatch. The detached process outlives the orchestrator CLI, so
/// this logic must be self-contained and never throw without writing the exit file.
/// </summary>
public static class DispatchProcessHost
{
    public const string SubcommandName = "__dispatch-run";
    public const string StartGatePathVariable = "MCG_DISPATCH_HOST_START_GATE";
    internal const string LowIntegritySetupArtifactName = "low-integrity-setup.json";
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(15);

    // Dispatch supervision: an unbounded wait lets a hung worker — or a stuck grandchild such as a
    // git process wedged on an index.lock — hold the owned job open indefinitely, which blocks the
    // conductor loop and strands the goal (observed: a git child kept a dispatch alive 34 minutes,
    // jamming a --watch loop for its full budget). The watchdog enforces a hard max runtime and an
    // idle-stall cap (no stdout/stderr growth), then reaps the whole tree. Both are overridable via
    // env so an operator can widen them for an unusually long legitimate dispatch.
    private static readonly TimeSpan DefaultMaxRuntime = TimeSpan.FromMinutes(60);
    private static readonly TimeSpan DefaultMaxIdle = TimeSpan.FromMinutes(20);
    private static readonly TimeSpan WatchdogProbeInterval = TimeSpan.FromSeconds(15);
    private const long CpuProgressEpsilonMs = 50L;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public sealed record DispatchRunParameters(
        string Command,
        string WorkingDirectory,
        string StdoutPath,
        string StderrPath,
        string ExitCodePath,
        string? HeartbeatPath,
        bool ShutdownBuildServerOnExit,
        bool DisableSharedCompilation,
        // OS worker sandbox: when SandboxLowIntegrity is set, the worker runs at LOW integrity (same
        // operator user) confined by Mandatory Integrity Control to the worktree + a Low CODEX_HOME/TEMP.
        // The worker can only EDIT the worktree (the shared .git stays medium and out of reach); the
        // orchestrator commits the worker's edits afterwards. Default = run at medium integrity.
        bool SandboxLowIntegrity = false,
        WorkerSandboxProvider Provider = WorkerSandboxProvider.Unknown);

    public static string WriteParameters(string path, DispatchRunParameters parameters)
    {
        File.WriteAllText(path, JsonSerializer.Serialize(parameters, JsonOptions));
        return path;
    }

    // OS worker sandbox via Mandatory Integrity Control. The worker runs at LOW integrity as the SAME
    // operator user — so the toolchain (node/codex) and codex auth are reachable (reads aren't
    // MIC-restricted) — but it can only WRITE Low-labeled objects (the worktree + a Low CODEX_HOME/TEMP),
    // never the medium-integrity profile or main repo. The host runs at medium and cannot launch a Low
    // child without privilege, so we prepend a self-drop wrapper to the worker command (a process may
    // lower its own integrity freely). Validated by scripts/Test-LowIntegrity.ps1.
    internal static void ApplyWorkerSandbox(ProcessStartInfo startInfo, DispatchRunParameters parameters)
    {
        if (!parameters.SandboxLowIntegrity || !OperatingSystem.IsWindows())
        {
            return;
        }

        // Label ONLY the worktree Low so the Low worker can edit it. The shared git common dir is
        // deliberately left at medium integrity: it lives OUTSIDE the worktree (in the main repo's
        // .git/worktrees), so the worker must not be able to write it — that is the write-confinement
        // guarantee. The worker only EDITS the worktree; the orchestrator (medium) commits those edits
        // afterwards (BackgroundDispatchRunner.TryCommitWorktreeEdits). This also removes the slow,
        // broad per-dispatch icacls /T walk over the whole .git that labeling the common dir required.
        if (!SetLowIntegrity(parameters.WorkingDirectory, recursive: true))
        {
            throw new InvalidOperationException($"Failed to apply Low integrity label to worktree '{parameters.WorkingDirectory}'.");
        }
        ProtectGitMetadata(parameters.WorkingDirectory);

        // Per-dispatch Low-labeled writable set: codex's home (seeded with the operator's auth so codex
        // stays authenticated) and a temp scratch. The sandbox root is labeled before child paths are
        // materialized so they inherit Low without a second recursive icacls traversal.
        var sandboxRoot = Path.Combine(parameters.WorkingDirectory, ".mcg-sandbox");
        Directory.CreateDirectory(sandboxRoot);
        if (!SetLowIntegrity(sandboxRoot, recursive: false, inheritToChildren: true))
        {
            throw new InvalidOperationException($"Failed to apply inheritable Low integrity label to sandbox root '{sandboxRoot}'.");
        }

        var codexHome = Path.Combine(sandboxRoot, "codex-home");
        var tempDir = Path.Combine(sandboxRoot, "temp");
        var sandboxBin = CreateSandboxBinDirectory(sandboxRoot);
        Directory.CreateDirectory(codexHome);
        Directory.CreateDirectory(tempDir);
        WriteWorkerCommandShims(sandboxBin, startInfo.Environment["PATH"]);

        SeedProviderEnvironment(startInfo, parameters.Provider, sandboxRoot, codexHome, parameters.StderrPath);

        // Keep the sandbox scratch out of git's view so it never registers as a dirty/untracked path:
        // the worktree must read as clean after the orchestrator commits the worker's real edits.
        ExcludeSandboxFromGit(parameters.WorkingDirectory);

        startInfo.Environment["CODEX_HOME"] = codexHome;
        startInfo.Environment["TEMP"] = tempDir;
        startInfo.Environment["TMP"] = tempDir;
        startInfo.Environment["PATH"] = BuildLowIntegrityPath(startInfo.Environment["PATH"], WorkerShell.Executable, sandboxBin);
        WriteLowIntegritySetupArtifact(sandboxRoot, parameters.WorkingDirectory);

        // Prepend a self-drop-to-Low wrapper. ArgumentList is [BaseArgs..., Command]; replace Command
        // with ". 'drop.ps1'; <Command>" so the worker (and its children: codex/node) run Low.
        var dropScript = Path.Combine(sandboxRoot, "drop-to-low.ps1");
        File.WriteAllText(dropScript, DropToLowScript);
        var lastIndex = startInfo.ArgumentList.Count - 1;
        if (lastIndex >= 0)
        {
            startInfo.ArgumentList[lastIndex] = $". '{dropScript}'; {startInfo.ArgumentList[lastIndex]}";
        }
    }

    internal static void SeedProviderEnvironment(
        ProcessStartInfo startInfo,
        WorkerSandboxProvider provider,
        string sandboxRoot,
        string codexHome,
        string? stderrPath = null)
    {
        if (provider == WorkerSandboxProvider.Claude)
        {
            SeedClaudeEnvironment(startInfo, sandboxRoot, stderrPath);
            return;
        }

        SeedCodexAuth(codexHome);
    }

    private static void SeedClaudeEnvironment(ProcessStartInfo startInfo, string sandboxRoot, string? stderrPath)
    {
        var claudeConfigDir = Path.Combine(sandboxRoot, "claude-config");
        Directory.CreateDirectory(claudeConfigDir);
        var settingsPath = Path.Combine(claudeConfigDir, "settings.json");
        if (!File.Exists(settingsPath))
        {
            File.WriteAllText(settingsPath, "{}\n");
        }

        var apiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            startInfo.Environment["ANTHROPIC_API_KEY"] = apiKey;
        }
        else if (!string.IsNullOrWhiteSpace(stderrPath))
        {
            AppendDispatchStderrDiagnostic(
                stderrPath,
                "Claude worker sandbox diagnostic: ANTHROPIC_API_KEY is not set; Claude may fail to authenticate.");
        }

        startInfo.Environment["CLAUDE_CONFIG_DIR"] = claudeConfigDir;
    }

    private static void AppendDispatchStderrDiagnostic(string stderrPath, string message)
    {
        var directory = Path.GetDirectoryName(stderrPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.AppendAllText(stderrPath, message + Environment.NewLine);
    }

    internal static FileStream OpenWorkerStderrStream(string stderrPath)
    {
        return new FileStream(stderrPath, FileMode.Append, FileAccess.Write, FileShare.Read);
    }

    internal static string BuildLowIntegrityPath(string? currentPath, string shellExecutable, string? sandboxBin = null)
    {
        var entries = new List<string>();
        if (!string.IsNullOrWhiteSpace(sandboxBin))
        {
            entries.Add(sandboxBin);
        }

        var shellDirectory = Path.GetDirectoryName(shellExecutable);
        if (!string.IsNullOrWhiteSpace(shellDirectory))
        {
            entries.Add(shellDirectory);
        }

        if (!string.IsNullOrWhiteSpace(currentPath))
        {
            foreach (var rawEntry in currentPath.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (IsWindowsAppsPathSegment(rawEntry) ||
                    entries.Any(existing => PathsEqual(existing, rawEntry)))
                {
                    continue;
                }

                entries.Add(rawEntry);
            }
        }

        return string.Join(Path.PathSeparator, entries);
    }

    internal static string CreateSandboxBinDirectory(string sandboxRoot)
    {
        var sandboxBin = Path.Combine(sandboxRoot, "bin");
        Directory.CreateDirectory(sandboxBin);
        return sandboxBin;
    }

    internal static void WriteWorkerCommandShims(string sandboxBin, string? currentPath)
    {
        Directory.CreateDirectory(sandboxBin);
        WriteCommandShim(Path.Combine(sandboxBin, "git.cmd"), "git", ResolveExecutableWithWhere("git", currentPath));
        WriteCommandShim(Path.Combine(sandboxBin, "dotnet.cmd"), "dotnet", ResolveExecutableWithWhere("dotnet", currentPath));
    }

    internal static string ResolveExecutableWithWhere(string commandName, string? currentPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "where.exe" : "which",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add(commandName);
        if (!string.IsNullOrWhiteSpace(currentPath))
        {
            startInfo.Environment["PATH"] = currentPath;
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to resolve '{commandName}' with {startInfo.FileName}.");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        if (!process.WaitForExit(30_000))
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException($"Timed out resolving '{commandName}' with {startInfo.FileName}.");
        }

        var resolved = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .FirstOrDefault(File.Exists);
        if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(resolved))
        {
            throw new InvalidOperationException($"Failed to resolve '{commandName}' with {startInfo.FileName}: {error.Trim()}");
        }

        return Path.GetFullPath(resolved);
    }

    internal static void WriteCommandShim(string shimPath, string commandName, string realExecutable)
    {
        var escapedExecutable = realExecutable.Replace("%", "%%", StringComparison.Ordinal);
        var contents = $"""
            @echo off
            setlocal EnableExtensions EnableDelayedExpansion
            set "MCG_REAL={escapedExecutable}"
            set "MCG_ATTEMPT=1"
            set "MCG_STDERR=%TEMP%\mcg-{commandName}-shim-%RANDOM%-%RANDOM%.err"
            :retry
            "%MCG_REAL%" %* 2>"%MCG_STDERR%"
            set "MCG_EXIT=!ERRORLEVEL!"
            type "%MCG_STDERR%" 1>&2 2>nul
            if "!MCG_EXIT!"=="0" (
                del "%MCG_STDERR%" >nul 2>nul
                exit /b 0
            )
            findstr /i /c:"CreateProcessAsUserW 1312" /c:"specified logon session does not exist" "%MCG_STDERR%" >nul 2>nul
            if errorlevel 1 (
                del "%MCG_STDERR%" >nul 2>nul
                exit /b !MCG_EXIT!
            )
            if "!MCG_ATTEMPT!"=="3" (
                echo [mcg-shim] CreateProcessAsUserW 1312 retry exhausted for {commandName}. 1>&2
                del "%MCG_STDERR%" >nul 2>nul
                exit /b !MCG_EXIT!
            )
            set /a MCG_ATTEMPT+=1
            if "!MCG_ATTEMPT!"=="2" (
                "%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -Command "Start-Sleep -Milliseconds 250" >nul 2>nul
            ) else (
                "%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -Command "Start-Sleep -Milliseconds 750" >nul 2>nul
            )
            goto retry
            """;
        File.WriteAllText(shimPath, contents.Replace("\r\n", "\n", StringComparison.Ordinal));
    }

    internal static void RunLowIntegrityLaunchPreflight(ProcessStartInfo workerStartInfo, DispatchRunParameters parameters)
    {
        if (!parameters.SandboxLowIntegrity || !OperatingSystem.IsWindows())
        {
            return;
        }

        RunLowIntegrityLaunchPreflightCommand("git", parameters.WorkingDirectory, workerStartInfo.Environment["PATH"]);
        RunLowIntegrityLaunchPreflightCommand("dotnet", parameters.WorkingDirectory, workerStartInfo.Environment["PATH"]);
    }

    private static void RunLowIntegrityLaunchPreflightCommand(string commandName, string workingDirectory, string? path)
    {
        var commandPath = ResolveCommandFromPath(commandName, path)
            ?? throw new InvalidOperationException($"Low-integrity launch preflight could not resolve '{commandName}' from the shimmed PATH.");
        var startInfo = new ProcessStartInfo
        {
            FileName = commandPath,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        if (OperatingSystem.IsWindows() &&
            (commandPath.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) ||
             commandPath.EndsWith(".bat", StringComparison.OrdinalIgnoreCase)))
        {
            startInfo.FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
            startInfo.Arguments = $"/d /s /c \"\"{commandPath}\" --version\"";
        }
        else
        {
            startInfo.ArgumentList.Add("--version");
        }
        if (!string.IsNullOrWhiteSpace(path))
        {
            startInfo.Environment["PATH"] = path;
        }

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            throw new InvalidOperationException($"Low-integrity launch preflight failed to start '{commandName} --version'.");
        }

        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        if (!process.WaitForExit(30_000))
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException($"Low-integrity launch preflight timed out running '{commandName} --version'.");
        }

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Low-integrity launch preflight failed for '{commandName} --version' with exit {process.ExitCode}: {stderr.Trim()} {stdout.Trim()}".Trim());
        }
    }

    internal static string? ResolveCommandFromPath(string commandName, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var extensions = OperatingSystem.IsWindows()
            ? new[] { ".cmd", ".exe", ".bat", ".com" }
            : new[] { string.Empty };
        foreach (var rawEntry in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var extension in extensions)
            {
                var candidate = Path.Combine(rawEntry, commandName + extension);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    internal static bool IsWindowsAppsPathSegment(string path)
    {
        var normalized = path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
            .TrimEnd(Path.DirectorySeparatorChar);
        return normalized.EndsWith(
            $"{Path.DirectorySeparatorChar}Microsoft{Path.DirectorySeparatorChar}WindowsApps",
            StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains(
                $"{Path.DirectorySeparatorChar}WindowsApps",
                StringComparison.OrdinalIgnoreCase);
    }

    private static bool PathsEqual(string left, string right)
    {
        try
        {
            left = Path.GetFullPath(left);
            right = Path.GetFullPath(right);
        }
        catch
        {
            // Compare the original strings when either path is malformed.
        }

        return string.Equals(
            left.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            right.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    // Appends ".mcg-sandbox/" to the worktree's local git exclude (.git/info/exclude, resolved via
    // rev-parse so linked worktrees resolve correctly). Local-only and untracked, so it confines the
    // sandbox scratch without dirtying the goal branch. Idempotent.
    private static void ExcludeSandboxFromGit(string worktree)
    {
        try
        {
            var pathResult = GitCli.Run(worktree, "rev-parse", "--git-path", "info/exclude");
            if (!pathResult.Succeeded || string.IsNullOrWhiteSpace(pathResult.Output))
            {
                return;
            }

            var excludeRaw = pathResult.Output.Trim();
            var excludePath = Path.IsPathRooted(excludeRaw)
                ? excludeRaw
                : Path.GetFullPath(Path.Combine(worktree, excludeRaw));
            Directory.CreateDirectory(Path.GetDirectoryName(excludePath)!);

            var existing = File.Exists(excludePath) ? File.ReadAllText(excludePath) : string.Empty;
            if (existing.Contains(".mcg-sandbox", StringComparison.Ordinal))
            {
                return;
            }

            var prefix = existing.Length > 0 && !existing.EndsWith('\n') ? "\n" : string.Empty;
            File.AppendAllText(excludePath, prefix + ".mcg-sandbox/\n");
        }
        catch
        {
            // Best-effort: if git ignores fail to write, the worktree inspection will simply see the
            // scratch dir; the commit recovery still excludes it via pathspec.
        }
    }

    // PowerShell that lowers the current process to Low integrity (lowering one's own token needs no
    // privilege). Dot-sourced before the worker command so the worker + its children run Low.
    private const string DropToLowScript = @"Add-Type -Namespace P -Name N -MemberDefinition @'
[DllImport(""kernel32.dll"")] public static extern System.IntPtr GetCurrentProcess();
[DllImport(""advapi32.dll"", SetLastError=true)] public static extern bool OpenProcessToken(System.IntPtr h, uint a, out System.IntPtr t);
[DllImport(""advapi32.dll"", SetLastError=true, CharSet=CharSet.Unicode)] public static extern bool ConvertStringSidToSidW(string s, out System.IntPtr sid);
[DllImport(""advapi32.dll"", SetLastError=true)] public static extern bool SetTokenInformation(System.IntPtr t, int c, ref TML info, int len);
[StructLayout(LayoutKind.Sequential)] public struct SAA { public System.IntPtr Sid; public uint Attr; }
[StructLayout(LayoutKind.Sequential)] public struct TML { public SAA Label; }
public static void DropToLow() {
    System.IntPtr tok, sid;
    if (!OpenProcessToken(GetCurrentProcess(), 0x0088, out tok)) throw new System.ComponentModel.Win32Exception();
    if (!ConvertStringSidToSidW(""S-1-16-4096"", out sid)) throw new System.ComponentModel.Win32Exception();
    var t = new TML(); t.Label.Sid = sid; t.Label.Attr = 0x20;
    if (!SetTokenInformation(tok, 25, ref t, Marshal.SizeOf(typeof(TML))+16)) throw new System.ComponentModel.Win32Exception();
}
'@
[P.N]::DropToLow()
";

    private static bool SetLowIntegrity(string path, bool recursive, bool inheritToChildren = true)
    {
        return SetIntegrity(path, inheritToChildren ? "(OI)(CI)L" : "L", recursive);
    }

    private static void ProtectGitMetadata(string worktree)
    {
        var checkoutGitFile = Path.Combine(worktree, ".git");
        if (File.Exists(checkoutGitFile))
        {
            if (!SetMediumIntegrity(checkoutGitFile))
            {
                throw new InvalidOperationException($"Failed to protect linked worktree git file '{checkoutGitFile}'.");
            }
        }

        string? commonDir = null;
        try
        {
            var commonDirResult = GitCli.Run(worktree, "rev-parse", "--git-common-dir");
            if (!commonDirResult.Succeeded || string.IsNullOrWhiteSpace(commonDirResult.Output))
            {
                return;
            }

            var commonDirRaw = commonDirResult.Output.Trim();
            commonDir = Path.IsPathRooted(commonDirRaw)
                ? commonDirRaw
                : Path.GetFullPath(Path.Combine(worktree, commonDirRaw));
        }
        catch
        {
            // Git metadata protection is best-effort; the checkout-local .git file is handled directly
            // above so linked worktrees keep their write confinement even when git probing fails.
        }

        if (commonDir is not null && Directory.Exists(commonDir) && !SetMediumIntegrity(commonDir))
        {
            throw new InvalidOperationException($"Failed to protect git common dir '{commonDir}'.");
        }
    }

    private static bool SetMediumIntegrity(string path)
    {
        return SetIntegrity(path, Directory.Exists(path) ? "(OI)(CI)M" : "M", recursive: false);
    }

    private static bool SetIntegrity(string path, string level, bool recursive)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "icacls",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            psi.ArgumentList.Add(path);
            psi.ArgumentList.Add("/setintegritylevel");
            psi.ArgumentList.Add(level);
            if (recursive)
            {
                psi.ArgumentList.Add("/T");
            }

            using var process = Process.Start(psi);
            if (process is null)
            {
                return false;
            }

            var copyOut = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null);
            var copyErr = process.StandardError.BaseStream.CopyToAsync(Stream.Null);
            var completed = WaitForIntegrityLabeler(process, TimeSpan.FromMinutes(2));
            try { Task.WaitAll([copyOut, copyErr], 2000); } catch { }
            return completed;
        }
        catch
        {
            return false;
        }
    }

    internal static bool WaitForIntegrityLabeler(Process process, TimeSpan timeout)
    {
        if (process.WaitForExit((int)timeout.TotalMilliseconds))
        {
            return true;
        }

        try { process.Kill(entireProcessTree: true); } catch { }
        try { process.WaitForExit(5000); } catch { }
        return false;
    }

    private static void WriteLowIntegritySetupArtifact(string sandboxRoot, string worktree)
    {
        var artifact = new
        {
            strategy = "worktree-recursive-sandbox-inherited",
            worktreeRecursiveRelabel = true,
            sandboxRecursiveRelabel = false,
            sandboxRoot,
            worktree
        };
        File.WriteAllText(
            Path.Combine(sandboxRoot, LowIntegritySetupArtifactName),
            JsonSerializer.Serialize(artifact, JsonOptions) + Environment.NewLine);
    }

    private static void SeedCodexAuth(string codexHome)
    {
        try
        {
            var src = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "auth.json");
            if (File.Exists(src))
            {
                File.Copy(src, Path.Combine(codexHome, "auth.json"), overwrite: true);
            }
        }
        catch { /* best-effort */ }
    }

    /// <summary>Entry point for the detached <c>__dispatch-run &lt;paramsPath&gt;</c> subcommand.</summary>
    public static int Run(string parametersPath)
    {
        DispatchRunParameters parameters;
        try
        {
            parameters = JsonSerializer.Deserialize<DispatchRunParameters>(File.ReadAllText(parametersPath), JsonOptions)
                ?? throw new InvalidOperationException("Dispatch parameters were empty.");
        }
        catch
        {
            return 1;
        }

        return RunCore(parameters);
    }

    private static int RunCore(DispatchRunParameters parameters)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var lastProgressAt = startedAt;
        long lastStdoutBytes = -1;
        long lastStderrBytes = -1;
        long lastCpuMs = 0L;
        var exitCode = 1;
        Process? worker = null;
        OwnedProcessGroup? workerGroup = null;

        void WriteHeartbeat(string state)
        {
            if (string.IsNullOrWhiteSpace(parameters.HeartbeatPath))
            {
                return;
            }

            var stdoutBytes = FileLength(parameters.StdoutPath);
            var stderrBytes = FileLength(parameters.StderrPath);
            var ownedCpuMs = SumOwnedCpuMs(workerGroup?.ProcessIds ?? []);

            if (HasProgressed(lastStdoutBytes + lastStderrBytes, stdoutBytes + stderrBytes, lastCpuMs, ownedCpuMs, CpuProgressEpsilonMs))
            {
                lastProgressAt = DateTimeOffset.UtcNow;
            }

            if (stdoutBytes != lastStdoutBytes || stderrBytes != lastStderrBytes)
            {
                lastStdoutBytes = stdoutBytes;
                lastStderrBytes = stderrBytes;
            }

            lastCpuMs = ownedCpuMs;

            var payload = new
            {
                pid = Environment.ProcessId,
                childPid = worker is { HasExited: false } ? worker.Id : (int?)null,
                ownedPids = workerGroup?.ProcessIds ?? [],
                startedAt = startedAt.ToString("o"),
                lastObservedAt = DateTimeOffset.UtcNow.ToString("o"),
                lastProgressAt = lastProgressAt.ToString("o"),
                state,
                stdoutBytes,
                stderrBytes,
                ownedCpuMs,
                exitFileExists = File.Exists(parameters.ExitCodePath)
            };

            try
            {
                var tmp = parameters.HeartbeatPath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(payload, JsonOptions));
                File.Move(tmp, parameters.HeartbeatPath, overwrite: true);
            }
            catch
            {
                // Heartbeat is best-effort; never let it fail the dispatch.
            }
        }

        using var heartbeatTimer = new Timer(_ => WriteHeartbeat("running"), null, Timeout.Infinite, Timeout.Infinite);

        try
        {
            // Redirect stdin so we can close it immediately: CLI workers otherwise inherit the
            // orchestrator's stdin. Under a background/detached launch that handle is an open pipe
            // that never reaches EOF, so the worker blocks indefinitely waiting for stdin.
            var startInfo = WorkerProcessRunner.BuildPowerShellStartInfo(parameters.Command, parameters.WorkingDirectory);

            if (parameters.DisableSharedCompilation)
            {
                startInfo.Environment["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0";
                startInfo.Environment["MSBUILDDISABLENODEREUSE"] = "1";
                startInfo.Environment["UseSharedCompilation"] = "false";
            }

            WriteHeartbeat(parameters.SandboxLowIntegrity ? "preparing-sandbox" : "starting");
            ApplyWorkerSandbox(startInfo, parameters);

            WriteHeartbeat(parameters.SandboxLowIntegrity ? "preflighting-sandbox" : "starting");
            RunLowIntegrityLaunchPreflight(startInfo, parameters);

            WriteHeartbeat("starting");
            RequireStartGate();
            worker = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Failed to start worker process.");
            workerGroup = OwnedProcessGroup.Attach(worker);

            // Close the worker's stdin immediately so it reads EOF instead of blocking on an
            // inherited/open pipe (see RedirectStandardInput note above).
            try { worker.StandardInput.Close(); } catch { /* worker may have already exited */ }

            // Stream raw bytes to the log files so the heartbeat's byte-growth progress detection works.
            using var stdout = new FileStream(parameters.StdoutPath, FileMode.Create, FileAccess.Write, FileShare.Read);
            using var stderr = OpenWorkerStderrStream(parameters.StderrPath);
            using var drainCts = new CancellationTokenSource();
            var copyOut = worker.StandardOutput.BaseStream.CopyToAsync(stdout, drainCts.Token);
            var copyErr = worker.StandardError.BaseStream.CopyToAsync(stderr, drainCts.Token);

            heartbeatTimer.Change(HeartbeatInterval, HeartbeatInterval);

            // Bounded supervision instead of an unbounded WaitForExit: reap the whole tree when
            // ShouldReapWorker says so (runtime cap, or idle/stall cap once the worker has streamed).
            var maxRuntime = ResolveWatchdogTimeout("MCG_DISPATCH_MAX_RUNTIME_MIN", DefaultMaxRuntime);
            var maxIdle = ResolveWatchdogTimeout("MCG_DISPATCH_MAX_IDLE_MIN", DefaultMaxIdle);
            while (!worker.WaitForExit((int)WatchdogProbeInterval.TotalMilliseconds))
            {
                var now = DateTimeOffset.UtcNow;
                var runFor = now - startedAt;
                var idleFor = now - lastProgressAt;
                var hasProducedOutput = lastStdoutBytes > 0 || lastStderrBytes > 0;
                if (!ShouldReapWorker(runFor, idleFor, hasProducedOutput, maxRuntime, maxIdle))
                {
                    continue;
                }

                var reason = runFor >= maxRuntime
                    ? $"exceeded max runtime {maxRuntime.TotalMinutes:0} min"
                    : $"stalled {idleFor.TotalMinutes:0} min with no output (idle cap {maxIdle.TotalMinutes:0} min)";
                try { File.AppendAllText(parameters.StderrPath, $"\n[dispatch-host] terminating worker tree: {reason}.\n"); }
                catch { /* diagnostics are best-effort */ }
                TryKillWorkerTree(worker, workerGroup);
                worker.WaitForExit(5000);
                break;
            }

            // Drain with a bounded timeout. A grandchild that inherits the pipe handle
            // (e.g. claude-cli's node child) keeps CopyToAsync alive indefinitely after
            // the worker exits. Cap the wait and cancel so the finally block always
            // writes the exit-code file.
            const int DrainTimeoutMs = 12_000;
            var drainTasks = new Task[] { copyOut, copyErr };
            if (!Task.WaitAll(drainTasks, DrainTimeoutMs))
            {
                drainCts.Cancel();
                TryKillWorkerTree(worker, workerGroup);
                try { Task.WaitAll(drainTasks, 2000); } catch { }
            }

            try { stdout.Flush(); } catch { }
            try { stderr.Flush(); } catch { }
            exitCode = worker.ExitCode;
        }
        catch (Exception ex)
        {
            exitCode = 1;
            // Capture launch/setup failures (e.g. launch-as-user under the OS sandbox) — otherwise the
            // worker never starts and nothing explains why (no worker means no redirected stderr).
            try { File.AppendAllText(parameters.StderrPath, $"[dispatch-host] worker launch/run failed: {ex}\n"); }
            catch { /* diagnostics are best-effort */ }
        }
        finally
        {
            heartbeatTimer.Change(Timeout.Infinite, Timeout.Infinite);
            if (parameters.ShutdownBuildServerOnExit)
            {
                TryShutdownBuildServer(parameters.WorkingDirectory);
            }

            workerGroup?.Dispose();
            WriteHeartbeat("exiting");
            TryWriteExitCode(parameters.ExitCodePath, exitCode);
        }

        return exitCode;
    }

    private static long FileLength(string path)
    {
        try
        {
            return File.Exists(path) ? new FileInfo(path).Length : 0L;
        }
        catch
        {
            return 0L;
        }
    }

    private static void TryKillWorkerTree(Process worker, OwnedProcessGroup? workerGroup)
    {
        try
        {
            workerGroup?.Kill();
        }
        catch
        {
            // Best-effort: fall back to direct tree kill below.
        }

        try
        {
            worker.Kill(entireProcessTree: true);
        }
        catch
        {
            // Best-effort: worker may have already exited.
        }
    }

    // The idle/stall cap measures byte-growth stalls, which only have meaning once a worker has
    // streamed output. A worker that has produced NO output yet (e.g. claude-cli -p buffers all
    // output to the end) is bounded by maxRuntime alone, so a healthy buffering worker is not
    // false-positive-reaped mid-task; a worker that streamed then went quiet is still reaped on idle.
    internal static bool ShouldReapWorker(
        TimeSpan runFor,
        TimeSpan idleFor,
        bool hasProducedOutput,
        TimeSpan maxRuntime,
        TimeSpan maxIdle)
    {
        if (runFor >= maxRuntime)
        {
            return true;
        }

        return hasProducedOutput && idleFor >= maxIdle;
    }

    internal static bool HasProgressed(long prevTotalBytes, long curTotalBytes, long prevCpuMs, long curCpuMs, long epsilonMs)
        => curTotalBytes != prevTotalBytes || curCpuMs - prevCpuMs > epsilonMs;

    private static long SumOwnedCpuMs(IReadOnlyList<int> processIds)
    {
        var total = 0L;
        foreach (var pid in processIds)
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                total += (long)p.TotalProcessorTime.TotalMilliseconds;
            }
            catch
            {
                // Process exited or access denied — contribute 0.
            }
        }
        return total;
    }

    private static TimeSpan ResolveWatchdogTimeout(string environmentVariable, TimeSpan fallback)
    {
        var raw = Environment.GetEnvironmentVariable(environmentVariable);
        if (!string.IsNullOrWhiteSpace(raw) &&
            int.TryParse(raw, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var minutes) &&
            minutes > 0)
        {
            return TimeSpan.FromMinutes(minutes);
        }

        return fallback;
    }

    private static void RequireStartGate()
    {
        var gatePath = Environment.GetEnvironmentVariable(StartGatePathVariable);
        if (string.IsNullOrWhiteSpace(gatePath))
        {
            return;
        }

        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (!File.Exists(gatePath) && DateTimeOffset.UtcNow < deadline)
        {
            Thread.Sleep(25);
        }

        if (!File.Exists(gatePath))
        {
            throw new InvalidOperationException("Dispatch host start gate was not released; refusing to launch worker outside the supervisor job.");
        }
    }

    private static void TryWriteExitCode(string path, int exitCode)
    {
        try
        {
            File.WriteAllText(path, exitCode.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        catch
        {
            // Best-effort; the orchestrator treats a missing exit file as still-running.
        }
    }

    private static void TryShutdownBuildServer(string workingDirectory)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "dotnet",
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            psi.ArgumentList.Add("build-server");
            psi.ArgumentList.Add("shutdown");
            using var process = Process.Start(psi);
            process?.WaitForExit(10000);
        }
        catch
        {
            // Best-effort build-server cleanup.
        }
    }
}
