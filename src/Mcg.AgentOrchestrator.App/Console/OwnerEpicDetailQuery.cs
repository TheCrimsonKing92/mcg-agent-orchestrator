namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal sealed class OwnerEpicDetailQuery(IOwnerConsoleEpicSource? source, TimeProvider clock)
{
    internal async Task<OwnerConsoleEpicViewModel?> LoadAsync(OwnerConsoleEpicWindow window,
        string? detailId, CancellationToken cancellationToken)
    {
        if (source is null) return null;
        var since = OwnerConsoleEpicViewModel.Cutoff(window, clock.GetUtcNow());
        if (detailId is not null)
        {
            var snapshot = await source.LoadDetailAsync(detailId, since, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return new(window, since, [snapshot.Row],
                OwnerConsoleEpicViewModel.BuildDetail(snapshot.Row, snapshot.Since, snapshot.Goals), clock.GetLocalNow());
        }
        var rows = await source.LoadAsync(since, cancellationToken).ConfigureAwait(false);
        return new(window, since, rows, LoadedAt: clock.GetLocalNow());
    }
}
