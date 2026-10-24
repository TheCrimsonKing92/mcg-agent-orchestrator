namespace Mcg.AgentOrchestrator.App.OwnerConsole;

// The lifecycle files read by one rebuild; matching always returns board-owned ids.
internal sealed record OwnerConsoleRefreshScope
{
    private readonly bool _all;
    private readonly string[] _goalIds;

    private OwnerConsoleRefreshScope(bool all, string[] goalIds)
    {
        _all = all;
        _goalIds = goalIds;
    }

    internal static OwnerConsoleRefreshScope All { get; } = new(true, []);
    internal static OwnerConsoleRefreshScope None { get; } = new(false, []);

    internal static OwnerConsoleRefreshScope For(IEnumerable<string> goalIds) =>
        new(false, goalIds.Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray());

    internal OwnerConsoleRefreshScope Union(OwnerConsoleRefreshScope other) =>
        _all || other._all ? All : _goalIds.Length == 0 ? other : other._goalIds.Length == 0 ? this :
            For(_goalIds.Concat(other._goalIds));

    internal IReadOnlyCollection<string> Resolve(IReadOnlyCollection<string> boardGoalIds) =>
        _all ? boardGoalIds : boardGoalIds.Where(boardId =>
            _goalIds.Any(id => boardId.StartsWith(id, StringComparison.OrdinalIgnoreCase))).ToArray();
}
