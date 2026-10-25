using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Check = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.AcceptanceManifestCheck;

namespace Mcg.AgentOrchestrator.Infrastructure;

// A remote verdict owns its check. Only absent verdicts enter the existing local batch.
internal static class RemoteFirstFocusedEvidence
{
    internal sealed record Outcome(IReadOnlyList<AcceptanceCheckResult> Results);

    internal static async Task<Outcome> RunAsync(GoalAcceptanceVerifierTestOverrides overrides,
        TimeProvider fallbackClock, string worktreePath, IReadOnlyList<Check> checks, GoalId? goalId,
        IAcceptanceRunExecutionContext? owner, bool continueAfterFailure,
        Func<IReadOnlyList<Check>, Task<IReadOnlyList<AcceptanceCheckResult>>> runLocal, CancellationToken token,
        Action? releaseLocalPermit = null)
    {
        RemoteLaneExecutorConfiguration configuration;
        try
        {
            configuration = RemoteLaneExecutorConfiguration.LoadForFocusedEvidence(
                overrides.RemoteLaneExecutorConfigurationPathForTests ?? RemoteLaneExecutorConfiguration.ResolveStorePath(worktreePath));
        }
        catch (Exception) { return new(await runLocal(checks).ConfigureAwait(false)); }
        if (configuration.FocusedEvidence.Mode != "remote-first" || goalId is null || owner is null ||
            string.IsNullOrWhiteSpace(owner.ResultsPrefix))
            return new(await runLocal(checks).ConfigureAwait(false));

        token.ThrowIfCancellationRequested();
        var clock = overrides.RemoteLaneTimeProviderForTests ?? fallbackClock;
        var runs = checks.Select(check => Candidate(check, owner.RunId, goalId.Value, clock)).ToArray();
        RemoteLaneCoordinator? coordinator = null;
        var releasingLocalPermit = false;
        try
        {
            var status = GitCli.Run(worktreePath, GitCli.DefaultTimeoutMilliseconds,
                "status", "--porcelain", "--untracked-files=all");
            var clean = status.Succeeded && !status.DrainTimedOut && status.ProcessStarted && string.IsNullOrWhiteSpace(status.Output);
            var head = clean ? GoalAcceptanceVerifier.ResolveGitScalar(worktreePath, "rev-parse", "HEAD") : null;
            var tree = clean ? GoalAcceptanceVerifier.ResolveGitScalar(worktreePath, "rev-parse", "HEAD^{tree}") : null;
            var main = clean ? GoalAcceptanceVerifier.ResolveGitScalar(worktreePath, "rev-parse", owner.IntegrationBranch) : null;
            if (!clean || string.IsNullOrWhiteSpace(head) || string.IsNullOrWhiteSpace(tree) || string.IsNullOrWhiteSpace(main))
            {
                foreach (var run in runs.OfType<Run>())
                    Finish(run, RemoteLaneOutcomeCode.NotEligibleExclusiveResource, "dirty-worktree");
            }
            else
            {
                var identity = new RemoteLaneCandidateIdentity(owner.RunId, goalId.Value, head, tree, main, "");
                var prefix = Path.Combine(Path.GetDirectoryName(owner.ResultsPrefix)!, "remote-first", GoalAcceptanceVerifier.ShortHash(owner.RunId), "lane");
                var executor = overrides.RemoteLaneExecutorForTests ??
                    (configuration.Executors.Any(entry => entry.Transport == "ssh")
                        ? new SshRemoteLaneExecutor(configuration, worktreePath, prefix, clock,
                            overrides.RemoteLaneTransportRunnerForTests ?? GoalAcceptanceVerifier.RunRemoteLaneTransportAsync,
                            overrides.RemoteLaneGitRunnerForTests ?? GitCli.Run)
                        : UnavailableRemoteLaneExecutor.Instance);
                var interval = overrides.RemoteLanePollInterval is { } value && value > TimeSpan.Zero ? value : TimeSpan.FromSeconds(1);
                coordinator = new(configuration, identity, worktreePath, prefix, executor, clock, interval, null,
                    claimClass: RemoteExecutorClaimClass.FocusedEvidence);
                // Claim every slot before starting work; completions cannot recycle a sick executor.
                var claimed = new List<(Run Run, RemoteLaneExecutorEntry Entry)>();
                var eligibility = FocusedEvidenceRemoteEligibility.Create(worktreePath, configuration.MachineLocalResourceKeys);
                foreach (var run in runs.OfType<Run>())
                {
                    run.Request = coordinator.CreateRequest(run.Request.Lane, run.Check.Project!, run.Request.Filter,
                        identity with { ManifestIdentity = run.Request.ManifestIdentity }, "");
                    if (eligibility.Decide(run.Check) is { Eligible: false } decision)
                        Finish(run, RemoteLaneOutcomeCode.NotEligibleExclusiveResource, decision.Reason!);
                    else if (coordinator.TryClaimIdleExecutor(out var yieldReason) is not { } entry)
                        Finish(run, RemoteLaneOutcomeCode.ShadowSkippedNoExecutor, yieldReason ?? "no-idle-executor");
                    else
                    {
                        run.Request = run.Request with { ExecutorId = entry.Id };
                        claimed.Add((run, entry));
                    }
                }
                if (claimed.Count > 0)
                {
                    releasingLocalPermit = true;
                    releaseLocalPermit?.Invoke();
                    releasingLocalPermit = false;
                }
                await Task.WhenAll(claimed.Select(pair => ExecuteAsync(pair.Run, pair.Entry))).ConfigureAwait(false);

                async Task ExecuteAsync(Run run, RemoteLaneExecutorEntry entry)
                {
                    var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
                    using var timerLifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
                    var timeout = owner.Settings.ResolveCheckTimeout(run.Check.TimeoutMinutes);
                    var bound = Task.Delay(timeout + TimeSpan.FromSeconds(configuration.FocusedEvidence.GraceSeconds), clock, timerLifetime.Token);
                    var work = Task.Run(() => PollAsync(run, entry, lifetime.Token));
                    Observe(work);
                    try
                    {
                        await Task.WhenAny(work, bound).ConfigureAwait(false);
                        token.ThrowIfCancellationRequested();
                        if (work.IsCompleted) await work.ConfigureAwait(false);
                        else Finish(run, RemoteLaneOutcomeCode.CancelledAfterGrace, "cancelled-after-grace");
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    { Finish(run, RemoteLaneOutcomeCode.CancelledAfterGrace, "caller-cancelled"); throw; }
                    catch (Exception ex) { Finish(run, RemoteLaneOutcomeCode.Unreachable, ex.GetType().Name + ": " + ex.Message); }
                    finally
                    {
                        var cancellation = lifetime.CancelAsync();
                        Observe(cancellation);
                        Observe(Task.WhenAll(work, cancellation).ContinueWith(completed =>
                        {
                            _ = completed.Exception;
                            lifetime.Dispose();
                        }, TaskScheduler.Default));
                        Observe(timerLifetime.CancelAsync());
                        run.Close();
                        try { coordinator.Release(entry.Id); } catch (Exception) { }
                    }
                }

                async Task PollAsync(Run run, RemoteLaneExecutorEntry entry, CancellationToken laneToken)
                {
                    var submission = await executor.SubmitAsync(run.Request, laneToken).ConfigureAwait(false);
                    if (submission.Handle is not { } handle)
                    {
                        Finish(run, submission.FailureReason switch
                            {
                                "transport-unavailable" => RemoteLaneOutcomeCode.TransportUnavailable,
                                "executor-busy" => RemoteLaneOutcomeCode.ExecutorBusy,
                                "executor-tools-mismatch" => RemoteLaneOutcomeCode.ExecutorToolsMismatch,
                                "executor-tools-unverified" => RemoteLaneOutcomeCode.ExecutorToolsUnverified,
                                _ => RemoteLaneOutcomeCode.Unreachable
                            },
                            submission.FailureReason ?? "submission-failed");
                        return;
                    }
                    if (!run.Attach(handle)) return;
                    var heartbeat = run.Started;
                    while (true)
                    {
                        laneToken.ThrowIfCancellationRequested();
                        if (handle.NewestHeartbeat is { } newest && newest > heartbeat) heartbeat = newest;
                        var now = clock.GetUtcNow();
                        if (now - run.Started > owner.Settings.ResolveCheckTimeout(run.Check.TimeoutMinutes))
                        { Finish(run, RemoteLaneOutcomeCode.LaneTimeout, "lane-timeout"); return; }
                        if (now - heartbeat > TimeSpan.FromSeconds(entry.LeaseSeconds))
                        { Finish(run, RemoteLaneOutcomeCode.LeaseExpired, LeaseReason(handle)); return; }
                        if (handle.TryGetResult() is { } result)
                        {
                            Inspect(run, result, prefix);
                            return;
                        }
                        await Task.Delay(interval, laneToken).ConfigureAwait(false);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex) when (!releasingLocalPermit)
        {
            foreach (var run in runs.OfType<Run>())
                Finish(run, RemoteLaneOutcomeCode.Unreachable, ex.GetType().Name + ": " + ex.Message);
        }
        finally
        {
            foreach (var run in runs.OfType<Run>()) run.Close();
            try { coordinator?.Dispose(); } catch (Exception) { }
        }

        token.ThrowIfCancellationRequested();
        var limit = checks.Count;
        if (!continueAfterFailure)
            for (var index = 0; index < runs.Length; index++)
                if (runs[index]?.Verdict is { Passed: false }) { limit = index + 1; break; }
        var localChecks = checks.Take(limit).Where((_, index) => runs[index]?.Verdict is null).ToArray();
        var local = localChecks.Length == 0 ? [] : await runLocal(localChecks).ConfigureAwait(false);
        var merged = new List<AcceptanceCheckResult>();
        var localIndex = 0;
        for (var index = 0; index < limit; index++)
        {
            var result = runs[index]?.Verdict;
            if (result is null)
            {
                if (localIndex == local.Count) break;
                result = local[localIndex++];
            }
            merged.Add(result);
            if (!continueAfterFailure && !result.Passed) break;
        }
        return new(merged);

        void Inspect(Run run, RemoteLaneResult result, string prefix)
        {
            if (RemoteLaneCoordinator.BindingMismatch(run.Request, result) is { } mismatch)
            { Finish(run, mismatch, "binding-mismatch", result); return; }
            var trx = GoalAcceptanceVerifierTestTelemetry.InspectTrxCompletionEvidence(result.TestResultPaths);
            var decision = GoalAcceptanceVerifierTestTelemetry.DecideTestShardCompletion(
                new GoalAcceptanceVerifier.CommandResult(result.ExitCode, ""), trx, requireTrxEvidence: true);
            var red = decision.FailedPredicate is AcceptanceShardCompletionPredicates.FailingTrx or
                AcceptanceShardCompletionPredicates.AssemblyCleanupFailure or AcceptanceShardCompletionPredicates.NonzeroExit;
            var code = decision.Passed ? RemoteLaneOutcomeCode.Accepted : red ? RemoteLaneOutcomeCode.RemoteRed : RemoteLaneOutcomeCode.TrxIncomplete;
            var failing = GoalAcceptanceVerifierTestTelemetry.ExtractTrxFailureIdentities(result.TestResultPaths);
            // Named test evidence is mandatory. Runner faults cannot become goal verdicts.
            if (!decision.Passed && (!red || failing.Count == 0 || result.ExitCode is 29 or 26 or 24))
            { Finish(run, code, "no-named-test-failure: " + decision.FailedPredicate, result); return; }
            var lanePrefix = Path.Combine(Path.GetDirectoryName(prefix)!, run.Request.Lane, "receipt");
            var paths = GoalAcceptanceVerifierTestTelemetry.CopyCompletedTestReceiptsToAttemptFolder(result.TestResultPaths, lanePrefix);
            var folder = Path.GetFullPath(Path.GetDirectoryName(lanePrefix)!) + Path.DirectorySeparatorChar;
            if (paths is null || paths.Count == 0 || paths.Any(path => !File.Exists(path) ||
                !Path.GetFullPath(path).StartsWith(folder, StringComparison.OrdinalIgnoreCase)))
            { Finish(run, RemoteLaneOutcomeCode.TrxIncomplete, "receipt-custody", result); return; }
            var retained = GoalAcceptanceVerifierTestTelemetry.InspectTrxCompletionEvidence(paths);
            var retainedDecision = GoalAcceptanceVerifierTestTelemetry.DecideTestShardCompletion(
                new GoalAcceptanceVerifier.CommandResult(result.ExitCode, ""), retained, requireTrxEvidence: true);
            var retainedFailures = GoalAcceptanceVerifierTestTelemetry.ExtractTrxFailureIdentities(paths);
            var coverage = GoalAcceptanceVerifierTestTelemetry.InspectFocusedEvidenceSelectionCoverage(run.Check.FocusedEvidenceSelections, paths);
            if (retained.ExecutedTestCount is not > 0 || retainedDecision.Passed != decision.Passed ||
                retainedDecision.FailedPredicate != decision.FailedPredicate ||
                (decision.Passed && (coverage.UncoveredSelections.Count > 0 || coverage.UnreadableReceiptPaths.Count > 0)) ||
                (!decision.Passed && !failing.SequenceEqual(retainedFailures)))
            { Finish(run, RemoteLaneOutcomeCode.TrxIncomplete, "retained-trx-incomplete", result); return; }
            var reason = decision.Passed ? $"remote-first accepted remote-executor={run.Request.ExecutorId}" :
                "remote-first verdict " + RemoteLaneRedEvidence.Retain(paths, lanePrefix, run.Request.ExecutorId, run.Check.Name, result.ExitCode);
            var verdict = new AcceptanceCheckResult(run.Check.Name, decision.Passed, result.ExitCode, decision.Passed ? null : reason,
                ResultSummary: $"remote-executor={run.Request.ExecutorId}" + (decision.Passed ? "" : " remote-red failing=" + string.Join(",", retainedFailures)),
                TestResultPaths: paths, TestResultAttemptId: owner.RunId, ExecutedTestCount: retained.ExecutedTestCount,
                DiscoveredTestCount: retained.DiscoveredTestCount, CompletionDecision: retainedDecision,
                TestProjectPath: run.Check.Project, FailingTestIdentities: retainedFailures);
            Finish(run, code, reason, result, verdict);
        }

        void Finish(Run run, RemoteLaneOutcomeCode code, string reason, RemoteLaneResult? result = null, AcceptanceCheckResult? verdict = null)
        {
            // Closing a timed-out lane also fences off a non-cooperative late completion.
            lock (run.Gate)
            {
                if (run.Finished) return;
                run.Finished = true;
                run.Verdict = verdict;
                reason = verdict is null ? "remote-first-fallback: " + reason : reason;
                var request = run.Request;
                var usage = RemoteLaneResourceUsage.TryRead(run.LastStatus());
                RemoteExecutorHealthLedger.Append(RemoteExecutorHealthLedger.ResolveStorePath(worktreePath), new(clock.GetUtcNow(),
                    request.ExecutorId, request.AttemptId, request.Lane, code,
                    code is RemoteLaneOutcomeCode.Accepted or RemoteLaneOutcomeCode.NotEligibleExclusiveResource or
                        RemoteLaneOutcomeCode.ExecutorToolsMismatch or RemoteLaneOutcomeCode.ExecutorBusy or
                        RemoteLaneOutcomeCode.ShadowSkippedNoExecutor or RemoteLaneOutcomeCode.CancelledAfterGrace ? null : "executor",
                    new(request.ExecutorId, request.Lane, request.VerifyingCommitSha, request.CandidateTreeSha, request.MainSha, request.FilterHash, request.ManifestIdentity),
                    result is null ? null : new(result.ExecutorId, result.Lane, result.VerifyingCommitSha, result.ObservedTreeSha, result.MainSha, result.FilterHash, result.ManifestIdentity),
                    reason, GoalId: request.GoalId, RemoteExecutedCount: verdict?.ExecutedTestCount,
                    RemoteFailingTests: verdict?.FailingTestIdentities, RemoteDurationSeconds: Math.Max(0, (clock.GetUtcNow() - run.Started).TotalSeconds),
                    RemoteTestHostCpuSeconds: usage?.TestHostCpuSeconds, RemoteTestHostPeakWorkingSetBytes: usage?.TestHostPeakWorkingSetBytes,
                    RemoteAvailableMemoryMbMin: usage?.AvailableMemoryMbMin, RemoteResourceSamples: usage?.ResourceSamples));
                var line = $"REMOTE_FIRST_FOCUSED_EVIDENCE lane={request.Lane} outcome={JsonNamingPolicy.KebabCaseLower.ConvertName(code.ToString())} reason={reason}";
                try { Console.WriteLine(line); } catch (Exception) { }
                try { overrides.OnRemoteLaneProgressLineForTests?.Invoke(line); } catch (Exception) { }
                try { owner.ReportRemoteLaneEvent(line); } catch (Exception) { }
            }
        }
    }

    private static Run? Candidate(Check check, string attempt, string goal, TimeProvider clock)
    {
        if (!check.IsFocusedEvidenceSelection || check.Project is null ||
            !Path.GetFileName(check.Project.Replace('\\', '/')).Equals("Mcg.AgentOrchestrator.Infrastructure.Tests.csproj", StringComparison.OrdinalIgnoreCase)) return null;
        var index = check.Arguments.ToList().FindIndex(arg => arg == "--filter");
        if (index < 0 || index + 1 >= check.Arguments.Count) return null;
        var filter = check.Arguments[index + 1];
        return new(check, new("", attempt, goal, RemoteFocusedEvidenceShadow.LaneName(check.Project, filter), check.Project,
            filter, GoalAcceptanceVerifier.ShortHash(filter), "", "", "",
            RemoteFocusedEvidenceShadow.ManifestIdentity(check.Project, filter, check.Runner ?? "")), clock.GetUtcNow());
    }

    private static string LeaseReason(IRemoteLaneHandle handle)
    {
        try
        {
            if ((handle as IRemoteLaneAttemptDiagnosticsSource)?.Snapshot().Poll is { FailedPollCount: > 0 } poll)
            {
                var text = (poll.LastFailedPoll?.StderrTail ?? "").Split(['\r', '\n'])[0].Trim();
                if (text.Length > 200) text = text[..200];
                return $"lease-expired failed-polls={poll.FailedPollCount}/{poll.PollCount} last-poll-error={text}";
            }
        }
        catch (Exception) { }
        return "lease-expired";
    }

    private static void Observe(Task task) => _ = task.ContinueWith(completed => { _ = completed.Exception; },
        CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);

    private sealed class Run(Check check, RemoteLaneRequest request, DateTimeOffset started)
    {
        internal object Gate { get; } = new();
        internal Check Check { get; } = check;
        internal RemoteLaneRequest Request { get; set; } = request;
        internal DateTimeOffset Started { get; } = started;
        internal AcceptanceCheckResult? Verdict { get; set; }
        internal bool Finished { get; set; }
        private bool _closed;
        private IRemoteLaneHandle? _handle;
        internal JsonElement? LastStatus()
        {
            lock (Gate)
            {
                try { return (_handle as IRemoteLaneAttemptDiagnosticsSource)?.Snapshot().LastStatus?.Clone(); }
                catch (Exception) { return null; }
            }
        }
        internal bool Attach(IRemoteLaneHandle handle)
        {
            lock (Gate)
                if (!_closed) { _handle = handle; return true; }
            Cleanup(handle);
            return false;
        }
        internal void Close()
        {
            IRemoteLaneHandle? handle;
            lock (Gate) { _closed = true; handle = _handle; _handle = null; }
            if (handle is not null) Cleanup(handle);
        }
        private static void Cleanup(IRemoteLaneHandle handle) => Observe(Task.Run(() =>
        {
            try { (handle as IRemoteLaneQueuedJobCancellation)?.RequestQueuedJobCancellation(); } catch (Exception) { }
            try { handle.Abandon(); } catch (Exception) { }
        }));
    }
}
