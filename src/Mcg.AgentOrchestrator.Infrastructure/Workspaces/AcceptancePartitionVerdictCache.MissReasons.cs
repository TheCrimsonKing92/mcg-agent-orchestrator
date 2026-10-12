using System.Globalization;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class PartitionVerdictMissReasons
{
    internal const string CacheKeyUnavailable = "cache-key-unavailable";
    internal const string ForcedFullRerun = "forced-full-rerun";
    internal const string ClosureHashUnavailable = "closure-hash-unavailable";
    internal const string NoGreenVerdictForClosure = "no-green-verdict-for-closure";
    internal const string NoPriorGreenForLane = "no-prior-green-for-lane";
    internal const string ManifestIdentityDiffers = "manifest-identity-differs";
    internal const string ClosureHashDiffers = "closure-hash-differs";
    internal const string ManifestAndClosureDiffer = "manifest-and-closure-differ";
    internal const string NoGreenVerdictForIdenticalTree = "no-green-verdict-for-identical-tree";
    internal const string NoGreenVerdictForWholeProjectClosure = "no-green-verdict-for-whole-project-closure";
    internal const string IdenticalTreeClosureHashMismatch = "identical-tree-closure-hash-mismatch";
    internal const string MissingStructuralCoverageEvidence = "missing-structural-coverage-evidence";
}

internal sealed record PartitionVerdictMissReceipt(string LaneId, string Reason, string? ClosureHash, string? ReasonCode = null);

internal sealed partial class AcceptancePartitionVerdictCache
{
    private readonly Dictionary<string, PartitionVerdictMissReceipt> _misses = new(StringComparer.OrdinalIgnoreCase);
    private long _executedLaneDurationMilliseconds;
    private long? _sharedPrebuildDurationMilliseconds;

    internal IReadOnlyList<PartitionVerdictMissReceipt> Misses
    {
        get
        {
            lock (_gate)
                return _misses.Values.OrderBy(miss => miss.LaneId, StringComparer.Ordinal).ToArray();
        }
    }

    private void RecordMiss(string laneId, string reason, string? closureHash = null, string? reasonCode = null)
    {
        var escapedLaneId = Uri.EscapeDataString(laneId);
        lock (_gate)
            _misses.TryAdd(escapedLaneId, new PartitionVerdictMissReceipt(escapedLaneId, reason, closureHash, reasonCode));
    }

    private string? ClosureMissReasonCode(AcceptanceClosureVerdictRecord? latest, string closureHash)
    {
        if (latest is null)
            return PartitionVerdictMissReasons.NoPriorGreenForLane;

        var manifestDiffers = !string.Equals(latest.ManifestIdentity, ManifestIdentity, StringComparison.Ordinal);
        var closureDiffers = !string.Equals(latest.ClosureHash, closureHash, StringComparison.OrdinalIgnoreCase);
        return (manifestDiffers, closureDiffers) switch
        {
            (true, true) => PartitionVerdictMissReasons.ManifestAndClosureDiffer,
            (true, false) => PartitionVerdictMissReasons.ManifestIdentityDiffers,
            (false, true) => PartitionVerdictMissReasons.ClosureHashDiffers,
            // A concurrent append can become visible after the exact lookup missed.
            (false, false) => null
        };
    }

    // Called while RecordExecution holds _gate.
    private void RecordExecutedDuration(long? durationMilliseconds) =>
        _executedLaneDurationMilliseconds += durationMilliseconds ?? 0;

    internal void RecordSharedPrebuildDuration(string attemptId, long? durationMilliseconds)
    {
        if (!string.Equals(attemptId, AttemptId, StringComparison.OrdinalIgnoreCase))
            return;

        lock (_gate)
            _sharedPrebuildDurationMilliseconds =
                (_sharedPrebuildDurationMilliseconds ?? 0) + (durationMilliseconds ?? 0);
    }

    private string FormatMeasurementTokens()
    {
        lock (_gate)
        {
            var tokens = new List<string>
            {
                $"reused_lanes={_reused.Count.ToString(CultureInfo.InvariantCulture)}",
                $"executed_lanes={_executed.Count.ToString(CultureInfo.InvariantCulture)}",
                $"executed_lane_duration_ms={_executedLaneDurationMilliseconds.ToString(CultureInfo.InvariantCulture)}",
                $"shared_prebuild_duration_ms={_sharedPrebuildDurationMilliseconds?.ToString(CultureInfo.InvariantCulture) ?? "not-run"}"
            };
            tokens.AddRange(_misses.Values
                .OrderBy(miss => miss.LaneId, StringComparer.Ordinal)
                .Select(miss => $"missed_lane={miss.LaneId}:{miss.Reason}" +
                    (miss.ClosureHash is null ? string.Empty : $":{miss.ClosureHash}") +
                    (miss.ReasonCode is null ? string.Empty : $":{miss.ReasonCode}")));
            return string.Join(' ', tokens);
        }
    }
}
