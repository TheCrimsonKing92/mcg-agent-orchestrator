using System.Diagnostics;
using Microsoft.Win32.SafeHandles;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal enum TrialContainmentLevel
{
    None,
    WindowsLowIntegrityNoBreakawayJob
}

internal sealed record TrialRootRequest(
    string SourceRepositoryPath,
    string BaseCommit,
    string? BaseDirectory = null,
    string? Name = null,
    IReadOnlyList<string>? ProtectedPaths = null,
    IReadOnlyDictionary<string, string?>? ExtraEnvironment = null);

internal sealed record ProtectedFileFingerprint(long Length, string Sha256);

internal sealed record ProtectedPathSnapshot(
    string RootPath,
    IReadOnlyDictionary<string, ProtectedFileFingerprint> Files);

internal sealed record TrialTeardownReport(
    string RootPath,
    bool RootRemoved,
    IReadOnlyList<int> PreTeardownProcessIds,
    IReadOnlyList<int> SurvivingProcessIds,
    bool JobExitConfirmed,
    IReadOnlyList<string> OutsideWrites,
    TimeSpan CreateDuration,
    TimeSpan DestroyDuration,
    string ReceiptPath,
    IReadOnlyList<string> Diagnostics)
{
    public bool Clean =>
        RootRemoved &&
        JobExitConfirmed &&
        SurvivingProcessIds.Count == 0 &&
        OutsideWrites.Count == 0;
}

internal interface ITrialJobExitWaiter
{
    bool WaitForExit(SafeFileHandle jobHandle, TimeSpan timeout);
}

internal sealed class SystemTrialJobExitWaiter : ITrialJobExitWaiter
{
    public bool WaitForExit(SafeFileHandle jobHandle, TimeSpan timeout) =>
        OwnedProcessGroup.WaitForJobExit(jobHandle, timeout);
}

internal interface ITrialProcessInventory
{
    IReadOnlyList<int> FindSurvivors(IReadOnlyCollection<int> observedProcessIds);
}

internal sealed class SystemTrialProcessInventory : ITrialProcessInventory
{
    public IReadOnlyList<int> FindSurvivors(IReadOnlyCollection<int> observedProcessIds)
    {
        var survivors = new List<int>();
        foreach (var processId in observedProcessIds.Distinct())
        {
            try
            {
                using var process = Process.GetProcessById(processId);
                if (!process.HasExited)
                {
                    survivors.Add(processId);
                }
            }
            catch (ArgumentException)
            {
                // The process no longer exists.
            }
            catch (InvalidOperationException)
            {
                // The process exited while it was inspected.
            }
        }

        return survivors;
    }
}

internal sealed class TrialProcessHandle(Process process, string stdoutPath, string stderrPath) : IDisposable
{
    public Process Process { get; } = process;

    public int ProcessId => Process.Id;

    public string StdoutPath { get; } = stdoutPath;

    public string StderrPath { get; } = stderrPath;

    public void Dispose() => Process.Dispose();
}

internal sealed record TrialOwnedProcess(OwnedProcessGroup Group, Process Process);

internal sealed partial class TrialRootLease : IDisposable
{
    internal const string ContainmentBoundary =
        "It denies writes to Medium-or-higher objects outside the root; it does NOT deny writes to other Low-labelled objects; and network egress is NOT contained.";

    private readonly object _sync = new();
    private readonly List<TrialOwnedProcess> _processes = [];
    private readonly IReadOnlyList<ProtectedPathSnapshot> _protectedSnapshots;
    private readonly ITrialProcessInventory _processInventory;
    private readonly ITrialJobExitWaiter _jobExitWaiter;
    private readonly bool _useContainedJob;
    private readonly bool _useLowIntegrityProcess;
    private TrialTeardownReport? _teardownReport;

    internal TrialRootLease(
        string rootPath,
        string baseDirectory,
        string resolvedBaseCommit,
        IReadOnlyDictionary<string, string?> childEnvironment,
        IReadOnlyList<ProtectedPathSnapshot> protectedSnapshots,
        ITrialProcessInventory processInventory,
        ITrialJobExitWaiter jobExitWaiter,
        bool useContainedJob,
        bool useLowIntegrityProcess,
        TimeSpan createDuration,
        TrialContainmentLevel containmentLevel)
    {
        RootPath = rootPath;
        BaseDirectory = baseDirectory;
        ResolvedBaseCommit = resolvedBaseCommit;
        GitPath = Path.Combine(rootPath, ".git");
        ArtifactsPath = Path.Combine(rootPath, ".trial-state", "artifacts");
        HarnessStatePath = Path.Combine(rootPath, ".trial-state", "harness");
        TempPath = Path.Combine(rootPath, ".trial-state", "temp");
        ProfilePath = Path.Combine(rootPath, ".trial-state", "profile");
        NuGetPackagesPath = Path.Combine(rootPath, ".trial-state", "nuget", "packages");
        ChildEnvironment = childEnvironment;
        _protectedSnapshots = protectedSnapshots;
        _processInventory = processInventory;
        _jobExitWaiter = jobExitWaiter;
        _useContainedJob = useContainedJob;
        _useLowIntegrityProcess = useLowIntegrityProcess;
        CreateDuration = createDuration;
        ContainmentLevel = containmentLevel;
    }

    public string RootPath { get; }

    public string BaseDirectory { get; }

    public string ResolvedBaseCommit { get; }

    public string GitPath { get; }

    public string ArtifactsPath { get; }

    public string HarnessStatePath { get; }

    public string TempPath { get; }

    public string ProfilePath { get; }

    public string NuGetPackagesPath { get; }

    public IReadOnlyDictionary<string, string?> ChildEnvironment { get; }

    public TimeSpan CreateDuration { get; }

    public TrialContainmentLevel ContainmentLevel { get; }

    public TrialTeardownReport? TeardownReport => _teardownReport;

    public void Dispose() => _ = Destroy();
}
