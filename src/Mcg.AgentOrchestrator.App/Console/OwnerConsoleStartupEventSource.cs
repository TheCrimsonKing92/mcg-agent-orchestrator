namespace Mcg.AgentOrchestrator.App.OwnerConsole;

// History access must not prevent opening the console. The unchanged pump owns read retries.
internal sealed class OwnerConsoleStartupEventSource : IConductEventSource
{
    private readonly string _path;
    private readonly TimeProvider _clock;
    private ConductEventFileSource? _source;

    internal OwnerConsoleStartupEventSource(string path, TimeProvider clock)
    {
        _path = path;
        _clock = clock;
        try { _source = new(path, clock); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public DateTimeOffset? LastActivity => _source?.LastActivity;

    public ValueTask<OwnerConductEvent> ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _source ??= new(_path, _clock);
        return _source.ReadAsync(cancellationToken);
    }

    public ValueTask DisposeAsync() => _source?.DisposeAsync() ?? ValueTask.CompletedTask;
}
