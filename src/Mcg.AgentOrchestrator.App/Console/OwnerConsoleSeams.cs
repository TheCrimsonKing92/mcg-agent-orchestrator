using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal interface IOwnerConsoleInput
{
    bool IsEditingLine { get; }
    ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken);
}

internal interface IOwnerConsoleOutput
{
    void Write(string text);
    void WriteLine(string text);
}

internal interface IConductEventSource : IAsyncDisposable
{
    ValueTask<OwnerConductEvent> ReadAsync(CancellationToken cancellationToken);
    DateTimeOffset? LastActivity { get; }
}

internal interface IOwnerQuestionSource
{
    Task<IReadOnlyList<OwnerQuestion>> ListOpenAsync(CancellationToken cancellationToken);
}

internal interface IOwnerAnswerSubmitter
{
    void Submit(OwnerQuestion question, string answer);
}

internal interface IConductorLiveness
{
    bool IsRunning();
}

internal interface IOwnerDigestSummary
{
    IReadOnlyList<string> ReadSummaryLines();
}

internal interface IGoalEventTail
{
    IReadOnlyList<string> ReadLast(string goalId, int count);
}

internal sealed record OwnerConductEvent(DateTimeOffset Timestamp, string EventKind, string? GoalId, string Detail);

internal enum OwnerQuestionKind { Clarification, HumanInput, StewardHold }

internal sealed record OwnerQuestion(
    string ItemId, string GoalId, OwnerQuestionKind Kind, string Text,
    string? BlastRadius = null, string? Confidence = null, string? ProposedDefault = null);

internal sealed record OwnerGoalCard(
    string Id, string Title, GoalStatus State, AgentRole? CurrentRole, DateTimeOffset? LastEvent);
