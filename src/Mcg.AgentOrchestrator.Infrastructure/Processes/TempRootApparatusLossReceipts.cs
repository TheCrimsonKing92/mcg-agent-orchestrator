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
        if (string.IsNullOrWhiteSpace(acceptanceAttemptResultsPrefix))
            return null;
        var prefix = Path.GetFullPath(acceptanceAttemptResultsPrefix)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return $"{prefix}.{FileName}";
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
        ApplyScope(environment, CurrentContext.Value?.GateInvocationId, CurrentContext.Value?.Path);
    }

    internal static void ApplyScope(
        IDictionary<string, string?> environment,
        string? gateInvocationId,
        string? path)
    {
        ArgumentNullException.ThrowIfNull(environment);
        if (string.IsNullOrWhiteSpace(gateInvocationId) || string.IsNullOrWhiteSpace(path))
        {
            environment.Remove(GateInvocationIdVariable);
            environment.Remove(ReceiptPathVariable);
            return;
        }

        environment[GateInvocationIdVariable] = gateInvocationId;
        environment[ReceiptPathVariable] = path;
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
