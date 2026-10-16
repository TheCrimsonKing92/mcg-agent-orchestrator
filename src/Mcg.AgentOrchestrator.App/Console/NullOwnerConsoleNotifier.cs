namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal sealed class NullOwnerConsoleNotifier : IOwnerConsoleNotifier
{
    public void Notify(OwnerConsoleNotificationTier tier) { }
}
