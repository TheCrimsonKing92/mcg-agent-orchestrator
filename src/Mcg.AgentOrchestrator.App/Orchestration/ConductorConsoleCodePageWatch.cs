using System.Runtime.InteropServices;

namespace Mcg.AgentOrchestrator.App.Orchestration;

/// <summary>Records console code-page changes for one loop without changing console state.</summary>
internal sealed class ConductorConsoleCodePageWatch
{
    private readonly Func<(uint Input, uint Output)?> _readCodePages;
    private (uint Input, uint Output)? _lastRecorded;

    internal ConductorConsoleCodePageWatch(Func<(uint Input, uint Output)?>? readCodePages = null)
    {
        _readCodePages = readCodePages ?? ReadCodePages;
        _lastRecorded = _readCodePages();
    }

    internal IReadOnlyList<string> Observe()
    {
        // A loop started without a console has no baseline and remains disabled.
        if (_lastRecorded is not { } before || _readCodePages() is not { } after || before == after)
            return [];

        _lastRecorded = after;
        return [$"CONSOLE_CODE_PAGE_CHANGED detail=\"input={before.Input}->{after.Input} output={before.Output}->{after.Output}\""];
    }

    private static (uint Input, uint Output)? ReadCodePages()
    {
        if (!OperatingSystem.IsWindows() || Windows.GetConsoleProcessList(new uint[1], 1) == 0)
            return null;
        return (Windows.GetConsoleCP(), Windows.GetConsoleOutputCP());
    }

    private static class Windows
    {
        [DllImport("kernel32.dll")]
        internal static extern uint GetConsoleProcessList([Out] uint[] processList, uint processCount);

        [DllImport("kernel32.dll")]
        internal static extern uint GetConsoleCP();

        [DllImport("kernel32.dll")]
        internal static extern uint GetConsoleOutputCP();
    }
}
