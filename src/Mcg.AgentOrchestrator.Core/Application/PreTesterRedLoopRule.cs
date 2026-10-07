namespace Mcg.AgentOrchestrator.Core;

public sealed record PreTesterRedLoopDecision(bool Trip, string Kind, IReadOnlyList<int> FailingCounts);

public static class PreTesterRedLoopRule
{
    public const int HardCap = 5;
    private const int Minimum = 3;

    public static PreTesterRedLoopDecision Evaluate(
        IReadOnlyList<IReadOnlyCollection<string>> failingSetsOldestFirst)
    {
        ArgumentNullException.ThrowIfNull(failingSetsOldestFirst);
        var sets = failingSetsOldestFirst
            .Select(set => new HashSet<string>(set, StringComparer.Ordinal)).ToArray();
        var counts = sets.Select(set => set.Count).ToArray();
        if (sets.Length < Minimum) return new(false, "below-minimum", counts);
        if (sets.Length >= HardCap) return new(true, "cap", counts);
        if (sets[^1].SetEquals(sets[^2])) return new(true, "repeat", counts);
        if (counts[^1] > counts[^2]) return new(true, "grew", counts);
        return new(false, "converging", counts);
    }
}
