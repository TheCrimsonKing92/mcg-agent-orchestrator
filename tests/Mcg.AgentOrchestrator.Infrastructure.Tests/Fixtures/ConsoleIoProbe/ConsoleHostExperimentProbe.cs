using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Win32.SafeHandles;

internal static class ConsoleHostExperimentProbe
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    internal static async Task<int> Run(string[] args)
    {
        var values = args.Chunk(2).ToDictionary(pair => pair[0], pair => pair[1], StringComparer.Ordinal);
        var reportPath = Path.GetFullPath(values["--report"]);
        var directory = Path.Combine(Path.GetTempPath(), "mcg-conhost-host", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(directory);
        var report = new Report
        {
            SwitchAtStartup = ChildConsoleLaunchPolicy.ExperimentForTests.ToString(),
            ConsoleWindow = Windows.GetConsoleWindow().ToInt64(),
            ConsoleProcessCount = Windows.GetConsoleProcessList(new uint[1], 1)
        };
        try
        {
            if (values.GetValueOrDefault("--startup-only", "false") != "true")
            {
                report.PowerShellExecutable = ResolveExecutable("pwsh");
                var launches = int.Parse(values.GetValueOrDefault("--launches", "4"), System.Globalization.CultureInfo.InvariantCulture);
                var arms = values.GetValueOrDefault("--arms", "both");
                if (launches <= 0 || arms is not ("both" or "off" or "inherit"))
                    throw new ArgumentException("Expected positive launches and arms both, off or inherit.");
                var workDirectory = values.GetValueOrDefault("--work-dir", directory);
                using var events = new WindowEvents();
                foreach (var mode in new[] { ChildConsoleExperiment.Off, ChildConsoleExperiment.InheritWindowlessConsole })
                {
                    if ((arms == "off" && mode != ChildConsoleExperiment.Off) ||
                        (arms == "inherit" && mode == ChildConsoleExperiment.Off)) continue;
                    ChildConsoleLaunchPolicy.ExperimentForTests = mode;
                    var arm = new Arm { Mode = mode.ToString(), LaunchCount = launches, HookInstalled = events.Installed, Desktop = events.Desktop };
                    report.Arms.Add(arm);
                    events.BeginArm(arm);
                    foreach (var command in new[] { "git", "pwsh", "dotnet" })
                    {
                        for (var index = 0; index < launches; index++)
                            arm.Children.Add(await RunOwned(Command(command, workDirectory), command, directory, events));
                        arm.StartChildren.Add(await RunStart(Command(command, workDirectory), command, events));
                    }
                    var release = Path.Combine(directory, Guid.NewGuid().ToString("n") + ".release");
                    var ready = release + ".ready";
                    var held = PowerShell("[IO.File]::WriteAllText($env:MCG_READY, 'ready'); while (!(Test-Path -LiteralPath $env:MCG_RELEASE)) { [Threading.Thread]::Yield() | Out-Null }", workDirectory);
                    held.Environment["MCG_READY"] = ready;
                    held.Environment["MCG_RELEASE"] = release;
                    arm.HeldProbe = await RunOwned(held, "held-pwsh", directory, events, ready, release);

                    var childScript = Path.Combine(directory, "descendant.ps1");
                    File.WriteAllText(childScript, ConsoleProbeScript);
                    var descendant = PowerShell(DescendantScript, workDirectory);
                    descendant.Environment["MCG_DESCENDANT_SCRIPT"] = childScript;
                    descendant.Environment["MCG_DESCENDANT_SHELL"] = ResolveExecutable("pwsh");
                    arm.Descendant = await RunOwned(descendant, "descendant", directory, events);

                    arm.CodePageBefore = Windows.GetConsoleOutputCP();
                    try
                    {
                        arm.CodePageChild = await RunOwned(StartInfo("cmd", workDirectory, "/d", "/c", "chcp", "65001"), "chcp", directory, events);
                        arm.CodePageAfter = Windows.GetConsoleOutputCP();
                    }
                    finally
                    {
                        if (!Windows.SetConsoleOutputCP(arm.CodePageBefore))
                            report.Error = new Win32Exception(Marshal.GetLastWin32Error(), "Could not restore host console code page.").ToString();
                    }
                    await events.EndArm();
                    arm.TotalConhosts = arm.Children.Sum(child => child.ConhostCount) + arm.HeldProbe.ConhostCount +
                        arm.Descendant.ConhostCount + arm.CodePageChild.ConhostCount;
                    arm.ExitCodes = arm.Children.Concat(arm.StartChildren).Select(child => child.ExitCode).ToArray();
                    arm.StdoutMatches = arm.Children.Concat(arm.StartChildren).Select(child =>
                    {
                        var baseline = report.Arms.FirstOrDefault(a => a.Mode == "Off")?.Children.FirstOrDefault(c => c.Command == child.Command);
                        return new OutputMatch(child.Command, baseline is null ? null : child.StdoutBase64 == baseline.StdoutBase64);
                    }).ToArray();
                }
            }
        }
        catch (Exception exception)
        {
            report.Error = exception.ToString();
        }
        finally
        {
            var json = JsonSerializer.Serialize(report, JsonOptions);
            Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
            File.WriteAllText(reportPath + ".tmp", json, new UTF8Encoding(false));
            File.Move(reportPath + ".tmp", reportPath, overwrite: true);
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
        return report.Error is null ? 0 : 1;
    }

    private static ProcessStartInfo Command(string command, string directory) => command switch
    {
        "git" => StartInfo(command, directory, "--version"),
        "pwsh" => StartInfo(command, directory, "-NoProfile", "-Command", "exit"),
        "dotnet" => StartInfo(command, directory, "--version"),
        _ => throw new ArgumentOutOfRangeException(nameof(command))
    };

    private static ProcessStartInfo PowerShell(string script, string directory) =>
        StartInfo("pwsh", directory, "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));

    private static ProcessStartInfo StartInfo(string executable, string directory, params string[] args)
    {
        var info = new ProcessStartInfo(ResolveExecutable(executable)) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = directory };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        info.Environment["DOTNET_NOLOGO"] = "1";
        info.Environment["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1";
        info.Environment.Remove("DOTNET_STARTUP_HOOKS");
        return info;
    }

    private static string ResolveExecutable(string name)
    {
        if (name == "pwsh")
        {
            // Avoid app execution aliases on the contained job-list launch path.
            // Reuse the repository's standalone PowerShell resolver, but require PowerShell 7.
            var shell = WorkerShell.Executable;
            if (Path.GetFileName(shell).Equals("pwsh.exe", StringComparison.OrdinalIgnoreCase) &&
                !WorkerShell.IsWindowsAppsPath(shell) && File.Exists(shell))
                return Path.GetFullPath(shell);
            throw new FileNotFoundException($"The conhost experiment requires standalone pwsh.exe; resolved {shell}.");
        }
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            var path = Path.Combine(directory.Trim('"'), name + ".exe");
            if (File.Exists(path)) return Path.GetFullPath(path);
        }
        throw new FileNotFoundException($"Required command {name}.exe was not found on PATH.");
    }

    private static async Task<Child> RunOwned(ProcessStartInfo info, string command, string directory, WindowEvents events,
        string? ready = null, string? release = null)
    {
        var stem = Path.Combine(directory, Guid.NewGuid().ToString("n"));
        using var start = OwnedProcessGroup.StartSuspendedContainedWithFileCapture(info, stem + ".out", stem + ".err");
        using var group = start.Group;
        var child = new Child { Command = command, ProcessId = start.Process.Id };
        events.Register(start.Process, group);
        var images = new Dictionary<int, Image>();
        Observe(group, images);
        if (!images.ContainsKey(start.Process.Id))
            throw new InvalidOperationException("Suspended root process image could not be classified before resume.");
        start.Resume();
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var exit = start.WaitForOwnedExitAsync(guard.Token);
        var released = release is null;
        try
        {
            // Poll actual membership, not elapsed time. The native exit handle is the completion signal.
            while (true)
            {
                Observe(group, images);
                if (!released && File.Exists(ready!))
                {
                    child.HeldImages = images.Values.ToArray();
                    File.WriteAllText(release!, "release");
                    released = true;
                }
                if (exit.IsCompleted)
                {
                    await exit;
                    if (!released) throw new InvalidOperationException("Held probe exited without publishing readiness.");
                    if (!group.TryGetActiveProcessIds(out var ids)) throw new InvalidOperationException("Job membership query failed.");
                    if (ids.Count == 0) break;
                }
                guard.Token.ThrowIfCancellationRequested();
                await Task.Yield();
            }
            child.ExitCode = start.TryReadOwnedExitCode() ?? throw new InvalidOperationException("Owned exit code unavailable.");
            if (!group.TryGetTotalProcesses(out var total)) throw new InvalidOperationException("Job TotalProcesses query failed.");
            child.TotalProcesses = total;
            child.Images = images.Values.ToArray();
            child.NonConhostProcesses = images.Values.Count(image => !image.IsConhost);
            child.ConhostCount = checked((int)total - child.NonConhostProcesses);
            // Unseen or unclassifiable members are explicit gaps, never silently treated as conhosts.
            child.UnclassifiedProcesses = checked((int)total - images.Count);
            child.StdoutBase64 = Convert.ToBase64String(File.ReadAllBytes(stem + ".out"));
            child.StderrBase64 = Convert.ToBase64String(File.ReadAllBytes(stem + ".err"));
            child.Stdout = Encoding.UTF8.GetString(Convert.FromBase64String(child.StdoutBase64));
            child.Stderr = Encoding.UTF8.GetString(Convert.FromBase64String(child.StderrBase64));
            return child;
        }
        finally
        {
            if (!released) File.WriteAllText(release!, "release");
            if (!exit.IsCompleted) group.Kill();
            try { await exit; } catch (OperationCanceledException) { }
        }
    }

    private static void Observe(OwnedProcessGroup group, Dictionary<int, Image> images)
    {
        if (!group.TryGetActiveProcessIds(out var ids)) throw new InvalidOperationException("Job membership query failed.");
        foreach (var id in ids)
        {
            if (images.ContainsKey(id)) continue;
            try
            {
                using var process = Process.GetProcessById(id);
                // The loader has not initialized MainModule in a suspended child: it can
                // be absent or report ntdll.dll. Query the process image independently of
                // its module list, before the root can exit after Resume.
                var capacity = 32768u;
                var path = new StringBuilder((int)capacity);
                if (Windows.QueryFullProcessImageNameW(process.Handle, 0, path, ref capacity))
                {
                    var executable = path.ToString();
                    images[id] = new Image(id, executable, ProcessObservationRoles.IsWindowsConsoleInfrastructure(executable));
                }
            }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
            catch (Win32Exception) { }
        }
    }

    private static async Task<Child> RunStart(ProcessStartInfo info, string command, WindowEvents events)
    {
        info.RedirectStandardInput = info.RedirectStandardOutput = info.RedirectStandardError = true;
        using var process = ProcessTreeGuiSuppression.Start(info) ?? throw new InvalidOperationException("Start-path child did not start.");
        events.Register(process, null);
        process.StandardInput.Close();
        using var output = new MemoryStream();
        using var error = new MemoryStream();
        var stdout = process.StandardOutput.BaseStream.CopyToAsync(output);
        var stderr = process.StandardError.BaseStream.CopyToAsync(error);
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            await process.WaitForExitAsync(guard.Token);
            await Task.WhenAll(stdout, stderr).WaitAsync(guard.Token);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));
            }
            await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(60));
        }
        return new Child
        {
            Command = command, ProcessId = process.Id, ExitCode = process.ExitCode,
            Stdout = Encoding.UTF8.GetString(output.ToArray()), Stderr = Encoding.UTF8.GetString(error.ToArray()),
            StdoutBase64 = Convert.ToBase64String(output.ToArray()), StderrBase64 = Convert.ToBase64String(error.ToArray())
        };
    }

    private const string ConsoleProbeScript = """
        Add-Type -TypeDefinition @'
        using System;
        using System.Runtime.InteropServices;
        public static class ConsoleState {
            [DllImport("kernel32.dll")] public static extern uint GetErrorMode();
            [DllImport("kernel32.dll")] public static extern IntPtr GetConsoleWindow();
            [DllImport("kernel32.dll")] public static extern uint GetConsoleProcessList(uint[] ids, uint count);
            [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr window);
        }
        '@
        $window = [ConsoleState]::GetConsoleWindow()
        [pscustomobject]@{
            hasConsole = ([ConsoleState]::GetConsoleProcessList([uint32[]]::new(1), 1) -gt 0)
            consoleVisible = [ConsoleState]::IsWindowVisible($window)
            errorMode = [ConsoleState]::GetErrorMode()
        } | ConvertTo-Json -Compress
        """;

    private static readonly string DescendantScript = ConsoleProbeScript + "\n" + """
        # MCG_ALLOW_DEFAULT_WINDOW_SETTINGS_PROBE: prove the inherited windowless console contract.
        $psi = [System.Diagnostics.ProcessStartInfo]::new($env:MCG_DESCENDANT_SHELL)
        $psi.UseShellExecute = $false
        $psi.RedirectStandardInput = $true
        $psi.RedirectStandardOutput = $true
        $psi.RedirectStandardError = $true
        $psi.ArgumentList.Add('-NoProfile')
        $psi.ArgumentList.Add('-NonInteractive')
        $psi.ArgumentList.Add('-File')
        $psi.ArgumentList.Add($env:MCG_DESCENDANT_SCRIPT)
        $grandchild = [System.Diagnostics.Process]::Start($psi)
        $grandchild.StandardInput.Close()
        $stdout = $grandchild.StandardOutput.ReadToEndAsync()
        $stderr = $grandchild.StandardError.ReadToEndAsync()
        $grandchild.WaitForExit()
        [Console]::Out.Write($stdout.GetAwaiter().GetResult())
        [Console]::Error.Write($stderr.GetAwaiter().GetResult())
        exit $grandchild.ExitCode
        """;

    private sealed class Report
    {
        public string? PowerShellExecutable { get; set; }
        public string SwitchAtStartup { get; init; } = "";
        public long ConsoleWindow { get; init; }
        public uint ConsoleProcessCount { get; init; }
        public List<Arm> Arms { get; } = [];
        public string? Error { get; set; }
    }

    private sealed class Arm
    {
        public string Mode { get; init; } = "";
        public int LaunchCount { get; init; }
        public int TotalConhosts { get; set; }
        public int[] ExitCodes { get; set; } = [];
        public OutputMatch[] StdoutMatches { get; set; } = [];
        public bool HookInstalled { get; init; }
        public string Desktop { get; init; } = "";
        public List<WindowEvent> WindowEvents { get; } = [];
        public int ShownWindowEvents => WindowEvents.Count(e => e.HarnessCaused && e.Kind == 0x8002);
        public int ForegroundEvents => WindowEvents.Count(e => e.HarnessCaused && e.Kind == 3);
        public List<string> HookErrors { get; } = [];
        public uint CodePageBefore { get; set; }
        public uint CodePageAfter { get; set; }
        public List<Child> Children { get; } = [];
        public List<Child> StartChildren { get; } = [];
        public Child HeldProbe { get; set; } = new();
        public Child Descendant { get; set; } = new();
        public Child CodePageChild { get; set; } = new();
    }

    private sealed class Child
    {
        public string Command { get; init; } = "";
        public int ProcessId { get; init; }
        public int ExitCode { get; set; }
        public string Stdout { get; set; } = "";
        public string Stderr { get; set; } = "";
        public string StdoutBase64 { get; set; } = "";
        public string StderrBase64 { get; set; } = "";
        public uint TotalProcesses { get; set; }
        public int NonConhostProcesses { get; set; }
        public int ConhostCount { get; set; }
        public int UnclassifiedProcesses { get; set; }
        public Image[] Images { get; set; } = [];
        public Image[] HeldImages { get; set; } = [];
    }

    private sealed record Image(int ProcessId, string Path, bool IsConhost);
    private sealed record OutputMatch(string Command, bool? MatchesOff);
    private sealed record WindowEvent(uint Kind, long Window, int ProcessId, long? StartedAt, int? ParentId, long? ParentStartedAt, string? Image,
        bool HarnessCaused);

    private sealed class WindowEvents : IDisposable
    {
        private const uint DrainMessage = 0x8001;
        private readonly object _sync = new();
        private readonly Thread _thread;
        private readonly ManualResetEventSlim _ready = new();
        private readonly Dictionary<int, long> _roots = [];
        private readonly List<SafeFileHandle> _jobs = [];
        private readonly Windows.WinEventCallback _callback;
        private readonly long _hostStartedAt;
        private TaskCompletionSource? _drained;
        private uint _threadId;
        private Arm? _arm;
        private Exception? _failure;
        internal bool Installed { get; private set; }
        internal string Desktop { get; private set; } = "";

        internal WindowEvents()
        {
            using var host = Process.GetCurrentProcess();
            _hostStartedAt = host.StartTime.ToUniversalTime().Ticks;
            _callback = OnEvent;
            _thread = new Thread(Pump) { IsBackground = true, Name = "conhost-experiment-window-events" };
            _thread.Start();
            if (!_ready.Wait(TimeSpan.FromSeconds(60))) throw new TimeoutException("Window hook thread did not publish readiness.");
            if (_failure is not null) throw new InvalidOperationException("Window hook installation failed.", _failure);
        }

        internal void BeginArm(Arm arm) { lock (_sync) _arm = arm; }

        internal void Register(Process process, OwnedProcessGroup? group)
        {
            lock (_sync)
            {
                _roots[process.Id] = process.StartTime.ToUniversalTime().Ticks;
                if (group is not null)
                {
                    if (!group.TryDuplicateAccountingHandle(out var job)) throw new InvalidOperationException("Could not retain child job for window attribution.");
                    _jobs.Add(job);
                }
            }
        }

        internal async Task EndArm()
        {
            Task completion;
            lock (_sync)
            {
                _drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                completion = _drained.Task;
                if (!Windows.PostThreadMessageW(_threadId, DrainMessage, UIntPtr.Zero, IntPtr.Zero))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            await completion.WaitAsync(TimeSpan.FromSeconds(60));
            lock (_sync)
            {
                // A Start-path event may have arrived before Register returned. Match its captured lifecycle identity.
                for (var index = 0; index < _arm!.WindowEvents.Count; index++)
                {
                    var e = _arm.WindowEvents[index];
                    if (e.StartedAt is { } started && _roots.TryGetValue(e.ProcessId, out var root) && root == started)
                        _arm.WindowEvents[index] = e with { HarnessCaused = true };
                    else if (IsInfrastructure(e.Image) && MatchesParent(e.ParentId, e.ParentStartedAt))
                        _arm.WindowEvents[index] = e with { HarnessCaused = true };
                }
                _arm = null;
                _roots.Clear();
                foreach (var job in _jobs) job.Dispose();
                _jobs.Clear();
            }
        }

        private void Pump()
        {
            IntPtr show = IntPtr.Zero, foreground = IntPtr.Zero;
            try
            {
                _threadId = Windows.GetCurrentThreadId();
                Windows.PeekMessageW(out _, IntPtr.Zero, 0, 0, 0);
                var desktopName = new char[256];
                if (!Windows.GetUserObjectInformationW(Windows.GetThreadDesktop(_threadId), 2, desktopName, 512, out _))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                Desktop = new string(desktopName).TrimEnd('\0');
                show = Windows.SetWinEventHook(0x8002, 0x8002, IntPtr.Zero, _callback, 0, 0, 0);
                foreground = Windows.SetWinEventHook(3, 3, IntPtr.Zero, _callback, 0, 0, 0);
                Installed = show != IntPtr.Zero && foreground != IntPtr.Zero;
                if (!Installed) throw new Win32Exception(Marshal.GetLastWin32Error(), "SetWinEventHook failed.");
                _ready.Set();
                int result;
                while ((result = Windows.GetMessageW(out var message, IntPtr.Zero, 0, 0)) > 0)
                {
                    if (message.MessageId == DrainMessage) { lock (_sync) _drained?.TrySetResult(); }
                    Windows.TranslateMessage(ref message);
                    Windows.DispatchMessageW(ref message);
                }
                if (result == -1) throw new Win32Exception(Marshal.GetLastWin32Error(), "Window event message pump failed.");
            }
            catch (Exception exception)
            {
                lock (_sync)
                {
                    _failure = exception;
                    _arm?.HookErrors.Add(exception.ToString());
                    _drained?.TrySetException(exception);
                }
            }
            finally
            {
                if (show != IntPtr.Zero) Windows.UnhookWinEvent(show);
                if (foreground != IntPtr.Zero) Windows.UnhookWinEvent(foreground);
                _ready.Set();
            }
        }

        private void OnEvent(IntPtr hook, uint kind, IntPtr window, int objectId, int childId, uint eventThread, uint time)
        {
            if (window == IntPtr.Zero || (kind == 0x8002 &&
                (objectId != 0 || childId != 0 || !Windows.IsWindowVisible(window) || Windows.GetAncestor(window, 2) != window))) return;
            lock (_sync)
            {
                if (_arm is null) return;
                try
                {
                    if (Windows.GetWindowThreadProcessId(window, out var rawId) == 0)
                        throw new InvalidOperationException("Window owner disappeared before it could be attributed.");
                    var id = checked((int)rawId);
                    using var process = Process.GetProcessById(id);
                    var started = process.StartTime.ToUniversalTime().Ticks;
                    var image = process.MainModule?.FileName;
                    var parent = ProcessParentIdResolver.TryGetParentProcessId(id);
                    var caused = _roots.TryGetValue(id, out var root) && root == started;
                    foreach (var job in _jobs)
                    {
                        if (!Windows.IsProcessInJob(process.Handle, job, out var member))
                            throw new Win32Exception(Marshal.GetLastWin32Error(), "Window-owner job query failed.");
                        caused |= member;
                    }
                    long? parentStarted = null;
                    if (IsInfrastructure(image) && parent is { } parentId)
                    {
                        try
                        {
                            using var parentProcess = Process.GetProcessById(parentId);
                            parentStarted = parentProcess.StartTime.ToUniversalTime().Ticks;
                        }
                        catch (ArgumentException) { }
                        catch (InvalidOperationException) { }
                        catch (Win32Exception) { }
                    }
                    caused |= IsInfrastructure(image) && MatchesParent(parent, parentStarted);
                    _arm.WindowEvents.Add(new WindowEvent(kind, window.ToInt64(), id, started, parent, parentStarted, image, caused));
                }
                catch (Exception exception)
                {
                    // Missing identity is a measurement failure, not evidence of zero harness events.
                    _arm.HookErrors.Add(exception.ToString());
                }
            }
        }

        private static bool IsInfrastructure(string? image) =>
            ProcessObservationRoles.IsWindowsConsoleInfrastructure(image) ||
            string.Equals(Path.GetFileName(image), "WerFault.exe", StringComparison.OrdinalIgnoreCase);

        private bool MatchesParent(int? parent, long? started) => parent is { } id && started is { } birth &&
            (id == Environment.ProcessId ? birth == _hostStartedAt : _roots.TryGetValue(id, out var root) && birth == root);

        public void Dispose()
        {
            if (_thread.IsAlive && !Windows.PostThreadMessageW(_threadId, 0x0012, UIntPtr.Zero, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            if (!_thread.Join(TimeSpan.FromSeconds(60))) throw new TimeoutException("Window hook thread did not stop.");
            foreach (var job in _jobs) job.Dispose();
            _ready.Dispose();
        }
    }

    private static class Windows
    {
        internal delegate void WinEventCallback(IntPtr hook, uint kind, IntPtr window, int objectId, int childId, uint thread, uint time);
        [DllImport("kernel32.dll")] internal static extern IntPtr GetConsoleWindow();
        [DllImport("kernel32.dll")] internal static extern uint GetConsoleProcessList([Out] uint[] ids, uint count);
        [DllImport("kernel32.dll")] internal static extern uint GetConsoleOutputCP();
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool SetConsoleOutputCP(uint codePage);
        [DllImport("kernel32.dll")] internal static extern uint GetCurrentThreadId();
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern bool QueryFullProcessImageNameW(IntPtr process, uint flags, StringBuilder path, ref uint capacity);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool IsProcessInJob(IntPtr process, SafeFileHandle job, out bool member);
        [DllImport("user32.dll")] internal static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll")] internal static extern IntPtr GetAncestor(IntPtr window, uint flags);
        [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
        [DllImport("user32.dll", SetLastError = true)] internal static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventCallback callback, uint process, uint thread, uint flags);
        [DllImport("user32.dll")] internal static extern bool UnhookWinEvent(IntPtr hook);
        [DllImport("user32.dll", SetLastError = true)] internal static extern bool PostThreadMessageW(uint thread, uint message, UIntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] internal static extern int GetMessageW(out Message message, IntPtr window, uint min, uint max);
        [DllImport("user32.dll")] internal static extern bool PeekMessageW(out Message message, IntPtr window, uint min, uint max, uint remove);
        [DllImport("user32.dll")] internal static extern bool TranslateMessage(ref Message message);
        [DllImport("user32.dll")] internal static extern IntPtr DispatchMessageW(ref Message message);
        [DllImport("user32.dll", SetLastError = true)] internal static extern IntPtr GetThreadDesktop(uint thread);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern bool GetUserObjectInformationW(IntPtr handle, int index, [Out] char[] value, uint length, out uint needed);
        [StructLayout(LayoutKind.Sequential)] internal struct Message
        {
            internal IntPtr Window;
            internal uint MessageId;
            internal UIntPtr WParam;
            internal IntPtr LParam;
            internal uint Time;
            internal int X, Y;
            internal uint Private;
        }
    }
}
