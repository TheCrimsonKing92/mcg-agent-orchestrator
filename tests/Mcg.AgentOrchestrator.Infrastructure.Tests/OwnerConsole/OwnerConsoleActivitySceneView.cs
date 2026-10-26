using Mcg.AgentOrchestrator.App.OwnerConsole;
using Terminal.Gui.App;

internal static class OwnerConsoleActivitySceneView
{
    internal static OwnerConsoleFullScreenView Create(IApplication app, OwnerConsoleScreenController controller,
        TimeProvider clock, OwnerConsoleInputDiagnostics? diagnostics) =>
        new(app, controller, () => Task.CompletedTask, clock: clock, diagnostics: diagnostics);
}
