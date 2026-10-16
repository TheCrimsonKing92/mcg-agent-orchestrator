namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal interface IOwnerConsoleNotifier
{
    void Notify(OwnerConsoleNotificationTier tier);
}
