using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Mcg.AgentOrchestrator.Core;
using Check = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.AcceptanceManifestCheck;

namespace Mcg.AgentOrchestrator.Infrastructure;

// Measurement owns no local check, receipt or hold. Only CompleteAsync writes parity rows;
// late executor completions cannot revise them or extend the local caller's grace deadline.
internal sealed class RemoteFocusedEvidenceShadow : IAsyncDisposable
{
    private readonly List<Run> _runs = [];
    private readonly TimeProvider _clock;
    private DateTimeOffset _started;
    private RemoteLaneCoordinator? _coordinator;
    private IRemoteLaneExecutor? _executor;
    private CancellationTokenSource? _lifetime;
    private TimeSpan _pollInterval;
    private int _graceSeconds;
    private string? _ledgerPath;
    private bool _closed;

    private RemoteFocusedEvidenceShadow(TimeProvider clock) => _clock = clock;

    internal static RemoteFocusedEvidenceShadow Start(GoalAcceptanceVerifierTestOverrides overrides,
        TimeProvider fallbackClock, string worktreePath, IReadOnlyList<Check> checks, GoalId? goalId,
        IAcceptanceRunExecutionContext? owner, CancellationToken token)
    {
        var shadow = new RemoteFocusedEvidenceShadow(overrides.RemoteLaneTimeProviderForTests ?? fallbackClock);
        try
        {
            shadow._started = shadow._clock.GetUtcNow();
            if (goalId is null || owner is null) return shadow;
            var configuration = RemoteLaneExecutorConfiguration.LoadForFocusedEvidence(
                overrides.RemoteLaneExecutorConfigurationPathForTests ??
                RemoteLaneExecutorConfiguration.ResolveStorePath(worktreePath));
            var settings = configuration.FocusedEvidence;
            if (settings.FaultReason is { } fault)
            {
                var line = $"REMOTE_FOCUSED_EVIDENCE_SHADOW_DISABLED reason={fault}";
                Console.WriteLine(line);
                overrides.OnRemoteLaneProgressLineForTests?.Invoke(line);
            }
            if (settings.Mode != "shadow" || !IsSampled(owner.RunId, settings.SampleEvery)) return shadow;
            var status = GitCli.Run(worktreePath, GitCli.DefaultTimeoutMilliseconds,
                "status", "--porcelain", "--untracked-files=all");
            if (!status.Succeeded || status.DrainTimedOut || !status.ProcessStarted ||
                !string.IsNullOrWhiteSpace(status.Output)) return shadow;
            var head = GoalAcceptanceVerifier.ResolveGitScalar(worktreePath, "rev-parse", "HEAD");
            var tree = GoalAcceptanceVerifier.ResolveGitScalar(worktreePath, "rev-parse", "HEAD^{tree}");
            if (string.IsNullOrWhiteSpace(head) || string.IsNullOrWhiteSpace(tree)) return shadow;
            var identity = new RemoteLaneCandidateIdentity(owner.RunId, goalId.Value, head, tree,
                GoalAcceptanceVerifier.ResolveGitScalar(worktreePath, "rev-parse", owner.IntegrationBranch) ?? "", "");
            var prefix = Path.Combine(AcceptancePartitionVerdictCache.ResolveHostStateRoot(worktreePath),
                ".orchestrator", "fe-shadow", GoalAcceptanceVerifier.ShortHash(owner.RunId), "shadow");
            shadow._ledgerPath = RemoteExecutorHealthLedger.ResolveStorePath(worktreePath);
            shadow._graceSeconds = settings.GraceSeconds;
            shadow._pollInterval = overrides.RemoteLanePollInterval is { } interval && interval > TimeSpan.Zero
                ? interval : TimeSpan.FromSeconds(1);
            shadow._lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
            shadow._executor = overrides.RemoteLaneExecutorForTests ??
                (configuration.Executors.Any(entry => entry.Transport == "ssh")
                    ? new SshRemoteLaneExecutor(configuration, worktreePath, prefix, shadow._clock,
                        overrides.RemoteLaneTransportRunnerForTests ?? GoalAcceptanceVerifier.RunRemoteLaneTransportAsync,
                        overrides.RemoteLaneGitRunnerForTests ?? GitCli.Run)
                    : UnavailableRemoteLaneExecutor.Instance);
            shadow._coordinator = new(configuration, identity, worktreePath, prefix, shadow._executor,
                shadow._clock, shadow._pollInterval, null);
            foreach (var check in checks)
            {
                if (!check.IsFocusedEvidenceSelection || check.Project is null ||
                    !Path.GetFileName(check.Project.Replace('\\', '/')).Equals(
                        "Mcg.AgentOrchestrator.Infrastructure.Tests.csproj", StringComparison.OrdinalIgnoreCase)) continue;
                var filterIndex = check.Arguments.ToList().FindIndex(arg => arg == "--filter");
                if (filterIndex < 0 || filterIndex + 1 >= check.Arguments.Count) continue;
                var filter = check.Arguments[filterIndex + 1];
                var request = shadow._coordinator.CreateRequest(LaneName(check.Project, filter), check.Project,
                    filter, identity with { ManifestIdentity = ManifestIdentity(check.Project, filter, check.Runner ?? "") },
                    "");
                var run = new Run(check.Name, request, shadow._clock.GetUtcNow());
                shadow._runs.Add(run);
                try
                {
                    var entry = shadow._coordinator.TryClaimIdleExecutor();
                    if (entry is null)
                    {
                        run.Ended = run.Started;
                        run.Work = Task.FromResult(new Observation(RemoteLaneOutcomeCode.ShadowSkippedNoExecutor));
                    }
                    else
                    {
                        run.Request = request with { ExecutorId = entry.Id };
                        run.Work = Task.Run(() => shadow.ExecuteAsync(run, entry));
                    }
                }
                catch (Exception ex)
                {
                    run.Ended = shadow._clock.GetUtcNow();
                    run.Work = Task.FromResult(new Observation(RemoteLaneOutcomeCode.Unreachable,
                        Reason: $"{ex.GetType().Name}: {ex.Message}"));
                }
            }
        }
        catch (Exception ex)
        {
            // Eligible runs already created still receive one row, even if starting a later run fails.
            foreach (var run in shadow._runs.Where(run => run.Work is null))
                run.Work = Task.FromResult(new Observation(RemoteLaneOutcomeCode.Unreachable, Reason: ex.Message));
        }
        return shadow;
    }

