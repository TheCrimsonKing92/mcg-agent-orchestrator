using System.Globalization;
using System.Collections.Concurrent;
using System.Text.Json;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed class ConductEventLogWriter
{
    public const string CurrentFileName = "conduct-events.log";
    internal const string PendingEventsDirectoryName = "pending-events";
    internal const long DefaultMaxBytes = 1_048_576;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly object RequiredEventDrainGate = new();
    private static readonly ConcurrentDictionary<string, byte> MigratedLegacyPendingPaths =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly string _path;
    private readonly long _maxBytes;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Action? _beforeRequiredEventDrain;
    private readonly Action? _beforeAppendCommit;
    private readonly object _lock = new();

    public ConductEventLogWriter(
        string path,
        long maxBytes = DefaultMaxBytes,
        Func<DateTimeOffset>? utcNow = null,
        Action? beforeRequiredEventDrain = null,
        Action? beforeAppendCommit = null)
    {
        _path = path;
        _maxBytes = maxBytes;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _beforeRequiredEventDrain = beforeRequiredEventDrain;
        _beforeAppendCommit = beforeAppendCommit;
        MigrateLegacyPendingEvents();
    }

    public string CurrentPath => _path;

    public void Append(string eventKind, string? goalId, string detail, DateTimeOffset? timestamp = null)
    {
        lock (_lock)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path) ?? ".");
            lock (RequiredEventDrainGate)
            {
                DrainRequiredEvents();
                _beforeAppendCommit?.Invoke();
                RotateIfNeeded();

                File.AppendAllText(_path, Serialize(eventKind, goalId, detail, timestamp));
            }
        }
    }

    public bool AppendRequired(string eventKind, string? goalId, string detail, DateTimeOffset? timestamp = null)
    {
        lock (_lock)
        {
            var directory = Path.GetDirectoryName(_path) ?? ".";
            Directory.CreateDirectory(directory);
            var pendingDirectory = Path.Combine(directory, PendingEventsDirectoryName);
            Directory.CreateDirectory(pendingDirectory);
            var pendingPath = Path.Combine(
                pendingDirectory,
                $"{Path.GetFileName(_path)}.pending-{Guid.NewGuid():N}.jsonl");
            File.WriteAllText(pendingPath, Serialize(eventKind, goalId, detail, timestamp));

            try
            {
                DrainRequiredEvents();
                return !File.Exists(pendingPath);
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

    private void DrainRequiredEvents()
    {
        _beforeRequiredEventDrain?.Invoke();
        lock (RequiredEventDrainGate)
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
                RotateIfNeeded();
                File.AppendAllText(_path, File.ReadAllText(pendingPath));
                File.Delete(pendingPath);
            }
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
            lock (RequiredEventDrainGate)
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
            }
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
    }
}

internal sealed record ConductEventRecord(
    DateTimeOffset Timestamp,
    string EventKind,
    string? GoalId,
    string Detail);
