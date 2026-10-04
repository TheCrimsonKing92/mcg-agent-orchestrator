namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed record AcceptanceSharedApparatusAffectedOwner(
    string PartitionId,
    int OwnerProcessId,
    DateTimeOffset OwnerStartedAt,
    string OwnedRootPath);

internal sealed record AcceptanceSharedApparatusInvalidation(
    TempRootApparatusLossReceiptV1 FirstReceipt,
    IReadOnlyList<AcceptanceSharedApparatusAffectedOwner> AffectedOwners);

internal static class AcceptanceSharedApparatusInvalidationClassifier
{
    internal static AcceptanceSharedApparatusInvalidation? Classify(
        string gateInvocationId,
        IReadOnlyList<(string PartitionId, AcceptanceCheckResult Result)> failedPartitions,
        IEnumerable<TempRootApparatusLossReceiptV1> receipts)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gateInvocationId);
        ArgumentNullException.ThrowIfNull(failedPartitions);
        ArgumentNullException.ThrowIfNull(receipts);

        TempRootApparatusLossReceiptV1? firstReceipt = null;
        var affectedOwners = new List<AcceptanceSharedApparatusAffectedOwner>();
        foreach (var receipt in receipts)
        {
            if (!IsEligibleReceipt(receipt, gateInvocationId))
            {
                continue;
            }

            var affected = failedPartitions
                .Where(failure => !failure.Result.Passed &&
                    failure.Result.ChildProcessId is > 0 &&
                    failure.Result.ChildProcessStartedAt is not null)
                .Select(failure => MatchOwner(receipt, failure))
                .Where(owner => owner is not null)
                .Select(owner => owner!)
                .DistinctBy(owner => owner.PartitionId, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (affected.Length < 2)
            {
                continue;
            }

            firstReceipt ??= receipt;
            if (!PathsEqual(firstReceipt.SharedRoot, receipt.SharedRoot))
            {
                continue;
            }

            foreach (var owner in affected)
            {
                if (!affectedOwners.Any(existing =>
                    existing.PartitionId.Equals(owner.PartitionId, StringComparison.OrdinalIgnoreCase)))
                {
                    affectedOwners.Add(owner);
                }
            }
        }

        return firstReceipt is null
            ? null
            : new AcceptanceSharedApparatusInvalidation(firstReceipt, affectedOwners);
    }

    private static bool IsEligibleReceipt(
        TempRootApparatusLossReceiptV1 receipt,
        string gateInvocationId) =>
        receipt.ContractVersion == 1 &&
        receipt.GateInvocationId.Equals(gateInvocationId, StringComparison.Ordinal) &&
        !string.IsNullOrWhiteSpace(receipt.ReceiptId) &&
        !string.IsNullOrWhiteSpace(receipt.SharedRoot) &&
        receipt.DestroyedOwners is { Count: >= 2 };

    private static AcceptanceSharedApparatusAffectedOwner? MatchOwner(
        TempRootApparatusLossReceiptV1 receipt,
        (string PartitionId, AcceptanceCheckResult Result) failure)
    {
        var expectedPath = TempRootJanitor.BuildOwnedRootPath(
            receipt.SharedRoot,
            failure.Result.ChildProcessId!.Value);
        var matches = receipt.DestroyedOwners
            .Where(owner => owner.OwnerProcessId == failure.Result.ChildProcessId &&
                owner.OwnerStartedAt == failure.Result.ChildProcessStartedAt &&
                PathsEqual(owner.OwnedRootPath, expectedPath))
            .ToArray();
        return matches.Length == 1
            ? new AcceptanceSharedApparatusAffectedOwner(
                failure.PartitionId,
                matches[0].OwnerProcessId,
                matches[0].OwnerStartedAt,
                matches[0].OwnedRootPath)
            : null;
    }

    private static bool PathsEqual(string left, string right)
    {
        try
        {
            return Path.GetFullPath(left).Equals(
                Path.GetFullPath(right),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}
