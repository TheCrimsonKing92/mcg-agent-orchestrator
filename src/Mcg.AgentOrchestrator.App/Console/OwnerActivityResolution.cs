namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal sealed record OwnerActivityResolution(DateTimeOffset At, bool AutomaticRetry = false, string? Actor = null);
