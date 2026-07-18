using System.Diagnostics;
using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal enum ConductorParallelAcceptanceAttemptOutcome
{
    Running,
    Passed,
    Failed,
    StaleCandidate,
    ProcessDied,
    CorruptArtifacts,
    Cancelled,
    BlockedBuildSlot,
    BlockedBuildLock,
    LaunchFailed,
    Reconciled
}

internal enum ConductorParallelAcceptanceAttemptDecisionKind
{
    Started,
    Running,
    Completed,
    TerminalWithoutRun
}

internal sealed record ConductorParallelAcceptanceAttempt(
    string AttemptId,
    string GoalId,
    string GoalPrefix,
    int SlotIndex,
    string? BranchHeadSha,
    string? MainHeadSha,
    DateTimeOffset StartedAt,
    DateTimeOffset LastHeartbeatAt,
    int OwnerProcessId,
    ConductorParallelAcceptanceAttemptOutcome Outcome,
    string StdoutPath,
    string StderrPath,
    string ExitCodePath,
    string HeartbeatPath,
    string ResultPath,
    string MetadataPath,
    string? ExecutionDirectory = null,
    string? PolicyName = null,
    IReadOnlyList<string>? ScopePaths = null,
    DateTimeOffset? CompletedAt = null,
    DateTimeOffset? ReconciledAt = null,
    string? Detail = null,
    int TransientFailureCount = 0,
    IReadOnlyList<string>? TestResultPaths = null,
    IReadOnlyList<string>? LeaseReceipts = null,
    int ReplayedLeaseReceiptCount = 0)
{
    public string CandidateKey => $"{GoalId}:{BranchHeadSha ?? "unknown-branch"}:{MainHeadSha ?? "unknown-main"}";
}

internal sealed record ConductorParallelAcceptanceAttemptDecision(
    ConductorParallelAcceptanceAttemptDecisionKind Kind,
    ConductorParallelAcceptanceAttempt Attempt,
    ConductorParallelAcceptanceRunResult? Run = null)
{
    public static ConductorParallelAcceptanceAttemptDecision Started(ConductorParallelAcceptanceAttempt attempt) =>
        new(ConductorParallelAcceptanceAttemptDecisionKind.Started, attempt);

    public static ConductorParallelAcceptanceAttemptDecision Running(ConductorParallelAcceptanceAttempt attempt) =>
        new(ConductorParallelAcceptanceAttemptDecisionKind.Running, attempt);

    public static ConductorParallelAcceptanceAttemptDecision Completed(
        ConductorParallelAcceptanceAttempt attempt,
        ConductorParallelAcceptanceRunResult run) =>
        new(ConductorParallelAcceptanceAttemptDecisionKind.Completed, attempt, run);

    public static ConductorParallelAcceptanceAttemptDecision TerminalWithoutRun(
        ConductorParallelAcceptanceAttempt attempt) =>
        new(ConductorParallelAcceptanceAttemptDecisionKind.TerminalWithoutRun, attempt);
}

internal sealed record ConductorParallelAcceptanceOwnedProcessLaunch(
    ConductorParallelAcceptanceAttempt Attempt,
    Action<int> ExecuteInCurrentProcess);

internal sealed record ConductorParallelAcceptanceOwnedProcessLaunchResult(int ProcessId);

internal delegate ConductorParallelAcceptanceRunResult ConductorParallelAcceptanceRunAcceptance(
    ConductorParallelAcceptanceCandidate candidate,
    ConductorAutonomyPolicy policy,
    DotnetBuildEnvironmentLease? stableSlotLease,
    CancellationToken cancellationToken);

internal sealed class ConductorParallelAcceptanceAttemptCoordinator
{
    internal const string OwnedProcessSubcommandName = "__acceptance-gate-attempt";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly object MetadataWriteGate = new();
    private const int RetainedAttemptCountPerGoal = 20;

    private readonly string _rootDirectory;
    private readonly string? _executionDirectory;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Func<int, bool> _isProcessAlive;
    private readonly Func<ConductorParallelAcceptanceOwnedProcessLaunch, ConductorParallelAcceptanceOwnedProcessLaunchResult> _launchOwnedProcess;
    private readonly bool _runInline;

    internal ConductorParallelAcceptanceAttemptCoordinator(
        string rootDirectory,
        string? executionDirectory = null,
        Func<DateTimeOffset>? utcNow = null,
        Func<int, bool>? isProcessAlive = null,
        Func<ConductorParallelAcceptanceOwnedProcessLaunch, ConductorParallelAcceptanceOwnedProcessLaunchResult>? launchOwnedProcess = null,
        bool runInline = false)
    {
        _rootDirectory = rootDirectory;
        _executionDirectory = executionDirectory;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _isProcessAlive = isProcessAlive ?? IsProcessAlive;
        _launchOwnedProcess = launchOwnedProcess ?? LaunchExternalOwnedProcess;
        _runInline = runInline;
    }

    internal ConductorParallelAcceptanceAttemptDecision Evaluate(
        ConductorParallelAcceptanceCandidate candidate,
        ConductorAutonomyPolicy policy,
        Func<ConductorParallelAcceptanceCandidate, ConductorAutonomyPolicy, ConductorParallelAcceptanceRunResult> runAcceptance) =>
        Evaluate(candidate, policy, (attemptCandidate, attemptPolicy, _, _) => runAcceptance(attemptCandidate, attemptPolicy));

    internal ConductorParallelAcceptanceAttemptDecision Evaluate(
        ConductorParallelAcceptanceCandidate candidate,
        ConductorAutonomyPolicy policy,
        ConductorParallelAcceptanceRunAcceptance runAcceptance)
    {
        var current = TryReadLatest(candidate.Goal.Id.Value);
        if (current is not null && IsReconciled(current))
        {
            current = null;
        }

        if (current is not null && IsTerminalWithoutRunOutcome(current.Outcome))
        {
            if (!string.Equals(current.CandidateKey, candidate.CandidateKey, StringComparison.Ordinal))
            {
                MarkStale(current);
                return Launch(candidate, policy, runAcceptance);
            }

            return ConductorParallelAcceptanceAttemptDecision.TerminalWithoutRun(current);
        }

        if (current is not null && !IsReconciled(current))
        {
            var terminal = TryCompleteRunningAttempt(current, candidate);
            if (terminal is { Run: not null })
            {
                if (!string.Equals(terminal.Attempt.CandidateKey, candidate.CandidateKey, StringComparison.Ordinal))
                {
                    MarkStale(terminal.Attempt);
                    return Launch(candidate, policy, runAcceptance);
                }

                return terminal;
            }

            if (terminal is not null)
            {
                if (!string.Equals(terminal.Attempt.CandidateKey, candidate.CandidateKey, StringComparison.Ordinal))
                {
                    MarkStale(terminal.Attempt);
                    return Launch(candidate, policy, runAcceptance);
                }

                return terminal;
            }

            return ConductorParallelAcceptanceAttemptDecision.Running(current);
        }

        return Launch(candidate, policy, runAcceptance);
    }

