namespace Mcg.AgentOrchestrator.Infrastructure;

internal enum TerminalBuildRootSkipReason
{
    GoalNotStored,
    GoalPrefixAmbiguous,
    GoalNotTerminal,
    NotContained,
    LeaseUnreadable,
    RegisteredOwnedRoot,
    WithinWindow,
    OwnerLive,
    BudgetExhausted,
    Held,
    RemovalFailed
}
