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
            var originalWindowStyle = startInfo.WindowStyle;

            try
            {
                _ = Windows.SetErrorMode(originalErrorMode | SuppressedErrorModeFlags);

                // Give console grandchildren a hidden console to inherit. CreateNoWindow would suppress
                // only the root process console, causing later console descendants to allocate visible ones.
                startInfo.CreateNoWindow = false;
                startInfo.WindowStyle = ProcessWindowStyle.Hidden;

                return Process.Start(startInfo)
                    ?? throw new InvalidOperationException($"Failed to start process: {startInfo.FileName}");
            }
            finally
            {
                startInfo.CreateNoWindow = originalCreateNoWindow;
                startInfo.WindowStyle = originalWindowStyle;
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