    internal void MarkReconciled(ConductorParallelAcceptanceAttempt attempt)
    {
        lock (MetadataWriteGate)
        {
            var current = TryReadAttemptFile(attempt.MetadataPath) ?? attempt;
            if (current.ReconciledAt.HasValue)
            {
                return;
            }

            WriteAttemptFile(current with
            {
                ReconciledAt = _utcNow(),
                LastHeartbeatAt = _utcNow()
            });
        }
    }

    internal IReadOnlyList<string> TakePendingLeaseReceipts(ConductorParallelAcceptanceAttempt attempt)
    {
        lock (MetadataWriteGate)
        {
            var current = TryReadAttemptFile(attempt.MetadataPath) ?? attempt;
            var receipts = current.LeaseReceipts ?? [];
            var replayedCount = Math.Clamp(current.ReplayedLeaseReceiptCount, 0, receipts.Count);
            if (replayedCount >= receipts.Count)
            {
                return [];
            }

            var pending = receipts.Skip(replayedCount).ToArray();
            WriteAttemptFile(current with
            {
                ReplayedLeaseReceiptCount = receipts.Count,
                LastHeartbeatAt = _utcNow()
            });
            return pending;
        }
    }

    private ConductorParallelAcceptanceAttemptDecision Launch(
        ConductorParallelAcceptanceCandidate candidate,
        ConductorAutonomyPolicy policy,
        ConductorParallelAcceptanceRunAcceptance runAcceptance)
    {
        var attempt = CreateAttempt(candidate, policy);
        try
        {
            Persist(attempt);
            WriteHeartbeat(attempt, "starting");
            File.AppendAllText(attempt.StdoutPath, $"acceptance attempt {attempt.AttemptId} started for {attempt.GoalPrefix} slot-{attempt.SlotIndex}{Environment.NewLine}");

            if (_runInline)
            {
                RunAttempt(attempt, candidate, policy, runAcceptance);
                var completed = TryReadLatest(candidate.Goal.Id.Value) ?? attempt;
                return TryCompleteRunningAttempt(completed, candidate)
                    ?? ConductorParallelAcceptanceAttemptDecision.TerminalWithoutRun(completed);
            }

            var launch = _launchOwnedProcess(new ConductorParallelAcceptanceOwnedProcessLaunch(
                attempt,
                childPid =>
                {
                    var activeAttempt = TryPersistOwnerProcess(attempt, childPid);
                    if (activeAttempt.Outcome != ConductorParallelAcceptanceAttemptOutcome.Running)
                    {
                        return;
                    }

                    RunAttempt(activeAttempt, candidate, policy, runAcceptance);
                }));
            var launched = TryPersistOwnerProcess(attempt, launch.ProcessId);
            if (launched.Outcome == ConductorParallelAcceptanceAttemptOutcome.Running)
            {
                WriteHeartbeat(launched, "running");
            }

            return ConductorParallelAcceptanceAttemptDecision.Started(launched);
        }
        catch (Exception ex)
        {
            var transientFailureCount = IsTransientAttemptIo(ex)
                ? CountConsecutiveTransientFailures(attempt) + 1
                : 0;
            var failed = attempt with
            {
                Outcome = ConductorParallelAcceptanceAttemptOutcome.LaunchFailed,
                CompletedAt = _utcNow(),
                Detail = ex.Message,
                TransientFailureCount = transientFailureCount
            };
            Persist(failed);
            TryAppend(attempt.StderrPath, $"launch failed: {ex}{Environment.NewLine}");
            TryWriteExit(attempt.ExitCodePath, 1);
            return ConductorParallelAcceptanceAttemptDecision.TerminalWithoutRun(failed);
        }
    }

    internal void RunAttemptForTests(
        ConductorParallelAcceptanceAttempt attempt,
        ConductorParallelAcceptanceCandidate candidate,
        ConductorAutonomyPolicy policy,
        Func<ConductorParallelAcceptanceCandidate, ConductorAutonomyPolicy, ConductorParallelAcceptanceRunResult> runAcceptance) =>
        RunAttemptForTests(attempt, candidate, policy, (attemptCandidate, attemptPolicy, _, _) => runAcceptance(attemptCandidate, attemptPolicy));

    internal void RunAttemptForTests(
        ConductorParallelAcceptanceAttempt attempt,
        ConductorParallelAcceptanceCandidate candidate,
        ConductorAutonomyPolicy policy,
        ConductorParallelAcceptanceRunAcceptance runAcceptance)
    {
        RunAttempt(attempt, candidate, policy, runAcceptance);
    }

