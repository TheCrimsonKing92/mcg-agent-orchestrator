using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class CarriedForwardGateSourceResolver
{
    internal sealed record Resolution(IReadOnlyList<string> CarriedFromShas, string? Diagnostic = null);

    internal static Resolution Resolve(string executionDirectory, GoalId goalId, string candidateSha)
    {
        if (string.IsNullOrWhiteSpace(candidateSha)) return new([]);

        IReadOnlyList<GoalOperationJournalEntry> entries;
        try { entries = GoalOperationJournal.Read(executionDirectory, goalId).Entries; }
        catch (Exception)
        {
            // An unreadable journal cannot authorize reuse of another candidate's evidence.
            return new([]);
        }

        var mappings = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            if (entry.GoalId != goalId ||
                !string.Equals(entry.Operation, ConductorDriver.AcceptanceMainAdvanceCarryOperation, StringComparison.Ordinal))
                continue;

            string? verified = null, current = null;
            foreach (var token in (entry.Detail ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var separator = token.IndexOf('=');
                if (separator < 0) continue;
                // The writer appends branch tokens after path-bearing receipt text: last key wins.
                switch (token[..separator])
                {
                    case "verifiedBranch": verified = token[(separator + 1)..]; break;
                    case "currentBranch": current = token[(separator + 1)..]; break;
                }
            }
            if (!Usable(verified) || !Usable(current)) continue;
            if (!mappings.TryGetValue(current!, out var parents))
                mappings[current!] = parents = new(StringComparer.OrdinalIgnoreCase);
            parents.Add(verified!);
        }

        var carried = new List<string>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { candidateSha };
        var cursor = candidateSha;
        while (mappings.TryGetValue(cursor, out var parents))
        {
            if (parents.Count > 1)
                return new([], $"GATE_TEST_OBLIGATION_CARRY_FORWARD_AMBIGUOUS id={goalId.Value[..Math.Min(8, goalId.Value.Length)]} " +
                    $"candidate={cursor} verified={string.Join(",", parents.OrderBy(sha => sha, StringComparer.Ordinal))}");
            var parent = parents.Single();
            // Reject the whole traversal, including any prefix discovered before a cycle.
            if (!visited.Add(parent)) return new([]);
            carried.Add(parent);
            cursor = parent;
        }
        return new(carried);
    }

    private static bool Usable(string? sha) =>
        !string.IsNullOrWhiteSpace(sha) && !string.Equals(sha, "unknown", StringComparison.OrdinalIgnoreCase);
}
