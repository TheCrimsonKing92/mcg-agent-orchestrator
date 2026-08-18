using System.Globalization;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Cli;

internal sealed record GoalBoardOptions(int? Limit)
{
    public const int DefaultLimit = 50;
    public const int MaximumLimit = 500;
    public const string Usage = "Usage: goals --board [--limit <n> | --all]";

    public static GoalBoardOptions Parse(IReadOnlyList<string> args)
    {
        if (args.Count < 2 ||
            !args[0].Equals("goals", StringComparison.OrdinalIgnoreCase) ||
            !args[1].Equals("--board", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(Usage);
        }

        int? limit = DefaultLimit;
        var sawLimit = false;
        var sawAll = false;
        for (var index = 2; index < args.Count; index++)
        {
            if (args[index].Equals("--all", StringComparison.OrdinalIgnoreCase))
            {
                if (sawAll || sawLimit)
                    throw new ArgumentException(Usage);
                sawAll = true;
                limit = null;
                continue;
            }

            if (args[index].Equals("--limit", StringComparison.OrdinalIgnoreCase))
            {
                if (sawLimit || sawAll || index + 1 >= args.Count ||
                    !int.TryParse(args[++index], NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ||
                    parsed is < 1 or > MaximumLimit)
                {
                    throw new ArgumentException(Usage);
                }

                sawLimit = true;
                limit = parsed;
                continue;
            }

            throw new ArgumentException(Usage);
        }

        return new GoalBoardOptions(limit);
    }
}

internal sealed record GoalBoardSignalFact(string Source, DateTimeOffset? At);

internal sealed record GoalBoardWorktreeFact(string State, int? Ahead, int? Behind)
{
    public static GoalBoardWorktreeFact Absent { get; } = new("-", null, null);
    public static GoalBoardWorktreeFact Unknown { get; } = new("unknown", null, null);
}

internal sealed record GoalBoardGoalFact(
    string Id,
    string Objective,
    GoalStatus Status,
    string Stage,
    string Work,
    IReadOnlyList<GoalBoardSignalFact> Signals,
    int? AttentionCount,
    int? IntentCount,
    string Backlog,
    GoalBoardWorktreeFact Worktree,
    bool DeadDispatch,
    string? DispatchRecoveryCommand,
    bool LiveAcceptance,
    bool LiveWorker,
    string FallbackCommand);

internal sealed record GoalBoardProjection(
    IReadOnlyList<string> Rows,
    int Shown,
    int Omitted,
    string? RerunInstruction);

internal static class GoalBoardProjector
{
    private const int MaximumRowLength = 360;
    private static readonly HashSet<GoalStatus> IncludedStatuses =
    [
        GoalStatus.Draft,
        GoalStatus.Active,
        GoalStatus.WaitingForHuman,
        GoalStatus.Parked,
        GoalStatus.Verifying,
        GoalStatus.Verified,
        GoalStatus.AcceptanceFailed,
        GoalStatus.Failed
    ];

    public static GoalBoardProjection Project(
        IEnumerable<GoalBoardGoalFact> facts,
        GoalBoardOptions options,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(options);

        var projected = facts
            .Where(fact => IncludedStatuses.Contains(fact.Status))
            .Select(fact => ProjectRow(fact, now))
            .OrderBy(row => row.Bucket)
            .ThenBy(row => row.SignalAt is null ? 1 : 0)
            .ThenByDescending(row => row.SignalAt)
            .ThenBy(row => row.Id, StringComparer.Ordinal)
            .ToArray();
        var shownRows = options.Limit is { } limit ? projected.Take(limit).ToArray() : projected;
        var omitted = projected.Length - shownRows.Length;
        var rerunLimit = Math.Min(GoalBoardOptions.MaximumLimit, projected.Length);
        return new GoalBoardProjection(
            shownRows.Select(row => row.Text).ToArray(),
            shownRows.Length,
            omitted,
            omitted > 0 ? $"rerun goals --board --all or --limit {rerunLimit}" : null);
    }

    internal static bool IsIncluded(GoalStatus status) => IncludedStatuses.Contains(status);

    private static ProjectedRow ProjectRow(GoalBoardGoalFact fact, DateTimeOffset now)
    {
        var latestSignal = fact.Signals
            .Select((signal, index) => new { Signal = signal, Index = index })
            .Where(candidate => IsUsable(candidate.Signal.At, now))
            .OrderByDescending(candidate => candidate.Signal.At)
            .ThenBy(candidate => candidate.Index)
            .FirstOrDefault();
        var signalAt = latestSignal?.Signal.At;
        var signal = latestSignal is null
            ? "unknown"
            : $"{Sanitize(latestSignal.Signal.Source, 24)}:{FormatAge(now - signalAt!.Value)}";
        var prefix = Prefix(fact.Id);
        var control = SelectControl(fact, prefix);
        var attention = fact.AttentionCount?.ToString(CultureInfo.InvariantCulture) ?? "unknown";
        var intents = fact.IntentCount?.ToString(CultureInfo.InvariantCulture) ?? "unknown";
        var ahead = fact.Worktree.Ahead?.ToString(CultureInfo.InvariantCulture) ?? "?";
        var behind = fact.Worktree.Behind?.ToString(CultureInfo.InvariantCulture) ?? "?";
        var tail =
            $" [{prefix}] status={fact.Status} stage={Sanitize(fact.Stage, 28)} " +
            $"work={Sanitize(fact.Work, 48)} signal={signal} attention={attention} intents={intents} " +
            $"backlog={Sanitize(fact.Backlog, 28)} worktree={Sanitize(fact.Worktree.State, 8)} ahead={ahead} behind={behind} {control}";
        var titleBudget = Math.Max(1, Math.Min(80, MaximumRowLength - tail.Length));
        var text = Sanitize(Title(fact.Objective), titleBudget) + tail;
        if (text.Length > MaximumRowLength)
        {
            throw new InvalidOperationException("Goal board mandatory fields exceed the 360-character row contract.");
        }

        return new ProjectedRow(fact.Id, Bucket(fact), signalAt, text);
    }

    private static string SelectControl(GoalBoardGoalFact fact, string prefix)
    {
        if (fact.AttentionCount > 0)
            return $"next=attention show {prefix}";
        if (fact.IntentCount > 0)
            return "held=operator intent pending";
        if (fact.DeadDispatch)
            return $"next={Sanitize(fact.DispatchRecoveryCommand ?? $"next {prefix} --full", 96)}";
        if (fact.LiveAcceptance)
            return "held=acceptance live";
        if (fact.LiveWorker)
            return "held=worker live";
        if (fact.Status == GoalStatus.Parked)
            return "held=parked";
        if (fact.Status is GoalStatus.Failed or GoalStatus.AcceptanceFailed)
            return $"next=next {prefix} --full";
        return $"next={Sanitize(fact.FallbackCommand, 96)}";
    }

    private static int Bucket(GoalBoardGoalFact fact)
    {
        if (fact.Status is GoalStatus.WaitingForHuman or GoalStatus.Failed or GoalStatus.AcceptanceFailed ||
            fact.DeadDispatch || fact.AttentionCount > 0 || fact.IntentCount > 0)
            return 0;
        if (fact.LiveAcceptance || fact.LiveWorker)
            return 1;
        if (fact.Status == GoalStatus.Parked)
            return 3;
        return 2;
    }

    private static bool IsUsable(DateTimeOffset? value, DateTimeOffset now) =>
        value is { } timestamp && timestamp != DateTimeOffset.MinValue && timestamp <= now;

    private static string FormatAge(TimeSpan age)
    {
        if (age < TimeSpan.FromMinutes(1))
            return $"{Math.Max(0, (int)age.TotalSeconds)}s";
        if (age < TimeSpan.FromHours(1))
            return $"{(int)age.TotalMinutes}m";
        if (age < TimeSpan.FromDays(2))
            return $"{(int)age.TotalHours}h";
        return $"{(int)age.TotalDays}d";
    }

    private static string Title(string objective) =>
        objective
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(line => !string.IsNullOrWhiteSpace(line)) ?? "untitled goal";

    private static string Prefix(string id) =>
        id[..Math.Min(8, id.Length)].ToLowerInvariant();

    private static string Sanitize(string? value, int maximumLength)
    {
        var normalized = string.IsNullOrWhiteSpace(value)
            ? "unknown"
            : string.Join(' ', value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return normalized.Length <= maximumLength ? normalized : normalized[..maximumLength];
    }

    private sealed record ProjectedRow(string Id, int Bucket, DateTimeOffset? SignalAt, string Text);
}
