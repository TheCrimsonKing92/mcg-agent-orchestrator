namespace Mcg.AgentOrchestrator.Core;

public sealed record PreTesterRedLoopDecision(bool Trip, string Kind, IReadOnlyList<int> FailingCounts);
public sealed record PreTesterRedRun(
    IReadOnlyCollection<string> FailingTests, IReadOnlyDictionary<string, string> Signatures);

public static class PreTesterRedLoopRule
{
    public const int HardCap = 5;
    private const int Minimum = 3;

    public static PreTesterRedLoopDecision Evaluate(
        IReadOnlyList<IReadOnlyCollection<string>> failingSetsOldestFirst)
    {
        ArgumentNullException.ThrowIfNull(failingSetsOldestFirst);
        IReadOnlyDictionary<string, string> empty = new Dictionary<string, string>(StringComparer.Ordinal);
        return EvaluateRuns(failingSetsOldestFirst.Select(set => new PreTesterRedRun(set, empty)).ToArray());
    }

    public static PreTesterRedLoopDecision EvaluateRuns(IReadOnlyList<PreTesterRedRun> runsOldestFirst)
    {
        ArgumentNullException.ThrowIfNull(runsOldestFirst);
        var sets = runsOldestFirst
            .Select(run => new HashSet<string>(run.FailingTests, StringComparer.Ordinal)).ToArray();
        var counts = sets.Select(set => set.Count).ToArray();
        if (sets.Length < Minimum) return new(false, "below-minimum", counts);
        if (sets.Length >= HardCap) return new(true, "cap", counts);
        if (sets[^1].SetEquals(sets[^2]) && !sets[^1].Any(name =>
                runsOldestFirst[^1].Signatures.TryGetValue(name, out var newest) &&
                runsOldestFirst[^2].Signatures.TryGetValue(name, out var previous) &&
                !string.Equals(newest, previous, StringComparison.Ordinal)))
            return new(true, "repeat", counts);
        if (counts[^1] > counts[^2]) return new(true, "grew", counts);
        return new(false, "converging", counts);
    }
}
