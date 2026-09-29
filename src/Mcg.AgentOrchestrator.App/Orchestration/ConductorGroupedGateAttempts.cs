using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record ConductorGroupedGateMember(
    string GoalId,
    string BranchRevision,
    string CandidateRevision,
    IReadOnlyList<string> LandingPaths,
    IReadOnlyList<string> ResourceKeys,
    ChangeRiskTier ChangeRiskTier)
{
    internal static ConductorGroupedGateMember From(GateReadyCandidateProjection member) => new(
        member.GoalId.Value, member.BranchRevision, member.CandidateRevision,
        member.LandingPaths, member.ResourceKeys, member.ChangeRiskTier);

    internal GateReadyCandidateProjection ToProjection(string mainRevision) => new(
        new GoalId(GoalId), GoalLifecycleState.Verified, GateReadyVerificationState.Satisfied,
        ChangeRiskTier, ConductorTransitionDecision.Auto, LandingPaths, ResourceKeys,
        new GateReadyMergeEvidence(BranchRevision, mainRevision,
            GateReadyMergeStatus.Clean, GateReadyMergeReason.NoConflictsDetected));
}

internal sealed record ConductorGroupedGateAttempt(
    string AttemptId,
    string Kind,
    IReadOnlyList<ConductorGroupedGateMember> Members,
    string MainRevision,
    string CombinedTreeRevision,
    string ManifestIdentity,
    string IdentityValue,
    DateTimeOffset StartedAt,
    int OwnerProcessId,
    int LaunchingGenerationId,
    string MetadataPath,
    string ResultPath,
    string ExitCodePath,
    string StdoutPath,
    string StderrPath,
    string ExecutionDirectory,
    string PolicyJson,
    DateTimeOffset? OwnerProcessStartedAt = null,
    string? OwnerExecutablePath = null,
    int? AdoptedByGenerationId = null,
    DateTimeOffset? ReconciledAt = null,
    string? Detail = null,
    string Outcome = "Running");

internal sealed record ConductorGroupedGateLaunchResult(
    int ProcessId, DateTimeOffset? StartedAt, string? ExecutablePath);

internal sealed class ConductorGroupedGateAttemptCoordinator
{
    internal const string OwnedProcessSubcommandName = "__acceptance-grouped-gate-attempt";
    private static readonly ConcurrentDictionary<int, Process> OwnedProcessDrains = new();
    private readonly string _root;
    private readonly Func<ConductorGroupedGateAttempt, ConductorGroupedGateLaunchResult> _launch;
    private readonly Func<int, bool> _isProcessAlive;
    private readonly Func<ConductorGroupedGateAttempt, bool> _stop;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Action<string>? _event;
    private readonly DotnetBuildStorageRoot _buildStorageRoot;

    internal int GenerationId { get; }

    internal ConductorGroupedGateAttemptCoordinator(
        string root,
        int? generationId = null,
        Func<ConductorGroupedGateAttempt, ConductorGroupedGateLaunchResult>? launch = null,
        Func<int, bool>? isProcessAlive = null,
        Func<ConductorGroupedGateAttempt, bool>? stop = null,
        Func<DateTimeOffset>? utcNow = null,
        Action<string>? eventSink = null,
        DotnetBuildStorageRoot? buildStorageRoot = null)
    {
        _root = root;
        GenerationId = generationId ?? Environment.ProcessId;
        _launch = launch ?? LaunchOwnedProcess;
        _isProcessAlive = isProcessAlive ?? IsProcessAlive;
        _stop = stop ?? StopIdentityRevalidated;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _event = eventSink;
        _buildStorageRoot = buildStorageRoot ?? DotnetBuildEnvironmentManager.CaptureStorageRoot();
    }

    internal IEnumerable<ConductorGroupedGateAttempt> ReadAll() =>
        Directory.Exists(_root)
            ? Directory.EnumerateFiles(_root, "*.attempt.json", SearchOption.AllDirectories)
                .Select(Read)
                .OrderBy(attempt => attempt.StartedAt)
                .ToArray()
            : [];

