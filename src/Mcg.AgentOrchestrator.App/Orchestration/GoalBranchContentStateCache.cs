namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed class GoalBranchContentStateCache
{
    private readonly Dictionary<(string MainSha, string Branch, string TipSha), GoalBranchContentState> _states = [];
    private string? _mainSha;

    internal bool TryGet(string mainSha, string branch, string tipSha, out GoalBranchContentState state)
    {
        ObserveMainSha(mainSha);
        return _states.TryGetValue((mainSha, branch, tipSha), out state);
    }

    internal void Store(string mainSha, string branch, string tipSha, GoalBranchContentState state)
    {
        ObserveMainSha(mainSha);
        if (state is GoalBranchContentState.Inconclusive or GoalBranchContentState.NotChecked)
        {
            return;
        }

        _states[(mainSha, branch, tipSha)] = state;
    }

    private void ObserveMainSha(string mainSha)
    {
        if (!string.Equals(_mainSha, mainSha, StringComparison.Ordinal))
        {
            _states.Clear();
            _mainSha = mainSha;
        }
    }
}