    internal static int RunOwnedProcess(string metadataPath)
    {
        ConductorParallelAcceptanceAttempt? attempt = null;
        try
        {
            attempt = JsonSerializer.Deserialize<ConductorParallelAcceptanceAttempt>(
                ReadAllTextSharedWithRetry(metadataPath),
                JsonOptions);
            if (attempt is null)
            {
                throw new InvalidOperationException("acceptance attempt metadata was empty");
            }

            RedirectConsole(attempt);
            var executionDirectory = !string.IsNullOrWhiteSpace(attempt.ExecutionDirectory)
                ? attempt.ExecutionDirectory!
                : OrchestratorWorkspace.ResolveRepoRoot(Environment.CurrentDirectory);
            var workspace = OrchestratorWorkspace.ForDirectory(executionDirectory, executionDirectory);
            var stateRepository = new SqliteOrchestratorStateRepository(workspace.SqliteStatePath);
            var kernel = stateRepository.LoadAsync().GetAwaiter().GetResult();
            var goal = kernel.Goals.FirstOrDefault(g => g.Id.Value == attempt.GoalId)
                ?? throw new InvalidOperationException($"goal {attempt.GoalPrefix} was not found for acceptance attempt");
            var providers = ProviderRegistryFactory.CreateDefaultProviders();
            var agentFallback = ProviderRegistryFactory.IsOllamaReachable() ? AgentCatalog.OllamaDefault() : null;
            var agents = AgentCatalogStore.Load(workspace.AgentCatalogPath, agentFallback).Agents;
            var profiles = WorkerProfileStore.Load(workspace.WorkerProfilePath);
            var driver = new ConductorDriver(
                kernel,
                workspace,
                new GoalAcceptanceVerifier(),
                agents,
                profiles,
                NullOperatorChannel.Instance,
                providers);
            var policy = ConductorAutonomyPolicy.All.FirstOrDefault(candidatePolicy =>
                    string.Equals(candidatePolicy.Name, attempt.PolicyName, StringComparison.OrdinalIgnoreCase))
                ?? ConductorAutonomyPolicy.Default;
            var candidate = ConductorParallelAcceptanceCandidate.Create(
                goal,
                attempt.SlotIndex,
                attempt.ScopePaths ?? [],
                attempt.BranchHeadSha,
                attempt.MainHeadSha);
            var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(
                Path.GetDirectoryName(Path.GetDirectoryName(attempt.MetadataPath) ?? string.Empty) ?? executionDirectory,
                executionDirectory);
            var activeAttempt = coordinator.TryPersistOwnerProcess(attempt, Environment.ProcessId);
            if (activeAttempt.Outcome != ConductorParallelAcceptanceAttemptOutcome.Running)
            {
                return 0;
            }

            coordinator.RunAttempt(
                activeAttempt,
                candidate,
                policy,
                driver.RunParallelLandingAcceptance);
            return 0;
        }
        catch (Exception ex)
        {
            if (attempt is not null)
            {
                TryAppend(attempt.StderrPath, $"{ex}{Environment.NewLine}");
                TryWriteExit(attempt.ExitCodePath, 1);
                var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(
                    Path.GetDirectoryName(Path.GetDirectoryName(attempt.MetadataPath) ?? string.Empty) ?? Environment.CurrentDirectory,
                    attempt.ExecutionDirectory);
                coordinator.CompleteWithoutResult(
                    attempt with { OwnerProcessId = Environment.ProcessId },
                    IsTransientAttemptIo(ex)
                        ? ConductorParallelAcceptanceAttemptOutcome.CorruptArtifacts
                        : ConductorParallelAcceptanceAttemptOutcome.Failed,
                    ex.Message,
                    transient: IsTransientAttemptIo(ex));
            }
            else
            {
                Console.Error.WriteLine(ex);
            }

            return 1;
        }
    }

    private void RunAttempt(
        ConductorParallelAcceptanceAttempt attempt,
        ConductorParallelAcceptanceCandidate candidate,
        ConductorAutonomyPolicy policy,
        ConductorParallelAcceptanceRunAcceptance runAcceptance)
    {
        DotnetBuildEnvironmentLease? stableSlotLease = null;
        ConductorParallelAcceptanceRunResult? run = null;
        (ConductorParallelAcceptanceAttemptOutcome Outcome, string Detail, bool Transient)? terminalWithoutResult = null;
        string? stderrDetail = null;
        try
        {
            WriteHeartbeat(attempt, "running");
            stableSlotLease = AcquireAttemptStableSlotLease(attempt, candidate);
            run = RunWithAttemptTelemetryContext(attempt, candidate, policy, stableSlotLease, runAcceptance);
        }
        catch (OperationCanceledException ex)
        {
            terminalWithoutResult = (ConductorParallelAcceptanceAttemptOutcome.Cancelled, ex.Message, false);
        }
        catch (Exception ex) when (IsTransientAttemptIo(ex))
        {
            terminalWithoutResult = (ConductorParallelAcceptanceAttemptOutcome.CorruptArtifacts, ex.Message, true);
        }
        catch (Exception ex)
        {
            run = ConductorParallelAcceptanceRunResult.Fault(candidate, ex);
            stderrDetail = ex.ToString();
        }
        finally
        {
            try
            {
                if (run is not null)
                {
                    CompleteWithRunResult(attempt, run, stderrDetail);
                }
                else if (terminalWithoutResult is { } terminal)
                {
                    CompleteWithoutResult(attempt, terminal.Outcome, terminal.Detail, terminal.Transient);
                }
            }
            finally
            {
                if (stableSlotLease is not null)
                {
                    stableSlotLease.Dispose();
                    EmitAttemptLeaseReceipt("release", attempt, candidate, Environment.ProcessId);
                }
            }
        }
    }

    private void CompleteWithRunResult(
        ConductorParallelAcceptanceAttempt attempt,
        ConductorParallelAcceptanceRunResult run,
        string? stderrDetail)
    {
        try
        {
            WriteResult(attempt.ResultPath, ToArtifact(run));
            var outcome = OutcomeFor(run);
            TryWriteExit(attempt.ExitCodePath, outcome == ConductorParallelAcceptanceAttemptOutcome.Passed ? 0 : 1);
            TryPersistTerminal(attempt, current => current with
            {
                BranchHeadSha = run.Candidate.BranchHeadSha,
                MainHeadSha = run.Candidate.MainHeadSha,
                Outcome = outcome,
                CompletedAt = _utcNow(),
                LastHeartbeatAt = _utcNow(),
                Detail = AcceptanceRunDetail(run),
                TestResultPaths = run.Acceptance?.TestResultPaths
            });
            if (!string.IsNullOrWhiteSpace(stderrDetail))
            {
                TryAppend(attempt.StderrPath, $"{stderrDetail}{Environment.NewLine}");
            }

            File.AppendAllText(attempt.StdoutPath, $"acceptance attempt {attempt.AttemptId} completed outcome={outcome}{Environment.NewLine}");
            WriteHeartbeat(attempt, "exiting");
        }
        catch (Exception ex) when (IsTransientAttemptIo(ex))
        {
            CompleteWithoutResult(
                attempt,
                ConductorParallelAcceptanceAttemptOutcome.CorruptArtifacts,
                ex.Message,
                transient: true);
        }
        catch (Exception ex)
        {
            CompleteWithoutResult(
                attempt,
                ConductorParallelAcceptanceAttemptOutcome.Failed,
                ex.Message);
            TryAppend(attempt.StderrPath, $"{ex}{Environment.NewLine}");
        }
    }

