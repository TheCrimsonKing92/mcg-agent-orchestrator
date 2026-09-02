using System.Text.Json;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed record TempRootApparatusDestroyedOwner(
    int OwnerProcessId,
    DateTimeOffset OwnerStartedAt,
    string OwnedRootPath);

internal sealed record TempRootApparatusLossReceiptV1(
    int ContractVersion,
    string GateInvocationId,
    string ReceiptId,
    string SharedRoot,
    IReadOnlyList<TempRootApparatusDestroyedOwner> DestroyedOwners,
    DateTimeOffset RecordedAt);

internal sealed record AcceptanceSharedApparatusAffectedOwner(
    string PartitionId,
    int OwnerProcessId,
    DateTimeOffset OwnerStartedAt,
    string OwnedRootPath);

internal sealed record AcceptanceSharedApparatusInvalidation(
    TempRootApparatusLossReceiptV1 FirstReceipt,
    IReadOnlyList<AcceptanceSharedApparatusAffectedOwner> AffectedOwners);

internal static class TempRootApparatusLossReceiptStore
{
    private const string FileName = "temp-root-apparatus-loss.v1.jsonl";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly AsyncLocal<ReceiptContext?> CurrentContext = new();

    internal static IDisposable PushScope(string gateInvocationId, string? path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gateInvocationId);
        var previous = CurrentContext.Value;
        CurrentContext.Value = string.IsNullOrWhiteSpace(path)
            ? null
            : new ReceiptContext(gateInvocationId, path);
        return new RestoreScope(() => CurrentContext.Value = previous);
    }

    internal static string? ResolvePath(string? acceptanceAttemptResultsPrefix)
    {
        var directory = string.IsNullOrWhiteSpace(acceptanceAttemptResultsPrefix)
            ? null
            : Path.GetDirectoryName(acceptanceAttemptResultsPrefix);
        return string.IsNullOrWhiteSpace(directory) ? null : Path.Combine(directory, FileName);
    }

    internal static void Append(string path, TempRootApparatusLossReceiptV1 receipt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(receipt);
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }
        SharedJsonlFile.AppendLine(path, JsonSerializer.Serialize(receipt, JsonOptions));
    }

    internal static void RecordDeletedOwners(IEnumerable<TempRootJanitorReapResult> results)
    {
        var context = CurrentContext.Value;
        if (context is null)
        {
            return;
        }

        foreach (var group in results
            .Where(result => result.Disposition == TempRootJanitorReapDisposition.Deleted &&
                result.CapturedOwner?.StartedAt is not null)
            .GroupBy(result => result.SharedRoot, StringComparer.OrdinalIgnoreCase))
        {
            var owners = group
                .Select(result => new TempRootApparatusDestroyedOwner(
                    result.ProcessId,
                    result.CapturedOwner!.StartedAt!.Value,
                    result.Path))
                .DistinctBy(owner => owner.OwnerProcessId)
                .ToArray();
            if (owners.Length < 2)
            {
                continue;
            }

            Append(
                context.Path,
                new TempRootApparatusLossReceiptV1(
                    1,
                    context.GateInvocationId,
                    Guid.NewGuid().ToString("N"),
                    group.Key,
                    owners,
                    DateTimeOffset.UtcNow));
        }
    }

    internal static IReadOnlyList<TempRootApparatusLossReceiptV1> Read(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return [];
        }

        var receipts = new List<TempRootApparatusLossReceiptV1>();
        foreach (var line in SharedJsonlFile.ReadAllLines(path))
        {
            try
            {
                if (JsonSerializer.Deserialize<TempRootApparatusLossReceiptV1>(line, JsonOptions) is { } receipt)
                {
                    receipts.Add(receipt);
                }
            }
            catch (JsonException)
            {
                // A malformed diagnostic must fail closed to the ordinary candidate-failure path.
            }
        }

        return receipts;
    }

    private sealed record ReceiptContext(string GateInvocationId, string Path);

    private sealed class RestoreScope(Action restore) : IDisposable
    {
        private Action? _restore = restore;
        public void Dispose() => Interlocked.Exchange(ref _restore, null)?.Invoke();
    }
}

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
            if (affected.Length >= 2)
            {
                return new AcceptanceSharedApparatusInvalidation(receipt, affected);
            }
        }

        return null;
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
