using System.Runtime.InteropServices;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed class WindowsForegroundLockReader : IForegroundLockReader
{
    private const uint SpiGetForegroundLockTimeout = 0x2000;

    public ForegroundLockReading Read()
    {
        if (!OperatingSystem.IsWindows()) return ForegroundLockReading.Unavailable("unsupported-platform");
        try
        {
            uint timeoutMs = 0;
            if (!SystemParametersInfo(SpiGetForegroundLockTimeout, 0, ref timeoutMs, 0))
                return ForegroundLockReading.Unavailable($"spi-error-{Marshal.GetLastWin32Error()}");
            return ForegroundLockReading.Available(timeoutMs, Environment.OSVersion.Version.Build);
        }
        catch (Exception exception)
        {
            return ForegroundLockReading.Unavailable($"foreground-lock-error:{exception.GetType().Name}");
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint action, uint parameter, ref uint value, uint flags);
}
