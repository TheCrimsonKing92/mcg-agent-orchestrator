using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Mcg.AgentOrchestrator.Infrastructure;

// Tickets express waiting identity, not permit ownership. Execution locks remain authoritative.
public sealed class BuildPermitWaitQueue
{
    // Four minutes is about 1.8 x the worst observed gate retry gap on 2026-10-24 (2 min 13 s).
    public static readonly TimeSpan TicketStaleAfter = TimeSpan.FromMinutes(4);
    // Ten minutes is about 1.5 x the worst refused evidence retry gap on 2026-10-25 (6 min 26 s).
    public static readonly TimeSpan EvidenceTicketStaleAfter = TimeSpan.FromMinutes(10);
    // Gates and focused evidence share five-minute promotion and remain FIFO with each other.
    // This leaves a displaced, earlier worker at least seven minutes of its 12 min queue budget.
    public static readonly TimeSpan GatePrecedenceAfter = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan SynchronizationRetryBudget = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan RefreshWriteInterval = TimeSpan.FromSeconds(1);

    private readonly string _directory;
    private readonly TimeProvider _time;
    private readonly Func<int, bool> _isAlive;
    private readonly Func<string, bool>? _isHeld;
    private readonly Func<string, string?>? _leaseId;
    internal Action? SynchronizationContended { get; init; }

    public BuildPermitWaitQueue(DotnetBuildStorageRoot storageRoot, TimeProvider timeProvider,
        Func<int, bool>? isProcessAlive = null, Func<string, bool>? isExecutionLockHeld = null,
        Func<string, string?>? executionLeaseId = null)
    {
        _directory = Path.Combine(storageRoot.RootPath, "build-slots", "wait-tickets");
        _time = timeProvider;
        _isAlive = isProcessAlive ?? IsProcessAlive;
        _isHeld = isExecutionLockHeld;
        _leaseId = executionLeaseId;
    }

    public sealed record Ticket(string Key, BuildPermitClaimantKind Kind, int OwnerPid, bool OwnerBound,
        DateTimeOffset FirstWaitedAt, DateTimeOffset LastRefreshedAt);

    private sealed record Satisfaction(string ExecutionLockPath, string LeaseId);

