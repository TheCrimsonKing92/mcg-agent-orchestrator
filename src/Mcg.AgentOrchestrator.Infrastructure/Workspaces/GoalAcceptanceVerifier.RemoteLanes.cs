using System.Runtime.CompilerServices;
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
    internal const int RemoteLaneOfferHistoryLineLimit = RemoteLaneOfferHistoryReader.LineLimit;

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
            ResolveGitScalar(path, "rev-parse", executionOwner.IntegrationBranch);
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
                () => TempRootApparatusLossReceiptStore.Read(executionOwner.ApparatusReceiptPath), ResolveClosureHash: _testOverrides.ResolvePartitionVerdictClosureHashForTests, ResolveChangedFiles: _testOverrides.ResolvePartitionVerdictChangedFilesForTests, IdenticalTreeChecks: [.. effectiveChecks.Where(check => AcceptanceIdenticalTreeReuseRule.TryGetCheckIdentity(check, out _))], MemberGoalIds: executionOwner.AttemptMemberGoalIds?.Select(id => id.Value).ToArray()),
            out var unavailableReason);
        if (partitionVerdictCache is null)
        {
            Console.WriteLine($"PARTITION_VERDICT_CACHE status=unavailable reason={unavailableReason}");
            Console.Out.Flush();
        }
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
            var local = AcceptanceLaneDurationStore.ResolveLocalEstimate(check);
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
            }) { RemoteLaneHistory = history };
        _remoteLaneOfferPlans.Add(coordinator, new RemoteLaneOfferPlan(inputs));
        if (cache is not null) _remoteLaneCoordinators.Add(cache, coordinator);
        else _cachelessRemoteLaneCoordinator = coordinator;
        return coordinator;
    }

    internal static IReadOnlyDictionary<(string Lane, string Filter), RemoteExecutorLaneSeconds>
        ReadRemoteLaneOfferHistory(string path) => RemoteLaneOfferHistoryReader.Read(path);

    private void ReportRemoteLanePolicy(string line)
    {
        Console.WriteLine(line);
        Console.Out.Flush(); try { _executionContext?.ReportRemoteLaneEvent(line.Length <= 512 ? line : line[..512]); } catch (Exception) { }
        _testOverrides.OnRemoteLaneProgressLineForTests?.Invoke(line);
    }
    private RemoteLaneCoordinator? ResolveGateRemoteLanes(AcceptancePartitionVerdictCache? cache) =>
        ShardPermitLaneClass != GateShardLaneClass.Gate ? null : cache is null ? _cachelessRemoteLaneCoordinator :
        _remoteLaneCoordinators.TryGetValue(cache, out var remote) ? remote : null;
    private RemoteLaneEarlyOffer.Context CreateRemoteLaneOfferContext(RemoteLaneCoordinator remote,
        IReadOnlyList<AcceptanceManifestCheck> shards, int concurrency, AcceptancePartitionVerdictCache? cache, GoalId? goal, int slot)
    {
        remote.Occupancy ??= new(goal?.Value, slot, _storageRoot, GateHeartbeatRunClass.Classify(_executionContext), GateHeartbeatRunClass.ResolveRunId(_executionContext));
        var plan = _remoteLaneOfferPlans.GetValue(remote, _ => throw new InvalidOperationException("Remote lane offer plan missing."));
        return new(lane => plan.Decide(lane, shards.All(UsesMicrosoftTestingPlatform) ? concurrency : 1,
                EngineSettings.ResolveCheckTimeout(shards.FirstOrDefault(check => check.Name == lane)?.TimeoutMinutes), ReportRemoteLanePolicy).Offer,
            (check, entry) => AcceptanceGateLaneProgress.RemoteStarted(goal, check.Name, entry.Id, slot, _storageRoot, EmitGateProgress),
            check => EngineSettings.ResolveCheckTimeout(check.TimeoutMinutes),
            check => plan.Deadline(check.Name, EngineSettings.ResolveCheckTimeout(check.TimeoutMinutes)), cache, _timeProvider);
    }
    private RemoteLaneEarlyOffer? StartRemoteLaneEarlyOffer(IReadOnlyList<AcceptanceManifestCheck> checks,
        AcceptancePartitionVerdictCache? cache, GoalId? goal, int? slot, DotnetBuildEnvironmentLease? lease,
        int concurrency, CancellationToken token)
    {
        if (!slot.HasValue || lease is null || concurrency <= 1 || ResolveGateRemoteLanes(cache) is not { } remote) return null;
        var shards = AcceptanceDotnetTestBatchScheduler.Group(checks, check => TryGetInfrastructurePartitionId(check, out _, out _))
            .FirstOrDefault(group => group.IsInfrastructurePartition)?.Checks ?? [];
        return RemoteLaneEarlyOffer.For(remote).Start(_testOverrides.RemoteLaneEarlyOfferDisabledForTests, shards,
            CreateRemoteLaneOfferContext(remote, shards, concurrency, cache, goal, slot.Value), token);
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
        var remote = ResolveGateRemoteLanes(cacheContext);
        await using var sharedApparatusCancellation = new AcceptanceGateShardCancellation(
            _executionContext?.ResolveCancellationProbe(boundary: false), cancellationToken, stopToken, remote);
        cancellationToken = sharedApparatusCancellation.CallerToken;
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
        var orderedShards = RemoteLaneEarlyOffer.Order(shardChecks).Select(shard => new IndexedShard(shard.Index, shard.Check)).ToArray();
        var outcomes = new ShardRunOutcome?[shardChecks.Count];
        var maxConcurrentExecutions = allShardsUseMtp ? Math.Min(maxConcurrentShards, shardChecks.Count) : 1;
        var pendingShards = orderedShards.ToList();
        var activeResourceKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var activeShards = new List<(IndexedShard Shard, Task Task, IReadOnlyList<string> ResourceKeys)>();
        var activeRemote = new List<(IndexedShard Shard, Task<RemoteLaneOutcome> Task)>();
        var reuseConsulted = new System.Collections.Concurrent.ConcurrentDictionary<int, bool>();
        var fallbackOutcomes = new System.Collections.Concurrent.ConcurrentDictionary<int, RemoteLaneOutcome>();
        var offerContext = remote is null ? null : CreateRemoteLaneOfferContext(remote, shardChecks, maxConcurrentShards, cacheContext, goalId, primarySlotIndex);
        var shardConcurrency = new GateShardConcurrencyCounter();
        var failures = new List<Exception>();
        AcceptanceLaneEarlyStop? earlyStop = null;
        var laneStopEnabled = cacheContext is not null && _testOverrides.PartitionVerdictWithinAttemptRerunEnabled;
        await sharedApparatusCancellation.WaitForOffersAsync().ConfigureAwait(false);

        bool IsConfirmedLocalFailure(IndexedShard shard, Task task) =>
            laneStopEnabled && task.IsCompleted &&
            outcomes[shard.Index]?.Result.LaneRerun?.Outcome == AcceptanceLaneRerunEvidence.ConfirmedFailure;

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
            var slowdown = GateLaneReceiptLedger.Record(worktreePath, goalId?.Value, shard.Check.Name, result, shardElapsed, _timeProvider.GetUtcNow());
            EmitShardTimingProgress(goalId, "shard-complete", shard.Check.Name, primarySlotIndex, shardElapsed, shardConcurrency.Count, _storageRoot, EmitGateProgress, result);
            GateLaneSlowdownDiff.TryEmitProgress(slowdown, goalId?.Value, shard.Check.Name, shardElapsed, EmitGateProgress);
        }

        while (pendingShards.Count > 0 || activeShards.Count > 0 || activeRemote.Count > 0)
        {
            if (sharedApparatusCancellation.IsCancellationRequested)
                pendingShards.Clear();
            // Offer remote work before filling local slots; these tasks never enter the local budget.
            if (remote is not null && !sharedApparatusCancellation.IsCancellationRequested &&
                !activeShards.Any(active => IsConfirmedLocalFailure(active.Shard, active.Task)))
            {
                for (var index = 0; index < pendingShards.Count; index++)
                {
                    var shard = pendingShards[index];
                    if (activeShards.Any(active => IsConfirmedLocalFailure(active.Shard, active.Task))) break;
                    if (reuseConsulted.ContainsKey(shard.Index)) continue;
                    var (offered, offerCancelled) = await sharedApparatusCancellation.TryOfferAsync(shard.Check, offerContext!).ConfigureAwait(false);
                    if (offerCancelled)
                    {
                        pendingShards.Clear();
                        break; // Drain active work before rethrowing caller/probe cancellation below.
                    }
                    if (offered is null) continue;
                    pendingShards.RemoveAt(index--);
                    reuseConsulted[shard.Index] = true;
                    if (offered.Reused is { } reused) CompleteShard(shard, reused, false, offered.ReuseElapsed);
                    else activeRemote.Add((shard, offered.Remote!));
                }
            }
            while (!cancellationToken.IsCancellationRequested &&
                   !stopToken.IsCancellationRequested &&
                   !sharedApparatusCancellation.IsCancellationRequested &&
                   !activeShards.Any(active => IsConfirmedLocalFailure(active.Shard, active.Task)) &&
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

                activeShards.Add((shard, RunShardAsync(shard), resourceKeys));
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

            // Observe a confirmed local failure before replenishing capacity from other completions.
            var completedTask = activeShards
                .Where(active => IsConfirmedLocalFailure(active.Shard, active.Task))
                .Select(active => active.Task).FirstOrDefault() ?? await Task.WhenAny(
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
                                    primarySlotIndex, remoteDuration, shardConcurrency.Count, _storageRoot, EmitGateProgress, accepted);
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
                    sharedApparatusCancellation.RecordFailure(failures, exception, earlyStop is not null || cacheContext?.SharedApparatusInvalidation is not null);
                }
                continue;
            }
            var completedIndex = activeShards.FindIndex(active => active.Task == completedTask);
            var completedShard = activeShards[completedIndex].Shard;
            var completedResourceKeys = activeShards[completedIndex].ResourceKeys;
            activeShards.RemoveAt(completedIndex);
            try
            {
                await completedTask.ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                sharedApparatusCancellation.RecordFailure(failures, exception, earlyStop is not null || cacheContext?.SharedApparatusInvalidation is not null);
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

            if (earlyStop is null && cacheContext?.SharedApparatusInvalidation is null &&
                !sharedApparatusCancellation.IsCancellationRequested && IsConfirmedLocalFailure(completedShard, completedTask))
            {
                earlyStop = new AcceptanceLaneEarlyStop(
                    completedShard.Check.Name,
                    pendingShards.OrderBy(shard => shard.Index).Select(shard => shard.Check.Name).ToArray(),
                    activeShards.Where(active => outcomes[active.Shard.Index] is null)
                        .OrderBy(active => active.Shard.Index).Select(active => active.Shard.Check.Name).ToArray(),
                    activeRemote.Where(active => outcomes[active.Shard.Index] is null &&
                            !(active.Task.IsCompletedSuccessfully && active.Task.Result.Accepted is not null))
                        .OrderBy(active => active.Shard.Index).Select(active => active.Shard.Check.Name).ToArray());
                pendingShards.Clear();
                await sharedApparatusCancellation.CancelAsync().ConfigureAwait(false);
                var now = _timeProvider.GetUtcNow();
                EmitGateProgress(new AcceptanceGateProgress(goalId?.Value, "lane-early-stop", earlyStop.ConfirmingLane,
                    primarySlotIndex, Environment.ProcessId, null, now, now, now,
                    _timeProvider.GetElapsedTime(wallClock), 0, string.Empty),
                    $"confirming_lane={QuoteProgressToken(earlyStop.ConfirmingLane)} lanes_stopped={earlyStop.StoppedCount}");
            }
            else if (cacheContext?.SharedApparatusInvalidation is not null)
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
            earlyStop is null && cacheContext?.SharedApparatusInvalidation is null && !stopToken.IsCancellationRequested)
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
        var completed = outcomes.Where(outcome => outcome is not null &&
                earlyStop?.CancelledMidRun.Contains(outcome.Result.Name, StringComparer.Ordinal) != true)
            .Select(outcome => outcome!).ToArray();
        var results = cacheContext?.ApplySharedApparatusInvalidation(
                completed.Select(outcome => outcome.Result).ToArray()) ??
                completed.Select(outcome => outcome.Result).ToArray();
        return new CheckBatchResult(
            earlyStop is null ? results : [.. results, earlyStop.ToReceiptCheck()],
            completed.Any(outcome => outcome.Retried), earlyStop);
    }
}
