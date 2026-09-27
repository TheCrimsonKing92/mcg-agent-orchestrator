using System.Text;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal sealed class ConductEventFileSource(string path, TimeProvider clock) : IConductEventSource
{
    private long _position = File.Exists(path) ? new FileInfo(path).Length : 0;
    private DateTime? _creationUtc = File.Exists(path) ? File.GetCreationTimeUtc(path) : null;
    private readonly Queue<OwnerConductEvent> _available = new();
    private byte[] _partial = [];

    public DateTimeOffset? LastActivity { get; private set; } = ReadLastTimestamp(path);

    public async ValueTask<OwnerConductEvent> ReadAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_available.Count > 0) return _available.Dequeue();
            ReadAvailable();
            if (_available.Count > 0) return _available.Dequeue();
            await Task.Delay(TimeSpan.FromMilliseconds(200), clock, cancellationToken);
        }
    }

    private void ReadAvailable()
    {
        if (!File.Exists(path)) { _position = 0; _creationUtc = null; _partial = []; return; }
        FileStream stream;
        try { stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); }
        catch (FileNotFoundException) { _position = 0; _partial = []; return; }
        using (stream)
        {
        var creationUtc = File.GetCreationTimeUtc(path);
        if (_creationUtc != creationUtc) { _position = 0; _partial = []; _creationUtc = creationUtc; }
        if (stream.Length < _position) { _position = 0; _partial = []; }
        if (stream.Length == _position) return;
        stream.Position = _position;
        using var data = new MemoryStream();
        stream.CopyTo(data);
        _position = stream.Position;
        var bytes = new byte[_partial.Length + data.Length];
        _partial.CopyTo(bytes, 0);
        data.ToArray().CopyTo(bytes, _partial.Length);
        var start = 0;
        for (var index = 0; index < bytes.Length; index++)
        {
            if (bytes[index] != (byte)'\n') continue;
            var line = Encoding.UTF8.GetString(bytes, start, index - start).TrimEnd('\r');
            if (TryParse(line, out var item))
            { _available.Enqueue(item!); LastActivity = item!.Timestamp; }
            start = index + 1;
        }
        _partial = bytes[start..];
        }
    }

    internal static bool TryParse(string line, out OwnerConductEvent? item)
    {
        item = null;
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            item = new OwnerConductEvent(
                root.GetProperty("timestamp").GetDateTimeOffset(),
                root.GetProperty("eventKind").GetString() ?? string.Empty,
                root.TryGetProperty("goalId", out var goalId) ? goalId.GetString() : null,
                root.TryGetProperty("detail", out var detail) ? detail.GetString() ?? string.Empty : string.Empty);
            return true;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        { return false; }
    }

    private static DateTimeOffset? ReadLastTimestamp(string file)
    {
        if (!File.Exists(file)) return null;
        DateTimeOffset? latest = null;
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line)
            if (TryParse(line, out var item)) latest = item!.Timestamp;
        return latest;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class ConductorLeaseLiveness(string orchestratorDirectory) : IConductorLiveness
{
    public bool IsRunning() => ConductorLoopLease.IsActive(orchestratorDirectory);
}
