namespace Mcg.AgentOrchestrator.Core;

/// <summary>A partition of objective bullets; removals are declarations, not attestation criteria.</summary>
public sealed class DeclaredTestRemovalSet
{
    private readonly IReadOnlyList<int?> _ordinaryIndices;

    private DeclaredTestRemovalSet(IReadOnlyList<string> declared)
    {
        var ordinary = new List<string>();
        var declarations = new List<AcceptanceCriterion>();
        var indices = new List<int?>();
        var distinctOrdinary = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var text in declared)
        {
            if (AcceptanceCriteriaParser.TryGetTestRemovalIdentity(text, out var identity))
            {
                declarations.Add(new AcceptanceCriterion(text, "test-removal", TestIdentity: identity));
                indices.Add(null);
                continue;
            }

            ordinary.Add(text);
            if (!distinctOrdinary.TryGetValue(text, out var index))
            {
                index = distinctOrdinary.Count;
                distinctOrdinary.Add(text, index);
            }
            indices.Add(index);
        }

        Ordinary = ordinary.AsReadOnly();
        Declarations = declarations.AsReadOnly();
        _ordinaryIndices = indices.AsReadOnly();
    }

    public IReadOnlyList<string> Ordinary { get; }
    public IReadOnlyList<AcceptanceCriterion> Declarations { get; }
    public int Count => Declarations.Count;

    public static DeclaredTestRemovalSet FromObjective(string objective) =>
        new(AcceptanceCriteriaParser.ParseDeclared(objective));

    /// <summary>Maps a 1-based declared position to a 0-based, distinct ordinary position.</summary>
    public int? GetOrdinaryIndex(int declaredIndex) =>
        declaredIndex >= 1 && declaredIndex <= _ordinaryIndices.Count
            ? _ordinaryIndices[declaredIndex - 1]
            : null;
}
