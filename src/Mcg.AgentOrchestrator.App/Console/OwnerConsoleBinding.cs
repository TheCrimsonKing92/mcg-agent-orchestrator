namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal enum OwnerConsoleScope { Global, Decisions, GoalDetail, EpicView, GateView }
internal enum OwnerConsoleEffect { View, ConsoleLocal, External }
internal enum OwnerConsoleCommandOwner { View, Controller }

// A typed intent and its optional key alias share one meaning and safety contract.
internal sealed record OwnerConsoleBinding(
    string Command, string Usage, char? Key, OwnerConsoleScope Scope,
    OwnerConsoleEffect Effect, OwnerConsoleCommandOwner Owner, bool Confirms,
    string Noun, string? HintLabel, string? KeyHelp, string CommandHelp);