    public string TicketPath(string key) => Path.Combine(_directory,
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..16] + ".json");

    public Ticket Register(BuildPermitClaimant claimant)
    {
        using var guard = Synchronize();
        return RegisterCore(claimant, _time.GetUtcNow());
    }

    public Ticket Adopt(BuildPermitClaimant claimant)
    {
        using var guard = Synchronize();
        var ticket = RegisterCore(claimant, _time.GetUtcNow()) with
        {
            OwnerPid = claimant.OwnerPid,
            OwnerBound = claimant.OwnerBound
        };
        Publish(TicketPath(claimant.Key), ticket);
        return ticket;
    }

    public IReadOnlyList<Ticket> Snapshot()
    {
        using var guard = Synchronize();
        return ReadOrdered(_time.GetUtcNow(), out _);
    }

    public bool Admit(BuildPermitClaimant claimant, int unheldPermitCount)
    {
        using var guard = Synchronize();
        var ordered = ReadOrdered(_time.GetUtcNow(), out _);
        return IsAdmitted(ordered, claimant, unheldPermitCount);
    }

    public void Remove(BuildPermitClaimant claimant)
    {
        using var guard = Synchronize();
        DeleteTicket(TicketPath(claimant.Key));
    }

    private FileStream Synchronize()
    {
        Directory.CreateDirectory(_directory);
        // Retry ordinary sharing collisions before falling back. Use real time here so this
        // short cross-process transaction cannot advance an injected acquisition clock.
        var started = Stopwatch.GetTimestamp();
        var spinner = new SpinWait();
        while (true)
        {
            try
            {
                return new FileStream(Path.Combine(_directory, ".queue.lock"), FileMode.OpenOrCreate,
                    FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException ex) when ((ex.HResult & 0xffff) is 32 or 33 &&
                (spinner.Count == 0 || Stopwatch.GetElapsedTime(started) < SynchronizationRetryBudget))
            {
                SynchronizationContended?.Invoke();
                spinner.SpinOnce();
            }
        }
    }

    private Ticket RegisterCore(BuildPermitClaimant claimant, DateTimeOffset now, bool throttleWrite = false)
    {
        var path = TicketPath(claimant.Key);
        Ticket? prior = null;
        if (File.Exists(path))
        {
            prior = ReadTicket(path);
            if (prior is null)
            {
                if (now - File.GetLastWriteTimeUtc(path) < TicketStaleAfter)
                    throw new IOException("corrupt-ticket");
                DeleteTicket(path);
            }
            else if (IsDead(prior, now) || IsSatisfied(path))
            {
                DeleteTicket(path);
                prior = null;
            }
        }
        var ticket = prior is null
            ? new Ticket(claimant.Key, claimant.Kind, claimant.OwnerPid, claimant.OwnerBound, now, now)
            : prior with { LastRefreshedAt = now };
        // Public Register always persists its refresh. Repeated acquisition polls can coalesce
        // writes within one second, far below the four-minute expiry window.
        if (!throttleWrite || prior is null || now - prior.LastRefreshedAt >= RefreshWriteInterval)
            Publish(path, ticket);
        return ticket;
    }

    private List<Ticket> ReadOrdered(DateTimeOffset now, out bool corrupt)
    {
        corrupt = false;
        var tickets = new List<Ticket>();
        foreach (var path in Directory.EnumerateFiles(_directory, "*.json"))
        {
            var ticket = ReadTicket(path);
            if (ticket is null)
            {
                if (now - File.GetLastWriteTimeUtc(path) >= TicketStaleAfter) DeleteTicket(path);
                else corrupt = true;
                continue;
            }
            if (IsDead(ticket, now)) { DeleteTicket(path); continue; }
            if (!IsSatisfied(path)) tickets.Add(ticket);
        }
        return tickets.OrderBy(t => (t.Kind is BuildPermitClaimantKind.Gate or BuildPermitClaimantKind.FocusedEvidence) &&
                now - t.FirstWaitedAt >= GatePrecedenceAfter ? 0 : 1)
            .ThenBy(t => t.FirstWaitedAt).ThenBy(t => t.Key, StringComparer.Ordinal).ToList();
    }

    private bool IsDead(Ticket ticket, DateTimeOffset now) =>
        now - ticket.LastRefreshedAt >= (ticket.Kind == BuildPermitClaimantKind.FocusedEvidence && ticket.OwnerBound
            ? EvidenceTicketStaleAfter : TicketStaleAfter) || (ticket.OwnerBound && !_isAlive(ticket.OwnerPid));

    private static Ticket? ReadTicket(string path)
    {
        try
        {
            var ticket = JsonSerializer.Deserialize<Ticket>(File.ReadAllText(path));
            return ticket is { Key.Length: > 0, OwnerPid: > 0 } && Enum.IsDefined(ticket.Kind) &&
                ticket.FirstWaitedAt != default && ticket.LastRefreshedAt >= ticket.FirstWaitedAt
                ? ticket : null;
        }
        catch (JsonException) { return null; }
    }

    private bool IsSatisfied(string ticketPath)
    {
        var path = ticketPath + ".satisfied";
        if (!File.Exists(path) || _isHeld is null || _leaseId is null) return false;
        var marker = JsonSerializer.Deserialize<Satisfaction>(File.ReadAllText(path));
        if (marker is null) return false;
        if (!_isHeld(marker.ExecutionLockPath) ||
            !string.Equals(_leaseId(marker.ExecutionLockPath), marker.LeaseId, StringComparison.Ordinal))
            DeleteTicket(ticketPath);
        // Acquisition consumed this waiting interval. Releasing the lease must never revive it.
        return true;
    }

    private static bool IsAdmitted(List<Ticket> ordered, BuildPermitClaimant claimant, int unheld)
    {
        var ahead = ordered.FindIndex(t => t.Key == claimant.Key);
        if (ahead < 0) ahead = ordered.Count;
        return ahead == 0 || unheld > ahead;
    }

    private static void Publish<T>(string path, T value)
    {
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(value));
            File.Move(temp, path, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private static void DeleteTicket(string path)
    {
        File.Delete(path);
        File.Delete(path + ".satisfied");
    }

    private static bool IsProcessAlive(int pid)
    {
        try { using var process = Process.GetProcessById(pid); return !process.HasExited; }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
        catch (System.ComponentModel.Win32Exception) { return true; }
    }

    internal static Waiter? CreateWaiter(BuildPermitClaimant? claimant, DotnetBuildStorageRoot storageRoot,
        TimeProvider time, Func<string, bool> isHeld, Func<string, string?> leaseId) => claimant is null
        ? null : new Waiter(new BuildPermitWaitQueue(storageRoot, time, isExecutionLockHeld: isHeld,
            executionLeaseId: leaseId), claimant);

    internal sealed class Waiter(BuildPermitWaitQueue queue, BuildPermitClaimant claimant) : IDisposable
    {
        private bool _deferReported;
        private bool _unavailableReported;
        private bool _acquired;
        public bool EnforcedThisRound { get; private set; }

        public bool CanScan(Func<int> countUnheld)
        {
            EnforcedThisRound = false;
            try
            {
                using var guard = queue.Synchronize();
                var now = queue._time.GetUtcNow();
                queue.RegisterCore(claimant, now, throttleWrite: true);
                var unheld = countUnheld();
                var ordered = queue.ReadOrdered(now, out var corrupt);
                if (corrupt) throw new IOException("corrupt-ticket");
                EnforcedThisRound = true;
                if (IsAdmitted(ordered, claimant, unheld)) return true;
                if (!_deferReported)
                {
                    Console.WriteLine($"PERMIT_QUEUE_DEFER wantedBy={claimant.Key} kind={claimant.Kind} ahead={ordered.FindIndex(t => t.Key == claimant.Key)} unheldPermits={unheld} head={ordered[0].Key}");
                    _deferReported = true;
                }
                return false;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                Unavailable(ex);
                return true;
            }
        }

        public DotnetBuildLeaseAcquisition Complete(DotnetBuildLeaseAcquisition result)
        {
            if (result is not DotnetBuildLeaseAcquisition.Acquired acquired) return result;
            _acquired = true;
            try { queue.Remove(claimant); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                RemovalFailed(ex);
                try
                {
                    // Atomic publication must work even when the queue transaction lock is
                    // occupied: that collision may be why removal failed in the first place.
                    Publish(queue.TicketPath(claimant.Key) + ".satisfied",
                        new Satisfaction(acquired.Lease.Environment.ExecutionLockPath, acquired.Lease.Environment.LeaseId));
                }
                catch (Exception markerError) when (markerError is IOException or UnauthorizedAccessException)
                { Console.WriteLine($"BUILD_PERMIT_QUEUE_SATISFACTION_FAILED claimant={claimant.Key} reason={markerError.GetType().Name}"); }
            }
            return result;
        }

        public void Dispose()
        {
            if (_acquired || claimant.Kind != BuildPermitClaimantKind.WorkerBuild) return;
            try { queue.Remove(claimant); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Unavailable(ex); }
        }

        private void Unavailable(Exception ex)
        {
            if (_unavailableReported) return;
            Console.WriteLine($"BUILD_PERMIT_QUEUE_UNAVAILABLE claimant={claimant.Key} reason={ex.GetType().Name}");
            _unavailableReported = true;
        }

        private void RemovalFailed(Exception ex) =>
            Console.WriteLine($"BUILD_PERMIT_QUEUE_REMOVAL_FAILED claimant={claimant.Key} reason={ex.GetType().Name}");
    }
}
