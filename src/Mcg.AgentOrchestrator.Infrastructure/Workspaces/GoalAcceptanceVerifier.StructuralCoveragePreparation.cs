using System.Text.Json;
using System.Runtime.ExceptionServices;
using System.Runtime.CompilerServices;
using Mcg.AgentOrchestrator.Core;
using static Mcg.AgentOrchestrator.Infrastructure.AcceptancePolicyShardPlanner;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed partial class GoalAcceptanceVerifier
{
    private sealed record StructuralCoverageProject(
        AcceptanceManifestCheck Check,
        AcceptanceStructuralCoverageRequest? Request,
        AcceptanceStructuralCoveragePrepared? Prepared,
        ExceptionDispatchInfo? Fault,
        RemovedTestProjectRecord? Removed = null);

    private sealed record StructuralCoveragePreparation(
        IReadOnlyList<StructuralCoverageProject> Projects,
        Action<IReadOnlyList<AcceptanceCheckResult>> SetCompletedChecks,
        AcceptanceCheckResult? TerminalResult);

    private async Task<StructuralCoveragePreparation> PrepareStructuralCoverageCheckAsync(
        IReadOnlyList<AcceptanceManifestCheck> effectiveChecks,
        IReadOnlyList<AcceptanceTestLane> infrastructureTestLanes,
        string worktreePath,
        GoalId? goalId,
        int? stableSlotIndex,
        DotnetBuildEnvironmentLease? stableSlotLease,
        string? currentAttemptId,
        IReadOnlyList<string> sanctionedRemovedTests,
        IReadOnlyList<AcceptanceManifestCheck> candidateManifestChecks,
        AcceptanceGateEngineSettings candidateSettings,
        bool ownerApprovalSatisfied,
        CancellationToken cancellationToken)
    {
        var mainWorktreePath = ResolveMainWorktreePath(worktreePath);
        if (string.IsNullOrWhiteSpace(mainWorktreePath))
        {
            throw new AcceptanceInfrastructureDeferredException(
                "trusted-main-worktree-unavailable",
                exitCode: null,
                outputTail: "Trusted main worktree could not be resolved for cross-generation discovery.");
        }

        var broadChecks = DiscoverTrustedStructuralCoverageProjects(worktreePath, mainWorktreePath)
            .Select(project => BuildTrustedStructuralCoverageCheck(worktreePath, project, effectiveChecks))
            .ToArray();
        if (broadChecks.Length == 0)
        {
            return new StructuralCoveragePreparation([], _ => { }, new AcceptanceCheckResult(
                "structural test coverage",
                false,
                1,
                "Structural coverage is enabled but trusted discovery found no test projects.",
                ResultSummary: "no trusted test project"));
        }

        var environment = ResolveExecutionEnvironment(
            goalId,
            "acceptance-coverage-discovery",
            stableSlotIndex,
            stableSlotLease);
        var projects = new List<StructuralCoverageProject>();
        IReadOnlyList<AcceptanceCheckResult>? completedChecks = null;
        foreach (var broadCheck in broadChecks)
        {
            if (ClassifyRemovedTestProject(worktreePath, mainWorktreePath, broadCheck.Project!,
                    candidateManifestChecks, candidateSettings, ownerApprovalSatisfied) is { } removal)
            {
                projects.Add(await PrepareRemovedTestProjectAsync(broadCheck, removal, mainWorktreePath,
                    environment, goalId, stableSlotIndex, stableSlotLease, cancellationToken).ConfigureAwait(false));
                continue;
            }
            try
            {
            var deletedTestFiles = ResolveDeletedTestFiles(worktreePath, broadCheck.Project!);
            var candidateDiscoveryArguments = BuildUnattendedDiscoveryArguments(
                broadCheck,
                EngineSettings,
                environment);
            IReadOnlyList<TestPartitionCoverage> ResolvePartitions()
            {
                IEnumerable<AcceptanceManifestCheck> partitionChecks = IsBroadInfrastructureTestCheck(broadCheck)
                    ? AcceptanceStructuralCoveragePartitionPlan.Resolve(
                        broadCheck,
                        effectiveChecks,
                        infrastructureTestLanes)
                    : effectiveChecks.Where(check =>
                        check.Type.Equals("dotnet-test", StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(NormalizePath(check.Project), NormalizePath(broadCheck.Project), StringComparison.OrdinalIgnoreCase));
                if (!partitionChecks.Any())
                    partitionChecks = [broadCheck];
                return partitionChecks
                    .Select(shard =>
                    {
                        var result = (completedChecks ?? throw new InvalidOperationException(
                            "Structural coverage partitions were resolved before lane completion.")).LastOrDefault(candidate =>
                            candidate.Name.Equals(shard.Name, StringComparison.OrdinalIgnoreCase));
                        return new TestPartitionCoverage(
                            shard.Name,
                            result?.Passed == true,
                            result?.TestResultPaths ?? [],
                            result?.LockRemediationApplied == true,
                            result?.TestResultAttemptId,
                            result?.TestResultRunOrdinal ?? 0,
                            result?.TestResultIsExplicitCrossAttemptReuse == true);
                    })
                    .ToArray();
            }

            Task<AcceptanceStructuralCoverageBaseline?> PrepareBaselineAsync(
                string baselineWorktreePath,
                string artifactsDirectoryName,
                string operationName,
                CancellationToken baselineCancellationToken) =>
                PrepareStructuralCoverageBaselineAsync(broadCheck, EngineSettings, environment,
                    goalId, stableSlotIndex, stableSlotLease, baselineWorktreePath,
                    artifactsDirectoryName, operationName, baselineCancellationToken);

            Task<AcceptanceContainedGenerationBaseline> PrepareContainedBaselineAsync(
                CancellationToken baselineCancellationToken) =>
                AcceptanceContainedGenerationBaseline.PrepareAsync(
                    worktreePath,
                    goalId?.Value ?? "operator",
                    (path, sha, token) => PrepareBaselineAsync(
                        path,
                        $"contained-coverage-baseline-{sha[..Math.Min(8, sha.Length)]}",
                        "acceptance-contained-coverage-baseline",
                        token),
                    AcceptanceGitTextResolver.Resolve,
                    static (directory, arguments) => GitCli.Run(directory, arguments),
                    baselineCancellationToken);

            var request = new AcceptanceStructuralCoverageRequest(
                    candidateDiscoveryArguments,
                    worktreePath,
                    EngineSettings.ResolveDiscoveryTimeout(),
                    UsesMicrosoftTestingPlatform(broadCheck),
                    ResolvePartitions,
                    () => DeletedTestFilesForProject(deletedTestFiles, broadCheck.Project!),
                    currentAttemptId,
                    sanctionedRemovedTests,
                    cancellationToken => PrepareBaselineAsync(
                        mainWorktreePath,
                        "main-coverage-baseline",
                        "acceptance-main-coverage-baseline",
                        cancellationToken),
                    PrepareContainedBaselineAsync);
            var prepared = await _structuralCoverageEvaluator.PrepareAsync(request, cancellationToken)
                .ConfigureAwait(false);
            projects.Add(new StructuralCoverageProject(broadCheck, request, prepared, null));
            if (prepared.CandidateDiscovery.ExitCode != 0 || prepared.BaselineDiscoveryIoException is not null ||
                prepared.BaselineDiscovery is { } discovery && (discovery.TimedOut || discovery.ExitCode != 0))
                break;
            }
            catch (Exception exception) when (exception is not OperationCanceledException ||
                                             !cancellationToken.IsCancellationRequested)
            {
                projects.Add(new StructuralCoverageProject(broadCheck, null, null,
                    ExceptionDispatchInfo.Capture(exception)));
                break;
            }
        }

        return new StructuralCoveragePreparation(projects, checks => completedChecks = checks, null);
    }

    private async Task<AcceptanceCheckResult> EvaluateStructuralCoverageCheckAsync(
        StructuralCoveragePreparation preparation,
        IReadOnlyList<AcceptanceCheckResult> completedChecks,
        CancellationToken cancellationToken)
    {
        if (preparation.TerminalResult is { } terminal)
            return terminal;

        preparation.SetCompletedChecks(completedChecks);
        var allSummaries = new List<string>();
        var removalFailures = new List<AcceptanceCheckResult>();
        var baselineLockRemediationApplied = false;
        foreach (var project in preparation.Projects)
        {
            if (project.Removed is { } removal)
            {
                var result = EvaluateRemovedTestProject(project.Check, removal);
                baselineLockRemediationApplied |= result.LockRemediationApplied;
                if (result.Passed) allSummaries.Add(result.ResultSummary!);
                else removalFailures.Add(result);
                continue;
            }
            project.Fault?.Throw();
            var broadCheck = project.Check;
            var evaluation = await _structuralCoverageEvaluator.CompareAsync(
                project.Request!, project.Prepared!, cancellationToken).ConfigureAwait(false);

            var candidateDiscovery = evaluation.CandidateDiscovery;
            if (candidateDiscovery.ExitCode != 0)
            {
                return new AcceptanceCheckResult(
                    $"structural test coverage: {broadCheck.Name}",
                    false,
                    candidateDiscovery.ExitCode,
                    TailOutput(candidateDiscovery.Output),
                    ResultSummary: "candidate trusted discovery failed");
            }

            if (evaluation.BaselineDiscoveryIoException is { } baselineDiscoveryException)
            {
                throw new AcceptanceInfrastructureDeferredException(
                    "trusted-main-discovery-io",
                    exitCode: null,
                    outputTail: baselineDiscoveryException.Message);
            }

            if (evaluation.BaselineDiscovery is { } mainDiscovery &&
                (mainDiscovery.TimedOut || mainDiscovery.ExitCode != 0))
            {
                throw new AcceptanceInfrastructureDeferredException(
                    mainDiscovery.TimedOut
                        ? "trusted-main-discovery-timeout"
                        : "trusted-main-discovery-failed",
                    mainDiscovery.ExitCode,
                    TailOutput(mainDiscovery.Output));
            }

            baselineLockRemediationApplied |= evaluation.BaselineLockRemediationApplied;
            var coverage = evaluation.Coverage
                ?? throw new InvalidOperationException("Structural coverage evaluation produced no verdict.");
            if (!coverage.Passed)
            {
                var details = new List<string>
                {
                    $"classification: {coverage.FailureClassification}",
                    coverage.Summary
                };
                details.AddRange(coverage.EmptyPartitions.Take(10).Select(name => $"empty partition: {name}"));
                var identityMismatches = coverage.IdentityMismatches ?? [];
                details.AddRange(identityMismatches.Take(10).Select(mismatch =>
                    $"missing test: discovered={JsonSerializer.Serialize(mismatch.Discovered)}; executed={JsonSerializer.Serialize(mismatch.Executed)}"));
                details.AddRange(coverage.MissingTests
                    .Except(identityMismatches.Select(mismatch => mismatch.Discovered), StringComparer.OrdinalIgnoreCase)
                    .Take(10)
                    .Select(name => $"missing test: {name}"));
                return new AcceptanceCheckResult(
                    $"structural test coverage: {broadCheck.Name}",
                    false,
                    1,
                    string.Join(Environment.NewLine, details),
                    LockRemediationApplied: baselineLockRemediationApplied,
                    ResultSummary: coverage.Summary,
                    FailureClassification: coverage.FailureClassification);
            }

            allSummaries.Add(coverage.Summary);
        }

        if (removalFailures.Count > 0)
            return CombineRemovedTestProjectFailures(removalFailures, allSummaries, baselineLockRemediationApplied);

        return new AcceptanceCheckResult(
            "structural test coverage",
            true,
            0,
            null,
            LockRemediationApplied: baselineLockRemediationApplied,
            ResultSummary: string.Join("; ", allSummaries));
    }

    private sealed record StructuralCoveragePreparationOutcome(
        StructuralCoveragePreparation? Preparation,
        ExceptionDispatchInfo? Fault,
        TimeSpan Duration);

    private static AcceptanceManifestCheck WithTrustedBaselineBuildIdentity(AcceptanceManifestCheck check) => new()
    {
        Name = $"{check.Name} [trusted main baseline]",
        Type = check.Type,
        Command = check.Command,
        Project = check.Project,
        Arguments = check.Arguments,
        Pattern = check.Pattern,
        FilePath = check.FilePath,
        TimeoutMinutes = check.TimeoutMinutes,
        Advisory = check.Advisory,
        Runner = check.Runner,
        EstimatedSerialSeconds = check.EstimatedSerialSeconds,
        ExclusiveResourceKeys = check.ExclusiveResourceKeys,
        IsFocusedEvidenceSelection = check.IsFocusedEvidenceSelection,
        FocusedEvidenceTokens = check.FocusedEvidenceTokens,
        FocusedEvidenceSelections = check.FocusedEvidenceSelections
    };

    private readonly object _coveragePreparationSync = new();
    // Invocation verifiers share the execution owner, not the preparation's instance fields.
    private static readonly ConditionalWeakTable<IAcceptanceRunExecutionContext, StructuralCoverageLockHold>
        CoverageLockHolds = new();
    private StructuralCoverageLockHold? _coverageLockHold;
    private Func<CancellationToken, Task<StructuralCoveragePreparation>>? _coveragePreparationFactory;

    private void BeginStructuralCoverageLockHoldForBatch(
        IReadOnlyList<AcceptanceManifestCheck> checks,
        int? stableSlotIndex,
        DotnetBuildEnvironmentLease? stableSlotLease,
        DotnetTestBuildPhase? buildPhase,
        int maxConcurrentShards)
    {
        // Match RunCheckBatchAsync's infrastructure shard branch: only that path signals
        // candidate-build completion while lanes run. Sequential MTP releases stay immediate.
        if (_coveragePreparationFactory is null || _coverageLockHold is not null ||
            !stableSlotIndex.HasValue || stableSlotLease is null || buildPhase is null || maxConcurrentShards <= 1)
            return;

        for (var index = 0; index < checks.Count; index++)
        {
            if (!TryGetInfrastructurePartitionId(checks[index], out _, out _))
                continue;
            var shardChecks = checks.Skip(index)
                .TakeWhile(check => TryGetInfrastructurePartitionId(check, out _, out _)).ToArray();
            // RunInfrastructureShardBatchAsync signals preparation only for an MTP prebuild.
            if (shardChecks.All(UsesMicrosoftTestingPlatform))
            {
                var owner = _executionContext ?? throw new InvalidOperationException(
                    "Structural coverage preparation requires an execution owner.");
                _coverageLockHold = CoverageLockHolds.GetValue(owner, _ => new StructuralCoverageLockHold());
                return;
            }
            index += shardChecks.Length - 1;
        }
    }
    private Task<StructuralCoveragePreparationOutcome>? _coveragePreparationTask;
    private CancellationTokenSource? _coveragePreparationCancellation;
    private bool _coveragePrechecksPassed;
    private bool _coverageCandidateBuildComplete;
    private bool _coverageHasIndependentChecks;

    private sealed class StructuralCoverageLockHold
    {
        private readonly object _sync = new();
        private readonly HashSet<DotnetBuildEnvironmentLease> _releaseRequests = [];
        private bool _ended;

        public void RequestRelease(DotnetBuildEnvironmentLease lease)
        {
            lock (_sync)
            {
                if (!_releaseRequests.Add(lease))
                    return;
                if (_ended)
                    lease.ReleaseExecutionLock();
            }
        }

        public void End()
        {
            lock (_sync)
            {
                if (_ended)
                    return;
                _ended = true;
                foreach (var lease in _releaseRequests)
                    lease.ReleaseExecutionLock();
            }
        }
    }

    private void ReleaseStableSlotExecutionLockUnlessCoverageHoldActive(DotnetBuildEnvironmentLease? lease)
    {
        if (lease is null)
            return;
        if (_executionContext is { } owner && CoverageLockHolds.TryGetValue(owner, out var hold))
            hold.RequestRelease(lease);
        else
            lease.ReleaseExecutionLock();
    }

    private async Task<CheckBatchResult> RunStructuralCoveragePrechecksAsync(Func<Task<CheckBatchResult>> runPrechecks)
    {
        var passed = false;
        try
        {
            var result = await runPrechecks().ConfigureAwait(false);
            passed = result.Results.All(check => check.Passed);
            if (passed)
                SignalStructuralCoveragePrechecksPassed();
            return result;
        }
        finally
        {
            if (!passed)
                _coverageLockHold?.End();
        }
    }

    private void SignalStructuralCoveragePrechecksPassed()
    {
        lock (_coveragePreparationSync)
        {
            _coveragePrechecksPassed = true;
            TryStartStructuralCoveragePreparation();
        }
    }

    private void SignalStructuralCoverageCandidateBuildComplete()
    {
        lock (_coveragePreparationSync)
        {
            _coverageCandidateBuildComplete = true;
            TryStartStructuralCoveragePreparation();
        }
    }

    private void TryStartStructuralCoveragePreparation()
    {
        if (!_coveragePrechecksPassed || !_coverageCandidateBuildComplete ||
            _coveragePreparationFactory is null || _coveragePreparationTask is not null)
            return;

        var owner = _executionContext ?? throw new InvalidOperationException(
            "Structural coverage preparation requires an execution owner.");
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(owner.CancellationToken);
        _coveragePreparationCancellation = cancellation;
        var task = owner.TrackStarted(Task.Run(async () =>
        {
            var started = _timeProvider.GetTimestamp();
            try
            {
                var preparation = await _coveragePreparationFactory(cancellation.Token).ConfigureAwait(false);
                return new StructuralCoveragePreparationOutcome(preparation, null,
                    _timeProvider.GetElapsedTime(started));
            }
            catch (Exception exception)
            {
                return new StructuralCoveragePreparationOutcome(null, ExceptionDispatchInfo.Capture(exception),
                    _timeProvider.GetElapsedTime(started));
            }
            finally
            {
                _coverageLockHold?.End();
            }
        }));
        _coveragePreparationTask = task;
        if (_testOverrides.OnStructuralCoveragePreparationFinishedForTests is { } onFinished)
            _ = task.ContinueWith(_ => onFinished(), CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private async Task<(StructuralCoveragePreparation Preparation, TimeSpan Duration, TimeSpan Wait)>
        AwaitStructuralCoveragePreparationAsync(CancellationToken cancellationToken)
    {
        Task<StructuralCoveragePreparationOutcome> task;
        lock (_coveragePreparationSync)
        {
            if (_coveragePreparationTask is null)
            {
                _coveragePrechecksPassed = true;
                _coverageCandidateBuildComplete = true;
                TryStartStructuralCoveragePreparation();
            }
            task = _coveragePreparationTask!;
        }

        var pending = !task.IsCompleted;
        var started = _timeProvider.GetTimestamp();
        var outcome = await task.WaitAsync(cancellationToken).ConfigureAwait(false);
        var wait = pending ? _timeProvider.GetElapsedTime(started) : TimeSpan.Zero;
        outcome.Fault?.Throw();
        return (outcome.Preparation!, outcome.Duration, wait);
    }

    private async Task<TimeSpan> DiscardStructuralCoveragePreparationAsync()
    {
        try
        {
            _coveragePreparationCancellation?.Cancel();
            if (_coveragePreparationTask is { } task)
            {
                try { return (await task.ConfigureAwait(false)).Duration; }
                catch { /* A discarded preparation never replaces the lane verdict. */ }
            }
            return TimeSpan.Zero;
        }
        finally
        {
            _coverageLockHold?.End();
        }
    }

}
