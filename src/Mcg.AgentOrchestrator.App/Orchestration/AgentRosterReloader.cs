using System.Collections;
using System.Security.Cryptography;
using System.Text;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal enum AgentRosterRefreshKind { Unchanged, Reloaded, Rejected }
internal sealed record AgentRosterRefresh(AgentRosterRefreshKind Kind, string? Detail = null);

// One conduct-loop thread owns refresh state; readers hold the view across snapshot swaps.
internal sealed class AgentRosterReloader
{
    private readonly string _catalogPath;
    private readonly Action<string> _emit;
    private readonly Func<string, byte[]> _readAllBytes;
    private AgentDefinition[] _snapshot;
    private (bool Exists, long Length, DateTime LastWriteTimeUtc) _stamp;
    private string? _acceptedHash;
    private string? _rejectedHash;
    private string? _readError;

    internal AgentRosterReloader(string catalogPath, IReadOnlyList<AgentDefinition> initial,
        Action<string> emit, Func<string, byte[]>? readAllBytes = null)
    {
        _catalogPath = catalogPath;
        _snapshot = initial.ToArray();
        _emit = emit;
        _readAllBytes = readAllBytes ?? File.ReadAllBytes;
        View = new AgentRosterView(this);
        try
        {
            var stamp = ReadStamp();
            if (stamp.Exists)
                _acceptedHash = Hash(_readAllBytes(_catalogPath));
            _stamp = stamp;
        }
        catch (Exception exception) when (IsNonFatal(exception))
        {
            Warn(exception);
        }
    }

    internal AgentRosterView View { get; }
    internal AgentDefinition[] ReadSnapshot() => Volatile.Read(ref _snapshot);

    internal static AgentRosterReloader ForConductLoop(OrchestratorWorkspace workspace,
        IReadOnlyList<AgentDefinition> initial) => new(workspace.AgentCatalogPath, initial, line =>
        {
            Console.WriteLine(line);
            new ConductEventLogWriter(workspace.ConductEventsLogPath).Append("agent-roster", null, line);
        });

    internal AgentRosterRefresh Refresh()
    {
        var previousStamp = _stamp;
        try
        {
            var stamp = ReadStamp();
            if (stamp == _stamp)
                return new(AgentRosterRefreshKind.Unchanged);
            if (!stamp.Exists)
            {
                var result = Reject("agents.json is missing");
                _stamp = stamp;
                _readError = null;
                return result;
            }

            // Commit the stamp only after a successful read, so editor locks are retried.
            var bytes = _readAllBytes(_catalogPath);
            var hash = Hash(bytes);
            if (hash == _acceptedHash || hash == _rejectedHash)
            {
                _stamp = stamp;
                _readError = null;
                return new(AgentRosterRefreshKind.Unchanged);
            }

            var offset = bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble) ? Encoding.UTF8.Preamble.Length : 0;
            var json = Encoding.UTF8.GetString(bytes, offset, bytes.Length - offset);
            if (!AgentCatalogStore.TryParseStrict(json, out var catalog, out var error))
            {
                var result = Reject(error!);
                _rejectedHash = hash;
                _stamp = stamp;
                _readError = null;
                return result;
            }

            var next = catalog!.Agents.ToArray();
            var detail = DescribeReload(ReadSnapshot(), next);
            // Emit first: even a failing sink must leave the previous roster in force.
            _emit(detail);
            Volatile.Write(ref _snapshot, next);
            _acceptedHash = hash;
            _rejectedHash = null;
            _readError = null;
            _stamp = stamp;
            return new(AgentRosterRefreshKind.Reloaded, detail);
        }
        catch (Exception exception) when (IsNonFatal(exception))
        {
            _stamp = previousStamp;
            var detail = RejectionLine(exception.Message);
            if (_readError != exception.Message)
            {
                _readError = exception.Message;
                try { _emit(detail); }
                catch (Exception emitException) when (IsNonFatal(emitException)) { Warn(emitException); }
            }
            return new(AgentRosterRefreshKind.Rejected, detail);
        }
    }

    private (bool Exists, long Length, DateTime LastWriteTimeUtc) ReadStamp()
    {
        var file = new FileInfo(_catalogPath);
        file.Refresh();
        return file.Exists ? (true, file.Length, file.LastWriteTimeUtc) : default;
    }

    private AgentRosterRefresh Reject(string error)
    {
        var detail = RejectionLine(error);
        _emit(detail);
        return new(AgentRosterRefreshKind.Rejected, detail);
    }

    private string RejectionLine(string error) =>
        $"AGENT_ROSTER_RELOAD_REJECTED error=\"{Sanitize(error)}\" kept_agents={ReadSnapshot().Length}";

    private static string DescribeReload(AgentDefinition[] previous, AgentDefinition[] next)
    {
        var old = previous.ToDictionary(agent => agent.Id.Value, StringComparer.OrdinalIgnoreCase);
        var current = next.ToDictionary(agent => agent.Id.Value, StringComparer.OrdinalIgnoreCase);
        var ordered = current.Keys.OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToArray();
        var changed = ordered.Where(id => old.TryGetValue(id, out var agent) && agent.Status != current[id].Status)
            .Select(id => $"{id}:{old[id].Status}->{current[id].Status}");
        var added = ordered.Where(id => !old.ContainsKey(id));
        var removed = old.Keys.Where(id => !current.ContainsKey(id)).OrderBy(id => id, StringComparer.OrdinalIgnoreCase);
        return $"AGENT_ROSTER_RELOADED agents={next.Length} changed={List(changed)} added={List(added)} removed={List(removed)}";
    }

    private static string List(IEnumerable<string> values)
    {
        var joined = string.Join(",", values);
        return joined.Length == 0 ? "none" : joined;
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static string Sanitize(string message) => message.Replace('\r', ' ').Replace('\n', ' ').Replace('"', '\'');
    private static bool IsNonFatal(Exception exception) =>
        exception is not OutOfMemoryException and not StackOverflowException and not AccessViolationException;

    private static void Warn(Exception exception)
    {
        try { Console.Error.WriteLine($"[AgentRosterReloader] WARNING: {Sanitize(exception.Message)}"); }
        catch (Exception warningException) when (IsNonFatal(warningException)) { }
    }
}

internal sealed class AgentRosterView(AgentRosterReloader owner) : IReadOnlyList<AgentDefinition>
{
    public int Count => owner.ReadSnapshot().Length;
    public AgentDefinition this[int index] => owner.ReadSnapshot()[index];
    public IEnumerator<AgentDefinition> GetEnumerator() =>
        ((IEnumerable<AgentDefinition>)owner.ReadSnapshot()).GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
