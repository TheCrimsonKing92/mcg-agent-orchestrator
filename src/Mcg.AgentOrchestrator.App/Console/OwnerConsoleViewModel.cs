using System.Collections.Immutable;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal sealed record OwnerConsoleViewModel(
    OwnerConsoleStatus Status, ImmutableArray<OwnerConsoleDecision> Decisions,
    ImmutableArray<OwnerConsoleBoardRow> Board, ImmutableArray<OwnerConsoleActivityItem> Activity,
    OwnerConsolePaneState? DecisionsState = null, OwnerConsolePaneState? ActivityState = null);

internal sealed record OwnerConsoleStatus(bool ConductorRunning, int ActiveGoals, int LiveDecisions,
    int HiddenQuestions, TimeSpan? LastEventAge, int LandedToday);

internal sealed record OwnerConsoleDecision(string Id, int Number, string GoalId, string GoalPrefix,
    OwnerQuestionKind Kind, string Summary, string FullText, string? BlastRadius,
    string? Confidence, string? ProposedDefault)
{
    internal OwnerQuestion ToQuestion() => new(Id, GoalId, Kind, FullText, BlastRadius, Confidence, ProposedDefault);
}

internal sealed record OwnerConsoleBoardRow(string GoalPrefix, string Epic, string Title,
    string State, string Stage, TimeSpan? Age, string GoalId);

internal sealed record OwnerConsoleActivityItem(DateTimeOffset Timestamp, string Kind, string Tag,
    string GoalPrefix, string Detail, string GoalTitle = "", string Phrase = "",
    string Why = "The reason was not recorded.", string Next = "Work continues.", string Act = "No action is needed.",
    string? Subject = null, string? OwnerQuestionId = null, OwnerActivityResolution? Resolution = null,
    IReadOnlyList<string>? Titles = null, string? GoalId = null, string? Question = null);

internal sealed record OwnerConsoleViewInputs(DateTimeOffset OpenedAt, DateTimeOffset? LastConductEvent,
    IReadOnlyList<OwnerConductEvent> RecentEvents, int LandedToday);
