namespace Mcg.AgentOrchestrator.App.OwnerConsole;

// The read model's session caches belong to one reader at a time, including late reads.
internal sealed class SerializedOwnerQuestionSource(IOwnerQuestionSource source) : IOwnerQuestionSource
{
    private readonly SemaphoreSlim _read = new(1, 1);

    public async Task<IReadOnlyList<OwnerQuestion>> ListOpenAsync(CancellationToken cancellationToken) =>
        (await ReadAsync(cancellationToken)).Live;

    public async Task<OwnerQuestionSnapshot> ReadAsync(CancellationToken cancellationToken)
    {
        await _read.WaitAsync(cancellationToken);
        try { return await source.ReadAsync(cancellationToken); }
        finally { _read.Release(); }
    }
}