    internal static bool IsSampled(string attemptId, int sampleEvery) => sampleEvery >= 1 &&
        BinaryPrimitives.ReadUInt32BigEndian(Hash(attemptId)) % (uint)sampleEvery == 0;
    internal static string LaneName(string project, string filter) =>
        "fe-" + Convert.ToHexString(Hash(project + "\n" + filter).AsSpan(0, 4)).ToLowerInvariant();
    internal static string ManifestIdentity(string project, string filter, string runner) =>
        Convert.ToHexString(Hash(project + "\n" + filter + "\n" + runner)).ToLowerInvariant();
    private static byte[] Hash(string value) => SHA256.HashData(Encoding.UTF8.GetBytes(value));

    private async Task<Observation> ExecuteAsync(Run run, RemoteLaneExecutorEntry entry)
    {
        var token = _lifetime!.Token;
        RemoteLaneResult? observed = null;
        RemoteLaneOutcomeCode? mismatch = null;
        try
        {
            token.ThrowIfCancellationRequested();
            var submission = await _executor!.SubmitAsync(run.Request, token).ConfigureAwait(false);
            if (submission.Handle is not { } handle)
                return new(submission.FailureReason == "transport-unavailable"
                    ? RemoteLaneOutcomeCode.TransportUnavailable : RemoteLaneOutcomeCode.Unreachable,
                    Reason: submission.FailureReason);
            if (!run.Attach(handle)) return new(RemoteLaneOutcomeCode.CancelledAfterGrace);
            var heartbeat = run.Started;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                if (handle.NewestHeartbeat is { } newest && newest > heartbeat) heartbeat = newest;
                if (_clock.GetUtcNow() - heartbeat > TimeSpan.FromSeconds(entry.LeaseSeconds))
                    return new(RemoteLaneOutcomeCode.LeaseExpired);
                if (handle.TryGetResult() is { } result)
                {
                    observed = result;
                    mismatch = RemoteLaneCoordinator.BindingMismatch(run.Request, result);
                    var trx = GoalAcceptanceVerifierTestTelemetry.InspectTrxCompletionEvidence(result.TestResultPaths);
                    var failingTests = GoalAcceptanceVerifierTestTelemetry.ExtractTrxFailureIdentities(result.TestResultPaths);
                    if (mismatch is { } bindingFault)
                        return new(bindingFault, result, trx.ExecutedTestCount, failingTests);
                    var decision = GoalAcceptanceVerifierTestTelemetry.DecideTestShardCompletion(
                        new GoalAcceptanceVerifier.CommandResult(result.ExitCode, ""), trx, requireTrxEvidence: true);
                    var red = decision.FailedPredicate is AcceptanceShardCompletionPredicates.FailingTrx or
                        AcceptanceShardCompletionPredicates.AssemblyCleanupFailure or AcceptanceShardCompletionPredicates.NonzeroExit;
                    return new(decision.Passed ? RemoteLaneOutcomeCode.Accepted : red
                        ? RemoteLaneOutcomeCode.RemoteRed : RemoteLaneOutcomeCode.TrxIncomplete,
                        result, trx.ExecutedTestCount, failingTests);
                }
                await Task.Delay(_pollInterval, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { return new(RemoteLaneOutcomeCode.CancelledAfterGrace, observed); }
        catch (Exception ex)
        { return new(mismatch ?? RemoteLaneOutcomeCode.Unreachable, observed, Reason: $"{ex.GetType().Name}: {ex.Message}"); }
        finally
        {
            run.Ended = _clock.GetUtcNow();
            run.Close(cancel: observed is null);
            // A completed shadow must not retain an idle slot throughout the remaining local batch.
            try { _coordinator!.Release(entry.Id); } catch (Exception) { }
        }
    }

    internal async Task CompleteAsync(IReadOnlyList<AcceptanceCheckResult> localResults)
    {
        if (_closed) return;
        var localEnd = _started;
        try
        {
            localEnd = _clock.GetUtcNow();
            var all = Task.WhenAll(_runs.Select(run => run.Work!));
            using var timerLifetime = CancellationTokenSource.CreateLinkedTokenSource(
                _lifetime?.Token ?? CancellationToken.None);
            var grace = Task.Delay(TimeSpan.FromSeconds(_graceSeconds), _clock, timerLifetime.Token);
            await Task.WhenAny(all, grace).ConfigureAwait(false);
            // Cancel the timer without synchronously running executor cancellation callbacks.
            _ = timerLifetime.CancelAsync();
            Finish(localResults, localEnd, null);
        }
        catch (Exception ex) { Finish(localResults, localEnd, ex.Message); }
    }

    private void Finish(IReadOnlyList<AcceptanceCheckResult> localResults, DateTimeOffset localEnd, string? reason)
    {
        if (_closed) return;
        _closed = true;
        Task cancellation = Task.CompletedTask;
        try
        {
            if (_lifetime is { } lifetime)
            {
                cancellation = lifetime.CancelAsync();
                Observe(cancellation);
            }
            foreach (var run in _runs)
            {
                var observation = run.Work is { IsCompletedSuccessfully: true } work
                    ? work.Result : new Observation(RemoteLaneOutcomeCode.CancelledAfterGrace, Reason: reason);
                run.Close();
                var local = localResults.FirstOrDefault(result => result.Name == run.CheckName);
                bool? agree = observation.Code is RemoteLaneOutcomeCode.Accepted or RemoteLaneOutcomeCode.RemoteRed && local is not null
                    ? (observation.Code == RemoteLaneOutcomeCode.Accepted) == local.Passed : null;
                var request = run.Request;
                var observed = observation.Result;
                RemoteExecutorHealthLedger.Append(_ledgerPath!, new(_clock.GetUtcNow(), request.ExecutorId,
                    request.AttemptId, request.Lane, observation.Code,
                    observation.Code is RemoteLaneOutcomeCode.Accepted or RemoteLaneOutcomeCode.CancelledAfterGrace or
                        RemoteLaneOutcomeCode.ShadowSkippedNoExecutor ? null : "executor",
                    Binding(request), observed is null ? null : new(observed.ExecutorId, observed.Lane,
                        observed.VerifyingCommitSha, observed.ObservedTreeSha, observed.MainSha, observed.FilterHash,
                        observed.ManifestIdentity), observation.Reason, EvidenceShadow: true, GoalId: request.GoalId,
                    RemoteExecutedCount: observation.ExecutedCount ?? 0, RemoteFailingTests: observation.FailingTests ?? [],
                    RemoteDurationSeconds: Math.Max(0, ((run.Work?.IsCompleted == true ? run.Ended : null) ??
                        _clock.GetUtcNow()).Subtract(run.Started).TotalSeconds),
                    LocalDurationSeconds: Math.Max(0, (localEnd - _started).TotalSeconds),
                    LocalVerdict: local is null ? "unavailable" : local.Passed ? "passed" : "failed", Agree: agree));
            }
        }
        catch (Exception) { /* Measurement must never fail the local verdict, including ledger faults. */ }
        finally
        {
            try { _coordinator?.Dispose(); } catch (Exception) { }
            if (_lifetime is { } lifetime)
                // Polling can finish before asynchronous callbacks; disposal must retain their registrations.
                Observe(Task.WhenAll(_runs.Select(run => (Task)run.Work!).Append(cancellation))
                    .ContinueWith(completed =>
                    {
                        // The disposal continuation succeeds even if a cancellation callback faulted.
                        _ = completed.Exception;
                        lifetime.Dispose();
                    }, TaskScheduler.Default));
        }
    }

    private static RemoteLaneBinding Binding(RemoteLaneRequest request) => new(request.ExecutorId, request.Lane,
        request.VerifyingCommitSha, request.CandidateTreeSha, request.MainSha, request.FilterHash, request.ManifestIdentity);
    private static void Observe(Task task) => _ = task.ContinueWith(completed => { _ = completed.Exception; },
        CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);

    public ValueTask DisposeAsync()
    {
        try { Finish([], _clock.GetUtcNow(), "local-run-faulted"); }
        catch (Exception) { /* Disposal cannot replace a local result or exception. */ }
        return ValueTask.CompletedTask;
    }

    private sealed record Observation(RemoteLaneOutcomeCode Code, RemoteLaneResult? Result = null,
        int? ExecutedCount = null, IReadOnlyList<string>? FailingTests = null, string? Reason = null);

    private sealed class Run(string checkName, RemoteLaneRequest request, DateTimeOffset started)
    {
        internal string CheckName { get; } = checkName;
        internal RemoteLaneRequest Request { get; set; } = request;
        internal DateTimeOffset Started { get; } = started;
        internal DateTimeOffset? Ended { get; set; }
        internal Task<Observation>? Work { get; set; }
        private readonly object _gate = new();
        private IRemoteLaneHandle? _handle;
        private bool _closed;
        internal bool Attach(IRemoteLaneHandle handle)
        {
            lock (_gate)
            {
                if (!_closed) { _handle = handle; return true; }
            }
            Cleanup(handle);
            return false;
        }
        internal void Close(bool cancel = true)
        {
            IRemoteLaneHandle? handle;
            lock (_gate) { _closed = true; handle = _handle; _handle = null; }
            if (handle is not null) Cleanup(handle, cancel);
        }
        private static void Cleanup(IRemoteLaneHandle handle, bool cancel = true) => Observe(Task.Run(() =>
        {
            try { if (cancel) (handle as IRemoteLaneQueuedJobCancellation)?.RequestQueuedJobCancellation(); } catch (Exception) { }
            try { handle.Abandon(); } catch (Exception) { }
        }));
    }
}
