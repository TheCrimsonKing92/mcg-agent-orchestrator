using System.Globalization;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed class ConductEventLogWriter
{
    public const string CurrentFileName = "conduct-events.log";
    internal const string PendingEventsDirectoryName = "pending-events";
    internal const long DefaultMaxBytes = 1_048_576;
    internal const int DefaultRotatedGenerationCount = 8;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly object RequiredEventDrainGate = new();
    private static readonly ConcurrentDictionary<string, byte> MigratedLegacyPendingPaths =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly string _path;
    private readonly long _maxBytes;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Action? _beforeRequiredEventDrain;
    private readonly Action? _beforeAppendCommit;
    private readonly int _rotatedGenerationCount;
    private readonly string _requiredEventMutexName;
    private readonly object _lock = new();

    public ConductEventLogWriter(
        string path,
        long maxBytes = DefaultMaxBytes,
        Func<DateTimeOffset>? utcNow = null,
        Action? beforeRequiredEventDrain = null,
        Action? beforeAppendCommit = null,
        int rotatedGenerationCount = DefaultRotatedGenerationCount)
    {
        _path = path;
        _maxBytes = maxBytes;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _beforeRequiredEventDrain = beforeRequiredEventDrain;
        _beforeAppendCommit = beforeAppendCommit;
        _rotatedGenerationCount = Math.Max(0, rotatedGenerationCount);
        _requiredEventMutexName = RequiredEventMutexName(path);
        MigrateLegacyPendingEvents();
    }

    public string CurrentPath => _path;

    public void Append(string eventKind, string? goalId, string detail, DateTimeOffset? timestamp = null)
    {
        lock (_lock)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path) ?? ".");
            _beforeRequiredEventDrain?.Invoke();
            WithRequiredEventGate(() =>
            {
                DrainRequiredEventsUnderGate();
                _beforeAppendCommit?.Invoke();
                RotateIfNeeded();

                File.AppendAllText(_path, Serialize(eventKind, goalId, detail, timestamp));
            });
        }
    }

    public bool AppendRequired(string eventKind, string? goalId, string detail, DateTimeOffset? timestamp = null)
    {
        lock (_lock)
        {
            try
            {
                _beforeRequiredEventDrain?.Invoke();
                return WithRequiredEventGate(() =>
                {
                    var directory = Path.GetDirectoryName(_path) ?? ".";
                    Directory.CreateDirectory(directory);
                    var pendingDirectory = Path.Combine(directory, PendingEventsDirectoryName);
                    Directory.CreateDirectory(pendingDirectory);
                    var pendingPath = Path.Combine(
                        pendingDirectory,
                        $"{Path.GetFileName(_path)}.pending-{Guid.NewGuid():N}.jsonl");
                    File.WriteAllText(pendingPath, Serialize(eventKind, goalId, detail, timestamp));
                    DrainRequiredEventsUnderGate();
                    return !File.Exists(pendingPath);
                });
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }
    }

    internal bool AppendRequired(ConductEvidenceLifecycleEvent record)
    {
        ArgumentNullException.ThrowIfNull(record);
        lock (_lock)
        {
            try
            {
                _beforeRequiredEventDrain?.Invoke();
                return WithRequiredEventGate(() =>
                {
                    var directory = Path.GetDirectoryName(_path) ?? ".";
                    Directory.CreateDirectory(directory);
                    var pendingDirectory = Path.Combine(directory, PendingEventsDirectoryName);
                    Directory.CreateDirectory(pendingDirectory);
                    var eventHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(record.EventId)));
                    var pendingPath = Path.Combine(
                        pendingDirectory,
                        $"{Path.GetFileName(_path)}.pending-{eventHash}.jsonl");
                    if (!File.Exists(pendingPath) && !RequiredEventAlreadyRecorded(record.EventId))
                    {
                        File.WriteAllText(pendingPath, JsonSerializer.Serialize(record, JsonOptions) + Environment.NewLine);
                    }

                    DrainRequiredEventsUnderGate();
                    return !File.Exists(pendingPath);
                });
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }
    }

    private string Serialize(string eventKind, string? goalId, string detail, DateTimeOffset? timestamp)
    {
        var record = new ConductEventRecord(
            timestamp ?? _utcNow(),
            eventKind,
            string.IsNullOrWhiteSpace(goalId) ? null : goalId,
            detail);
        return JsonSerializer.Serialize(record, JsonOptions) + Environment.NewLine;
    }

    private void DrainRequiredEventsUnderGate()
    {
        var directory = Path.GetDirectoryName(_path) ?? ".";
        var pendingDirectory = Path.Combine(directory, PendingEventsDirectoryName);
        if (!Directory.Exists(pendingDirectory))
        {
            return;
        }

        var pattern = $"{Path.GetFileName(_path)}.pending-*.jsonl";
        foreach (var pendingPath in Directory.GetFiles(pendingDirectory, pattern).Order(StringComparer.Ordinal))
        {
            var payload = File.ReadAllText(pendingPath);
            var eventId = TryReadEventId(payload);
            if (eventId is not null && RequiredEventAlreadyRecorded(eventId))
            {
                File.Delete(pendingPath);
                continue;
            }

            RotateIfNeeded();
            File.AppendAllText(_path, payload);
            File.Delete(pendingPath);
        }
    }

    private void WithRequiredEventGate(Action action) =>
        WithRequiredEventGate(() =>
        {
            action();
            return true;
        });

    private T WithRequiredEventGate<T>(Func<T> action)
    {
        lock (RequiredEventDrainGate)
        {
            using var crossProcessGate = new Mutex(initiallyOwned: false, _requiredEventMutexName);
            var ownsGate = false;
            try
            {
                try
                {
                    ownsGate = crossProcessGate.WaitOne();
                }
                catch (AbandonedMutexException)
                {
                    ownsGate = true;
                }

                return action();
            }
            finally
            {
                if (ownsGate)
                {
                    crossProcessGate.ReleaseMutex();
                }
            }
        }
    }

    internal static string RequiredEventMutexName(string path)
    {
        var normalizedPath = Path.GetFullPath(path);
        if (OperatingSystem.IsWindows())
        {
            normalizedPath = normalizedPath.ToUpperInvariant();
        }

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedPath)));
        return $"Mcg.AgentOrchestrator.ConductEventLog.{hash}";
    }

    private bool RequiredEventAlreadyRecorded(string eventId)
    {
        var directory = Path.GetDirectoryName(_path) ?? ".";
        if (!Directory.Exists(directory))
        {
            return false;
        }

        var stem = Path.GetFileNameWithoutExtension(_path);
        var extension = Path.GetExtension(_path);
        foreach (var path in Directory.EnumerateFiles(directory, $"{stem}*{extension}", SearchOption.TopDirectoryOnly))
        {
            foreach (var line in File.ReadLines(path))
            {
                if (string.Equals(TryReadEventId(line), eventId, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static string? TryReadEventId(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty("event_id", out var value)
                ? value.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private void MigrateLegacyPendingEvents()
    {
        var directory = Path.GetDirectoryName(_path) ?? ".";
        if (!Directory.Exists(directory))
        {
            return;
        }

        var normalizedPath = Path.GetFullPath(_path);
        if (!MigratedLegacyPendingPaths.TryAdd(normalizedPath, 0))
        {
            return;
        }

        try
        {
            WithRequiredEventGate(() =>
            {
                var pattern = $"{Path.GetFileName(_path)}.pending-*.jsonl";
                var legacyPaths = Directory.GetFiles(directory, pattern, SearchOption.TopDirectoryOnly);
                if (legacyPaths.Length == 0)
                {
                    return;
                }

                var pendingDirectory = Path.Combine(directory, PendingEventsDirectoryName);
                Directory.CreateDirectory(pendingDirectory);
                foreach (var legacyPath in legacyPaths)
                {
                    File.Move(legacyPath, Path.Combine(pendingDirectory, Path.GetFileName(legacyPath)));
                }
            });
        }
        catch (IOException)
        {
            MigratedLegacyPendingPaths.TryRemove(normalizedPath, out _);
        }
        catch (UnauthorizedAccessException)
        {
            MigratedLegacyPendingPaths.TryRemove(normalizedPath, out _);
        }
    }

    private void RotateIfNeeded()
    {
        if (_maxBytes <= 0 || !File.Exists(_path))
            return;

        var info = new FileInfo(_path);
        if (info.Length < _maxBytes)
            return;

        var stamp = _utcNow().ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        var rotatedPath = Path.Combine(
            info.DirectoryName ?? ".",
            $"{Path.GetFileNameWithoutExtension(_path)}-{stamp}{info.Extension}");
        var candidate = rotatedPath;
        for (var index = 1; File.Exists(candidate); index++)
        {
            candidate = Path.Combine(
                info.DirectoryName ?? ".",
                $"{Path.GetFileNameWithoutExtension(_path)}-{stamp}-{index}{info.Extension}");
        }

        File.Move(_path, candidate);
        PruneRotatedGenerations(info.DirectoryName ?? ".", info.Name);
    }

    private void PruneRotatedGenerations(string directory, string currentFileName)
    {
        var stem = Path.GetFileNameWithoutExtension(currentFileName);
        var extension = Path.GetExtension(currentFileName);
        var rotated = Directory.EnumerateFiles(directory, $"{stem}-*{extension}", SearchOption.TopDirectoryOnly)
            .Select(path => new FileInfo(path))
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .ThenByDescending(file => file.Name, StringComparer.Ordinal)
            .Skip(_rotatedGenerationCount)
            .ToArray();
        foreach (var file in rotated)
        {
            try
            {
                file.Delete();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A reader may still hold a rotated generation. Keep it for the next rotation.
            }
        }
    }
}

internal sealed record ConductEventRecord(
    DateTimeOffset Timestamp,
    string EventKind,
    string? GoalId,
    string Detail);

internal sealed record ConductEvidenceLifecycleEvent(
    DateTimeOffset Timestamp,
    string EventKind,
    string GoalId,
    [property: JsonPropertyName("goal")] string Goal,
    [property: JsonPropertyName("attempt")] string Attempt,
    [property: JsonPropertyName("ordinal")] int Ordinal,
    [property: JsonPropertyName("event_id")] string EventId,
    [property: JsonPropertyName("duration_s")] object? DurationSeconds = null,
    [property: JsonPropertyName("outcome")] string? Outcome = null,
    [property: JsonPropertyName("tests_executed")] object? TestsExecuted = null,
    [property: JsonPropertyName("superseded_by")] string? SupersededBy = null,
    [property: JsonPropertyName("cause")] string? Cause = null,
    [property: JsonPropertyName("detail")] string? Detail = null,
    [property: JsonPropertyName("candidate_sha")] string? CandidateSha = null,
    [property: JsonPropertyName("policy")] string? Policy = null,
    [property: JsonPropertyName("batch_id")] string? BatchId = null,
    [property: JsonPropertyName("member_requests")] IReadOnlyList<string>? MemberRequests = null,
    [property: JsonPropertyName("request_disposition")] string? RequestDisposition = null,
    [property: JsonPropertyName("finding_round_fingerprint")] string? FindingRoundFingerprint = null,
    [property: JsonPropertyName("finding_stable_id")] string? FindingStableId = null,
    [property: JsonPropertyName("request_identity")] string? RequestIdentity = null,
    [property: JsonPropertyName("receipt_id")] string? ReceiptId = null,
    [property: JsonPropertyName("request_dispositions")] IReadOnlyList<FindingEvidenceRequestDisposition>? RequestDispositions = null);
