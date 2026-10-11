namespace Mcg.AgentOrchestrator.App.Orchestration;

// Ancestry facts are immutable for a commit-id pair; retain only the current main's facts.
internal sealed class GoalAncestryCache
{
    private readonly Dictionary<string, bool> _entries = new(StringComparer.Ordinal);
    private string? _mainSha;

    internal int Count => _entries.Count;

    public bool TryGet(string mainSha, string candidateSha, out bool isAncestor)
    {
        ObserveMain(mainSha);
        return _entries.TryGetValue(candidateSha, out isAncestor);
    }

    public void Store(string mainSha, string candidateSha, bool isAncestor)
    {
        ObserveMain(mainSha);
        _entries[candidateSha] = isAncestor;
    }

    private void ObserveMain(string mainSha)
    {
        if (string.Equals(_mainSha, mainSha, StringComparison.Ordinal))
            return;

        _entries.Clear();
        _mainSha = mainSha;
    }
}