    private DotnetBuildEnvironmentLease AcquireAttemptStableSlotLease(
        ConductorParallelAcceptanceAttempt attempt,
        ConductorParallelAcceptanceCandidate candidate)
    {
        var environment = DotnetBuildEnvironmentManager.CreateStableSlotAttempt(candidate.SlotIndex);
        var acquisition = DotnetBuildEnvironmentManager.TryAcquireLeaseExecutionLock(environment, TimeSpan.Zero);
        if (acquisition is DotnetBuildLeaseAcquisition.Acquired acquired)
        {
            EmitAttemptLeaseReceipt("acquire", attempt, candidate, Environment.ProcessId);
            EmitAttemptLeaseReceipt("handoff", attempt, candidate, Environment.ProcessId);
            return acquired.Lease;
        }

        if (acquisition is DotnetBuildLeaseAcquisition.SlotsBusy busy)
        {
            var holderPid = busy.BusySlots.FirstOrDefault(slot => slot.SlotIndex == candidate.SlotIndex).OwnerProcessId;
            EmitAttemptLeaseReceipt("yield", attempt, candidate, holderPid);
            throw new DotnetBuildSlotsBusyException(busy);
        }

        if (acquisition is DotnetBuildLeaseAcquisition.BuildLockBlocked blocked)
        {
            throw new BuildLockBlockedException(blocked.Attribution);
        }

        throw new InvalidOperationException("Unknown dotnet build lease acquisition result.");
    }

    private static ConductorParallelAcceptanceRunResult RunWithAttemptTelemetryContext(
        ConductorParallelAcceptanceAttempt attempt,
        ConductorParallelAcceptanceCandidate candidate,
        ConductorAutonomyPolicy policy,
        DotnetBuildEnvironmentLease stableSlotLease,
        ConductorParallelAcceptanceRunAcceptance runAcceptance)
    {
        var previous = Environment.GetEnvironmentVariable(GoalAcceptanceVerifier.AcceptanceAttemptTrxPrefixVariable);
        var prefix = Path.Combine(Path.GetDirectoryName(attempt.MetadataPath) ?? Environment.CurrentDirectory, attempt.AttemptId);
        Environment.SetEnvironmentVariable(GoalAcceptanceVerifier.AcceptanceAttemptTrxPrefixVariable, prefix);
        try
        {
            return runAcceptance(candidate, policy, stableSlotLease, CancellationToken.None);
        }
        finally
        {
            Environment.SetEnvironmentVariable(GoalAcceptanceVerifier.AcceptanceAttemptTrxPrefixVariable, previous);
        }
    }

    private void EmitAttemptLeaseReceipt(
        string action,
        ConductorParallelAcceptanceAttempt attempt,
        ConductorParallelAcceptanceCandidate candidate,
        int? holderPid)
    {
        var pid = holderPid?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown";
        var line = $"ACCEPTANCE_LEASE_{action.ToUpperInvariant()} goal={attempt.GoalPrefix} attempt={attempt.AttemptId} slot=slot-{candidate.SlotIndex} holderPid={pid}";
        Console.WriteLine(line);
        PersistLeaseReceipt(attempt, line);
    }

    private void PersistLeaseReceipt(ConductorParallelAcceptanceAttempt attempt, string line)
    {
        lock (MetadataWriteGate)
        {
            var current = TryReadAttemptFile(attempt.MetadataPath);
            if (current is null || !string.Equals(current.AttemptId, attempt.AttemptId, StringComparison.Ordinal))
            {
                return;
            }

            var receipts = (current.LeaseReceipts ?? []).ToList();
            if (receipts.Contains(line, StringComparer.Ordinal))
            {
                return;
            }

            receipts.Add(line);
            WriteAttemptFile(current with
            {
                LeaseReceipts = receipts,
                LastHeartbeatAt = _utcNow()
            });
        }
    }

    private void CompleteWithoutResult(
        ConductorParallelAcceptanceAttempt attempt,
        ConductorParallelAcceptanceAttemptOutcome outcome,
        string detail,
        bool transient = false)
    {
        TryPersistTerminal(attempt, current => current with
        {
            Outcome = outcome,
            CompletedAt = _utcNow(),
            LastHeartbeatAt = _utcNow(),
            Detail = detail,
            TransientFailureCount = transient
                ? CountConsecutiveTransientFailures(current) + 1
                : current.TransientFailureCount
        });
        TryWriteExit(attempt.ExitCodePath, 1);
        TryAppend(attempt.StderrPath, $"{outcome}: {detail}{Environment.NewLine}");
        WriteHeartbeat(attempt, "exiting");
    }

