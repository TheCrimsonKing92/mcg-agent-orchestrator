using System.Collections.Immutable;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal sealed record OwnerConsoleViewModel(
    OwnerConsoleStatus Status, ImmutableArray<OwnerConsoleDecision> Decisions,
    ImmutableArray<OwnerConsoleBoardRow> Board, ImmutableArray<OwnerConsoleActivityItem> Activity);

internal sealed record OwnerConsoleStatus(bool ConductorRunning, int ActiveGoals, int LiveDecisions,
    int HiddenQuestions, TimeSpan? LastEventAge, int LandingsSinceOpen);

internal sealed record OwnerConsoleDecision(string Id, int Number, string GoalId, string GoalPrefix,
    OwnerQuestionKind Kind, string Summary, string FullText, string? BlastRadius,
    string? Confidence, string? ProposedDefault)
{
    internal OwnerQuestion ToQuestion() => new(Id, GoalId, Kind, FullText, BlastRadius, Confidence, ProposedDefault);
}

internal sealed record OwnerConsoleBoardRow(string GoalPrefix, string Epic, string Title,
    string State, string Stage, TimeSpan? Age);

internal sealed record OwnerConsoleActivityItem(DateTimeOffset Timestamp, string Kind, string Tag,
    string GoalPrefix, string Detail);

internal sealed record OwnerConsoleViewInputs(DateTimeOffset OpenedAt, DateTimeOffset? LastConductEvent,
    IReadOnlyList<OwnerConductEvent> RecentEvents, int LandingsSinceOpen);
