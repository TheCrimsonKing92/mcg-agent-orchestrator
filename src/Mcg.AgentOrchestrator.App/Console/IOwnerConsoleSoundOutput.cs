namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal interface IOwnerConsoleSoundOutput
{
    void PlaySystemSound(uint messageBeepType);
    bool TryPlayFile(string path);
}