    internal static ConductorGroupedGateAttempt Read(string path) =>
        JsonSerializer.Deserialize<ConductorGroupedGateAttempt>(File.ReadAllText(path))
        ?? throw new InvalidDataException($"Grouped gate attempt metadata is empty: {path}");

    internal static void Save(ConductorGroupedGateAttempt attempt)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(attempt.MetadataPath)!);
        var temp = attempt.MetadataPath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(attempt));
            File.Move(temp, attempt.MetadataPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    private static T WithMetadataLock<T>(string metadataPath, Func<T> action)
    {
        var lockPath = metadataPath + ".lock";
        for (var attempt = 0; attempt < 50; attempt++)
        {
            try
            {
                using var lease = new FileStream(lockPath, FileMode.OpenOrCreate,
                    FileAccess.ReadWrite, FileShare.None);
                return action();
            }
            catch (IOException) when (attempt < 49)
            {
                Thread.Sleep(10);
            }
        }
        throw new IOException($"Could not acquire grouped gate metadata lock: {lockPath}");
    }

    private static ConductorGroupedGateAttempt Update(
        string metadataPath, Func<ConductorGroupedGateAttempt, ConductorGroupedGateAttempt> change) =>
        WithMetadataLock(metadataPath, () =>
        {
            var updated = change(Read(metadataPath));
            Save(updated);
            return updated;
        });

    internal static ConductorGroupedGateAttempt? TryClaimOwner(string metadataPath,
        int processId, DateTimeOffset startedAt, string? executablePath) =>
        WithMetadataLock(metadataPath, () =>
        {
            var current = Read(metadataPath);
            if (current.ReconciledAt is not null || current.Outcome != "Running" ||
                File.Exists(current.ResultPath) || File.Exists(current.ExitCodePath) ||
                (current.OwnerProcessId > 0 && current.OwnerProcessId != processId)) return null;
            var claimed = current with
            {
                OwnerProcessId = processId,
                OwnerProcessStartedAt = startedAt,
                OwnerExecutablePath = executablePath
            };
            Save(claimed);
            return claimed;
        });

    internal ConductorGroupedGateAttempt Create(
        string kind, IReadOnlyList<GateReadyCandidateProjection> members,
        string mainRevision, string treeRevision, string manifestIdentity,
        string identityValue, string executionDirectory, ConductorAutonomyPolicy policy)
    {
        var id = Guid.NewGuid().ToString("N");
        var prefix = Path.Combine(_root, id, id);
        return new ConductorGroupedGateAttempt(
            id, kind, members.Select(ConductorGroupedGateMember.From).ToArray(),
            mainRevision, treeRevision, manifestIdentity, identityValue, _utcNow(), 0,
            GenerationId, prefix + ".attempt.json", prefix + ".result.json", prefix + ".exit",
            prefix + ".out.log", prefix + ".err.log", executionDirectory, policy.ToJson());
    }

    internal ConductorGroupedGateAttempt Launch(ConductorGroupedGateAttempt attempt)
    {
        Save(attempt); // The attempt exists before a child can start.
        ConductorGroupedGateLaunchResult launched;
        try
        {
            launched = _launch(attempt);
            if (launched.ProcessId <= 0)
                throw new InvalidOperationException("Grouped gate launcher returned no process id.");
        }
        catch (Exception ex)
        {
            Update(attempt.MetadataPath, current => current with
            {
                ReconciledAt = _utcNow(), Outcome = "LaunchFailed",
                Detail = $"launch-failed:{ex.GetType().Name}:{ex.Message}"
            });
            throw;
        }
        // A child can claim its own pid before this write. If this write fails, leave the Running
        // record for that claim rather than marking a possibly live gate as a failed launch.
        try
        {
            return Update(attempt.MetadataPath, current => current.ReconciledAt is null
                ? current with
                {
                    OwnerProcessId = launched.ProcessId,
                    OwnerProcessStartedAt = launched.StartedAt,
                    OwnerExecutablePath = launched.ExecutablePath
                }
                : current);
        }
        catch (IOException)
        {
            return attempt with { OwnerProcessId = launched.ProcessId,
                OwnerProcessStartedAt = launched.StartedAt,
                OwnerExecutablePath = launched.ExecutablePath };
        }
    }

    internal bool IsAlive(int processId) => processId > 0 && _isProcessAlive(processId);

    internal string? IdentityDifference(
        ConductorGroupedGateAttempt attempt, string kind,
        IReadOnlyList<GateReadyCandidateProjection> members,
        string mainRevision, string treeRevision)
    {
        if (!string.Equals(attempt.Kind, kind, StringComparison.Ordinal) ||
            attempt.Members.Count != members.Count ||
            !attempt.Members.Select(member => member.GoalId).ToHashSet(StringComparer.Ordinal)
                .SetEquals(members.Select(member => member.GoalId.Value)))
            return "members";
        var revisions = attempt.Members.ToDictionary(member => member.GoalId,
            member => member.CandidateRevision, StringComparer.Ordinal);
        if (members.Any(member => revisions[member.GoalId.Value] != member.CandidateRevision))
            return "candidate";
        if (!string.Equals(attempt.MainRevision, mainRevision, StringComparison.Ordinal))
            return "main";
        return !string.Equals(attempt.CombinedTreeRevision, treeRevision, StringComparison.Ordinal)
            ? "tree" : null;
    }

    internal bool ShouldAdopt(ConductorGroupedGateAttempt attempt) =>
        ConductorOrphanGateAttemptAdoption.ShouldAdopt(attempt, GenerationId, _isProcessAlive, File.Exists);

    internal ConductorGroupedGateAttempt Adopt(ConductorGroupedGateAttempt attempt)
    {
        if (attempt.AdoptedByGenerationId == GenerationId) return attempt;
        var changed = false;
        attempt = Update(attempt.MetadataPath, current =>
        {
            if (current.AdoptedByGenerationId == GenerationId || current.ReconciledAt is not null)
                return current;
            changed = true;
            return current with { AdoptedByGenerationId = GenerationId };
        });
        if (changed) _event?.Invoke($"ACCEPTANCE_COHORT_ADOPTED kind={attempt.Kind} " +
            $"members={string.Join('+', attempt.Members.Select(member => member.GoalId))} " +
            $"identity={attempt.IdentityValue} owner={attempt.OwnerProcessId} " +
            $"launchingGeneration={attempt.LaunchingGenerationId}");
        return attempt;
    }

    internal bool Refuse(ConductorGroupedGateAttempt attempt, string field)
    {
        if (!_stop(attempt)) return false;
        Update(attempt.MetadataPath, current => current with
        {
            ReconciledAt = _utcNow(), Outcome = "Reconciled",
            Detail = $"refused:identity-changed field={field}"
        });
        _event?.Invoke($"ACCEPTANCE_COHORT_ADOPTION_REFUSED kind={attempt.Kind} " +
            $"members={string.Join('+', attempt.Members.Select(member => member.GoalId))} " +
            $"reason=identity-changed field={field}");
        return true;
    }

    internal ConductorGroupedGateAttempt Reconcile(
        ConductorGroupedGateAttempt attempt, string detail) =>
        Update(attempt.MetadataPath, current => current with
        { ReconciledAt = _utcNow(), Outcome = "Reconciled", Detail = detail });

    internal ConductorGroupedGateAttempt TryReconcileDead(
        ConductorGroupedGateAttempt attempt, string detail) =>
        Update(attempt.MetadataPath, current => IsAlive(current.OwnerProcessId)
            ? current
            : current with { ReconciledAt = _utcNow(), Outcome = "Reconciled", Detail = detail });

    private static bool IsProcessAlive(int pid)
    {
        try { using var process = Process.GetProcessById(pid); return !process.HasExited; }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    private static bool StopIdentityRevalidated(ConductorGroupedGateAttempt attempt)
    {
        if (attempt.OwnerProcessId <= 0 || attempt.OwnerProcessStartedAt is null ||
            string.IsNullOrWhiteSpace(attempt.OwnerExecutablePath)) return false;
        RepoProcessCliCommand.ProcessSnapshot? Snapshot()
        {
            var current = ProcessCommandLines.Snapshot([attempt.OwnerProcessId]);
            return current.Records.TryGetValue(attempt.OwnerProcessId, out var process)
                ? new RepoProcessCliCommand.ProcessSnapshot(process.ProcessId, process.ParentProcessId,
                    process.Name, process.ExecutablePath, process.StartedAt,
                    process.CommandLine, process.Status)
                : null;
        }
        var recorded = Snapshot();
        if (recorded is null) return true; // The recorded child is already gone.
        var expected = recorded with
        {
            StartedAt = attempt.OwnerProcessStartedAt,
            ExecutablePath = attempt.OwnerExecutablePath
        };
        var current = Snapshot();
        if (RepoProcessCliCommand.EvaluateStopRevalidation(
                expected, current, [OwnedProcessSubcommandName, attempt.MetadataPath]) is not null)
            return false;
        return WorkerProcessJobs.TryKillOrFallback(attempt.OwnerProcessId);
    }

    private ConductorGroupedGateLaunchResult LaunchOwnedProcess(ConductorGroupedGateAttempt attempt)
    {
        var startInfo = BuildOwnedProcessStartInfo(attempt, _buildStorageRoot);
        var process = ProcessTreeGuiSuppression.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start grouped acceptance gate child.");
        var processId = process.Id;
        try
        {
            process.StandardInput.Close();
            // The child redirects to its own durable files before executing a gate. These drains cover startup output.
            process.OutputDataReceived += (_, line) => Append(attempt.StdoutPath, line.Data);
            process.ErrorDataReceived += (_, line) => Append(attempt.StderrPath, line.Data);
            process.Exited += (_, _) =>
            {
                if (OwnedProcessDrains.TryRemove(processId, out var completed)) completed.Dispose();
            };
            process.EnableRaisingEvents = true;
            OwnedProcessDrains[processId] = process;
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
        {
            // The child owns its durable streams; an unavailable startup pipe must not be classified as
            // a failed launch after the process exists.
        }
        DateTimeOffset? startedAt = null;
        string? executablePath = null;
        try { startedAt = process.StartTime.ToUniversalTime(); }
        catch (System.ComponentModel.Win32Exception) { }
        catch (InvalidOperationException) { }
        try { executablePath = process.MainModule?.FileName; }
        catch (System.ComponentModel.Win32Exception) { }
        catch (InvalidOperationException) { }
        var result = new ConductorGroupedGateLaunchResult(processId, startedAt, executablePath);
        try
        {
            if (process.HasExited && OwnedProcessDrains.TryRemove(processId, out var completed))
                completed.Dispose();
        }
        catch (InvalidOperationException) { }
        return result;
    }

    internal static ProcessStartInfo BuildOwnedProcessStartInfo(
        ConductorGroupedGateAttempt attempt,
        DotnetBuildStorageRoot buildStorageRoot,
        string? executable = null,
        IReadOnlyList<string>? commandLineArgs = null)
    {
        executable ??= Environment.ProcessPath ?? "dotnet";
        commandLineArgs ??= Environment.GetCommandLineArgs();
        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = attempt.ExecutionDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase) &&
            commandLineArgs.Count > 0)
            startInfo.ArgumentList.Add(commandLineArgs[0]);
        startInfo.ArgumentList.Add(OwnedProcessSubcommandName);
        startInfo.ArgumentList.Add(attempt.MetadataPath);
        GoalAcceptanceVerifier.ConfigureHermeticVerificationEnvironment(startInfo.Environment, attempt.ExecutionDirectory);
        startInfo.Environment[DotnetBuildEnvironmentManager.IsolatedRootOverrideVariable] =
            buildStorageRoot.RootPath;
        return startInfo;
    }

    private static void Append(string path, string? line)
    {
        if (line is null) return;
        try { File.AppendAllText(path, line + Environment.NewLine); }
        catch (IOException) { }
    }
}