    private ConductorParallelAcceptanceAttemptDecision? TryCompleteRunningAttempt(
        ConductorParallelAcceptanceAttempt attempt,
        ConductorParallelAcceptanceCandidate candidate)
    {
        ConductorParallelAcceptanceAttemptDecision durablePassed;
        if (attempt.Outcome == ConductorParallelAcceptanceAttemptOutcome.Running &&
            IsAttemptExecutionLeaseStillHeld(attempt))
        {
            return null;
        }

        if (File.Exists(attempt.ResultPath))
        {
            try
            {
                var artifact = JsonSerializer.Deserialize<ConductorParallelAcceptanceRunArtifact>(
                    ReadAllTextSharedWithRetry(attempt.ResultPath),
                    JsonOptions);
                if (artifact is null)
                {
                    return MarkCorrupt(attempt, "result artifact was empty");
                }

                var run = FromArtifact(candidate, artifact);
                var completed = PersistResultCandidate(attempt, run);
                return ConductorParallelAcceptanceAttemptDecision.Completed(completed, run);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
            {
                if (ex is IOException or UnauthorizedAccessException)
                {
                    if (TryBuildDurablePassedCompletion(attempt, candidate, out durablePassed))
                    {
                        return durablePassed;
                    }

                    return _isProcessAlive(attempt.OwnerProcessId) ? null : MarkTransientArtifactReadFailure(attempt, ex.Message);
                }

                return MarkCorrupt(attempt, ex.Message);
            }
        }

        if (File.Exists(attempt.ExitCodePath))
        {
            var latest = TryReadAttemptFile(attempt.MetadataPath);
            if (latest is not null && TryBuildDurablePassedCompletion(latest, candidate, out durablePassed))
            {
                return durablePassed;
            }

            if (latest is not null && IsTerminalWithoutRunOutcome(latest.Outcome))
            {
                return ConductorParallelAcceptanceAttemptDecision.TerminalWithoutRun(latest);
            }

            return MarkCorrupt(attempt, "exit artifact exists without a result artifact");
        }

        if (!_isProcessAlive(attempt.OwnerProcessId))
        {
            var latest = TryReadAttemptFile(attempt.MetadataPath);
            if (latest is not null && TryBuildDurablePassedCompletion(latest, candidate, out durablePassed))
            {
                return durablePassed;
            }

            if (latest is not null && IsTerminalWithoutRunOutcome(latest.Outcome))
            {
                return ConductorParallelAcceptanceAttemptDecision.TerminalWithoutRun(latest);
            }

            var dead = attempt with
            {
                Outcome = ConductorParallelAcceptanceAttemptOutcome.ProcessDied,
                CompletedAt = _utcNow(),
                Detail = "owner process was not alive and no terminal result artifact existed"
            };
            Persist(dead);
            return ConductorParallelAcceptanceAttemptDecision.TerminalWithoutRun(dead);
        }

        return null;
    }

    private static bool IsAttemptExecutionLeaseStillHeld(ConductorParallelAcceptanceAttempt attempt)
    {
        if (DotnetBuildEnvironmentManager.IsStableSlotExecutionLeaseAvailable(attempt.SlotIndex))
        {
            return false;
        }

        var ownerProcessId = DotnetBuildEnvironmentManager.GetStableSlotExecutionLeaseOwner(attempt.SlotIndex);
        return ownerProcessId.HasValue && ownerProcessId.Value == attempt.OwnerProcessId;
    }

    private void MarkStale(ConductorParallelAcceptanceAttempt attempt)
    {
        Persist(attempt with
        {
            Outcome = ConductorParallelAcceptanceAttemptOutcome.StaleCandidate,
            CompletedAt = _utcNow(),
            ReconciledAt = _utcNow(),
            Detail = "candidate branch/main SHA moved before reconciliation"
        });
    }

    private bool TryBuildDurablePassedCompletion(
        ConductorParallelAcceptanceAttempt attempt,
        ConductorParallelAcceptanceCandidate candidate,
        out ConductorParallelAcceptanceAttemptDecision decision)
    {
        decision = null!;
        var latest = TryReadAttemptFile(attempt.MetadataPath) ?? attempt;
        if (latest.Outcome != ConductorParallelAcceptanceAttemptOutcome.Passed)
        {
            return false;
        }

        var effectiveCandidate = ConductorParallelAcceptanceCandidate.Create(
            candidate.Goal,
            latest.SlotIndex,
            latest.ScopePaths ?? candidate.ScopePaths,
            latest.BranchHeadSha ?? candidate.BranchHeadSha,
            latest.MainHeadSha ?? candidate.MainHeadSha);
        var run = ConductorParallelAcceptanceRunResult.Accepted(
            effectiveCandidate,
            new AcceptanceVerificationSummary(
                true,
                [],
                BranchHeadSha: latest.BranchHeadSha,
                MainHeadSha: latest.MainHeadSha,
                TestResultPaths: latest.TestResultPaths));
        var completed = PersistResultCandidate(latest, run);
        decision = ConductorParallelAcceptanceAttemptDecision.Completed(completed, run);
        return true;
    }

    private ConductorParallelAcceptanceAttempt PersistResultCandidate(
        ConductorParallelAcceptanceAttempt attempt,
        ConductorParallelAcceptanceRunResult run)
    {
        lock (MetadataWriteGate)
        {
            var current = TryReadAttemptFile(attempt.MetadataPath) ?? attempt;
            if (!string.Equals(current.AttemptId, attempt.AttemptId, StringComparison.Ordinal))
            {
                return current;
            }

            var updated = current with
            {
                BranchHeadSha = run.Candidate.BranchHeadSha,
                MainHeadSha = run.Candidate.MainHeadSha,
                Outcome = current.Outcome == ConductorParallelAcceptanceAttemptOutcome.Running
                    ? OutcomeFor(run)
                    : current.Outcome,
                CompletedAt = current.CompletedAt ?? _utcNow(),
                LastHeartbeatAt = _utcNow(),
                Detail = current.Detail ?? AcceptanceRunDetail(run),
                TestResultPaths = run.Acceptance?.TestResultPaths ?? current.TestResultPaths
            };
            WriteAttemptFile(updated);
            return updated;
        }
    }

    private ConductorParallelAcceptanceAttemptDecision MarkCorrupt(
        ConductorParallelAcceptanceAttempt attempt,
        string detail)
    {
        var corrupt = attempt with
        {
            Outcome = ConductorParallelAcceptanceAttemptOutcome.CorruptArtifacts,
            CompletedAt = _utcNow(),
            Detail = detail
        };
        Persist(corrupt);
        return ConductorParallelAcceptanceAttemptDecision.TerminalWithoutRun(corrupt);
    }

    private ConductorParallelAcceptanceAttemptDecision MarkTransientArtifactReadFailure(
        ConductorParallelAcceptanceAttempt attempt,
        string detail)
    {
        var transient = attempt with
        {
            Outcome = ConductorParallelAcceptanceAttemptOutcome.CorruptArtifacts,
            CompletedAt = _utcNow(),
            Detail = detail,
            TransientFailureCount = CountConsecutiveTransientFailures(attempt) + 1
        };
        Persist(transient);
        return ConductorParallelAcceptanceAttemptDecision.TerminalWithoutRun(transient);
    }

    private ConductorParallelAcceptanceAttempt CreateAttempt(
        ConductorParallelAcceptanceCandidate candidate,
        ConductorAutonomyPolicy policy)
    {
        var startedAt = _utcNow();
        var rawId = $"{candidate.GoalPrefix}-{candidate.SlotIndex}-{startedAt:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}";
        var id = rawId[..Math.Min(64, rawId.Length)];
        var directory = Path.Combine(_rootDirectory, candidate.Goal.Id.Value);
        Directory.CreateDirectory(directory);
        PruneOldAttempts(directory, RetainedAttemptCountPerGoal - 1);
        var prefix = Path.Combine(directory, id);
        return new ConductorParallelAcceptanceAttempt(
            id,
            candidate.Goal.Id.Value,
            candidate.GoalPrefix,
            candidate.SlotIndex,
            candidate.BranchHeadSha,
            candidate.MainHeadSha,
            startedAt,
            startedAt,
            Environment.ProcessId,
            ConductorParallelAcceptanceAttemptOutcome.Running,
            prefix + ".out.log",
            prefix + ".err.log",
            prefix + ".exit.txt",
            prefix + ".heartbeat.json",
            prefix + ".result.json",
            prefix + ".attempt.json",
            _executionDirectory,
            policy.Name,
            candidate.ScopePaths);
    }

    private ConductorParallelAcceptanceAttempt? TryReadLatest(string goalId)
    {
        var directory = Path.Combine(_rootDirectory, goalId);
        if (!Directory.Exists(directory))
        {
            return null;
        }

        ConductorParallelAcceptanceAttempt? latest = null;
        foreach (var path in Directory.EnumerateFiles(directory, "*.attempt.json"))
        {
            ConductorParallelAcceptanceAttempt? attempt;
            try
            {
                attempt = JsonSerializer.Deserialize<ConductorParallelAcceptanceAttempt>(
                    ReadAllTextSharedWithRetry(path),
                    JsonOptions);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                return new ConductorParallelAcceptanceAttempt(
                    Path.GetFileNameWithoutExtension(path),
                    goalId,
                    goalId[..Math.Min(8, goalId.Length)],
                    0,
                    null,
                    null,
                    _utcNow(),
                    _utcNow(),
                    0,
                    ConductorParallelAcceptanceAttemptOutcome.CorruptArtifacts,
                    path + ".out.log",
                    path + ".err.log",
                    path + ".exit.txt",
                    path + ".heartbeat.json",
                    path + ".result.json",
                    path,
                    CompletedAt: _utcNow(),
                    Detail: ex.Message);
            }

            if (attempt is null)
            {
                continue;
            }

            latest = latest is null || attempt.StartedAt > latest.StartedAt ? attempt : latest;
        }

        return latest;
    }

    private void Persist(ConductorParallelAcceptanceAttempt attempt)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(attempt.MetadataPath) ?? _rootDirectory);
        lock (MetadataWriteGate)
        {
            WriteAttemptFile(attempt with { LastHeartbeatAt = _utcNow() });
        }
    }

    private bool TryPersistTerminal(
        ConductorParallelAcceptanceAttempt attempt,
        Func<ConductorParallelAcceptanceAttempt, ConductorParallelAcceptanceAttempt> transition)
    {
        lock (MetadataWriteGate)
        {
            var current = TryReadAttemptFile(attempt.MetadataPath);
            if (current is null ||
                !string.Equals(current.AttemptId, attempt.AttemptId, StringComparison.Ordinal) ||
                current.Outcome != ConductorParallelAcceptanceAttemptOutcome.Running)
            {
                TryAppend(attempt.StderrPath, $"terminal outcome ignored because durable attempt is no longer running{Environment.NewLine}");
                return false;
            }

            WriteAttemptFile(transition(current));
            return true;
        }
    }

    private ConductorParallelAcceptanceAttempt TryPersistOwnerProcess(
        ConductorParallelAcceptanceAttempt attempt,
        int ownerProcessId)
    {
        lock (MetadataWriteGate)
        {
            var current = TryReadAttemptFile(attempt.MetadataPath);
            if (current is null ||
                !string.Equals(current.AttemptId, attempt.AttemptId, StringComparison.Ordinal))
            {
                var missing = attempt with
                {
                    OwnerProcessId = ownerProcessId,
                    LastHeartbeatAt = _utcNow()
                };
                WriteAttemptFile(missing);
                return missing;
            }

            if (current.Outcome != ConductorParallelAcceptanceAttemptOutcome.Running)
            {
                return current;
            }

            var updated = current with
            {
                OwnerProcessId = ownerProcessId,
                LastHeartbeatAt = _utcNow()
            };
            WriteAttemptFile(updated);
            return updated;
        }
    }

    private void WriteHeartbeat(ConductorParallelAcceptanceAttempt attempt, string state)
    {
        var now = _utcNow();
        var payload = new
        {
            pid = Environment.ProcessId,
            childPid = attempt.OwnerProcessId > 0 ? attempt.OwnerProcessId : (int?)null,
            ownedPids = attempt.OwnerProcessId > 0 ? new[] { attempt.OwnerProcessId } : Array.Empty<int>(),
            startedAt = attempt.StartedAt.ToString("O"),
            lastObservedAt = now.ToString("O"),
            lastProgressAt = now.ToString("O"),
            state,
            stdoutBytes = FileLength(attempt.StdoutPath),
            stderrBytes = FileLength(attempt.StderrPath),
            ownedCpuMs = 0L,
            exitFileExists = File.Exists(attempt.ExitCodePath)
        };

        try
        {
            var tmp = TemporarySiblingPath(attempt.HeartbeatPath);
            File.WriteAllText(tmp, JsonSerializer.Serialize(payload, JsonOptions));
            File.Move(tmp, attempt.HeartbeatPath, overwrite: true);
        }
        catch
        {
            // Heartbeat is evidence, not the gate result.
        }
    }

    private ConductorParallelAcceptanceOwnedProcessLaunchResult LaunchExternalOwnedProcess(
        ConductorParallelAcceptanceOwnedProcessLaunch launch)
    {
        if (string.IsNullOrWhiteSpace(launch.Attempt.ExecutionDirectory))
        {
            throw new InvalidOperationException("acceptance attempt execution directory was not recorded");
        }

        var commandLineArgs = Environment.GetCommandLineArgs();
        var executable = Environment.ProcessPath ?? "dotnet";
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = launch.Attempt.ExecutionDirectory
        };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase) &&
            commandLineArgs.Length > 0)
        {
            startInfo.ArgumentList.Add(commandLineArgs[0]);
        }

        startInfo.ArgumentList.Add(OwnedProcessSubcommandName);
        startInfo.ArgumentList.Add(launch.Attempt.MetadataPath);
        startInfo.Environment[OrchestratorWorkspace.RepoRootEnvironmentVariable] = launch.Attempt.ExecutionDirectory;

        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("failed to start acceptance attempt process");
        var processId = process.Id;
        process.Dispose();
        return new ConductorParallelAcceptanceOwnedProcessLaunchResult(processId);
    }

    private static void RedirectConsole(ConductorParallelAcceptanceAttempt attempt)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(attempt.StdoutPath) ?? ".");
        var stdout = new StreamWriter(new FileStream(attempt.StdoutPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
        var stderr = new StreamWriter(new FileStream(attempt.StderrPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
        Console.SetOut(stdout);
        Console.SetError(stderr);
    }

    private static ConductorParallelAcceptanceRunArtifact ToArtifact(ConductorParallelAcceptanceRunResult run)
    {
        if (run.Exception is not null)
        {
            return new ConductorParallelAcceptanceRunArtifact(
                "fault",
                run.Exception switch
                {
                    DotnetBuildSlotsBusyException => "blocked-build-slot",
                    BuildLockBlockedException => "blocked-build-lock",
                    OperationCanceledException => "cancelled",
                    _ => "exception"
                },
                run.Exception.Message,
                null,
                null,
                null,
                run.Candidate.BranchHeadSha,
                run.Candidate.MainHeadSha,
                null,
                null);
        }

        if (run.EarlyResult is { Outcome: var outcome })
        {
            return outcome switch
            {
                ConductorAdvanceOutcome.Held held => EarlyArtifact("early-held", held.State, held.Reason, run),
                ConductorAdvanceOutcome.Escalated escalated => EarlyArtifact("early-escalated", escalated.State, escalated.Reason, run),
                ConductorAdvanceOutcome.Done done => EarlyArtifact("early-done", done.State, run.EarlyOutcome?.Detail, run),
                ConductorAdvanceOutcome.Executed executed => EarlyArtifact("early-executed", executed.FromState, executed.Description, run),
                _ => new("fault", "exception", "unknown early acceptance result", null, null, null, run.Candidate.BranchHeadSha, run.Candidate.MainHeadSha, null, null)
            };
        }

        return new ConductorParallelAcceptanceRunArtifact(
            "accepted",
            null,
            null,
            null,
            null,
            run.Acceptance,
            run.Candidate.BranchHeadSha,
            run.Candidate.MainHeadSha,
            null,
            null);
    }

    private static ConductorParallelAcceptanceRunArtifact EarlyArtifact(
        string kind,
        GoalLifecycleState state,
        string? message,
        ConductorParallelAcceptanceRunResult run) =>
        new(
            kind,
            null,
            null,
            state.ToString(),
            message,
            null,
            run.Candidate.BranchHeadSha,
            run.Candidate.MainHeadSha,
            run.EarlyOutcome?.Kind,
            run.EarlyOutcome?.Detail);

    private static ConductorParallelAcceptanceRunResult FromArtifact(
        ConductorParallelAcceptanceCandidate candidate,
        ConductorParallelAcceptanceRunArtifact artifact)
    {
        var effectiveCandidate = ConductorParallelAcceptanceCandidate.Create(
            candidate.Goal,
            candidate.SlotIndex,
            candidate.ScopePaths,
            artifact.BranchHeadSha ?? candidate.BranchHeadSha,
            artifact.MainHeadSha ?? candidate.MainHeadSha);

        return artifact.Kind switch
        {
            "accepted" when artifact.Acceptance is not null =>
                ConductorParallelAcceptanceRunResult.Accepted(effectiveCandidate, artifact.Acceptance),
            "early-held" => ConductorParallelAcceptanceRunResult.Early(
                effectiveCandidate,
                new ConductorAdvanceResult(
                    effectiveCandidate.Goal.Id.Value,
                    effectiveCandidate.GoalPrefix,
                    string.Empty,
                    new ConductorAdvanceOutcome.Held(ParseState(artifact.State), artifact.Message ?? "held")),
                RehydrateEarlyOutcome(artifact)),
            "early-escalated" => ConductorParallelAcceptanceRunResult.Early(
                effectiveCandidate,
                new ConductorAdvanceResult(
                    effectiveCandidate.Goal.Id.Value,
                    effectiveCandidate.GoalPrefix,
                    string.Empty,
                    new ConductorAdvanceOutcome.Escalated(ParseState(artifact.State), artifact.Message ?? "escalated")),
                RehydrateEarlyOutcome(artifact)),
            "early-done" => ConductorParallelAcceptanceRunResult.Early(
                effectiveCandidate,
                new ConductorAdvanceResult(
                    effectiveCandidate.Goal.Id.Value,
                    effectiveCandidate.GoalPrefix,
                    string.Empty,
                    new ConductorAdvanceOutcome.Done(ParseState(artifact.State))),
                RehydrateEarlyOutcome(artifact)),
            "early-executed" => ConductorParallelAcceptanceRunResult.Early(
                effectiveCandidate,
                new ConductorAdvanceResult(
                    effectiveCandidate.Goal.Id.Value,
                    effectiveCandidate.GoalPrefix,
                    string.Empty,
                    new ConductorAdvanceOutcome.Executed(ParseState(artifact.State), artifact.Message ?? "executed")),
                RehydrateEarlyOutcome(artifact)),
            "fault" => ConductorParallelAcceptanceRunResult.Fault(effectiveCandidate, RehydrateFault(artifact)),
            _ => throw new InvalidOperationException("unrecognized acceptance attempt result artifact")
        };
    }

    private static ConductorParallelAcceptanceEarlyOutcome? RehydrateEarlyOutcome(
        ConductorParallelAcceptanceRunArtifact artifact) =>
        string.IsNullOrWhiteSpace(artifact.EarlyOutcomeKind)
            ? null
            : new ConductorParallelAcceptanceEarlyOutcome(
                artifact.EarlyOutcomeKind,
                ParseState(artifact.State),
                artifact.EarlyOutcomeDetail ?? artifact.Message ?? artifact.EarlyOutcomeKind);

    private static Exception RehydrateFault(ConductorParallelAcceptanceRunArtifact artifact) =>
        artifact.FaultKind switch
        {
            "blocked-build-slot" => new DotnetBuildSlotsBusyException(
                new DotnetBuildLeaseAcquisition.SlotsBusy("background-acceptance", [])),
            "blocked-build-lock" => new BuildLockBlockedException(
                new BuildLockAttribution("unknown", [], "background-acceptance", "acceptance", "background-acceptance")),
            "cancelled" => new OperationCanceledException(artifact.FaultMessage),
            _ => new InvalidOperationException(artifact.FaultMessage ?? "background acceptance failed")
        };

    private static GoalLifecycleState ParseState(string? value) =>
        Enum.TryParse<GoalLifecycleState>(value, out var state) ? state : GoalLifecycleState.Verified;

    private static ConductorParallelAcceptanceAttemptOutcome OutcomeFor(ConductorParallelAcceptanceRunResult run)
    {
        if (run.Exception is DotnetBuildSlotsBusyException)
        {
            return ConductorParallelAcceptanceAttemptOutcome.BlockedBuildSlot;
        }

        if (run.Exception is BuildLockBlockedException)
        {
            return ConductorParallelAcceptanceAttemptOutcome.BlockedBuildLock;
        }

        if (run.Exception is OperationCanceledException)
        {
            return ConductorParallelAcceptanceAttemptOutcome.Cancelled;
        }

        if (run.Exception is not null)
        {
            return ConductorParallelAcceptanceAttemptOutcome.Failed;
        }

        if (run.EarlyResult is not null)
        {
            return run.EarlyResult.WasEscalated
                ? ConductorParallelAcceptanceAttemptOutcome.Failed
                : ConductorParallelAcceptanceAttemptOutcome.Passed;
        }

        return run.Acceptance?.Passed == true
            ? ConductorParallelAcceptanceAttemptOutcome.Passed
            : ConductorParallelAcceptanceAttemptOutcome.Failed;
    }

    private static bool IsTerminalWithoutRunOutcome(ConductorParallelAcceptanceAttemptOutcome outcome) =>
        outcome is ConductorParallelAcceptanceAttemptOutcome.StaleCandidate
            or ConductorParallelAcceptanceAttemptOutcome.ProcessDied
            or ConductorParallelAcceptanceAttemptOutcome.CorruptArtifacts
            or ConductorParallelAcceptanceAttemptOutcome.Cancelled
            or ConductorParallelAcceptanceAttemptOutcome.BlockedBuildSlot
            or ConductorParallelAcceptanceAttemptOutcome.BlockedBuildLock
            or ConductorParallelAcceptanceAttemptOutcome.LaunchFailed;

    private static bool IsReconciled(ConductorParallelAcceptanceAttempt attempt) =>
        attempt.ReconciledAt.HasValue ||
        attempt.Outcome == ConductorParallelAcceptanceAttemptOutcome.Reconciled;

    private static string AcceptanceRunDetail(ConductorParallelAcceptanceRunResult run)
    {
        if (run.Exception is not null)
        {
            return run.Exception.Message;
        }

        if (run.EarlyResult is not null)
        {
            return run.EarlyOutcome?.Detail ?? run.EarlyResult.Outcome.ToString() ?? "early result";
        }

        return run.Acceptance?.Passed == true ? "acceptance passed" : "acceptance failed";
    }

    private static void WriteResult(string path, ConductorParallelAcceptanceRunArtifact artifact)
    {
        var tmp = TemporarySiblingPath(path);
        File.WriteAllText(tmp, JsonSerializer.Serialize(artifact, JsonOptions));
        File.Move(tmp, path, overwrite: true);
    }

    private static string TemporarySiblingPath(string path) =>
        $"{path}.{Guid.NewGuid():N}.tmp";

    private static void PruneOldAttempts(string directory, int retainCount)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        var staleAttempts = Directory.EnumerateFiles(directory, "*.attempt.json")
            .Select(path => new
            {
                Path = path,
                Attempt = TryReadAttemptFile(path),
                Timestamp = File.GetLastWriteTimeUtc(path)
            })
            .OrderByDescending(item => item.Attempt?.StartedAt.UtcDateTime ?? item.Timestamp)
            .Skip(Math.Max(0, retainCount))
            .ToArray();

        foreach (var item in staleAttempts)
        {
            var prefix = item.Path[..^".attempt.json".Length];
            foreach (var path in Directory.EnumerateFiles(directory, Path.GetFileName(prefix) + ".*"))
            {
                TryDeleteFile(path);
            }
        }
    }

    private static void TryDeleteFile(string path)
    {
        try { File.Delete(path); } catch { }
    }

    private static ConductorParallelAcceptanceAttempt? TryReadAttemptFile(string path)
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<ConductorParallelAcceptanceAttempt>(
                    ReadAllTextSharedWithRetry(path),
                    JsonOptions)
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static void WriteAttemptFile(ConductorParallelAcceptanceAttempt attempt)
    {
        var payload = JsonSerializer.Serialize(attempt, JsonOptions);
        for (var retry = 0; ; retry++)
        {
            var tmp = TemporarySiblingPath(attempt.MetadataPath);
            File.WriteAllText(tmp, payload);
            try
            {
                File.Move(tmp, attempt.MetadataPath, overwrite: true);
                return;
            }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && retry < 10)
            {
                try { File.Delete(tmp); } catch { }
                Thread.Sleep(TimeSpan.FromMilliseconds(25 * (retry + 1)));
            }
        }
    }

    private static bool IsProcessAlive(int processId)
    {
        if (processId <= 0)
        {
            return false;
        }

        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch
        {
            return false;
        }
    }

    private static long FileLength(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return 0L;
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return stream.Length;
        }
        catch
        {
            return 0L;
        }
    }

    private static void TryWriteExit(string path, int exitCode)
    {
        try { File.WriteAllText(path, exitCode.ToString(System.Globalization.CultureInfo.InvariantCulture)); }
        catch { }
    }

    private static void TryAppend(string path, string text)
    {
        try { File.AppendAllText(path, text); }
        catch { }
    }

    private int CountConsecutiveTransientFailures(ConductorParallelAcceptanceAttempt attempt)
    {
        var directory = Path.GetDirectoryName(attempt.MetadataPath);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return 0;
        }

        foreach (var prior in Directory.EnumerateFiles(directory, "*.attempt.json")
            .Select(TryReadAttemptFile)
            .OfType<ConductorParallelAcceptanceAttempt>()
            .Where(candidate =>
                string.Equals(candidate.CandidateKey, attempt.CandidateKey, StringComparison.Ordinal) &&
                !string.Equals(candidate.AttemptId, attempt.AttemptId, StringComparison.Ordinal))
            .OrderByDescending(candidate => candidate.StartedAt))
        {
            return IsTransientTerminalFailure(prior)
                ? Math.Max(1, prior.TransientFailureCount)
                : 0;
        }

        return 0;
    }

    internal static bool IsTransientTerminalFailure(ConductorParallelAcceptanceAttempt attempt) =>
        attempt.TransientFailureCount > 0 &&
        (attempt.Outcome == ConductorParallelAcceptanceAttemptOutcome.LaunchFailed ||
            attempt.Outcome == ConductorParallelAcceptanceAttemptOutcome.CorruptArtifacts);

    private static bool IsTransientAttemptIo(Exception ex) =>
        ex is not DotnetBuildSlotsBusyException and not BuildLockBlockedException &&
        (ex is IOException or UnauthorizedAccessException ||
            ex.InnerException is not null && IsTransientAttemptIo(ex.InnerException));

    private static string ReadAllTextSharedWithRetry(string path)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                return reader.ReadToEnd();
            }
            catch (IOException ex) when (IsSharingViolation(ex) && attempt < 5)
            {
                Thread.Sleep(TimeSpan.FromMilliseconds(25 * (attempt + 1)));
            }
        }
    }

    private static bool IsSharingViolation(IOException ex)
    {
        var code = ex.HResult & 0xFFFF;
        return code is 32 or 33;
    }
}

internal sealed record ConductorParallelAcceptanceRunArtifact(
    string Kind,
    string? FaultKind,
    string? FaultMessage,
    string? State,
    string? Message,
    AcceptanceVerificationSummary? Acceptance,
    string? BranchHeadSha,
    string? MainHeadSha,
    string? EarlyOutcomeKind,
    string? EarlyOutcomeDetail);
