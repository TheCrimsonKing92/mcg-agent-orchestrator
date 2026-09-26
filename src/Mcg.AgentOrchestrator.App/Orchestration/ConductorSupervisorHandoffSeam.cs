using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record ConductorSupervisorBuildIdentity(string CommitSha, string RunDirectory);

internal sealed record ConductorSupervisorProcessIdentity(int ProcessId, DateTimeOffset StartedAt);

internal sealed record ConductorSupervisorBuildSnapshot(
    string CommitSha, string RunDirectory, string AppDllPath);

internal sealed record ConductorSupervisorPendingSnapshot(
    string RunDirectory, string AppDllPath, string RepositoryHead,
    string StagedSourceCommit, string SelfCheckDetail);

internal sealed record ConductorSupervisorHandoffRecord(
    string Token,
    ConductorSupervisorProcessIdentity Incumbent,
    ConductorSupervisorBuildIdentity FromBuild,
    ConductorSupervisorBuildSnapshot AdoptedBuild,
    ConductorSupervisorPendingSnapshot? PendingSuccessor,
    int RenewalsWithoutProgress,
    string? BlockedActivationCommit,
    int ConsecutiveFailures,
    string ReadyPath,
    ConductorSupervisorActiveChild? ActiveChild = null);

internal sealed record ConductorSupervisorReadyRecord(
    string Token, ConductorSupervisorProcessIdentity Successor,
    ConductorSupervisorBuildIdentity Build);

internal sealed record ConductorSupervisorActiveChild(
    ConductorSupervisorProcessIdentity Process, string ExitArtifactPath,
    string StdoutPath, string StderrPath, int Attempt);

internal interface IConductorSupervisorHandoffSeam
{
    string? IncomingRecordPath { get; }
    ConductorSupervisorProcessIdentity Self { get; }
    void Acquire(ConductorSupervisorBuildIdentity build);
    bool IsOwner(ConductorSupervisorProcessIdentity process);
    void Transfer(ConductorSupervisorProcessIdentity from, ConductorSupervisorProcessIdentity to,
        ConductorSupervisorBuildIdentity build);
    string WriteRecord(ConductorSupervisorHandoffRecord record);
    ConductorSupervisorHandoffRecord ReadRecord(string path);
    void WriteReady(string path, ConductorSupervisorReadyRecord ready);
    ConductorSupervisorReadyRecord? ReadReady(string path);
    ConductLoopLaunchResult Launch(ConductLoopLaunchRequest request);
    bool IsAlive(ConductorSupervisorProcessIdentity process);
    bool IsRunning(int processId);
    ConductorSupervisorProcessIdentity Identify(int processId);
    Task<ConductorSupervisorProcessResult> ObserveChildAsync(
        ConductorSupervisorActiveChild child, CancellationToken cancellationToken);
    void StopPending(int processId);
}

internal sealed record ConductorSupervisorHandoffOptions(
    IConductorSupervisorHandoffSeam Seam,
    ConductorSupervisorBuildIdentity OwnBuild,
    TimeSpan Timeout,
    TimeSpan PollDelay);

internal sealed class SystemConductorSupervisorHandoffSeam : IConductorSupervisorHandoffSeam
{
    internal const string InboundEnvironmentVariable = "MCG_ORCHESTRATOR_SUPERVISOR_HANDOFF_RECORD";
    private readonly string _directory;
    private readonly string _leasePath;
    private readonly IConductLockPidProbe _probe = new ConductLockPidProbe();
    private readonly Dictionary<int, DateTimeOffset> _launched = [];

