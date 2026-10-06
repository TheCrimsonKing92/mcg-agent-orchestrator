using System.Text.Json;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class StructuralCoverageFailureDetail
{
    internal static IReadOnlyList<string> Format(TestCoverageInvariantResult coverage, int limit)
    {
        var cap = Math.Max(0, limit);
        var identityMismatches = coverage.IdentityMismatches ?? [];
        var mismatchNames = identityMismatches.Select(mismatch => mismatch.Discovered)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var remainingMissing = coverage.MissingTests.Where(name => !mismatchNames.Contains(name)).ToArray();
        var receipts = remainingMissing
            .Where(name => name.StartsWith("cross-generation-", StringComparison.Ordinal))
            .ToArray();
        var missingTests = remainingMissing
            .Where(name => !name.StartsWith("cross-generation-", StringComparison.Ordinal))
            .ToArray();
        var details = new List<string>
        {
            $"classification: {coverage.FailureClassification}",
            coverage.Summary
        };
        if (identityMismatches.Count > 0 || missingTests.Length > 0)
        {
            details.Add("meaning: each listed test exists and was discovered but no shard executed it; adding new tests cannot clear this");
        }
        if (receipts.Length > 0)
        {
            details.Add("meaning: the candidate's discovered test count or main baseline generation does not reconcile; the receipts below give the counts");
        }

        AppendList(details, coverage.EmptyPartitions, cap, "empty partitions", name => $"empty partition: {name}");
        AppendList(details, identityMismatches, cap, "identity mismatches", mismatch =>
            $"missing test: discovered={JsonSerializer.Serialize(mismatch.Discovered)}; executed={JsonSerializer.Serialize(mismatch.Executed)}");
        AppendList(details, receipts, cap, "receipts", receipt => $"receipt: {receipt}");
        AppendList(details, missingTests, cap, "missing tests", name => $"missing test: {name}");
        return details;
    }

    private static void AppendList<T>(
        List<string> details,
        IReadOnlyList<T> items,
        int cap,
        string kind,
        Func<T, string> format)
    {
        details.AddRange(items.Take(cap).Select(format));
        if (items.Count > cap)
        {
            details.Add($"omitted: {items.Count - cap} more {kind} not listed");
        }
    }
}
