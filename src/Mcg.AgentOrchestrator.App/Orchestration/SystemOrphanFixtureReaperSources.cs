using System.Globalization;
using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed class SystemOrphanFixtureReaperSources(
    string orchestratorDirectory, IReadOnlyList<string> sharedStorePaths) : IOrphanFixtureReaperSources
{
    private readonly IReadOnlyList<string> _storeDirectories = sharedStorePaths
        .Select(path => Path.GetDirectoryName(path)!)
        .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    private readonly IReadOnlyList<string> _testRoots = TempRootJanitor.GetStandardSharedRoots();
    private const string SupervisorDirectory = "continuity";

    public IReadOnlyList<string> SharedTestRoots => _testRoots;
    public IReadOnlyList<string> EvidenceRoots =>
        [.. _testRoots, OrchestratorTempRoot.GetParent()];

    public string? ReadPolicyJson()
    {
        var path = Path.Combine(orchestratorDirectory, "conductor-policy.json");
        try { return File.ReadAllText(path); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    public IReadOnlyDictionary<int, ProcessInspectionRecord> ReadSnapshot()
    {
        if (!OperatingSystem.IsWindows())
            return new Dictionary<int, ProcessInspectionRecord>();
        var snapshot = ProcessCommandLines.Snapshot();
        if (snapshot.Failure is { } failure)
            throw new IOException($"Process inspection failed: {failure.Status} {failure.Operation}");
        return snapshot.Records;
    }

    public IReadOnlyList<OwnedFixtureRoot> ListOwnedRoots() =>
        TempRootJanitor.ListOwnedFixtureRoots(_testRoots);

    public IReadOnlyCollection<int> ReadConductorPids()
    {
        var pids = new HashSet<int>();
        foreach (var directory in _storeDirectories)
        {
            var path = Path.Combine(directory, "conduct-loop.lock");
            FileStream stream;
            try { stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete); }
            catch (FileNotFoundException) { continue; }
            using (stream)
            {
                using var reader = new StreamReader(stream);
                if (!int.TryParse(reader.ReadLine(), NumberStyles.None, CultureInfo.InvariantCulture,
                        out var pid) || pid <= 0)
                    throw new InvalidDataException($"Invalid conductor lock: {path}");
                pids.Add(pid);
            }
        }
        return pids;
    }

    public IReadOnlyCollection<int> ReadSupervisorPids()
    {
        var pids = new HashSet<int>();
        foreach (var directory in _storeDirectories)
        {
            var path = Path.Combine(directory, SupervisorDirectory, "conduct-supervisor.lock");
            string json;
            try { json = File.ReadAllText(path); }
            catch (FileNotFoundException) { continue; }
            catch (DirectoryNotFoundException) { continue; }
            if (string.IsNullOrWhiteSpace(json)) continue;
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("Process", out var process) ||
                !process.TryGetProperty("ProcessId", out var processId) ||
                !processId.TryGetInt32(out var pid) || pid <= 0)
                throw new InvalidDataException($"Invalid supervisor lease: {path}");
            pids.Add(pid);
        }
        return pids;
    }

    public IReadOnlyCollection<int> ReadRegisteredPids()
    {
        var pids = new HashSet<int>();
        foreach (var path in sharedStorePaths)
        {
            if (!File.Exists(path) || !StateDbMigrations.IsUpToDate(path))
                throw new InvalidDataException($"Spawn registry unavailable: {path}");
            // SpawnRegistry.ListActive treats SQLite error 1 as empty for legacy compatibility.
            // Protection reads must let that error fail the pass instead.
            using var connection = StateDbConnectionFactory.Open(path, StateDbConnectionProfile.FastFailRead);
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT process_id FROM spawn_registry WHERE released_at IS NULL";
            using var reader = command.ExecuteReader();
            while (reader.Read())
                pids.Add(reader.GetInt32(0));
        }
        return pids;
    }

    public IReadOnlyCollection<int> ReadLineagePids(
        IReadOnlyDictionary<int, ProcessInspectionRecord> snapshot)
    {
        var pids = new HashSet<int>();
        var cursor = Environment.ProcessId;
        if (!snapshot.ContainsKey(cursor))
            throw new InvalidDataException("Current process missing from snapshot");
        while (snapshot.TryGetValue(cursor, out var child) && pids.Add(cursor))
        {
            if (child.ParentProcessId <= 0 || !snapshot.TryGetValue(child.ParentProcessId, out var parent))
                break;
            if (parent.StartedAt is null || child.StartedAt is null || parent.StartedAt > child.StartedAt)
                break;
            cursor = parent.ProcessId;
        }
        return pids;
    }

    public ProcessInspectionRecord? ReadCurrent(int processId)
    {
        var snapshot = ProcessCommandLines.Snapshot([processId]);
        if (snapshot.Failure is not null) return null;
        return snapshot.Records.GetValueOrDefault(processId);
    }

    public bool TryStopTree(int processId) => WorkerProcessJobs.TryKillOrFallbackWithoutRegistry(processId);
}
