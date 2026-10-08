using System.Runtime.CompilerServices;
using System.Globalization;
using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using static Mcg.AgentOrchestrator.Infrastructure.AcceptancePolicyShardPlanner;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed partial class GoalAcceptanceVerifier
{
    internal static Task<CommandResult> RunRemoteLaneTransportAsync(
        string[] args, string directory, TimeSpan bound, CancellationToken token) =>
        RunProcessAsync(args, directory, bound, token);

    private readonly ConditionalWeakTable<AcceptancePartitionVerdictCache, RemoteLaneCoordinator> _remoteLaneCoordinators = new();
    private RemoteLaneCoordinator? _cachelessRemoteLaneCoordinator;
    private readonly ConditionalWeakTable<RemoteLaneCoordinator, RemoteLaneOfferPlan> _remoteLaneOfferPlans = new();
    internal const int RemoteLaneOfferHistoryLineLimit = 500;

    private (AcceptancePartitionVerdictCache? Cache, RemoteLaneCoordinator? RemoteLanes)
        CreatePartitionVerdictCacheAndRemoteLanes(
            GoalId? goalId,
            string worktreePath,
            IReadOnlyList<AcceptanceManifestCheck> effectiveChecks,
            AcceptanceGateEngineSettings engineSettings,
            AcceptanceAttemptExecutionOwner executionOwner)
    {
        Func<string, string?> resolveCandidateTreeSha = path =>
            _testOverrides.ResolvePartitionVerdictCandidateTreeShaForTests?.Invoke(path) ??
            ResolveGitScalar(path, "rev-parse", "HEAD^{tree}");
        Func<string, string?> resolveMainSha = path =>
            _testOverrides.ResolvePartitionVerdictMainShaForTests?.Invoke(path) ??
            ResolveGitScalar(path, "rev-parse", "main");
        Func<string, string?> resolveVerifyingCommitSha = path =>
            _testOverrides.ResolvePartitionVerdictVerifyingCommitShaForTests?.Invoke(path) ??
            ResolveGitScalar(path, "rev-parse", "HEAD");
        Func<string> resolveManifestIdentity = () => ComputeEffectiveAcceptanceManifestIdentity(effectiveChecks, engineSettings);
        var partitionVerdictCache = AcceptancePartitionVerdictCache.Create(
            new AcceptancePartitionVerdictCacheOptions(
                goalId,
                worktreePath,
                effectiveChecks,
                engineSettings.PartitionVerdictFullRerunEveryN,
                _testOverrides.PartitionVerdictWithinAttemptRerunEnabled,
                resolveCandidateTreeSha,
                resolveMainSha,
                resolveVerifyingCommitSha,
                () => executionOwner.Identity.AttemptId,
                resolveManifestIdentity,
                () => EngineSettings.EnforceStructuralCoverage,
                () => TempRootApparatusLossReceiptStore.Read(executionOwner.ApparatusReceiptPath), ResolveClosureHash: _testOverrides.ResolvePartitionVerdictClosureHashForTests, DisableLaneReuseShadow: _testOverrides.DisableLaneReuseShadowForTests, IdenticalTreeChecks: [.. effectiveChecks.Where(check => AcceptanceIdenticalTreeReuseRule.TryGetCheckIdentity(check, out _))]));
        RemoteLaneCandidateIdentity? cohortIdentity = null;
        if (partitionVerdictCache is null && executionOwner.CohortRemoteLanes)
        {
            var candidateTreeSha = resolveCandidateTreeSha(worktreePath);
            var mainSha = resolveMainSha(worktreePath);
            var verifyingCommitSha = resolveVerifyingCommitSha(worktreePath);
            if (!string.IsNullOrWhiteSpace(candidateTreeSha) && !string.IsNullOrWhiteSpace(mainSha) &&
                !string.IsNullOrWhiteSpace(verifyingCommitSha))
                cohortIdentity = new RemoteLaneCandidateIdentity(executionOwner.Identity.AttemptId,
                    executionOwner.GateRunIdentity ?? string.Empty, NormalizeShaToken(verifyingCommitSha),
                    NormalizeShaToken(candidateTreeSha), NormalizeShaToken(mainSha), resolveManifestIdentity());
        }
        return (partitionVerdictCache, LoadRemoteLanes(worktreePath, partitionVerdictCache, effectiveChecks, cohortIdentity));
    }

    private RemoteLaneCoordinator? LoadRemoteLanes(string worktreePath, AcceptancePartitionVerdictCache? cache,
        IReadOnlyList<AcceptanceManifestCheck> effectiveChecks,
        IRemoteLaneCandidateIdentity? candidateIdentity = null)
    {
        var configuration = RemoteLaneExecutorConfiguration.Load(_testOverrides.RemoteLaneExecutorConfigurationPathForTests ??
            RemoteLaneExecutorConfiguration.ResolveStorePath(worktreePath));
        if (!configuration.Enabled)
        {
            var line = $"REMOTE_LANES_DISABLED reason={configuration.DisabledReason}";
            Console.WriteLine(line);
            Console.Out.Flush();
            _testOverrides.OnRemoteLaneProgressLineForTests?.Invoke(line);
            return null;
        }
        var identity = (IRemoteLaneCandidateIdentity?)cache ?? candidateIdentity;
        if (identity is null || ShardPermitLaneClass != GateShardLaneClass.Gate) return null;
        var lanes = effectiveChecks.Where(check => TryGetInfrastructurePartitionId(check, out _, out _)).ToArray();
        configuration = configuration.ResolveLanes(lanes.Select(check => check.Name));
        var historyPath = _testOverrides.RemoteLaneOfferHistoryPathForTests ?? RemoteExecutorHealthLedger.ResolveStorePath(worktreePath);
        var history = ReadRemoteLaneOfferHistory(historyPath);
        var failures = new RemoteLaneFailureHistory().Read(historyPath);
        var clock = _testOverrides.RemoteLaneTimeProviderForTests ?? _timeProvider;
        var decisionTime = clock.GetUtcNow();
        var inputs = lanes.ToDictionary(check => check.Name, check =>
        {
            var local = AcceptanceLaneDurationStore.ResolveObservedSeconds(check);
            TryGetInfrastructurePartitionId(check, out _, out var filter);
            history.TryGetValue((check.Name, ShortHash(filter)), out var remote);
            failures.TryGetValue((check.Name, ShortHash(filter)), out var streak);
            return new RemoteLaneOfferInputs(check.Name, local.MedianSeconds, local.Samples,
                check.EstimatedSerialSeconds, remote?.MedianSeconds, remote?.Samples ?? 0, 0,
                streak?.Count ?? 0, streak?.NewestFailureAt, decisionTime);
        }, StringComparer.Ordinal);
        IRemoteLaneExecutor executor = _testOverrides.RemoteLaneExecutorForTests ??
            (configuration.Executors.Any(entry => entry.Transport == "ssh")
                ? new SshRemoteLaneExecutor(configuration, worktreePath, _executionContext?.ResultsPrefix, clock,
                    _testOverrides.RemoteLaneTransportRunnerForTests ?? RunProcessAsync,
                    _testOverrides.RemoteLaneGitRunnerForTests ?? GitCli.Run)
                : UnavailableRemoteLaneExecutor.Instance);
        var coordinator = new RemoteLaneCoordinator(configuration, identity, worktreePath, _executionContext?.ResultsPrefix,
            executor, clock,
            _testOverrides.RemoteLanePollInterval ?? TimeSpan.FromSeconds(1), _testOverrides.OnRemoteLaneOutcomeForTests,
            line =>
            {
                try { _executionContext?.ReportRemoteLaneEvent(line); } catch (Exception) { }
                _testOverrides.OnRemoteLaneEventForTests?.Invoke(line);
            });
        _remoteLaneOfferPlans.Add(coordinator, new RemoteLaneOfferPlan(inputs));
        if (cache is not null) _remoteLaneCoordinators.Add(cache, coordinator);
        else _cachelessRemoteLaneCoordinator = coordinator;
        return coordinator;
    }

    internal static IReadOnlyDictionary<(string Lane, string Filter), RemoteExecutorLaneSeconds>
        ReadRemoteLaneOfferHistory(string path)
    {
        var rows = new List<RemoteExecutorOutcomeRow>();
        var keys = new Dictionary<string, (string Lane, string Filter)>(StringComparer.Ordinal);
        try
        {
            // Retain and parse only the tail, including malformed and non-accepted lines in the limit.
            foreach (var line in SharedJsonlFile.ReadLines(path).TakeLast(RemoteLaneOfferHistoryLineLimit))
            {
                try
                {
                    using var document = JsonDocument.Parse(line);
                    var root = document.RootElement;
                    if (root.GetProperty("outcome").GetString() != "accepted") continue;
                    var lane = root.GetProperty("lane").GetString();
                    var filter = root.GetProperty("expected").GetProperty("filter").GetString();
                    if (string.IsNullOrWhiteSpace(lane) || string.IsNullOrWhiteSpace(filter)) continue;
                    var attempt = root.GetProperty("attempt");
                    double? seconds = null;
                    if (attempt.TryGetProperty("steps", out var steps) && steps.ValueKind == JsonValueKind.Array &&
                        attempt.TryGetProperty("fetches", out var fetches) && fetches.ValueKind == JsonValueKind.Array)
                    {
                        var starts = steps.EnumerateArray().Select(step => step.GetProperty("started_at").GetDateTimeOffset()).ToArray();
                        var ends = fetches.EnumerateArray().Select(step => step.GetProperty("ended_at").GetDateTimeOffset()).ToArray();
                        if (starts.Length > 0 && ends.Length > 0) seconds = (ends.Max() - starts.Min()).TotalSeconds;
                    }
                    if (seconds is not > 0 && attempt.TryGetProperty("last_status", out var status) &&
                        status.ValueKind == JsonValueKind.Object && status.TryGetProperty("seconds", out var duration) &&
                        duration.TryGetDouble(out var fallback)) seconds = fallback;
                    if (seconds is not { } total || !double.IsFinite(total) || total <= 0) continue;
                    var key = JsonSerializer.Serialize(new[] { lane, filter });
                    keys[key] = (lane, filter);
                    // One aggregate executor combines all hosts; no interval is needed for the report median.
                    rows.Add(new(DateTimeOffset.MinValue, "offer-history", "", key, "accepted", null, total));
                }
                catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or KeyNotFoundException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return RemoteExecutorReport.Build([], rows, []).Executors.SelectMany(executor => executor.Lanes)
            .ToDictionary(lane => keys[lane.Lane]);
    }

    private sealed class RemoteLaneOfferPlan(IReadOnlyDictionary<string, RemoteLaneOfferInputs> inputs)
    {
        private readonly object _sync = new();
        private readonly Dictionary<string, RemoteLaneOfferDecision> _decisions = new(StringComparer.Ordinal);
        private double? _finish;

        internal RemoteLaneOfferDecision Decide(string lane, int concurrency, Action<string> report)
        {
            lock (_sync)
            {
                if (_decisions.TryGetValue(lane, out var existing)) return existing;
                _finish ??= RemoteLaneOfferPolicy.ExpectedLocalFinishSeconds(inputs.Values.Select(input =>
                    RemoteLaneOfferPolicy.ResolveLocalSeconds(input.LocalMedianSeconds, input.LocalSamples,
                        input.ManifestEstimateSeconds)), concurrency);
                var decision = RemoteLaneOfferPolicy.Decide(inputs[lane] with { ExpectedLocalFinishSeconds = _finish.Value });
                _decisions.Add(lane, decision);
                report(string.Create(CultureInfo.InvariantCulture,
                    $"REMOTE_LANE_POLICY lane=\"{lane}\" decision={(decision.Offer ? "offer" : "keep-local")} reason={decision.Reason} L={decision.LocalSeconds:0.###} Ln={decision.Inputs.LocalSamples} R={decision.RemoteSeconds:0.###} Rn={decision.Inputs.RemoteSamples} F={_finish.Value:0.###}") + RemoteLaneOfferPolicy.FailureFields(decision));
                return decision;
            }
        }
    }

    private void ReportRemoteLanePolicy(string line)
    {
        Console.WriteLine(line);
        Console.Out.Flush();
        _testOverrides.OnRemoteLaneProgressLineForTests?.Invoke(line);
    }

    private async Task<CheckBatchResult> RunInfrastructureShardBatchAsync(
        IReadOnlyList<AcceptanceManifestCheck> shardChecks,
        AcceptancePartitionVerdictCache? cacheContext,
        string worktreePath,
        GoalId? goalId,
        int primarySlotIndex,
        DotnetBuildEnvironmentLease primaryLease,
        DotnetTestBuildPhase? primaryBuildPhase,
        int maxConcurrentShards,
        CancellationToken cancellationToken,
        SemaphoreSlim? slotBudget = null,
        CancellationToken stopToken = default)
    {
        AcceptanceGatePhaseAccountant.TransitionCurrent(AcceptanceGatePhaseNames.SharedPrebuild);
        var allShardsUseMtp = shardChecks.All(UsesMicrosoftTestingPlatform);
        if (allShardsUseMtp && primaryBuildPhase is not null)
        {
            var prebuild = await AcceptanceGateCancellationMonitor.RunAsync(
                activeToken => EnsureDotnetTestBuildPhaseAsync(
                    primaryBuildPhase, shardChecks[0], worktreePath, goalId, primarySlotIndex, primaryLease,
                    "acceptance-infrastructure-shards-prebuild", activeToken),
                _executionContext?.ResolveCancellationProbe(boundary: false),
                cancellationToken).ConfigureAwait(false);
            if (prebuild.ContributesToCheck)
                cacheContext?.RecordSharedPrebuildDuration(cacheContext.AttemptId, prebuild.Run.Result.DurationMilliseconds);
            if (prebuild.Run.Result.Passed)
            {
                VerifyPrebuiltMtpArtifacts(shardChecks, primaryBuildPhase);
                ReleaseStableSlotExecutionLockUnlessCoverageHoldActive(primaryLease);
                SignalStructuralCoverageCandidateBuildComplete();
            }
        }

        AcceptanceGatePhaseAccountant.TransitionCurrent(AcceptanceGatePhaseNames.LaneExecution);
        var wallClock = _timeProvider.GetTimestamp();
        var orderedShards = shardChecks
            .Select((check, index) => new IndexedShard(index, check))
            .OrderByDescending(shard => AcceptanceLaneDurationStore.ResolveSortSeconds(shard.Check))
            .ThenBy(shard => shard.Index)
            .ToArray();
        var outcomes = new ShardRunOutcome?[shardChecks.Count];
        var maxConcurrentExecutions = allShardsUseMtp
            ? Math.Min(maxConcurrentShards, shardChecks.Count)
            : 1;
        var pendingShards = orderedShards.ToList();
        var activeResourceKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var activeShards = new List<(Task Task, IReadOnlyList<string> ResourceKeys)>();
        var activeRemote = new List<(IndexedShard Shard, Task<RemoteLaneOutcome> Task)>();
        var reuseConsulted = new System.Collections.Concurrent.ConcurrentDictionary<int, bool>();
        var fallbackOutcomes = new System.Collections.Concurrent.ConcurrentDictionary<int, RemoteLaneOutcome>();
        RemoteLaneCoordinator? remote = null;
        if (cacheContext is not null && ShardPermitLaneClass == GateShardLaneClass.Gate)
            _remoteLaneCoordinators.TryGetValue(cacheContext, out remote);
        else if (cacheContext is null && ShardPermitLaneClass == GateShardLaneClass.Gate)
            remote = _cachelessRemoteLaneCoordinator;
        var shardConcurrency = new GateShardConcurrencyCounter();
        var failures = new List<Exception>();
        using var sharedApparatusCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, stopToken);
        using var remoteCancellation = sharedApparatusCancellation.Token.Register(() => remote?.AbandonAll());

        async Task RunShardAsync(IndexedShard shard)
        {
            if (slotBudget is not null)
                await slotBudget.WaitAsync(sharedApparatusCancellation.Token).ConfigureAwait(false);
            try
            {
                await ExecuteShardAsync(shard).ConfigureAwait(false);
            }
            finally
            {
                slotBudget?.Release();
                if (fallbackOutcomes.TryGetValue(shard.Index, out var fallback)) remote?.PollLateOnce(fallback);
            }
        }

        async Task ExecuteShardAsync(IndexedShard shard)
        {
            using var shardExecution = shardConcurrency.Enter();
            _testOverrides.OnInfrastructureShardResourcesAcquiredForTests?.Invoke(shard.Check.Name);
            var shardStarted = _timeProvider.GetTimestamp();
            var worker = new ShardWorkerLease(
                primarySlotIndex,
                primaryLease,
                primaryBuildPhase);
            var shardResultsDirectory = ResolveInfrastructureShardResultsDirectory(
                worker.Lease.Environment,
                AcceptanceAttemptResultsPrefix);
            var run = await RunCheckWithPartitionVerdictCacheAsync(
                shard.Check,
                cacheContext,
                worktreePath,
                goalId,
                worker.SlotIndex,
                worker.Lease,
                worker.BuildPhase,
                sharedApparatusCancellation.Token,
                shardResultsDirectory,
                reuseConsulted.ContainsKey(shard.Index)).ConfigureAwait(false);
            var shardElapsed = _timeProvider.GetElapsedTime(shardStarted);
            CompleteShard(shard, run.Result, run.Retried, shardElapsed);
        }

        void CompleteShard(IndexedShard shard, AcceptanceCheckResult result, bool retried, TimeSpan shardElapsed)
        {
            AcceptanceGatePhaseAccountant.RecordCurrentLaneSample(shardElapsed);
            outcomes[shard.Index] = new ShardRunOutcome(result, retried);
            AcceptanceLaneDurationStore.Record(shard.Check, result, shardElapsed);
            EmitShardTimingProgress(
                goalId,
                "shard-complete",
                shard.Check.Name,
                primarySlotIndex,
                shardElapsed,
                shardConcurrency.Count,
                _storageRoot, EmitGateProgress);
        }

        while (pendingShards.Count > 0 || activeShards.Count > 0 || activeRemote.Count > 0)
        {
            if (stopToken.IsCancellationRequested)
                pendingShards.Clear();
            // Offer remote work before filling local slots; these tasks never enter the local budget.
            if (remote is not null && !sharedApparatusCancellation.IsCancellationRequested)
            {
                for (var index = 0; index < pendingShards.Count; index++)
                {
                    var shard = pendingShards[index];
                    if (reuseConsulted.ContainsKey(shard.Index) || !remote.IsEligible(shard.Check)) continue;
                    if (!_remoteLaneOfferPlans.GetValue(remote, _ => throw new InvalidOperationException("Remote lane offer plan missing."))
                        .Decide(shard.Check.Name, allShardsUseMtp ? maxConcurrentShards : 1, ReportRemoteLanePolicy).Offer) continue;
                    var entry = remote.TryClaimIdleExecutor();
                    if (entry is null) continue;
                    pendingShards.RemoveAt(index--);
                    reuseConsulted[shard.Index] = true;
                    var reuseStarted = _timeProvider.GetTimestamp();
                    if (cacheContext?.TryReuse(shard.Check) is { } reused)
                    {
                        remote.Release(entry.Id);
                        CompleteShard(shard, reused, false, _timeProvider.GetElapsedTime(reuseStarted));
                        continue;
                    }
                    activeRemote.Add((shard, remote.RunRemoteAsync(shard.Check, entry,
                        EngineSettings.ResolveCheckTimeout(shard.Check.TimeoutMinutes), sharedApparatusCancellation.Token)));
                }
            }
            while (!cancellationToken.IsCancellationRequested &&
                   !stopToken.IsCancellationRequested &&
                   activeShards.Count < maxConcurrentExecutions)
            {
                var runnableIndex = pendingShards.FindIndex(shard =>
                    OrderExclusiveResourceKeys(shard.Check.ExclusiveResourceKeys)
                        .All(key => !activeResourceKeys.Contains(key)));
                if (runnableIndex < 0)
                {
                    break;
                }

                var shard = pendingShards[runnableIndex];
                pendingShards.RemoveAt(runnableIndex);
                var resourceKeys = OrderExclusiveResourceKeys(shard.Check.ExclusiveResourceKeys)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                foreach (var resourceKey in resourceKeys)
                {
                    if (!activeResourceKeys.Add(resourceKey))
                    {
                        throw new InvalidOperationException(
                            $"Infrastructure shard scheduler reserved active resource key '{resourceKey}' twice.");
                    }
                }

                activeShards.Add((RunShardAsync(shard), resourceKeys));
            }

            if (activeShards.Count >= maxConcurrentExecutions)
                foreach (var waiting in pendingShards.Where(shard => fallbackOutcomes.ContainsKey(shard.Index)))
                    _testOverrides.OnRemoteLaneFallbackWaitingForLocalSlotForTests?.Invoke(waiting.Check.Name);

            if (activeShards.Count == 0 && activeRemote.Count == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (stopToken.IsCancellationRequested || pendingShards.Count == 0)
                    break;
                throw new InvalidOperationException(
                    "Infrastructure shard scheduler has pending work but no runnable or active shard.");
            }

            var completedTask = await Task.WhenAny(
                activeShards.Select(active => active.Task).Concat(activeRemote.Select(active => (Task)active.Task))).ConfigureAwait(false);
            var remoteIndex = activeRemote.FindIndex(active => active.Task == completedTask);
            if (remoteIndex >= 0)
            {
                var completedRemote = activeRemote[remoteIndex];
                activeRemote.RemoveAt(remoteIndex);
                try
                {
                    var outcome = await completedRemote.Task.ConfigureAwait(false);
                    if (outcome.Accepted is { } accepted)
                    {
                        cacheContext?.RecordExecution(completedRemote.Shard.Check, accepted, "remote_first_run");
                        outcomes[completedRemote.Shard.Index] = new ShardRunOutcome(accepted, false);
                        if (outcome.RemoteDuration is { } remoteDuration)
                        {
                            try
                            {
                                AcceptanceGatePhaseAccountant.RecordCurrentRemoteLaneSample(remoteDuration);
                                EmitShardTimingProgress(goalId, "remote-shard-complete", completedRemote.Shard.Check.Name,
                                    primarySlotIndex, remoteDuration, shardConcurrency.Count, _storageRoot, EmitGateProgress);
                            }
                            catch (Exception) { /* Timing observations cannot change the gate verdict. */ }
                        }
                    }
                    else if (!sharedApparatusCancellation.IsCancellationRequested)
                    {
                        fallbackOutcomes[completedRemote.Shard.Index] = outcome;
                        pendingShards.Insert(0, completedRemote.Shard);
                    }
                }
                catch (Exception exception)
                {
                    if (exception is not OperationCanceledException ||
                        (cacheContext?.SharedApparatusInvalidation is null && !stopToken.IsCancellationRequested)) failures.Add(exception);
                }
                continue;
            }
            var completedIndex = activeShards.FindIndex(active => active.Task == completedTask);
            var completedResourceKeys = activeShards[completedIndex].ResourceKeys;
            activeShards.RemoveAt(completedIndex);
            try
            {
                await completedTask.ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                if (exception is not OperationCanceledException ||
                    (cacheContext?.SharedApparatusInvalidation is null && !stopToken.IsCancellationRequested))
                {
                    failures.Add(exception);
                }
            }
            finally
            {
                foreach (var resourceKey in completedResourceKeys)
                {
                    if (!activeResourceKeys.Remove(resourceKey))
                    {
                        throw new InvalidOperationException(
                            $"Infrastructure shard scheduler released inactive resource key '{resourceKey}'.");
                    }
                }
            }

            if (cacheContext?.SharedApparatusInvalidation is not null)
            {
                pendingShards.Clear();
                await sharedApparatusCancellation.CancelAsync().ConfigureAwait(false);
            }
        }

        if (failures.Count > 0)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failures[0]).Throw();
        }

        cancellationToken.ThrowIfCancellationRequested();
        var wallElapsed = _timeProvider.GetElapsedTime(wallClock);
        AcceptanceGatePhaseAccountant.RecordCurrentLaneScheduling(maxConcurrentExecutions, shardConcurrency.Peak);
        AcceptanceGatePhaseAccountant.RecordCurrentLaneExecution(wallElapsed);
        AcceptanceGatePhaseAccountant.TransitionCurrent(AcceptanceGatePhaseNames.CheckExecution);
        if (outcomes.Any(outcome => outcome is null) &&
            cacheContext?.SharedApparatusInvalidation is null && !stopToken.IsCancellationRequested)
        {
            throw new InvalidOperationException(
                "Concurrent infrastructure shard execution completed without a verdict for every shard.");
        }

        EmitShardTimingProgress(
            goalId,
            "shards-complete",
            $"{shardChecks.Count}-infrastructure-shards",
            primarySlotIndex,
            wallElapsed,
            shardConcurrency.Count,
            _storageRoot, EmitGateProgress);
        var completed = outcomes.Where(outcome => outcome is not null).Select(outcome => outcome!).ToArray();
        return new CheckBatchResult(
            cacheContext?.ApplySharedApparatusInvalidation(
                completed.Select(outcome => outcome.Result).ToArray()) ??
                completed.Select(outcome => outcome.Result).ToArray(),
            completed.Any(outcome => outcome.Retried));
    }

}
