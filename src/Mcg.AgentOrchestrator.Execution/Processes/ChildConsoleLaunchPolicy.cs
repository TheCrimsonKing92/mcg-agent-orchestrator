using System.Runtime.InteropServices;

namespace Mcg.AgentOrchestrator.Infrastructure;

/// <summary>
/// Selects the console policy for one child launch without changing the launcher's
/// console attachment or standard handles.
/// </summary>
internal sealed record ChildConsoleLaunchPolicy(bool LauncherHasConsoleWindow, bool InheritLauncherConsole = false)
{
    internal const uint CreateNoWindow = 0x08000000;

    internal uint ChildCreationFlags => LauncherHasConsoleWindow || InheritLauncherConsole ? 0u : CreateNoWindow;

    internal bool ChildCreateNoWindow => !LauncherHasConsoleWindow && !InheritLauncherConsole;

    internal const ChildConsoleExperiment DefaultExperiment = ChildConsoleExperiment.InheritWindowlessConsole;
    internal const string OffSwitchVariable = "MCG_CHILD_CONSOLE_INHERIT";

    internal static ChildConsoleExperiment ExperimentForTests { get; set; } =
        ParseOffSwitch(Environment.GetEnvironmentVariable(OffSwitchVariable));

    internal static ChildConsoleExperiment ParseOffSwitch(string? value) =>
        string.Equals(value, "off", StringComparison.OrdinalIgnoreCase) ? ChildConsoleExperiment.Off : DefaultExperiment;

    // Test-only injection point. It is intentionally evaluated before the process-wide
    // error-mode lock so preparation cannot serialize unrelated launches.
    internal static Action? PrepareDelayHookForTests { get; set; }

    internal static ChildConsoleLaunchPolicy Prepare(bool requestOwnConsole = false)
    {
        PrepareDelayHookForTests?.Invoke();
        return Select(requestOwnConsole ? ChildConsoleExperiment.Off : ExperimentForTests, Windows.GetConsoleWindow() != IntPtr.Zero,
            static () => Windows.GetConsoleProcessList(new uint[1], 1) > 0);
    }

    internal static ChildConsoleLaunchPolicy Select(
        ChildConsoleExperiment experiment, bool launcherHasConsoleWindow, Func<bool> launcherAttachedToConsole) =>
        new(launcherHasConsoleWindow,
            experiment == ChildConsoleExperiment.InheritWindowlessConsole &&
            !launcherHasConsoleWindow && launcherAttachedToConsole());

    private static class Windows
    {
        [DllImport("kernel32.dll")]
        internal static extern IntPtr GetConsoleWindow();

        [DllImport("kernel32.dll")]
        internal static extern uint GetConsoleProcessList([Out] uint[] processList, uint processCount);
    }
}

internal enum ChildConsoleExperiment { Off, InheritWindowlessConsole }
