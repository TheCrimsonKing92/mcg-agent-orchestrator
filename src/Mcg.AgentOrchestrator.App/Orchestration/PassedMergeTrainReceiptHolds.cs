using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Owned by one batch loop. A restart intentionally resets the tick budget and releases.
internal sealed class PassedMergeTrainReceiptHolds
{
    internal const int PassedTrainReceiptHoldTickLimit = 10;

    internal sealed record Observation(
        string MainRevision,
        IReadOnlyDictionary<GoalId, string?> CandidateRevisions,
        IReadOnlySet<GoalId> NotReadyGoalIds);

    internal sealed record Decision(
        int HeldTicks = 0,
        GoalId? BlockedMember = null,
        IReadOnlyList<GoalId>? HeldGoalIds = null,
        bool Released = false,
        string? Moved = null);

    private readonly Dictionary<string, (int Ticks, IReadOnlyList<GoalId> Members)> _heldTicks =
        new(StringComparer.Ordinal);
    private readonly HashSet<string> _released = new(StringComparer.Ordinal);
    private readonly HashSet<string> _observed = new(StringComparer.Ordinal);

    internal IReadOnlySet<GoalId> HeldGoalIds => _heldTicks.Values
        .SelectMany(hold => hold.Members).ToHashSet();

    internal void BeginPass() => _observed.Clear();

    internal Decision Observe(MergeTrainReceipt receipt, Observation observation)
    {
        var id = receipt.ReceiptId;
        if (!_observed.Add(id))
        {
            return new();
        }

        var moved = !string.Equals(receipt.Identity.ObservedMainRevision, observation.MainRevision,
            StringComparison.Ordinal) ? "main" : null;
        var unknownRevision = false;
        foreach (var member in receipt.Identity.Members)
        {
            if (!observation.CandidateRevisions.TryGetValue(member.GoalId, out var revision) || revision is null)
            {
                unknownRevision = true;
            }
            else if (moved is null && !string.Equals(member.CandidateRevision, revision, StringComparison.Ordinal))
            {
                moved = $"member:{member.GoalId.Value[..8]}";
            }
        }
        if (moved is not null || unknownRevision)
        {
            var wasHeld = _heldTicks.Remove(id);
            // Preserve the existing silent skip for blocked receipts never held by this loop.
            return new(Moved: wasHeld ? moved : null);
        }
        if (_released.Contains(id))
        {
            return new();
        }

        var blocked = receipt.Identity.Members.First(member =>
            observation.NotReadyGoalIds.Contains(member.GoalId));
        var ticks = _heldTicks.GetValueOrDefault(id).Ticks + 1;
        if (ticks >= PassedTrainReceiptHoldTickLimit)
        {
            _heldTicks.Remove(id);
            _released.Add(id);
            return new(Released: true);
        }
        var members = receipt.Identity.Members.Select(member => member.GoalId).ToArray();
        _heldTicks[id] = (ticks, members);
        // A criterion evidence gap can block grouped admission while leaving solo admission
        // eligible, so every member needs the receipt hold until it is released or stale.
        return new(ticks, blocked.GoalId, members);
    }

    internal void EndPass()
    {
        foreach (var id in _heldTicks.Keys.Where(id => !_observed.Contains(id)).ToArray())
        {
            _heldTicks.Remove(id);
        }
    }
}