    internal SystemConductorSupervisorHandoffSeam(string directory)
    {
        _directory = directory;
        _leasePath = Path.Combine(directory, "conduct-supervisor.lock");
        IncomingRecordPath = Environment.GetEnvironmentVariable(InboundEnvironmentVariable);
        Environment.SetEnvironmentVariable(InboundEnvironmentVariable, null);
        using var process = Process.GetCurrentProcess();
        Self = new ConductorSupervisorProcessIdentity(process.Id,
            new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero));
    }

    public string? IncomingRecordPath { get; }
    public ConductorSupervisorProcessIdentity Self { get; }

    internal static ConductorSupervisorBuildIdentity ReadOwnBuild()
    {
        var runDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppContext.BaseDirectory));
        var marker = Path.Combine(runDirectory, "Mcg.AgentOrchestrator.App.dll.git-head");
        var commit = File.Exists(marker) ? File.ReadAllText(marker).Trim() : "unknown";
        return new ConductorSupervisorBuildIdentity(string.IsNullOrWhiteSpace(commit) ? "unknown" : commit,
            runDirectory);
    }

    public void Acquire(ConductorSupervisorBuildIdentity build)
    {
        Directory.CreateDirectory(_directory);
        using var stream = OpenLease();
        var owner = ReadLease(stream);
        if (owner is not null && !owner.Process.Equals(Self) && IsAlive(owner.Process))
            throw new InvalidOperationException($"Supervisor lease held by live pid {owner.Process.ProcessId}.");
        WriteLease(stream, new LeaseOwner(Self, build));
    }

    public bool IsOwner(ConductorSupervisorProcessIdentity process)
    {
        try
        {
            using var stream = OpenLease();
            return ReadLease(stream)?.Process.Equals(process) == true;
        }
        catch (IOException)
        {
            return false;
        }
    }

    public void Transfer(ConductorSupervisorProcessIdentity from, ConductorSupervisorProcessIdentity to,
        ConductorSupervisorBuildIdentity build)
    {
        using var stream = OpenLease();
        if (ReadLease(stream)?.Process.Equals(from) != true)
            throw new InvalidOperationException("Supervisor lease changed before handoff.");
        WriteLease(stream, new LeaseOwner(to, build));
    }

    public string WriteRecord(ConductorSupervisorHandoffRecord record)
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, $"supervisor-handoff-{record.Token}.json");
        WriteAtomic(path, record);
        return path;
    }

    public ConductorSupervisorHandoffRecord ReadRecord(string path) =>
        JsonSerializer.Deserialize<ConductorSupervisorHandoffRecord>(File.ReadAllText(path))
        ?? throw new InvalidDataException("Supervisor handoff record is empty.");

    public void WriteReady(string path, ConductorSupervisorReadyRecord ready) => WriteAtomic(path, ready);

    public ConductorSupervisorReadyRecord? ReadReady(string path)
    {
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<ConductorSupervisorReadyRecord>(File.ReadAllText(path)); }
        catch (IOException) { return null; }
        catch (JsonException) { return null; }
    }

    public ConductLoopLaunchResult Launch(ConductLoopLaunchRequest request)
    {
        var launched = ConductorLoopHandoff.LaunchDetached(request);
        using var process = Process.GetProcessById(launched.ProcessId);
        _launched[launched.ProcessId] = new DateTimeOffset(
            process.StartTime.ToUniversalTime(), TimeSpan.Zero);
        return launched;
    }

    public bool IsAlive(ConductorSupervisorProcessIdentity process) =>
        _probe.IsSameProcess(process.ProcessId, process.StartedAt);

    public bool IsRunning(int processId) =>
        _launched.TryGetValue(processId, out var startedAt) && _probe.IsSameProcess(processId, startedAt);

    public ConductorSupervisorProcessIdentity Identify(int processId)
    {
        using var process = Process.GetProcessById(processId);
        return new ConductorSupervisorProcessIdentity(processId,
            new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero));
    }

    public async Task<ConductorSupervisorProcessResult> ObserveChildAsync(
        ConductorSupervisorActiveChild child, CancellationToken cancellationToken)
    {
        Process process;
        try { process = Process.GetProcessById(child.Process.ProcessId); }
        catch (ArgumentException) when (ConductorContinuityExitArtifact.TryRead(child.ExitArtifactPath) is not null)
        {
            return new ConductorSupervisorProcessResult(0, child.Process.ProcessId,
                child.StdoutPath, child.StderrPath);
        }
        using (process)
        {
        if (!IsAlive(child.Process))
        {
            if (ConductorContinuityExitArtifact.TryRead(child.ExitArtifactPath) is not null)
                return new ConductorSupervisorProcessResult(0, child.Process.ProcessId,
                    child.StdoutPath, child.StderrPath);
            throw new InvalidOperationException("Adopted child identity changed before attachment.");
        }
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            var terminated = process.HasExited;
            if (!terminated)
            {
                process.Kill(entireProcessTree: true);
                terminated = process.WaitForExit(5000);
            }
            return new ConductorSupervisorProcessResult(-1, child.Process.ProcessId,
                child.StdoutPath, child.StderrPath, terminated);
        }
        return new ConductorSupervisorProcessResult(process.ExitCode, child.Process.ProcessId,
            child.StdoutPath, child.StderrPath);
        }
    }

    public void StopPending(int processId)
    {
        if (IsRunning(processId)) ConductorLoopHandoff.StopFailedSuccessor(processId);
    }

    private FileStream OpenLease() => new(_leasePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

    private static LeaseOwner? ReadLease(FileStream stream)
    {
        if (stream.Length == 0) return null;
        stream.Position = 0;
        return JsonSerializer.Deserialize<LeaseOwner>(stream);
    }

    private static void WriteLease(FileStream stream, LeaseOwner owner)
    {
        stream.SetLength(0);
        stream.Position = 0;
        JsonSerializer.Serialize(stream, owner);
        stream.Flush(flushToDisk: true);
    }

    private static void WriteAtomic<T>(string path, T value)
    {
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(value));
        File.Move(temporary, path, overwrite: true);
    }

    private sealed record LeaseOwner(ConductorSupervisorProcessIdentity Process,
        ConductorSupervisorBuildIdentity Build);
}
