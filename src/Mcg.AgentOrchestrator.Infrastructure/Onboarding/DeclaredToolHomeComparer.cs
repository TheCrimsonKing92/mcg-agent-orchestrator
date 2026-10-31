namespace Mcg.AgentOrchestrator.Infrastructure;

public static class DeclaredToolHomeComparer
{
    public static IReadOnlyList<ManifestFactComparison> Compare(IReadOnlyList<DeclaredToolHome> hand,
        IReadOnlyList<DeclaredToolHome>? learned)
    {
        var handHomes = hand.ToDictionary(home => home.Variable, home => home.Directory, StringComparer.Ordinal);
        var learnedHomes = (learned ?? []).ToDictionary(home => home.Variable, home => home.Directory, StringComparer.Ordinal);
        return handHomes.Keys.Union(learnedHomes.Keys, StringComparer.Ordinal)
            .OrderBy(variable => variable, StringComparer.Ordinal).Select(variable =>
            {
                handHomes.TryGetValue(variable, out var handDirectory);
                learnedHomes.TryGetValue(variable, out var learnedDirectory);
                var matches = string.Equals(handDirectory, learnedDirectory, StringComparison.Ordinal);
                return new ManifestFactComparison($"toolHome[{variable}]",
                    matches ? ManifestFactResultKind.Match : ManifestFactResultKind.DiffersFromHand,
                    handDirectory ?? "none", learnedDirectory ?? "none",
                    matches ? "Agrees with the owner answer." : "Declared tool home differs from the owner answer.");
            }).ToArray();
    }
}
