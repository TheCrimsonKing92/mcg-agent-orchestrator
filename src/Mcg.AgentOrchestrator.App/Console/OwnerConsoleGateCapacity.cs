namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal sealed record OwnerConsoleGateCapacity(IReadOnlyList<OwnerConsoleGateExecutorSlots> Executors, int LocalBudget);
