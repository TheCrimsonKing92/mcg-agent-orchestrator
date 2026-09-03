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
    internal const string GateInvocationIdVariable = "MCG_ACCEPTANCE_GATE_INVOCATION_ID";
    internal const string ReceiptPathVariable = "MCG_ACCEPTANCE_TEMP_ROOT_LOSS_RECEIPT_PATH";
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

    internal static void RecordDeletedOwners(
        string sharedRoot,
        IEnumerable<TempRootApparatusDestroyedOwner> destroyedOwners)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sharedRoot);
        ArgumentNullException.ThrowIfNull(destroyedOwners);
        var context = ResolveContext();
        if (context is null)
        {
            return;
        }

        var owners = destroyedOwners
            .Where(owner => owner.OwnerProcessId > 0 &&
                owner.OwnerStartedAt != default &&
                PathsEqual(
                    owner.OwnedRootPath,
                    TempRootJanitor.BuildOwnedRootPath(sharedRoot, owner.OwnerProcessId)))
            .DistinctBy(owner => owner.OwnerProcessId)
            .ToArray();
        if (owners.Length < 2)
        {
            return;
        }

        Append(
            context.Path,
            new TempRootApparatusLossReceiptV1(
                1,
                context.GateInvocationId,
                Guid.NewGuid().ToString("N"),
                sharedRoot,
                owners,
                DateTimeOffset.UtcNow));
    }

    internal static void ApplyCurrentScope(IDictionary<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        if (CurrentContext.Value is not { } context)
        {
            environment.Remove(GateInvocationIdVariable);
            environment.Remove(ReceiptPathVariable);
            return;
        }

        environment[GateInvocationIdVariable] = context.GateInvocationId;
        environment[ReceiptPathVariable] = context.Path;
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

    private static ReceiptContext? ResolveContext()
    {
        if (CurrentContext.Value is { } current)
        {
            return current;
        }

        var gateInvocationId = Environment.GetEnvironmentVariable(GateInvocationIdVariable);
        var path = Environment.GetEnvironmentVariable(ReceiptPathVariable);
        return string.IsNullOrWhiteSpace(gateInvocationId) || string.IsNullOrWhiteSpace(path)
            ? null
            : new ReceiptContext(gateInvocationId, path);
    }

    private static bool PathsEqual(string left, string right)
    {
        try
        {
            return Path.GetFullPath(left).Equals(Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

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
