using System.Collections.Concurrent;
using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using AcceptanceManifestCheck = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.AcceptanceManifestCheck;

namespace Mcg.AgentOrchestrator.Infrastructure;

// Three samples are the minimum at which one anomalous run cannot control a median: with one
// sample it determines the value, and with two it still moves the midpoint by half its deviation.
// Below three, the reviewed authored seed remains the safer ordering hint. The median of the latest
// five samples tolerates two anomalies once full while adapting to a lasting lane or host cost shift
// after three new gate runs; a larger window would preserve the staleness this store exists to remove.
// Ordering cannot affect a gate verdict, so responsiveness is preferable to a slower estimator.
internal static class AcceptanceLaneDurationStore
{
    internal const int MinimumSamples = 3;
    internal const int TrailingSampleCount = 5;
    internal const string FileName = "acceptance-lane-durations.jsonl";
    private const int CurrentVersion = 1;
    private static readonly AsyncLocal<RecordingScope?> CurrentScope = new();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    internal static IDisposable PushRecordingScope(string worktreePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(worktreePath);
        var path = ResolveStorePath(worktreePath);
        var scope = new RecordingScope(CurrentScope.Value, path, LoadSnapshot(path));
        CurrentScope.Value = scope;
        return scope;
    }

    internal static string ResolveStorePath(string worktreePath) =>
        Path.Combine(
            AcceptancePartitionVerdictCache.ResolveHostStateRoot(worktreePath),
            ".orchestrator",
            FileName);

    internal static double ResolveSortSeconds(AcceptanceManifestCheck check) =>
        CurrentScope.Value?.ResolveSortSeconds(check) ?? check.EstimatedSerialSeconds;

    internal static (double? MedianSeconds, int Samples) ResolveObservedSeconds(AcceptanceManifestCheck check) =>
        CurrentScope.Value?.ResolveObservedSeconds(check) ?? (null, 0);

    internal static void Record(
        AcceptanceManifestCheck check,
        AcceptanceCheckResult result,
        TimeSpan elapsed) =>
        CurrentScope.Value?.Record(check, result, elapsed);

    internal static void Flush() => CurrentScope.Value?.Flush();

    private static Snapshot LoadSnapshot(string path)
    {
        try
        {
            var samples = new Dictionary<LaneKey, Queue<double>>();
            foreach (var line in SharedJsonlFile.ReadAllLines(path))
            {
                if (TryDeserialize(line) is not { } observation ||
                    observation.Version != CurrentVersion ||
                    string.IsNullOrWhiteSpace(observation.LaneName) ||
                    string.IsNullOrWhiteSpace(observation.FilterHash) ||
                    !IsValidDuration(observation.DurationSeconds))
                {
                    continue;
                }

                var key = new LaneKey(observation.LaneName, observation.FilterHash);
                if (!samples.TryGetValue(key, out var trailing))
                {
                    trailing = new Queue<double>(TrailingSampleCount);
                    samples.Add(key, trailing);
                }

                if (trailing.Count == TrailingSampleCount)
                {
                    trailing.Dequeue();
                }

                trailing.Enqueue(observation.DurationSeconds);
            }

            return new Snapshot(samples.ToDictionary(
                pair => pair.Key,
                pair => (IReadOnlyList<double>)pair.Value.ToArray()));
        }
        catch (Exception exception) when (IsNonFatalStoreFailure(exception))
        {
            return Snapshot.Empty;
        }
    }

    private static AcceptanceLaneDurationObservation? TryDeserialize(string line)
    {
        try
        {
            return JsonSerializer.Deserialize<AcceptanceLaneDurationObservation>(line, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool TryCreateKey(AcceptanceManifestCheck check, out LaneKey key)
    {
        key = default;
        if (!GoalAcceptanceVerifier.TryGetInfrastructurePartitionId(check, out _, out var filter))
        {
            return false;
        }

        key = new LaneKey(check.Name, GoalAcceptanceVerifier.ShortHash(filter));
        return true;
    }

    private static bool IsValidDuration(double seconds) =>
        double.IsFinite(seconds) && seconds > 0;

    private static bool IsNonFatalStoreFailure(Exception exception) =>
        exception is not (OutOfMemoryException or StackOverflowException or AccessViolationException);

    private readonly record struct LaneKey(string LaneName, string FilterHash);

    private sealed record AcceptanceLaneDurationObservation(
        int Version,
        string LaneName,
        string FilterHash,
        double DurationSeconds,
        DateTimeOffset RecordedAt);

    private sealed class Snapshot(IReadOnlyDictionary<LaneKey, IReadOnlyList<double>> samples)
    {
        internal static Snapshot Empty { get; } = new(
            new Dictionary<LaneKey, IReadOnlyList<double>>());

        internal (double? MedianSeconds, int Samples) ResolveObservedSeconds(LaneKey key)
        {
            if (!samples.TryGetValue(key, out var values)) return (null, 0);
            return (TryResolve(key, out var seconds) ? seconds : null, values.Count);
        }

        internal bool TryResolve(LaneKey key, out double seconds)
        {
            seconds = default;
            if (!samples.TryGetValue(key, out var values) || values.Count < MinimumSamples)
            {
                return false;
            }

            var ordered = values.Order().ToArray();
            var middle = ordered.Length / 2;
            seconds = ordered.Length % 2 == 1
                ? ordered[middle]
                : (ordered[middle - 1] + ordered[middle]) / 2d;
            return true;
        }
    }

    private sealed class RecordingScope(
        RecordingScope? previous,
        string path,
        Snapshot snapshot) : IDisposable
    {
        private readonly ConcurrentQueue<AcceptanceLaneDurationObservation> _observations = new();
        private int _flushed;
        private int _disposed;

        internal (double? MedianSeconds, int Samples) ResolveObservedSeconds(AcceptanceManifestCheck check) =>
            TryCreateKey(check, out var key) ? snapshot.ResolveObservedSeconds(key) : (null, 0);

        internal double ResolveSortSeconds(AcceptanceManifestCheck check) =>
            TryCreateKey(check, out var key) && snapshot.TryResolve(key, out var observedSeconds)
                ? observedSeconds
                : check.EstimatedSerialSeconds;

        internal void Record(
            AcceptanceManifestCheck check,
            AcceptanceCheckResult result,
            TimeSpan elapsed)
        {
            if (!result.Passed ||
                result.TestResultIsExplicitCrossAttemptReuse ||
                !TryCreateKey(check, out var key) ||
                !IsValidDuration(elapsed.TotalSeconds))
            {
                return;
            }

            _observations.Enqueue(new AcceptanceLaneDurationObservation(
                CurrentVersion,
                key.LaneName,
                key.FilterHash,
                elapsed.TotalSeconds,
                DateTimeOffset.UtcNow));
        }

        internal void Flush()
        {
            if (Interlocked.Exchange(ref _flushed, 1) != 0 || _observations.IsEmpty)
            {
                return;
            }

            try
            {
                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                SharedJsonlFile.AppendLines(
                    path,
                    _observations.Select(observation => JsonSerializer.Serialize(observation, JsonOptions)));
            }
            catch (Exception exception) when (IsNonFatalStoreFailure(exception))
            {
                // Observations affect scheduling only; store failure must never affect the gate verdict.
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            if (ReferenceEquals(CurrentScope.Value, this))
            {
                CurrentScope.Value = previous;
            }
        }
    }
}
