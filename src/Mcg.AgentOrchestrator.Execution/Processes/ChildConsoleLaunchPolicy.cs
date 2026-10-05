using System.Runtime.InteropServices;

namespace Mcg.AgentOrchestrator.Infrastructure;

/// <summary>
/// Selects the console policy for one child launch without changing the launcher's
/// console attachment or standard handles.
/// </summary>
internal sealed record ChildConsoleLaunchPolicy(bool LauncherHasConsoleWindow)
{
    internal const uint CreateNoWindow = 0x08000000;

    internal uint ChildCreationFlags => LauncherHasConsoleWindow ? 0u : CreateNoWindow;

    internal bool ChildCreateNoWindow => !LauncherHasConsoleWindow;

    // Test-only injection point. It is intentionally evaluated before the process-wide
    // error-mode lock so preparation cannot serialize unrelated launches.
    internal static Action? PrepareDelayHookForTests { get; set; }

    internal static ChildConsoleLaunchPolicy Prepare()
    {
        PrepareDelayHookForTests?.Invoke();
        return new ChildConsoleLaunchPolicy(Windows.GetConsoleWindow() != IntPtr.Zero);
    }

    private static class Windows
    {
        [DllImport("kernel32.dll")]
        internal static extern IntPtr GetConsoleWindow();
    }
}
