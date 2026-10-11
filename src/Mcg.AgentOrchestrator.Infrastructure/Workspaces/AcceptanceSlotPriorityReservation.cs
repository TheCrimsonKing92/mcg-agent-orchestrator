namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed class AcceptanceSlotPriorityReservation : IDisposable
{
    private readonly List<FileStream> _locks = [];
    private readonly TimeProvider _clock;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private bool _released;

    private AcceptanceSlotPriorityReservation(TimeProvider clock, TimeSpan maximumHold,
        Func<TimeSpan, CancellationToken, Task> delay)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maximumHold, TimeSpan.Zero);
        _clock = clock;
        _delay = delay;
        MaximumHold = maximumHold;
        TakenAt = clock.GetUtcNow();
    }

    internal DateTimeOffset TakenAt { get; }
    internal TimeSpan MaximumHold { get; }
    internal int HeldSlotCount => _locks.Count;
    internal int ReservedSlotCount { get; private set; }
    internal bool IsExpired { get; private set; }
    internal int ReleaseCount { get; private set; }

    /// <summary>The hold bound is roundCount * interRoundDelay plus one acquisition timeout.</summary>
    internal static TimeSpan MaximumHoldFor(int roundCount, TimeSpan interRoundDelay, TimeSpan acquisitionTimeout) =>
        TimeSpan.FromTicks(checked(roundCount * interRoundDelay.Ticks + acquisitionTimeout.Ticks));

    internal static AcceptanceSlotPriorityReservation Take(DotnetBuildStorageRoot storageRoot,
        TimeProvider clock, TimeSpan maximumHold, Func<TimeSpan, CancellationToken, Task> delay)
    {
        var reservation = new AcceptanceSlotPriorityReservation(clock, maximumHold, delay);
        try
        {
            for (var slot = 0; slot < DotnetBuildEnvironmentManager.StableSlotCount; slot++)
            {
                FileStream? stream = null;
                try
                {
                    var path = DotnetBuildEnvironmentManager.AcceptancePriorityLockPath(slot, storageRoot);
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
                    stream.Lock(0, 1);
                    reservation._locks.Add(stream);
                    stream = null;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
                finally
                {
                    stream?.Dispose();
                }
            }
            reservation.ReservedSlotCount = reservation.HeldSlotCount;
            return reservation;
        }
        catch
        {
            reservation.Dispose();
            throw;
        }
    }

    internal bool ObserveExpiry()
    {
        if (!_released && _clock.GetUtcNow() - TakenAt >= MaximumHold)
        {
            IsExpired = true;
            Dispose();
        }
        return IsExpired;
    }

    internal void Delay(TimeSpan duration, CancellationToken cancellationToken)
    {
        ObserveExpiry();
        try
        {
            _delay(duration, cancellationToken).GetAwaiter().GetResult();
        }
        finally
        {
            ObserveExpiry();
        }
    }

    public void Dispose()
    {
        if (_released) return;
        _released = true;
        ReleaseCount++;
        for (var index = _locks.Count - 1; index >= 0; index--)
        {
            var stream = _locks[index];
            try { stream.Unlock(0, 1); }
            catch (IOException) { }
            finally { stream.Dispose(); }
        }
        _locks.Clear();
    }
}
