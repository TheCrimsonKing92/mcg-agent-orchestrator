using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static partial class ProcessTreeGuiSuppression
{
    internal const uint FailCriticalErrors = 0x0001;
    internal const uint NoGpFaultErrorBox = 0x0002;
    internal const uint NoOpenFileErrorBox = 0x8000;
    internal const uint SuppressedErrorModeFlags = FailCriticalErrors | NoGpFaultErrorBox | NoOpenFileErrorBox;

    private static readonly object WindowsLaunchLock = new();

    internal sealed class ConsoleSpawnScope : IDisposable
    {
        private readonly Action? _onDispose;
        private readonly uint? _originalErrorMode;
        private readonly LaunchLockEntryPoint? _entryPoint;
        private readonly long _waitTicks;
        private readonly long _acquiredTimestamp;
        private long _childCreateTicks;
        private bool _disposed;

        internal ConsoleSpawnScope(
            bool childConsolePolicyApplied,
            uint childCreationFlags = 0,
            Action? onDispose = null,
            uint? originalErrorMode = null,
            LaunchLockEntryPoint? entryPoint = null,
            long waitTicks = 0,
            long acquiredTimestamp = 0)
        {
            ChildConsolePolicyApplied = childConsolePolicyApplied;
            ChildCreationFlags = childCreationFlags;
            _onDispose = onDispose;
            _originalErrorMode = originalErrorMode;
            _entryPoint = entryPoint;
            _waitTicks = waitTicks;
            _acquiredTimestamp = acquiredTimestamp;
        }

        internal bool ChildConsolePolicyApplied { get; }

        internal uint ChildCreationFlags { get; }

        internal void RecordChildCreateTicks(long ticks) => _childCreateTicks = ticks;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            // Console ownership belongs to the child created by this scope. The
            // launcher never changes its console membership or standard handles.
            _disposed = true;
            long holdEnded = 0;
            try
            {
                try
                {
                    if (_originalErrorMode is { } originalErrorMode)
                    {
                        _ = Windows.SetErrorMode(originalErrorMode);
                    }
                }
                finally
                {
                    holdEnded = Stopwatch.GetTimestamp();
                }
                _onDispose?.Invoke();
            }
            finally
            {
                if (_entryPoint is { } entryPoint)
                {
                    LaunchLockTelemetry.Record(
                        entryPoint,
                        _waitTicks,
                        holdEnded - _acquiredTimestamp,
                        _childCreateTicks);
                }
            }
        }
    }

    internal static ConsoleSpawnScope AcquireConsoleForChildSpawn()
    {
        if (!OperatingSystem.IsWindows())
        {
            return new ConsoleSpawnScope(childConsolePolicyApplied: false);
        }

        var childConsolePolicy = ChildConsoleLaunchPolicy.Prepare();
        var waitStarted = Stopwatch.GetTimestamp();
        Monitor.Enter(WindowsLaunchLock);
        var acquired = Stopwatch.GetTimestamp();
        try
        {
            return new ConsoleSpawnScope(
                childConsolePolicyApplied: childConsolePolicy.ChildCreateNoWindow,
                childConsolePolicy.ChildCreationFlags,
                onDispose: () => Monitor.Exit(WindowsLaunchLock),
                entryPoint: LaunchLockEntryPoint.AcquireConsoleForChildSpawn,
                waitTicks: acquired - waitStarted,
                acquiredTimestamp: acquired);
        }
        catch
        {
            Monitor.Exit(WindowsLaunchLock);
            throw;
        }
    }

    internal static ConsoleSpawnScope AcquireSuppressedChildSpawn(bool requestOwnConsole = false)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new ConsoleSpawnScope(childConsolePolicyApplied: false);
        }

        var childConsolePolicy = ChildConsoleLaunchPolicy.Prepare(requestOwnConsole);
        var waitStarted = Stopwatch.GetTimestamp();
        Monitor.Enter(WindowsLaunchLock);
        var acquired = Stopwatch.GetTimestamp();
        var originalErrorMode = Windows.GetErrorMode();
        try
        {
            _ = Windows.SetErrorMode(originalErrorMode | SuppressedErrorModeFlags);
            return new ConsoleSpawnScope(
                childConsolePolicyApplied: childConsolePolicy.ChildCreateNoWindow,
                childConsolePolicy.ChildCreationFlags,
                onDispose: () => Monitor.Exit(WindowsLaunchLock),
                originalErrorMode: originalErrorMode,
                entryPoint: LaunchLockEntryPoint.AcquireSuppressedChildSpawn,
                waitTicks: acquired - waitStarted,
                acquiredTimestamp: acquired);
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

        var waitStarted = Stopwatch.GetTimestamp();
        Monitor.Enter(WindowsLaunchLock);
        var acquired = Stopwatch.GetTimestamp();
        var originalErrorMode = Windows.GetErrorMode();
        try
        {
            _ = Windows.SetErrorMode(originalErrorMode | SuppressedErrorModeFlags);
            return new ConsoleSpawnScope(
                childConsolePolicyApplied: false,
                onDispose: () => Monitor.Exit(WindowsLaunchLock),
                originalErrorMode: originalErrorMode,
                entryPoint: LaunchLockEntryPoint.AcquireErrorModeForChildSpawn,
                waitTicks: acquired - waitStarted,
                acquiredTimestamp: acquired);
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
        var waitStarted = Stopwatch.GetTimestamp();
        long acquired = 0;
        long holdEnded = 0;
        try
        {
            lock (WindowsLaunchLock)
            {
                acquired = Stopwatch.GetTimestamp();
                try
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
                finally
                {
                    holdEnded = Stopwatch.GetTimestamp();
                }
            }
        }
        finally
        {
            if (acquired != 0)
            {
                LaunchLockTelemetry.Record(
                    LaunchLockEntryPoint.Start,
                    acquired - waitStarted,
                    holdEnded - acquired);
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
