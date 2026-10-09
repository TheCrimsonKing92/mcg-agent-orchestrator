namespace Mcg.AgentOrchestrator.App.OwnerConsole;

// Session observations of the same read model used by DECISIONS. A goal prefix is not an item identity.
internal sealed class OwnerNeedsYouLedger
{
    private readonly List<OwnerAttentionObservation> _entries = [];

    internal void Observe(IReadOnlyList<OwnerQuestion> live, DateTimeOffset now)
    {
        foreach (var entry in _entries.Where(entry => entry.ResolvedAt is null).ToArray())
            if (!live.Any(question => SameItem(question, entry.Question)))
                _entries[_entries.IndexOf(entry)] = entry with { ResolvedAt = now };
        foreach (var question in live)
            if (!_entries.Any(entry => entry.ResolvedAt is null && SameItem(entry.Question, question)))
                _entries.Add(new(question, now));
        while (_entries.Count > OwnerConsoleViewModelBuilder.MaxActivityItems)
        {
            var index = _entries.FindIndex(entry => entry.ResolvedAt is not null);
            _entries.RemoveAt(index < 0 ? 0 : index);
        }
    }

    internal OwnerAttentionObservation[] Snapshot() => _entries.ToArray();

    private static bool SameItem(OwnerQuestion left, OwnerQuestion right) => left.ItemId == right.ItemId &&
        left.GoalId == right.GoalId && left.Text == right.Text;
}
