namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal sealed record OwnerEpicLoadResult(OwnerConsoleEpicViewModel? Model, string? Failure, bool Refreshing)
{
    internal OwnerConsoleEpicLoadState State => Failure is not null ? OwnerConsoleEpicLoadState.Failed :
        Model is null ? OwnerConsoleEpicLoadState.Unavailable : OwnerConsoleEpicLoadState.Loaded;
}
