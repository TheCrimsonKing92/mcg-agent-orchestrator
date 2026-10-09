using Terminal.Gui.Input;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal static class OwnerConsolePaneNavigator
{
    internal static int? Target(Key key, int current, int count, int pageHeight)
    {
        if (key != Key.Home && key != Key.End && key != Key.PageUp && key != Key.PageDown) return null;
        if (count == 0) return -1;
        var target = key == Key.Home ? 0 : key == Key.End ? count - 1 :
            Math.Max(0, current) + (key == Key.PageUp ? -1 : 1) * Math.Max(1, pageHeight);
        return Math.Clamp(target, 0, count - 1);
    }
}
