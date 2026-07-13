using System.Globalization;
using System.Text.Json;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed class ConductEventLogWriter
{
    public const string CurrentFileName = "conduct-events.log";
    internal const long DefaultMaxBytes = 1_048_576;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _path;
    private readonly long _maxBytes;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly object _lock = new();

    public ConductEventLogWriter(string path, long maxBytes = DefaultMaxBytes, Func<DateTimeOffset>? utcNow = null)
    {
        _path = path;
        _maxBytes = maxBytes;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    public string CurrentPath => _path;

    public void Append(string eventKind, string? goalId, string detail, DateTimeOffset? timestamp = null)
    {
        lock (_lock)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path) ?? ".");
            RotateIfNeeded();

            var record = new ConductEventRecord(
                timestamp ?? _utcNow(),
                eventKind,
                string.IsNullOrWhiteSpace(goalId) ? null : goalId,
                detail);
            File.AppendAllText(_path, JsonSerializer.Serialize(record, JsonOptions) + Environment.NewLine);
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
