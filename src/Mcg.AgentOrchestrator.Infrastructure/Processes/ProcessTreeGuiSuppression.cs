using System.Diagnostics;
using System.Runtime.InteropServices;

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
        private readonly Action? _onDispose;
        private bool _disposed;

        internal ConsoleSpawnScope(
            bool childConsolePolicyApplied,
            uint childCreationFlags = 0,
            Action? onDispose = null)
        {
            ChildConsolePolicyApplied = childConsolePolicyApplied;
            ChildCreationFlags = childCreationFlags;
            _onDispose = onDispose;
        }

        internal bool ChildConsolePolicyApplied { get; }

        internal uint ChildCreationFlags { get; }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            // Console ownership belongs to the child created by this scope. The
            // launcher never changes its console membership or standard handles.
            _disposed = true;
            _onDispose?.Invoke();
        }
    }

    internal static ConsoleSpawnScope AcquireConsoleForChildSpawn()
    {
        if (!OperatingSystem.IsWindows())
        {
            return new ConsoleSpawnScope(childConsolePolicyApplied: false);
        }

        var childConsolePolicy = ChildConsoleLaunchPolicy.Prepare();
        Monitor.Enter(WindowsLaunchLock);
        try
        {
            return new ConsoleSpawnScope(
                childConsolePolicyApplied: childConsolePolicy.ChildCreateNoWindow,
                childConsolePolicy.ChildCreationFlags,
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
            return new ConsoleSpawnScope(childConsolePolicyApplied: false);
        }

        var childConsolePolicy = ChildConsoleLaunchPolicy.Prepare();
        Monitor.Enter(WindowsLaunchLock);
        var originalErrorMode = Windows.GetErrorMode();
        try
        {
            _ = Windows.SetErrorMode(originalErrorMode | SuppressedErrorModeFlags);
            return new ConsoleSpawnScope(
                childConsolePolicyApplied: childConsolePolicy.ChildCreateNoWindow,
                childConsolePolicy.ChildCreationFlags,
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

    internal static ConsoleSpawnScope AcquireErrorModeForChildSpawn()
    {
        if (!OperatingSystem.IsWindows())
        {
            return new ConsoleSpawnScope(childConsolePolicyApplied: false);
        }

        Monitor.Enter(WindowsLaunchLock);
        var originalErrorMode = Windows.GetErrorMode();
        try
        {
            _ = Windows.SetErrorMode(originalErrorMode | SuppressedErrorModeFlags);
            return new ConsoleSpawnScope(
                childConsolePolicyApplied: false,
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

        var childConsolePolicy = ChildConsoleLaunchPolicy.Prepare();
        lock (WindowsLaunchLock)
        {
            var originalErrorMode = Windows.GetErrorMode();
            var originalCreateNoWindow = startInfo.CreateNoWindow;

            try
            {
                _ = Windows.SetErrorMode(originalErrorMode | SuppressedErrorModeFlags);
                startInfo.CreateNoWindow = childConsolePolicy.ChildCreateNoWindow;

                return Process.Start(startInfo)
                    ?? throw new InvalidOperationException($"Failed to start process: {startInfo.FileName}");
            }
            finally
            {
                startInfo.CreateNoWindow = originalCreateNoWindow;
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

    }
}
