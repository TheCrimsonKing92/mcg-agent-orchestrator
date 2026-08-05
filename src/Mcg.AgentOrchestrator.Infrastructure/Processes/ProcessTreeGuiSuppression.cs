using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class ProcessTreeGuiSuppression
{
    internal const uint FailCriticalErrors = 0x0001;
    internal const uint NoGpFaultErrorBox = 0x0002;
    internal const uint NoOpenFileErrorBox = 0x8000;
    internal const uint SuppressedErrorModeFlags = FailCriticalErrors | NoGpFaultErrorBox | NoOpenFileErrorBox;

    private static readonly object WindowsLaunchLock = new();

    internal sealed class ConsoleSpawnScope : IDisposable
    {
        private readonly IDisposable? _hiddenConsole;
        private readonly Action? _onDispose;
        private bool _disposed;

        internal ConsoleSpawnScope(
            bool hiddenConsoleAcquired,
            IDisposable? hiddenConsole = null,
            Action? onDispose = null)
        {
            HiddenConsoleAcquired = hiddenConsoleAcquired;
            _hiddenConsole = hiddenConsole;
            _onDispose = onDispose;
        }

        internal bool HiddenConsoleAcquired { get; }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                _hiddenConsole?.Dispose();
            }
            finally
            {
                _disposed = true;
                _onDispose?.Invoke();
            }
        }
    }

    internal static ConsoleSpawnScope AcquireConsoleForChildSpawn()
    {
        if (!OperatingSystem.IsWindows())
        {
            return new ConsoleSpawnScope(hiddenConsoleAcquired: false);
        }

        Monitor.Enter(WindowsLaunchLock);
        try
        {
            var hiddenConsole = Windows.GetConsoleWindow() == IntPtr.Zero
                ? Windows.CreateHiddenConsoleAttachmentReplacingNonWindowConsole()
                : null;
            return new ConsoleSpawnScope(
                hiddenConsoleAcquired: hiddenConsole is not null,
                hiddenConsole,
                onDispose: () => Monitor.Exit(WindowsLaunchLock));
        }
        catch
        {
            Monitor.Exit(WindowsLaunchLock);
            throw;
        }
    }

    internal static ConsoleSpawnScope AcquireSuppressedChildSpawn()
    {
        if (!OperatingSystem.IsWindows())
        {
            return new ConsoleSpawnScope(hiddenConsoleAcquired: false);
        }

        Monitor.Enter(WindowsLaunchLock);
        var originalErrorMode = Windows.GetErrorMode();
        try
        {
            _ = Windows.SetErrorMode(originalErrorMode | SuppressedErrorModeFlags);
            var hiddenConsole = Windows.GetConsoleWindow() == IntPtr.Zero
                ? Windows.CreateHiddenConsoleAttachmentReplacingNonWindowConsole()
                : null;
            return new ConsoleSpawnScope(
                hiddenConsoleAcquired: hiddenConsole is not null,
                hiddenConsole,
                onDispose: () =>
                {
                    _ = Windows.SetErrorMode(originalErrorMode);
                    Monitor.Exit(WindowsLaunchLock);
                });
        }
        catch
        {
            _ = Windows.SetErrorMode(originalErrorMode);
            Monitor.Exit(WindowsLaunchLock);
            throw;
        }
    }

    public static Process Start(ProcessStartInfo startInfo)
    {
        ArgumentNullException.ThrowIfNull(startInfo);

        if (!OperatingSystem.IsWindows())
        {
            return Process.Start(startInfo)
                ?? throw new InvalidOperationException($"Failed to start process: {startInfo.FileName}");
        }

        if (startInfo.UseShellExecute)
        {
            throw new InvalidOperationException("Process tree GUI suppression requires UseShellExecute=false.");
        }

        lock (WindowsLaunchLock)
        {
            var originalErrorMode = Windows.GetErrorMode();
            var originalCreateNoWindow = startInfo.CreateNoWindow;
            Windows.HiddenConsoleAttachment? hiddenConsole = null;

            try
            {
                _ = Windows.SetErrorMode(originalErrorMode | SuppressedErrorModeFlags);

                if (Windows.GetConsoleWindow() == IntPtr.Zero)
                {
                    hiddenConsole = Windows.CreateHiddenConsoleAttachmentReplacingNonWindowConsole();
                }

                // The root must inherit the launcher's console so arbitrary console grandchildren do
                // not allocate their own visible console later in the tree.
                startInfo.CreateNoWindow = false;

                return Process.Start(startInfo)
                    ?? throw new InvalidOperationException($"Failed to start process: {startInfo.FileName}");
            }
            finally
            {
                startInfo.CreateNoWindow = originalCreateNoWindow;
                hiddenConsole?.Dispose();
                _ = Windows.SetErrorMode(originalErrorMode);
            }
        }
    }

    private static class Windows
    {
        [DllImport("kernel32.dll")]
        internal static extern uint GetErrorMode();

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern uint SetErrorMode(uint uMode);

        [DllImport("kernel32.dll")]
        internal static extern IntPtr GetConsoleWindow();

        internal sealed class HiddenConsoleAttachment : IDisposable
        {
            private readonly bool _reattachParentConsoleOnDispose;

            public HiddenConsoleAttachment(bool reattachParentConsoleOnDispose)
            {
                _reattachParentConsoleOnDispose = reattachParentConsoleOnDispose;
            }

            public void Dispose()
            {
                _ = FreeConsole();
                if (_reattachParentConsoleOnDispose)
                {
                    _ = AttachConsole(AttachParentProcess);
                }
            }
        }

        internal static HiddenConsoleAttachment CreateHiddenConsoleAttachmentReplacingNonWindowConsole()
        {
            var detachedExistingConsole = FreeConsole();
            var processInformation = default(ProcessInformation);
            var startupInfo = new StartupInfo
            {
                Cb = Marshal.SizeOf<StartupInfo>(),
                DwFlags = StartfUseShowWindow,
                WShowWindow = SwHide
            };
            var systemDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System);
            var hostPath = Path.Combine(systemDirectory, "cmd.exe");
            if (!File.Exists(hostPath))
            {
                throw new InvalidOperationException($"Cannot create hidden console host because cmd.exe was not found at '{hostPath}'.");
            }

            var commandLine = new StringBuilder($"\"{hostPath}\" /d /q /k cd .");
            var attached = false;
            try
            {
                if (!CreateProcessW(
                    hostPath,
                    commandLine,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    inheritHandles: false,
                    CreateNewConsole | CreateSuspended,
                    IntPtr.Zero,
                    systemDirectory,
                    ref startupInfo,
                    out processInformation))
                {
                    ThrowLastWin32Error("CreateProcessW failed while creating a hidden console host.");
                }

                if (ResumeThread(processInformation.Thread) == uint.MaxValue)
                {
                    ThrowLastWin32Error($"ResumeThread failed for hidden console host pid {processInformation.ProcessId}.");
                }

                if (!AttachConsoleWithRetry(processInformation.ProcessId))
                {
                    ThrowLastWin32Error($"AttachConsole failed for hidden console host pid {processInformation.ProcessId}.");
                }

                attached = true;
                var consoleWindow = GetConsoleWindow();
                if (consoleWindow == IntPtr.Zero)
                {
                    throw new InvalidOperationException("AttachConsole succeeded but GetConsoleWindow returned zero for the hidden console.");
                }

                if (IsWindowVisible(consoleWindow))
                {
                    throw new InvalidOperationException("Hidden console host created a visible console window.");
                }

                return new HiddenConsoleAttachment(detachedExistingConsole);
            }
            catch
            {
                if (attached)
                {
                    _ = FreeConsole();
                }

                if (detachedExistingConsole)
                {
                    _ = AttachConsole(AttachParentProcess);
                }

                throw;
            }
            finally
            {
                if (processInformation.Process != IntPtr.Zero)
                {
                    _ = TerminateProcess(processInformation.Process, 0);
                    _ = CloseHandle(processInformation.Process);
                }

                if (processInformation.Thread != IntPtr.Zero)
                {
                    _ = CloseHandle(processInformation.Thread);
                }
            }
        }

        private static void ThrowLastWin32Error(string message)
        {
            var error = Marshal.GetLastWin32Error();
            throw new Win32Exception(error, $"{message} Win32Error={error}.");
        }

        private static bool AttachConsoleWithRetry(int processId)
        {
            const int Attempts = 200;
            for (var attempt = 0; attempt < Attempts; attempt++)
            {
                if (AttachConsole(processId))
                {
                    return true;
                }

                if (Marshal.GetLastWin32Error() != ErrorInvalidHandle)
                {
                    return false;
                }

                Thread.Sleep(10);
            }

            return false;
        }

        private const uint CreateSuspended = 0x00000004;
        private const uint CreateNewConsole = 0x00000010;
        private const uint StartfUseShowWindow = 0x00000001;
        private const int ErrorInvalidHandle = 6;
        private const int AttachParentProcess = -1;
        private const short SwHide = 0;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CreateProcessW(
            string? applicationName,
            StringBuilder commandLine,
            IntPtr processAttributes,
            IntPtr threadAttributes,
            bool inheritHandles,
            uint creationFlags,
            IntPtr environment,
            string? currentDirectory,
            ref StartupInfo startupInfo,
            out ProcessInformation processInformation);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AttachConsole(int dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool FreeConsole();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint ResumeThread(IntPtr hThread);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct StartupInfo
        {
            public int Cb;
            public IntPtr Reserved;
            public IntPtr Desktop;
            public IntPtr Title;
            public int X;
            public int Y;
            public int XSize;
            public int YSize;
            public int XCountChars;
            public int YCountChars;
            public int FillAttribute;
            public uint DwFlags;
            public short WShowWindow;
            public short Reserved2;
            public IntPtr Reserved2Pointer;
            public IntPtr StdInput;
            public IntPtr StdOutput;
            public IntPtr StdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ProcessInformation
        {
            public IntPtr Process;
            public IntPtr Thread;
            public int ProcessId;
            public int ThreadId;
        }
    }
}
