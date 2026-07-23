using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed record AcceptanceCheckResult(
    string Name,
    bool Passed,
    int? ExitCode,
    string? OutputTail,
    string? ArtifactsPath = null,
    string? BrokerName = null,
    string? LeaseId = null,
    long? DurationMilliseconds = null,
    bool LockRemediationApplied = false,
    string? ResultSummary = null,
    bool Advisory = false,
    IReadOnlyList<string>? TestResultPaths = null);

public sealed record AcceptanceVerificationResult(
    bool Passed,
    bool Skipped,
    int? ExitCode,
    string? OutputTail,
    bool Retried = false,
    string? ArtifactsPath = null,
    IReadOnlyList<AcceptanceCheckResult>? Checks = null,
    IReadOnlyList<string>? TestResultPaths = null);

public sealed record FocusedEvidenceRunResult(
    string Request,
    bool Accepted,
    bool Passed,
    string Summary,
    IReadOnlyList<AcceptanceCheckResult> Checks);

public interface IGoalAcceptanceVerifier
{
    Task<AcceptanceVerificationResult> RunAsync(
        string worktreePath,
        GoalId? goalId = null,
        IReadOnlyList<string>? changedFiles = null,
        int? stableSlotIndex = null,
        DotnetBuildEnvironmentLease? stableSlotLease = null,
        CancellationToken cancellationToken = default);

    Task<FocusedEvidenceRunResult> RunFocusedEvidenceAsync(
        string worktreePath,
        GoalId? goalId,
        string request,
        int? stableSlotIndex = null,
        DotnetBuildEnvironmentLease? stableSlotLease = null,
        CancellationToken cancellationToken = default);
}

public sealed class GoalAcceptanceVerifier : IGoalAcceptanceVerifier
{
    internal sealed record CommandResult(
        int ExitCode,
        string Output,
        bool TimedOut = false,
        string? CommandLine = null,
        string? StdoutPath = null,
        string? StderrPath = null,
        TimeSpan? Timeout = null,
        TimeSpan? Elapsed = null,
        TaskProcessResourceAccounting? ResourceAccounting = null,
        bool ResourceAccountingExpected = false,
        long StdoutBytes = 0,
        long StderrBytes = 0);

    private static readonly Regex TestAttrPattern = new(
        @"^\[(?:Fact|Theory|Xunit\.Fact\()",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex TautologyPattern = new(
        @"Assert\.True\(\s*true\s*\)|Assert\.False\(\s*false\s*\)|Assert\.Equal\(\s*(?<v>\w+)\s*,\s*\k<v>\s*\)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly string[] DiffBaseArgs = ["git", "diff", "--unified=0", "main...HEAD", "--"];
    private const string CoreProject = "src/Mcg.AgentOrchestrator.Core/Mcg.AgentOrchestrator.Core.csproj";
    private const string InfrastructureProject = "src/Mcg.AgentOrchestrator.Infrastructure/Mcg.AgentOrchestrator.Infrastructure.csproj";
    private const string AppProject = "src/Mcg.AgentOrchestrator.App/Mcg.AgentOrchestrator.App.csproj";
    private const string CoreTestsProject = "tests/Mcg.AgentOrchestrator.Core.Tests/Mcg.AgentOrchestrator.Core.Tests.csproj";
    private const string InfrastructureTestsProject = "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj";
    private const string PartitionVerdictJournalOperation = "acceptance:partition-verdict";
    private const string PartitionVerdictCacheJournalOperation = "acceptance:partition-verdict-cache";
    private const int MaxFocusedEvidenceTargets = 4;
    private const int DefaultPartitionVerdictFullRerunEveryN = 5;
    public const string AcceptanceAttemptTrxPrefixVariable = "MCG_ACCEPTANCE_GATE_ATTEMPT_TRX_PREFIX";

    private static readonly Dictionary<string, string[]> ReferencingProjectsByProject = new(StringComparer.OrdinalIgnoreCase)
    {
        [CoreProject] = [InfrastructureProject, AppProject, CoreTestsProject, InfrastructureTestsProject],
        [InfrastructureProject] = [AppProject, InfrastructureTestsProject],
        [AppProject] = [InfrastructureTestsProject],
        [CoreTestsProject] = [],
        [InfrastructureTestsProject] = []
    };

    private static readonly InfrastructureTestLane[] InfrastructureTestLanes =
    [
        new("Cli", "FullyQualifiedName~CliCommandTests"),
        new("Cli help", "FullyQualifiedName~CliHelpTests"),
        new("Worker dispatch", "FullyQualifiedName~WorkerDispatchTests"),
        new("Worker profiles", "FullyQualifiedName~WorkerProfileTests"),
        new("Worker processes", "FullyQualifiedName~WorkerProcessJobsTests"),
        new("Worker shell", "FullyQualifiedName~WorkerShellTests"),
        new("Worker sandbox planner", "FullyQualifiedName~WorkerSandboxCapabilityPlannerTests"),
        new("Dispatch process host", "FullyQualifiedName~DispatchProcessHostTests"),
        new("Goal worktree", "FullyQualifiedName~GoalWorktreeTests"),
        new("Goal acceptance verifier", "FullyQualifiedName~GoalAcceptanceVerifierTests"),
        new("Dashboard rendering", "FullyQualifiedName~DashboardRenderingTests"),
        new("Dashboard host", "FullyQualifiedName~DashboardHostTests&Category!=HostIntegration"),
        new("Dashboard validation", "FullyQualifiedName~DashboardValidationHarnessTests"),
        new("Advance loop", "FullyQualifiedName~AdvanceLoopTests"),
        new("Conductor batch loop", "FullyQualifiedName~ConductorBatchLoopTests"),
        new("Conductor driver", "FullyQualifiedName~ConductorDriverTests"),
        new("Conduct watch sweep scoping", "FullyQualifiedName~ConductWatchSweepScopingTests"),
        new("Remainder",
            "FullyQualifiedName!~CliCommandTests&FullyQualifiedName!~CliHelpTests" +
            "&FullyQualifiedName!~WorkerDispatchTests&FullyQualifiedName!~WorkerProfileTests" +
            "&FullyQualifiedName!~WorkerProcessJobsTests&FullyQualifiedName!~WorkerShellTests" +
            "&FullyQualifiedName!~WorkerSandboxCapabilityPlannerTests&FullyQualifiedName!~DispatchProcessHostTests" +
            "&FullyQualifiedName!~GoalWorktreeTests&FullyQualifiedName!~GoalAcceptanceVerifierTests" +
            "&FullyQualifiedName!~DashboardRenderingTests&FullyQualifiedName!~DashboardHostTests" +
            "&FullyQualifiedName!~DashboardValidationHarnessTests&FullyQualifiedName!~AdvanceLoopTests" +
            "&FullyQualifiedName!~ConductorBatchLoopTests&FullyQualifiedName!~ConductorDriverTests" +
            "&FullyQualifiedName!~ConductWatchSweepScopingTests&Category!=HostIntegration")
    ];

    private readonly Func<string[], string, TimeSpan, CancellationToken, Task<CommandResult>> _runner;
    private readonly TimeProvider _timeProvider;
    private readonly Action<TimeSpan> _leaseSleep;
    private static readonly AsyncLocal<GateHeartbeatContext?> CurrentGateHeartbeatContext = new();
    private static readonly AsyncLocal<Action<AcceptanceGateProgress>?> CurrentGateProgressSink = new();
    private static readonly AsyncLocal<Func<bool>?> CurrentGateCancellationProbe = new();
    private static readonly JsonSerializerOptions PartitionVerdictJournalJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };
    private static readonly object PartitionVerdictJournalGate = new();
    internal static TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(5);
    internal static TimeSpan ProgressInterval { get; set; } = TimeSpan.FromSeconds(30);
    internal static TimeSpan TransientNoHolderBuildLockWaitWindow { get; set; } = TimeSpan.FromSeconds(75);
    internal static TimeSpan TransientNoHolderBuildLockPollInterval { get; set; } = TimeSpan.FromMilliseconds(250);
    internal static int TransientNoHolderBuildLockMaxRetryCycles { get; set; } = 2;
    internal static Func<string, string?>? ResolveBaseBuildMainShaForTests { get; set; }
    internal static Func<string, string?>? ResolvePartitionVerdictCandidateTreeShaForTests { get; set; }
    internal static Func<string, string?>? ResolvePartitionVerdictMainShaForTests { get; set; }
    internal static Func<string, string?>? ResolvePartitionVerdictVerifyingCommitShaForTests { get; set; }
    internal static int PartitionVerdictFullRerunEveryN { get; set; } = DefaultPartitionVerdictFullRerunEveryN;
    // When true (default), a failed infrastructure-test PARTITION is re-run ONCE within the same
    // acceptance attempt; if the re-run passes, the failure was an intermittent flake and the partition
    // is treated as passed (Retried=true keeps it visible). A genuine red still fails both runs, so real
    // failures are unaffected. This is the within-attempt companion to the cross-attempt partition-verdict
    // cache. Tests that assert exact per-attempt partition run counts disable it explicitly.
    internal static bool PartitionVerdictWithinAttemptRerunEnabled { get; set; } = true;
    internal static DotnetBaseBuildCache? BaseBuildCacheForTests { get; set; }
    private static readonly string[] CacheableProjects =
    [
        CoreProject,
        InfrastructureProject,
        AppProject,
        CoreTestsProject,
        InfrastructureTestsProject
    ];

    public GoalAcceptanceVerifier() : this(RunProcessAsync, TimeProvider.System) { }

    internal GoalAcceptanceVerifier(Func<string[], string, CancellationToken, Task<CommandResult>> runner)
        : this(runner, TimeProvider.System)
    {
    }

    internal GoalAcceptanceVerifier(
        Func<string[], string, CancellationToken, Task<CommandResult>> runner,
        TimeProvider timeProvider,
        Action<TimeSpan>? leaseSleep = null)
        : this((arguments, workingDirectory, _, cancellationToken) =>
            runner(arguments, workingDirectory, cancellationToken), timeProvider, leaseSleep)
    {
    }

    internal GoalAcceptanceVerifier(Func<string[], string, TimeSpan, CancellationToken, Task<CommandResult>> runner)
        : this(runner, TimeProvider.System)
    {
    }

    internal GoalAcceptanceVerifier(
        Func<string[], string, TimeSpan, CancellationToken, Task<CommandResult>> runner,
        TimeProvider timeProvider,
        Action<TimeSpan>? leaseSleep = null)
    {
        _runner = runner;
        _timeProvider = timeProvider;
        _leaseSleep = leaseSleep ?? Thread.Sleep;
    }

    public static IDisposable PushGateProgressSink(Action<AcceptanceGateProgress> sink)
    {
        var previous = CurrentGateProgressSink.Value;
        CurrentGateProgressSink.Value = sink;
        return new RestoreAction(() => CurrentGateProgressSink.Value = previous);
    }

    public static IDisposable PushGateCancellationProbe(Func<bool> shouldCancel)
    {
        var previous = CurrentGateCancellationProbe.Value;
        CurrentGateCancellationProbe.Value = shouldCancel;
        return new RestoreAction(() => CurrentGateCancellationProbe.Value = previous);
    }

    public async Task<AcceptanceVerificationResult> RunAsync(
        string worktreePath,
        GoalId? goalId = null,
        IReadOnlyList<string>? changedFiles = null,
        int? stableSlotIndex = null,
        DotnetBuildEnvironmentLease? stableSlotLease = null,
        CancellationToken cancellationToken = default)
    {
        // Shut down build servers to release file locks before running tests.
        await _runner(
            ["dotnet", "build-server", "shutdown"],
            worktreePath,
            AcceptanceCheckTimeouts.DefaultTimeout,
            cancellationToken).ConfigureAwait(false);

        var manifest = AcceptanceManifest.Load(worktreePath, changedFiles);
        var policyShardPlan = BuildPolicyShardPlan(changedFiles);

        // Apply policy-required checks from the change scope. Focused project checks replace
        // matching unfiltered project checks so a narrow App change does not still run the full
        // Infrastructure project suite from the tracked manifest.
        var policyRequiredChecks = BuildRequiredPolicyChecks(changedFiles);
        var policyEffectiveChecks = BuildPolicyEffectiveChecks(manifest.Checks, changedFiles, policyRequiredChecks, policyShardPlan);
        var effectiveChecks = ExpandBroadInfrastructureChecks(policyEffectiveChecks);
        var partitionVerdictCache = CreatePartitionVerdictCacheContext(worktreePath, goalId, effectiveChecks);
        var dotnetTestBuildPhase = GateUsesStableSlot(stableSlotIndex, stableSlotLease)
            ? CreateDotnetTestBuildPhase(worktreePath, effectiveChecks, changedFiles, policyShardPlan)
            : null;

        var advisoryChecks = LoadAdvisoryChecks(worktreePath);

        var checks = new List<AcceptanceCheckResult>();
        var retried = false;

        // When the manifest has both a solution-wide dotnet-test check and granular
        // per-project .csproj checks covered by it, run the solution once and synthesize
        // results for the granular checks so the policy gate finds all required names
        // without re-running the full test suite.
        var solutionCheck = effectiveChecks.FirstOrDefault(c =>
            c.Type.Equals("dotnet-test", StringComparison.OrdinalIgnoreCase) &&
            (string.IsNullOrWhiteSpace(c.Project) || c.Project.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)));

        var scopedChecks = BuildChangeScopedChecks(solutionCheck, effectiveChecks, changedFiles, policyShardPlan);
        var runSolutionCheck = solutionCheck is not null && scopedChecks is null;
        var deferredChecks = runSolutionCheck
            ? BuildDeferredChecks(solutionCheck, effectiveChecks, worktreePath)
            : [];

        if (scopedChecks is not null)
        {
            var scopedNames = scopedChecks.Select(c => c.Name).ToHashSet(StringComparer.Ordinal);

            foreach (var check in effectiveChecks.Where(c =>
                !c.Type.Equals("dotnet-test", StringComparison.OrdinalIgnoreCase)))
            {
                var checkResult = await RunCheckWithPartitionVerdictCacheAsync(check, partitionVerdictCache, worktreePath, goalId, stableSlotIndex, stableSlotLease, dotnetTestBuildPhase, cancellationToken).ConfigureAwait(false);
                retried |= checkResult.Retried;
                checks.Add(checkResult.Result);
                if (!checkResult.Result.Passed &&
                    ShouldStopAfterFailedCheck(partitionVerdictCache, check))
                {
                    break;
                }
            }

            if (checks.All(check => check.Passed))
            {
                foreach (var check in effectiveChecks.Where(c =>
                    c.Type.Equals("dotnet-test", StringComparison.OrdinalIgnoreCase) &&
                    scopedNames.Contains(c.Name)))
                {
                    var checkResult = await RunCheckWithPartitionVerdictCacheAsync(check, partitionVerdictCache, worktreePath, goalId, stableSlotIndex, stableSlotLease, dotnetTestBuildPhase, cancellationToken).ConfigureAwait(false);
                    retried |= checkResult.Retried;
                    checks.Add(checkResult.Result);
                    if (!checkResult.Result.Passed &&
                        ShouldStopAfterFailedCheck(partitionVerdictCache, check))
                    {
                        break;
                    }
                }
            }
        }
        else if (deferredChecks.Count > 0)
        {
            var deferredNames = deferredChecks.Select(c => c.Name).ToHashSet(StringComparer.Ordinal);

            var nonDotnetPassed = true;
            foreach (var check in effectiveChecks.Where(c =>
                !c.Type.Equals("dotnet-test", StringComparison.OrdinalIgnoreCase)))
            {
                var checkResult = await RunCheckWithPartitionVerdictCacheAsync(check, partitionVerdictCache, worktreePath, goalId, stableSlotIndex, stableSlotLease, dotnetTestBuildPhase, cancellationToken).ConfigureAwait(false);
                retried |= checkResult.Retried;
                checks.Add(checkResult.Result);
                if (!checkResult.Result.Passed &&
                    ShouldStopAfterFailedCheck(partitionVerdictCache, check))
                {
                    nonDotnetPassed = false;
                    break;
                }
            }

            if (nonDotnetPassed)
            {
                var slnRun = await RunCheckWithCancellationProbeAsync(solutionCheck!, worktreePath, goalId, stableSlotIndex, stableSlotLease, dotnetTestBuildPhase, cancellationToken).ConfigureAwait(false);
                retried |= slnRun.Retried;
                checks.Add(slnRun.Result);

                foreach (var deferred in deferredChecks)
                {
                    checks.Add(new AcceptanceCheckResult(
                        deferred.Name,
                        slnRun.Result.Passed,
                        slnRun.Result.ExitCode,
                        slnRun.Result.Passed ? null : slnRun.Result.OutputTail,
                        slnRun.Result.ArtifactsPath,
                        slnRun.Result.BrokerName,
                        slnRun.Result.LeaseId,
                        slnRun.Result.DurationMilliseconds,
                        slnRun.Retried,
                        $"covered by: {solutionCheck!.Name}"));
                }

                if (slnRun.Result.Passed)
                {
                    foreach (var check in effectiveChecks.Where(c =>
                        c.Type.Equals("dotnet-test", StringComparison.OrdinalIgnoreCase) &&
                        !c.Name.Equals(solutionCheck!.Name, StringComparison.Ordinal) &&
                        !deferredNames.Contains(c.Name)))
                    {
                        var checkResult = await RunCheckWithPartitionVerdictCacheAsync(check, partitionVerdictCache, worktreePath, goalId, stableSlotIndex, stableSlotLease, dotnetTestBuildPhase, cancellationToken).ConfigureAwait(false);
                        retried |= checkResult.Retried;
                        checks.Add(checkResult.Result);
                        if (!checkResult.Result.Passed &&
                            ShouldStopAfterFailedCheck(partitionVerdictCache, check))
                        {
                            break;
                        }
                    }
                }
            }
        }
        else
        {
            foreach (var check in effectiveChecks)
            {
                var checkResult = await RunCheckWithPartitionVerdictCacheAsync(check, partitionVerdictCache, worktreePath, goalId, stableSlotIndex, stableSlotLease, dotnetTestBuildPhase, cancellationToken).ConfigureAwait(false);
                retried |= checkResult.Retried;
                checks.Add(checkResult.Result);
                if (!checkResult.Result.Passed &&
                    ShouldStopAfterFailedCheck(partitionVerdictCache, check))
                {
                    break;
                }
            }
        }

        if (FinalizePartitionVerdictCache(partitionVerdictCache, checks) is { } partitionCacheReceipt)
        {
            checks.Add(partitionCacheReceipt);
        }

        AddCoveredBroadInfrastructureResults(checks, policyRequiredChecks, changedFiles);
        AddCoveredBroadInfrastructureResults(checks, manifest.Checks, changedFiles);
        AddCoveredPolicyAliasResults(checks, policyRequiredChecks, effectiveChecks);
        AddPolicyShardReceiptResults(checks, manifest.Checks, effectiveChecks, policyShardPlan);

        if (checks.All(check => check.Passed) && manifest.ForbiddenChangedPathGlobs.Count > 0)
        {
            checks.Add(await RunForbiddenChangedPathsCheckAsync(manifest.ForbiddenChangedPathGlobs, worktreePath, cancellationToken).ConfigureAwait(false));
        }

        if (checks.All(check => check.Passed) && ProposalValidationApplies(worktreePath, changedFiles))
        {
            checks.Add(RunStateEffectProposalSchemaCheck(worktreePath, changedFiles));
        }

        // Advisory checks: always run, failures are recorded but do not affect overall Passed.
        foreach (var advisoryCheck in advisoryChecks)
        {
            var checkResult = await RunCheckWithCancellationProbeAsync(advisoryCheck, worktreePath, goalId, stableSlotIndex, stableSlotLease, dotnetTestBuildPhase, cancellationToken).ConfigureAwait(false);
            checks.Add(checkResult.Result with { Advisory = true });
        }

        var testFileChanges = changedFiles?.Where(IsTestFile).ToArray();
        if (testFileChanges is { Length: > 0 })
        {
            checks.Add(await RunTestTamperCheckAsync(testFileChanges, worktreePath, cancellationToken).ConfigureAwait(false));
        }

        var failedCheck = checks.FirstOrDefault(check => !check.Advisory && !check.Passed);
        var artifactsPath = checks.LastOrDefault(check => !string.IsNullOrWhiteSpace(check.ArtifactsPath))?.ArtifactsPath;
        var testResultPaths = CollectTestResultPaths(checks);

        return new AcceptanceVerificationResult(
            Passed: failedCheck is null,
            Skipped: false,
            ExitCode: failedCheck?.ExitCode ?? 0,
            OutputTail: failedCheck?.OutputTail,
            Retried: retried,
            ArtifactsPath: artifactsPath,
            Checks: checks,
            TestResultPaths: testResultPaths);
    }

    public async Task<FocusedEvidenceRunResult> RunFocusedEvidenceAsync(
        string worktreePath,
        GoalId? goalId,
        string request,
        int? stableSlotIndex = null,
        DotnetBuildEnvironmentLease? stableSlotLease = null,
        CancellationToken cancellationToken = default)
    {
        if (!TryBuildFocusedEvidenceChecks(request, out var focusedChecks, out var rejection))
        {
            return new FocusedEvidenceRunResult(
                request,
                Accepted: false,
                Passed: false,
                Summary: rejection,
                Checks: []);
        }

        await _runner(
            ["dotnet", "build-server", "shutdown"],
            worktreePath,
            AcceptanceCheckTimeouts.DefaultTimeout,
            cancellationToken).ConfigureAwait(false);

        var dotnetTestBuildPhase = GateUsesStableSlot(stableSlotIndex, stableSlotLease)
            ? CreateDotnetTestBuildPhase(worktreePath, focusedChecks, changedFiles: null, PolicyShardPlan.NotApplicable("focused evidence"))
            : null;
        var checks = new List<AcceptanceCheckResult>();
        foreach (var check in focusedChecks)
        {
            var checkResult = await RunCheckWithCancellationProbeAsync(
                check,
                worktreePath,
                goalId,
                stableSlotIndex,
                stableSlotLease,
                dotnetTestBuildPhase,
                cancellationToken).ConfigureAwait(false);
            checks.Add(checkResult.Result);
            if (!checkResult.Result.Passed)
            {
                break;
            }
        }

        var failed = checks.FirstOrDefault(check => !check.Passed);
        var receiptPaths = checks
            .Select(check => check.ArtifactsPath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var summary = failed is null
            ? $"focused evidence passed: {checks.Count} check(s); receipts: {FormatReceiptPaths(receiptPaths)}"
            : $"focused evidence failed: {failed.Name} exit {failed.ExitCode}; receipts: {FormatReceiptPaths(receiptPaths)}";
        return new FocusedEvidenceRunResult(
            request,
            Accepted: true,
            Passed: failed is null,
            Summary: summary,
            Checks: checks);
    }

    private static bool ShouldStopAfterFailedCheck(
        PartitionVerdictCacheContext? cacheContext,
        AcceptanceManifestCheck check) =>
        cacheContext is null ||
        !TryGetInfrastructurePartitionId(check, out _, out _);

    private async Task<(AcceptanceCheckResult Result, bool Retried)> RunCheckWithPartitionVerdictCacheAsync(
        AcceptanceManifestCheck check,
        PartitionVerdictCacheContext? cacheContext,
        string worktreePath,
        GoalId? goalId,
        int? stableSlotIndex,
        DotnetBuildEnvironmentLease? stableSlotLease,
        DotnetTestBuildPhase? dotnetTestBuildPhase,
        CancellationToken cancellationToken)
    {
        if (cacheContext is not null &&
            TryBuildPartitionCacheKey(cacheContext, check, out var partitionId, out var filterHash, out var cacheKey) &&
            !cacheContext.ForceFullRerun &&
            cacheContext.TryGetGreen(cacheKey) is { } cached)
        {
            var reused = new PartitionVerdictReuseReceipt(partitionId, cached.AttemptId, cacheKey);
            cacheContext.Reused.Add(reused);
            return (new AcceptanceCheckResult(
                check.Name,
                true,
                0,
                null,
                ResultSummary:
                    $"partition-verdict-cache reused source_attempt_id={cached.AttemptId} cache_key={cacheKey}",
                TestResultPaths: cached.TestResultPaths), false);
        }

        var fresh = await RunCheckWithCancellationProbeAsync(
            check,
            worktreePath,
            goalId,
            stableSlotIndex,
            stableSlotLease,
            dotnetTestBuildPhase,
            cancellationToken).ConfigureAwait(false);

        // Within-attempt flake tolerance: a failed infrastructure PARTITION can be an intermittent flake
        // (a concurrent test process grabbing a build-slot lease -> SlotsBusy, a live-repo-HEAD race, a
        // testhost handle still settling). Re-run the failed partition ONCE with the same slot lease and
        // build phase; if the re-run passes, the failure was a flake and the partition is treated as
        // passed. A genuine red fails both runs. Bounded to a single retry, only for true partitions.
        if (PartitionVerdictWithinAttemptRerunEnabled &&
            !fresh.Result.Passed &&
            cacheContext is not null &&
            TryGetInfrastructurePartitionId(check, out _, out _))
        {
            var rerun = await RunCheckWithCancellationProbeAsync(
                check,
                worktreePath,
                goalId,
                stableSlotIndex,
                stableSlotLease,
                dotnetTestBuildPhase,
                cancellationToken).ConfigureAwait(false);
            fresh = rerun.Result.Passed ? (rerun.Result, true) : fresh;
        }

        if (cacheContext is not null &&
            TryBuildPartitionCacheKey(cacheContext, check, out partitionId, out filterHash, out cacheKey))
        {
            cacheContext.Executed.Add(new PartitionVerdictExecutionReceipt(
                partitionId,
                fresh.Result.Passed ? "GREEN" : "RED"));
            cacheContext.FreshRecords.Add(new PartitionVerdictRecord(
                cacheContext.GoalId,
                cacheContext.AttemptId,
                cacheContext.CandidateTreeSha,
                cacheContext.MainSha,
                filterHash,
                partitionId,
                cacheKey,
                fresh.Result.Passed,
                fresh.Result.Passed ? "GREEN" : "RED",
                fresh.Result.TestResultPaths ?? [],
                DateTimeOffset.UtcNow));
        }

        return fresh;
    }

    private async Task<(AcceptanceCheckResult Result, bool Retried)> RunCheckWithCancellationProbeAsync(
        AcceptanceManifestCheck check,
        string worktreePath,
        GoalId? goalId,
        int? stableSlotIndex,
        DotnetBuildEnvironmentLease? stableSlotLease,
        DotnetTestBuildPhase? dotnetTestBuildPhase,
        CancellationToken cancellationToken)
    {
        ThrowIfGateCancellationRequested(cancellationToken);
        var result = await RunCheckAsync(
            check,
            worktreePath,
            goalId,
            stableSlotIndex,
            stableSlotLease,
            dotnetTestBuildPhase,
            cancellationToken).ConfigureAwait(false);
        ThrowIfGateCancellationRequested(cancellationToken);
        return result;
    }

    private static void ThrowIfGateCancellationRequested(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (CurrentGateCancellationProbe.Value?.Invoke() == true)
        {
            throw new OperationCanceledException("acceptance gate attempt cancelled by goal disposition");
        }
    }

    private static IReadOnlyList<string> CollectTestResultPaths(IEnumerable<AcceptanceCheckResult> checks) =>
        checks
            .SelectMany(check => check.TestResultPaths ?? [])
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static bool TryBuildFocusedEvidenceChecks(
        string request,
        out IReadOnlyList<AcceptanceManifestCheck> checks,
        out string rejection)
    {
        checks = [];
        rejection = string.Empty;
        var items = request
            .Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (items.Length == 0)
        {
            rejection = "empty evidence request";
            return false;
        }

        var built = new List<AcceptanceManifestCheck>();
        var totalTargets = 0;
        foreach (var item in items)
        {
            var project = InfrastructureTestsProject;
            var expression = item;
            var separator = item.IndexOf(':', StringComparison.Ordinal);
            if (separator >= 0)
            {
                var alias = item[..separator].Trim();
                expression = item[(separator + 1)..].Trim();
                if (!TryResolveFocusedEvidenceProject(alias, out project))
                {
                    rejection = $"unsupported evidence request project alias '{alias}'";
                    return false;
                }
            }

            if (!TryNormalizeFocusedEvidenceFilter(expression, out var filter, out var targetCount, out rejection))
            {
                return false;
            }

            totalTargets += targetCount;
            if (totalTargets > MaxFocusedEvidenceTargets)
            {
                rejection = $"evidence request exceeds focused target limit ({MaxFocusedEvidenceTargets})";
                return false;
            }

            built.Add(new AcceptanceManifestCheck
            {
                Name = $"reviewer focused evidence: {ProjectLabel(project)} {filter}",
                Type = "dotnet-test",
                Project = project,
                Arguments = ["--verbosity", "minimal", "--filter", filter],
                TimeoutMinutes = 10
            });
        }

        checks = built;
        return true;
    }

    private static bool TryResolveFocusedEvidenceProject(string alias, out string project)
    {
        var normalized = alias.Replace('\\', '/').Trim();
        project = normalized switch
        {
            "Core.Tests" or "Core" or "Mcg.AgentOrchestrator.Core.Tests" => CoreTestsProject,
            "Infrastructure.Tests" or "Infrastructure" or "Mcg.AgentOrchestrator.Infrastructure.Tests" => InfrastructureTestsProject,
            _ when normalized.EndsWith(CoreTestsProject, StringComparison.OrdinalIgnoreCase) => CoreTestsProject,
            _ when normalized.EndsWith(InfrastructureTestsProject, StringComparison.OrdinalIgnoreCase) => InfrastructureTestsProject,
            _ => string.Empty
        };
        return project.Length > 0;
    }

    private static bool TryNormalizeFocusedEvidenceFilter(
        string expression,
        out string filter,
        out int targetCount,
        out string rejection)
    {
        filter = string.Empty;
        targetCount = 0;
        rejection = string.Empty;
        var trimmed = expression.Trim();
        if (trimmed.Length == 0)
        {
            rejection = "empty focused evidence filter";
            return false;
        }

        if (trimmed.Equals("all", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("full", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("full-suite", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains(".sln", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains('*', StringComparison.Ordinal) ||
            trimmed.Contains('!', StringComparison.Ordinal))
        {
            rejection = "unbounded evidence request rejected; use focused FullyQualifiedName~TestClass filters only";
            return false;
        }

        if (trimmed.Contains("FullyQualifiedName~", StringComparison.OrdinalIgnoreCase))
        {
            targetCount = Regex.Matches(trimmed, @"FullyQualifiedName\s*~\s*[A-Za-z_][A-Za-z0-9_.]*", RegexOptions.IgnoreCase).Count;
            if (targetCount == 0)
            {
                rejection = "focused evidence filter did not name a test class";
                return false;
            }

            filter = Regex.Replace(trimmed, @"\s+", "");
            return true;
        }

        var classNames = trimmed
            .Split([',', '|'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (classNames.Length == 0 ||
            classNames.Any(name => !Regex.IsMatch(name, @"^[A-Za-z_][A-Za-z0-9_.]*$")))
        {
            rejection = "focused evidence request must be a FullyQualifiedName~ filter or comma-separated test class list";
            return false;
        }

        targetCount = classNames.Length;
        filter = string.Join("|", classNames.Select(name => $"FullyQualifiedName~{name}"));
        return true;
    }

    private static string FormatReceiptPaths(IReadOnlyList<string> paths) =>
        paths.Count == 0 ? "none" : string.Join(", ", paths);

    private static List<AcceptanceManifestCheck> BuildDeferredChecks(
        AcceptanceManifestCheck? solutionCheck,
        IReadOnlyList<AcceptanceManifestCheck> allChecks,
        string worktreePath)
    {
        if (solutionCheck is null)
            return [];

        string? slnContent = null;
        if (!string.IsNullOrWhiteSpace(solutionCheck.Project) &&
            solutionCheck.Project.EndsWith(".sln", StringComparison.OrdinalIgnoreCase))
        {
            var slnPath = Path.Combine(worktreePath, solutionCheck.Project);
            if (File.Exists(slnPath))
                slnContent = File.ReadAllText(slnPath);
        }

        return allChecks
            .Where(c =>
                c.Type.Equals("dotnet-test", StringComparison.OrdinalIgnoreCase) &&
                !c.Name.Equals(solutionCheck.Name, StringComparison.Ordinal) &&
                !string.IsNullOrWhiteSpace(c.Project) &&
                c.Project.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) &&
                IsProjectInSolution(c.Project, solutionCheck.Project, slnContent))
            .ToList();
    }

    private static List<AcceptanceManifestCheck>? BuildChangeScopedChecks(
        AcceptanceManifestCheck? solutionCheck,
        IReadOnlyList<AcceptanceManifestCheck> allChecks,
        IReadOnlyList<string>? changedFiles,
        PolicyShardPlan policyShardPlan)
    {
        if (solutionCheck is null ||
            changedFiles is null ||
            changedFiles.Count == 0 ||
            policyShardPlan.ForceFull ||
            !ChangeScopedAcceptanceEnabled())
        {
            return null;
        }

        var summary = RepositoryChangeClassifier.Classify(changedFiles);
        // Do NOT gate on summary.RequiresBroadVerification: core/infra changes are escalation-broad
        // (LandingDecision still escalates them) but are test-narrow-able. Genuine full-suite cases
        // are caught by the build/security guards here and by plan.RequiresBroadVerification below.
        if (summary.HasBuildSystemChanges ||
            summary.HasSecuritySensitiveChanges)
        {
            return null;
        }

        var plan = RepositoryTestImpactPlanner.Plan(summary);
        if (!plan.RequiresBuild ||
            plan.RequiresBroadVerification ||
            plan.Checks.Any(check => check.Command.Count == 0))
        {
            return null;
        }

        var scoped = new List<AcceptanceManifestCheck>();
        foreach (var plannedCheck in plan.Checks)
        {
            var plannedManifestCheck = PolicyCheckToManifestCheck(plannedCheck);
            foreach (var scopedCheck in ExpandBroadInfrastructureCheck(plannedManifestCheck))
            {
                var existing = allChecks.FirstOrDefault(check =>
                    check.Type.Equals("dotnet-test", StringComparison.OrdinalIgnoreCase) &&
                    !check.Name.Equals(solutionCheck.Name, StringComparison.Ordinal) &&
                    DotnetCheckMatches(check, scopedCheck));
                scoped.Add(existing ?? scopedCheck);
            }
        }

        foreach (var policyShard in allChecks.Where(check => IsRunnablePolicyShardCheck(check, policyShardPlan)))
        {
            if (scoped.Any(existing => DotnetCheckMatches(existing, policyShard)))
                continue;

            scoped.Add(policyShard);
        }

        return scoped.Count == 0 ? null : scoped;
    }

    private static bool DotnetCheckMatches(AcceptanceManifestCheck left, AcceptanceManifestCheck right) =>
        string.Equals(NormalizePath(left.Project), NormalizePath(right.Project), StringComparison.OrdinalIgnoreCase) &&
        left.Arguments.SequenceEqual(right.Arguments, StringComparer.OrdinalIgnoreCase);

    private static string? NormalizePath(string? path) =>
        string.IsNullOrWhiteSpace(path) ? path : path.Replace('\\', '/').Trim();

    private static bool ChangeScopedAcceptanceEnabled()
    {
        var value = Environment.GetEnvironmentVariable("MCG_ACCEPTANCE_CHANGE_SCOPED");
        return string.IsNullOrWhiteSpace(value) ||
            value.Equals("1", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("on", StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<AcceptanceManifestCheck> BuildPolicyEffectiveChecks(
        IReadOnlyList<AcceptanceManifestCheck> manifestChecks,
        IReadOnlyList<string>? changedFiles,
        IReadOnlyList<AcceptanceManifestCheck>? policyRequiredChecks = null,
        PolicyShardPlan? policyShardPlan = null)
    {
        if (changedFiles is null || changedFiles.Count == 0)
            return manifestChecks;

        policyShardPlan ??= BuildPolicyShardPlan(changedFiles);
        var requiredPolicyChecks = policyRequiredChecks ?? BuildRequiredPolicyChecks(changedFiles);
        var plannedChecks = requiredPolicyChecks
            .Where(check => !policyShardPlan.ForceFull ||
                !check.Type.Equals("dotnet-test", StringComparison.OrdinalIgnoreCase))
            .SelectMany(ExpandBroadInfrastructureCheck)
            .ToArray();
        var focusedProjectChecks = plannedChecks
            .Where(IsFocusedProjectDotnetCheck)
            .ToArray();

        var effective = manifestChecks
            .Where(check => !IsSkippedPolicyShardCheck(check, policyShardPlan))
            .Where(check => !IsReplacedByFocusedProjectCheck(check, focusedProjectChecks))
            .ToList();
        var injectedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var plannedManifestCheck in plannedChecks)
        {
            var commandKey = ManifestCheckKey(plannedManifestCheck);
            if (effective.Any(check => ManifestCheckKey(check).Equals(commandKey, StringComparison.OrdinalIgnoreCase)))
                continue;

            if (!injectedKeys.Add(commandKey))
                continue;

            effective.Add(plannedManifestCheck);
        }

        return effective;
    }

    private static IReadOnlyList<AcceptanceManifestCheck> BuildRequiredPolicyChecks(IReadOnlyList<string>? changedFiles)
    {
        if (changedFiles is null || changedFiles.Count == 0)
            return [];

        var policy = VerificationPolicyCompiler.Compile(
            AgentRole.Reviewer,
            goalObjective: string.Empty,
            taskDescription: string.Empty,
            verificationPlan: null,
            changedFiles);
        return policy.Checks
            .Where(c =>
                c.Required &&
                (c.Kind.Equals("dotnet-test", StringComparison.OrdinalIgnoreCase) ||
                    c.Kind.Equals("browser-smoke", StringComparison.OrdinalIgnoreCase)))
            .Select(PolicyCheckToManifestCheck)
            .Where(c => c is not null)
            .Select(c => c!)
            .ToArray();
    }

    private static PolicyShardPlan BuildPolicyShardPlan(IReadOnlyList<string>? changedFiles)
    {
        if (changedFiles is null || changedFiles.Count == 0)
            return PolicyShardPlan.NotApplicable("no changed files");

        var normalizedFiles = changedFiles
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(NormalizePath)
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (normalizedFiles.Length == 0)
            return PolicyShardPlan.NotApplicable("no changed files");

        var summary = RepositoryChangeClassifier.Classify(normalizedFiles);
        var changedProjects = normalizedFiles
            .Select(TryMapPathToProject)
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var closure = BuildProjectDependencyClosure(changedProjects);
        var evidence = BuildPolicyShardEvidence(changedProjects, closure);
        var fullShardReason = FullShardReason(normalizedFiles, summary, changedProjects);
        return fullShardReason is not null
            ? PolicyShardPlan.Full($"{fullShardReason}; {evidence}", closure)
            : PolicyShardPlan.Scoped(evidence, closure);
    }

    private static string? FullShardReason(
        IReadOnlyList<string> changedFiles,
        RepositoryChangeSummary summary,
        IReadOnlyList<string> changedProjects)
    {
        if (FullShardOverrideEnabled())
            return "MCG_ACCEPTANCE_FULL_SHARDS=1";

        if (!ChangeScopedAcceptanceEnabled())
            return "MCG_ACCEPTANCE_CHANGE_SCOPED disabled";

        foreach (var path in changedFiles)
        {
            var fileName = Path.GetFileName(path);
            var extension = Path.GetExtension(path);
            if (extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".props", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".targets", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".sln", StringComparison.OrdinalIgnoreCase) ||
                fileName.Equals("global.json", StringComparison.OrdinalIgnoreCase) ||
                fileName.Equals("AssemblyInfo.cs", StringComparison.OrdinalIgnoreCase))
            {
                return $"build-system file changed: {path}";
            }

            if (path.StartsWith("scripts/", StringComparison.OrdinalIgnoreCase))
                return $"script changed: {path}";

        }

        if (!summary.IsDocsOnly && summary.HasBehaviorChanges && changedProjects.Count == 0)
            return "changed files did not map to a known project";

        return null;
    }

    private static bool FullShardOverrideEnabled()
    {
        var value = Environment.GetEnvironmentVariable("MCG_ACCEPTANCE_FULL_SHARDS");
        return value is not null &&
            (value.Equals("1", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("on", StringComparison.OrdinalIgnoreCase));
    }

    private static HashSet<string> BuildProjectDependencyClosure(IReadOnlyList<string> changedProjects)
    {
        var closure = new HashSet<string>(changedProjects, StringComparer.OrdinalIgnoreCase);
        var pending = new Queue<string>(changedProjects);
        while (pending.Count > 0)
        {
            var project = pending.Dequeue();
            if (!ReferencingProjectsByProject.TryGetValue(project, out var referencingProjects))
                continue;

            foreach (var referencingProject in referencingProjects)
            {
                if (closure.Add(referencingProject))
                    pending.Enqueue(referencingProject);
            }
        }

        return closure;
    }

    private static string BuildPolicyShardEvidence(
        IReadOnlyList<string> changedProjects,
        IReadOnlySet<string> closure)
    {
        var changed = changedProjects.Count == 0
            ? "(none)"
            : string.Join(", ", changedProjects.Select(ProjectLabel));
        var affected = closure.Count == 0
            ? "(none)"
            : string.Join(", ", closure.OrderBy(ProjectLabel, StringComparer.OrdinalIgnoreCase).Select(ProjectLabel));
        return $"changed projects: {changed}; dependency closure: {affected}";
    }

    private static string? TryMapPathToProject(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        var normalized = NormalizePath(path)!;
        if (normalized.StartsWith("src/Mcg.AgentOrchestrator.Core/", StringComparison.OrdinalIgnoreCase))
            return CoreProject;
        if (normalized.StartsWith("src/Mcg.AgentOrchestrator.Infrastructure/", StringComparison.OrdinalIgnoreCase))
            return InfrastructureProject;
        if (normalized.StartsWith("src/Mcg.AgentOrchestrator.App/", StringComparison.OrdinalIgnoreCase))
            return AppProject;
        if (normalized.StartsWith("tests/Mcg.AgentOrchestrator.Core.Tests/", StringComparison.OrdinalIgnoreCase))
            return CoreTestsProject;
        if (normalized.StartsWith("tests/Mcg.AgentOrchestrator.Infrastructure.Tests/", StringComparison.OrdinalIgnoreCase))
            return InfrastructureTestsProject;
        return null;
    }

    private static string ProjectLabel(string project) =>
        project.Equals(CoreProject, StringComparison.OrdinalIgnoreCase) ? "Core" :
        project.Equals(InfrastructureProject, StringComparison.OrdinalIgnoreCase) ? "Infrastructure" :
        project.Equals(AppProject, StringComparison.OrdinalIgnoreCase) ? "App" :
        project.Equals(CoreTestsProject, StringComparison.OrdinalIgnoreCase) ? "Core.Tests" :
        project.Equals(InfrastructureTestsProject, StringComparison.OrdinalIgnoreCase) ? "Infrastructure.Tests" :
        project;

    private static List<AcceptanceManifestCheck> ExpandBroadInfrastructureChecks(
        IReadOnlyList<AcceptanceManifestCheck> manifestChecks)
    {
        var effective = new List<AcceptanceManifestCheck>();
        foreach (var check in manifestChecks)
        {
            if (!IsBroadInfrastructureTestCheck(check))
            {
                effective.Add(check);
                continue;
            }

            effective.AddRange(ExpandBroadInfrastructureCheck(check));
        }

        return effective;
    }

    private static IEnumerable<AcceptanceManifestCheck> ExpandBroadInfrastructureCheck(AcceptanceManifestCheck check)
    {
        if (!IsBroadInfrastructureTestCheck(check))
        {
            yield return check;
            yield break;
        }

        foreach (var lane in InfrastructureTestLanes)
        {
            yield return BuildInfrastructureShardCheck(check, lane);
        }
    }

    private static AcceptanceManifestCheck BuildInfrastructureShardCheck(
        AcceptanceManifestCheck check,
        InfrastructureTestLane lane) =>
        new()
        {
            Name = $"{check.Name}: {lane.Name}",
            Type = check.Type,
            Command = check.Command,
            Project = check.Project,
            Arguments = [.. check.Arguments, "--filter", lane.Filter],
            Pattern = check.Pattern,
            FilePath = check.FilePath,
            TimeoutMinutes = check.TimeoutMinutes,
            Advisory = check.Advisory,
            Runner = check.Runner
        };

    private static bool IsBroadInfrastructureTestCheck(AcceptanceManifestCheck check) =>
        check.Type.Equals("dotnet-test", StringComparison.OrdinalIgnoreCase) &&
        !string.IsNullOrWhiteSpace(check.Project) &&
        IsInfrastructureTestProject(check.Project) &&
        !check.Arguments.Any(argument => argument.Equals("--filter", StringComparison.OrdinalIgnoreCase));

    private static bool IsFocusedProjectDotnetCheck(AcceptanceManifestCheck check) =>
        check.Type.Equals("dotnet-test", StringComparison.OrdinalIgnoreCase) &&
        !string.IsNullOrWhiteSpace(check.Project) &&
        check.Project.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) &&
        check.Arguments.Any(argument => argument.Equals("--filter", StringComparison.OrdinalIgnoreCase));

    private static bool IsFullPolicyShardCheck(AcceptanceManifestCheck check) =>
        IsPolicyShardProjectCheck(check) &&
        !check.Arguments.Any(argument => argument.Equals("--filter", StringComparison.OrdinalIgnoreCase));

    private static bool IsPolicyShardProjectCheck(AcceptanceManifestCheck check) =>
        check.Type.Equals("dotnet-test", StringComparison.OrdinalIgnoreCase) &&
        !string.IsNullOrWhiteSpace(check.Project) &&
        (IsCoreTestProject(check.Project) || IsInfrastructureTestProject(check.Project));

    private static bool IsSkippedPolicyShardCheck(AcceptanceManifestCheck check, PolicyShardPlan policyShardPlan) =>
        policyShardPlan.Applies &&
        !policyShardPlan.ForceFull &&
        IsFullPolicyShardCheck(check) &&
        !policyShardPlan.IncludesProject(check.Project);

    private static bool IsRunnablePolicyShardCheck(AcceptanceManifestCheck check, PolicyShardPlan policyShardPlan) =>
        policyShardPlan.Applies &&
        !policyShardPlan.ForceFull &&
        IsPolicyShardProjectCheck(check) &&
        policyShardPlan.IncludesProject(check.Project);

    private static bool IsInfrastructureTestProject(string project) =>
        project.EndsWith(
            InfrastructureTestsProject,
            StringComparison.OrdinalIgnoreCase) ||
        project.EndsWith(
            "tests\\Mcg.AgentOrchestrator.Infrastructure.Tests\\Mcg.AgentOrchestrator.Infrastructure.Tests.csproj",
            StringComparison.OrdinalIgnoreCase);

    private static bool IsCoreTestProject(string project) =>
        project.EndsWith(
            CoreTestsProject,
            StringComparison.OrdinalIgnoreCase) ||
        project.EndsWith(
            "tests\\Mcg.AgentOrchestrator.Core.Tests\\Mcg.AgentOrchestrator.Core.Tests.csproj",
            StringComparison.OrdinalIgnoreCase);

    private static bool IsReplacedByFocusedProjectCheck(
        AcceptanceManifestCheck manifestCheck,
        IReadOnlyList<AcceptanceManifestCheck> focusedProjectChecks) =>
        manifestCheck.Type.Equals("dotnet-test", StringComparison.OrdinalIgnoreCase) &&
        !string.IsNullOrWhiteSpace(manifestCheck.Project) &&
        manifestCheck.Project.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) &&
        !manifestCheck.Arguments.Any(argument => argument.Equals("--filter", StringComparison.OrdinalIgnoreCase)) &&
        focusedProjectChecks.Any(focused =>
            string.Equals(NormalizePath(focused.Project), NormalizePath(manifestCheck.Project), StringComparison.OrdinalIgnoreCase));

    private static string ManifestCheckKey(AcceptanceManifestCheck check) =>
        $"{check.Type}:{check.Command}:{NormalizePath(check.Project)}:{string.Join('\u001f', check.Arguments)}";

    private static AcceptanceManifestCheck? PolicyCheckToManifestCheck(VerificationPolicyCheck check)
    {
        if (check.Kind.Equals("dotnet-test", StringComparison.OrdinalIgnoreCase))
        {
            var parts = SplitCommandLine(check.CommandLine);
            if (parts.Length < 2 ||
                !parts[0].Equals("dotnet", StringComparison.OrdinalIgnoreCase) ||
                !parts[1].Equals("test", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return DotnetCommandToManifestCheck(check.Name, parts);
        }

        if (check.Kind.Equals("browser-smoke", StringComparison.OrdinalIgnoreCase))
        {
            var parts = SplitCommandLine(check.CommandLine);
            var script = parts.Length == 0 ? @".\scripts\Run-DashboardBrowserScript.ps1" : parts[0];
            string[] scriptArguments = parts.Length > 1
                ? parts[1..]
                : [@".\scripts\dashboard-smoke.js"];
            return new AcceptanceManifestCheck
            {
                Name = check.Name,
                Type = "browser-smoke",
                Command = "powershell",
                Arguments = ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script, .. scriptArguments]
            };
        }

        return null;
    }

    private static AcceptanceManifestCheck PolicyCheckToManifestCheck(RepositoryTestImpactCheck check)
    {
        // Command format: ["dotnet", "test", <optional project>, ...args]
        return DotnetCommandToManifestCheck(check.Name, [.. check.Command]);
    }

    private static AcceptanceManifestCheck DotnetCommandToManifestCheck(string name, string[] command)
    {
        var remaining = command.Skip(2).ToArray();
        var project = remaining.Length > 0 && !remaining[0].StartsWith("-", StringComparison.Ordinal)
            ? remaining[0]
            : null;
        var arguments = project is null ? remaining : remaining.Skip(1).ToArray();
        return new AcceptanceManifestCheck
        {
            Name = name,
            Type = "dotnet-test",
            Project = project,
            Arguments = arguments
        };
    }

    private static string[] SplitCommandLine(string commandLine) =>
        commandLine.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static void AddCoveredBroadInfrastructureResults(
        List<AcceptanceCheckResult> checks,
        IReadOnlyList<AcceptanceManifestCheck> policyEffectiveChecks,
        IReadOnlyList<string>? changedFiles)
    {
        if (changedFiles is null || changedFiles.Count == 0)
            return;

        foreach (var broadCheck in policyEffectiveChecks.Where(IsBroadInfrastructureTestCheck))
        {
            if (checks.Any(result => result.Name.Equals(broadCheck.Name, StringComparison.OrdinalIgnoreCase)))
                continue;

            var shardResults = ExpandBroadInfrastructureCheck(broadCheck)
                .Select(shard => checks.FirstOrDefault(result =>
                    result.Name.Equals(shard.Name, StringComparison.OrdinalIgnoreCase)))
                .Where(result => result is not null)
                .Select(result => result!)
                .ToArray();
            if (shardResults.Length == 0)
                continue;

            var failedShard = shardResults.FirstOrDefault(result => !result.Passed);
            if (failedShard is not null)
            {
                checks.Add(new AcceptanceCheckResult(
                    broadCheck.Name,
                    false,
                    failedShard.ExitCode,
                    failedShard.OutputTail,
                    failedShard.ArtifactsPath,
                    failedShard.BrokerName,
                    failedShard.LeaseId,
                    failedShard.DurationMilliseconds,
                    failedShard.LockRemediationApplied,
                    $"covered by failed partition: {failedShard.Name}"));
                continue;
            }

            if (shardResults.Length != InfrastructureTestLanes.Length)
                continue;

            var lastShard = shardResults[^1];
            checks.Add(new AcceptanceCheckResult(
                broadCheck.Name,
                true,
                0,
                null,
                lastShard.ArtifactsPath,
                lastShard.BrokerName,
                lastShard.LeaseId,
                shardResults.Sum(result => result.DurationMilliseconds ?? 0),
                shardResults.Any(result => result.LockRemediationApplied),
                $"covered by {shardResults.Length} partitioned checks"));
        }
    }

    private static void AddCoveredPolicyAliasResults(
        List<AcceptanceCheckResult> checks,
        IReadOnlyList<AcceptanceManifestCheck> policyRequiredChecks,
        IReadOnlyList<AcceptanceManifestCheck> effectiveChecks)
    {
        foreach (var policyCheck in policyRequiredChecks)
        {
            if (IsBroadInfrastructureTestCheck(policyCheck) ||
                checks.Any(result => result.Name.Equals(policyCheck.Name, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var policyKey = ManifestCheckKey(policyCheck);
            var coveringCheck = effectiveChecks.FirstOrDefault(check =>
                ManifestCheckKey(check).Equals(policyKey, StringComparison.OrdinalIgnoreCase) &&
                !check.Name.Equals(policyCheck.Name, StringComparison.OrdinalIgnoreCase));
            if (coveringCheck is null)
                continue;

            var coveringResult = checks.FirstOrDefault(result =>
                result.Name.Equals(coveringCheck.Name, StringComparison.OrdinalIgnoreCase));
            if (coveringResult is null)
                continue;

            checks.Add(new AcceptanceCheckResult(
                policyCheck.Name,
                coveringResult.Passed,
                coveringResult.ExitCode,
                coveringResult.Passed ? null : coveringResult.OutputTail,
                coveringResult.ArtifactsPath,
                coveringResult.BrokerName,
                coveringResult.LeaseId,
                coveringResult.DurationMilliseconds,
                coveringResult.LockRemediationApplied,
                $"covered by: {coveringCheck.Name}"));
        }
    }

    private static void AddPolicyShardReceiptResults(
        List<AcceptanceCheckResult> checks,
        IReadOnlyList<AcceptanceManifestCheck> manifestChecks,
        IReadOnlyList<AcceptanceManifestCheck> effectiveChecks,
        PolicyShardPlan policyShardPlan)
    {
        if (!policyShardPlan.Applies || policyShardPlan.ForceFull)
            return;

        foreach (var shard in manifestChecks.Where(IsFullPolicyShardCheck))
        {
            if (checks.Any(result => result.Name.Equals(shard.Name, StringComparison.OrdinalIgnoreCase)))
                continue;

            if (!policyShardPlan.IncludesProject(shard.Project))
            {
                checks.Add(new AcceptanceCheckResult(
                    shard.Name,
                    true,
                    null,
                    null,
                    ResultSummary:
                    $"skipped: no changed file in dependency closure; shard project: {ProjectLabel(NormalizePath(shard.Project)!)}; {policyShardPlan.Evidence}"));
                continue;
            }

            var coveringCheck = effectiveChecks.FirstOrDefault(check =>
                IsPolicyShardProjectCheck(check) &&
                string.Equals(NormalizePath(check.Project), NormalizePath(shard.Project), StringComparison.OrdinalIgnoreCase));
            if (coveringCheck is null)
                continue;

            var coveringResult = checks.FirstOrDefault(result =>
                result.Name.Equals(coveringCheck.Name, StringComparison.OrdinalIgnoreCase));
            if (coveringResult is null)
                continue;

            checks.Add(new AcceptanceCheckResult(
                shard.Name,
                coveringResult.Passed,
                coveringResult.ExitCode,
                coveringResult.Passed ? null : coveringResult.OutputTail,
                coveringResult.ArtifactsPath,
                coveringResult.BrokerName,
                coveringResult.LeaseId,
                coveringResult.DurationMilliseconds,
                coveringResult.LockRemediationApplied,
                $"covered by: {coveringCheck.Name}; changed file in dependency closure; {policyShardPlan.Evidence}"));
        }
    }

    private static PartitionVerdictCacheContext? CreatePartitionVerdictCacheContext(
        string worktreePath,
        GoalId? goalId,
        IReadOnlyList<AcceptanceManifestCheck> effectiveChecks)
    {
        if (goalId is null)
            return null;

        var partitionCount = effectiveChecks.Count(check =>
            TryGetInfrastructurePartitionId(check, out _, out _));
        if (partitionCount == 0)
            return null;

        var candidateTreeSha = ResolvePartitionVerdictCandidateTreeShaForTests?.Invoke(worktreePath) ??
            ResolveGitScalar(worktreePath, "rev-parse", "HEAD^{tree}");
        var mainSha = ResolvePartitionVerdictMainShaForTests?.Invoke(worktreePath) ??
            ResolveGitScalar(worktreePath, "rev-parse", "main");
        var verifyingCommitSha = ResolvePartitionVerdictVerifyingCommitShaForTests?.Invoke(worktreePath) ??
            ResolveGitScalar(worktreePath, "rev-parse", "HEAD");
        if (string.IsNullOrWhiteSpace(candidateTreeSha) ||
            string.IsNullOrWhiteSpace(mainSha) ||
            string.IsNullOrWhiteSpace(verifyingCommitSha))
        {
            return null;
        }

        var journalPath = PartitionVerdictJournalPath(worktreePath, goalId.Value);
        var journal = ReadPartitionVerdictJournal(journalPath);
        var pairKey = PartitionVerdictPairKey(goalId.Value, candidateTreeSha, mainSha);
        var priorReuseAttemptCount = LatestPartitionReuseAttemptCount(journal, pairKey);
        var reusableGreenExists = effectiveChecks.Any(check =>
            TryBuildPartitionCacheKey(
                goalId.Value,
                candidateTreeSha,
                mainSha,
                check,
                out _,
                out _,
                out var cacheKey) &&
            LatestGreenPartitionVerdict(journal, goalId.Value, cacheKey) is not null);
        var backstopEveryN = Math.Max(1, PartitionVerdictFullRerunEveryN);
        var forceFullRerun = reusableGreenExists && priorReuseAttemptCount + 1 >= backstopEveryN;

        return new PartitionVerdictCacheContext(
            goalId.Value,
            NormalizeShaToken(candidateTreeSha),
            NormalizeShaToken(mainSha),
            NormalizeShaToken(verifyingCommitSha),
            CurrentAcceptanceAttemptId(),
            pairKey,
            journalPath,
            journal,
            partitionCount,
            priorReuseAttemptCount,
            forceFullRerun);
    }

    private static AcceptanceCheckResult? FinalizePartitionVerdictCache(
        PartitionVerdictCacheContext? cacheContext,
        IReadOnlyList<AcceptanceCheckResult> checks)
    {
        if (cacheContext is null ||
            cacheContext.Reused.Count == 0 && cacheContext.Executed.Count == 0)
        {
            return null;
        }

        var aggregateVerdict = cacheContext.Executed.Any(executed =>
            executed.Verdict.Equals("RED", StringComparison.OrdinalIgnoreCase))
                ? "RED"
                : "GREEN";
        var attemptCount = 0;
        if (cacheContext.ForceFullRerun ||
            (cacheContext.Reused.Count == 0 && cacheContext.Executed.Count >= cacheContext.PartitionCount))
        {
            attemptCount = 0;
        }
        else if (cacheContext.Reused.Count > 0)
        {
            attemptCount = cacheContext.PriorReuseAttemptCount + 1;
        }
        var summaryRecordedAt = DateTimeOffset.UtcNow;
        var receipt =
            $"partition-verdict-cache reused_partitions={FormatPartitionReuseReceipt(cacheContext.Reused)} " +
            $"executed_partitions={FormatPartitionExecutionReceipt(cacheContext.Executed)} " +
            $"aggregate_verdict={aggregateVerdict} verifying_commit_sha={cacheContext.VerifyingCommitSha} " +
            $"reroll_attempt_count={attemptCount.ToString(System.Globalization.CultureInfo.InvariantCulture)} " +
            $"forced_full_rerun={cacheContext.ForceFullRerun.ToString().ToLowerInvariant()} " +
            "before_reroll_wall_time=20-25m after_reroll_wall_time=2-7m";
        AppendPartitionVerdictJournalEntries(
            cacheContext.JournalPath,
            BuildPartitionVerdictJournalEntries(cacheContext, receipt, aggregateVerdict, attemptCount, summaryRecordedAt));
        Console.WriteLine($"PARTITION_VERDICT_CACHE {receipt}");
        Console.Out.Flush();
        return new AcceptanceCheckResult(
            "infrastructure partition verdict cache",
            true,
            0,
            null,
            ResultSummary: receipt,
            Advisory: true);
    }

    private static PartitionVerdictRecord? LatestGreenPartitionVerdict(
        PartitionVerdictJournalSnapshot journal,
        string goalId,
        string cacheKey)
    {
        var latest = journal.Records
            .Where(record =>
                record.GoalId.Equals(goalId, StringComparison.OrdinalIgnoreCase) &&
                record.CacheKey.Equals(cacheKey, StringComparison.OrdinalIgnoreCase))
            .OrderBy(record => record.RecordedAt)
            .LastOrDefault();
        return latest?.Passed == true ? latest : null;
    }

    private static bool TryBuildPartitionCacheKey(
        PartitionVerdictCacheContext cacheContext,
        AcceptanceManifestCheck check,
        out string partitionId,
        out string filterHash,
        out string cacheKey) =>
        TryBuildPartitionCacheKey(
            cacheContext.GoalId,
            cacheContext.CandidateTreeSha,
            cacheContext.MainSha,
            check,
            out partitionId,
            out filterHash,
            out cacheKey);

    private static bool TryBuildPartitionCacheKey(
        string goalId,
        string candidateTreeSha,
        string mainSha,
        AcceptanceManifestCheck check,
        out string partitionId,
        out string filterHash,
        out string cacheKey)
    {
        partitionId = string.Empty;
        filterHash = string.Empty;
        cacheKey = string.Empty;
        if (!TryGetInfrastructurePartitionId(check, out partitionId, out var filter))
            return false;

        filterHash = ShortHash(filter);
        cacheKey =
            $"{goalId}:{NormalizeShaToken(candidateTreeSha)}:{NormalizeShaToken(mainSha)}:{filterHash}".ToLowerInvariant();
        return true;
    }

    private static bool TryGetInfrastructurePartitionId(
        AcceptanceManifestCheck check,
        out string partitionId,
        out string filter)
    {
        partitionId = string.Empty;
        filter = string.Empty;
        if (!check.Type.Equals("dotnet-test", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(check.Project) ||
            !IsInfrastructureTestProject(check.Project) ||
            !TryExtractFilter(check.Arguments, out filter))
        {
            return false;
        }

        var separator = check.Name.IndexOf(": ", StringComparison.Ordinal);
        if (separator < 0 ||
            !check.Name[..separator].Equals("infrastructure tests", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        partitionId = Slug(check.Name[(separator + 2)..]);
        return true;
    }

    private static bool TryExtractFilter(IReadOnlyList<string> arguments, out string filter)
    {
        filter = string.Empty;
        for (var index = 0; index < arguments.Count - 1; index++)
        {
            if (arguments[index].Equals("--filter", StringComparison.OrdinalIgnoreCase))
            {
                filter = arguments[index + 1];
                return !string.IsNullOrWhiteSpace(filter);
            }
        }

        return false;
    }

    private static string FormatPartitionReuseReceipt(IReadOnlyList<PartitionVerdictReuseReceipt> reused) =>
        reused.Count == 0
            ? "[]"
            : "[" + string.Join("|", reused.Select(receipt =>
                $"{{partition_id={receipt.PartitionId},source_attempt_id={receipt.SourceAttemptId},cache_key={receipt.CacheKey}}}")) + "]";

    private static string FormatPartitionExecutionReceipt(IReadOnlyList<PartitionVerdictExecutionReceipt> executed) =>
        executed.Count == 0
            ? "[]"
            : "[" + string.Join("|", executed.Select(receipt =>
                $"{{partition_id={receipt.PartitionId},verdict={receipt.Verdict}}}")) + "]";

    private static string PartitionVerdictJournalPath(string worktreePath, string goalId) =>
        Path.Combine(
            ResolvePartitionVerdictJournalRoot(worktreePath),
            ".orchestrator",
            "goal-operations",
            $"{goalId}.jsonl");

    private static string ResolvePartitionVerdictJournalRoot(string worktreePath)
    {
        var fullPath = Path.GetFullPath(worktreePath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var directory = new DirectoryInfo(fullPath);
        if (directory.Parent?.Name.Equals(GoalWorktrees.DirectoryName, StringComparison.OrdinalIgnoreCase) == true &&
            directory.Parent.Parent is not null)
        {
            return directory.Parent.Parent.FullName;
        }

        return fullPath;
    }

    private static PartitionVerdictJournalSnapshot ReadPartitionVerdictJournal(string path)
    {
        lock (PartitionVerdictJournalGate)
        {
            if (!File.Exists(path))
                return new PartitionVerdictJournalSnapshot([], []);

            var records = new List<PartitionVerdictRecord>();
            var summaries = new List<PartitionVerdictJournalSummary>();
            foreach (var line in File.ReadLines(path))
            {
                if (TryDeserializePartitionVerdictJournalEntry(line) is not { } entry)
                    continue;

                if (entry.Operation.Equals(PartitionVerdictJournalOperation, StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(entry.PartitionVerdictCacheKey) &&
                    !string.IsNullOrWhiteSpace(entry.PartitionId) &&
                    !string.IsNullOrWhiteSpace(entry.PartitionFilterHash) &&
                    !string.IsNullOrWhiteSpace(entry.PartitionVerdict) &&
                    !string.IsNullOrWhiteSpace(entry.PartitionAttemptId) &&
                    !string.IsNullOrWhiteSpace(entry.BranchHeadSha) &&
                    !string.IsNullOrWhiteSpace(entry.MainHeadSha))
                {
                    records.Add(new PartitionVerdictRecord(
                        entry.GoalId.Value,
                        entry.PartitionAttemptId,
                        NormalizeShaToken(entry.BranchHeadSha),
                        NormalizeShaToken(entry.MainHeadSha),
                        entry.PartitionFilterHash,
                        entry.PartitionId,
                        entry.PartitionVerdictCacheKey,
                        entry.PartitionVerdict.Equals("GREEN", StringComparison.OrdinalIgnoreCase),
                        entry.PartitionVerdict,
                        entry.PartitionTestResultPaths ?? [],
                        entry.At));
                }
                else if (entry.Operation.Equals(PartitionVerdictCacheJournalOperation, StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(entry.PartitionPairKey) &&
                    entry.PartitionReuseAttemptCount is { } reuseAttemptCount)
                {
                    summaries.Add(new PartitionVerdictJournalSummary(
                        entry.PartitionPairKey,
                        reuseAttemptCount,
                        entry.PartitionForcedFullRerun ?? false,
                        entry.At));
                }
            }

            return new PartitionVerdictJournalSnapshot(records, summaries);
        }
    }

    private static PartitionVerdictJournalEntry? TryDeserializePartitionVerdictJournalEntry(string line)
    {
        try
        {
            return JsonSerializer.Deserialize<PartitionVerdictJournalEntry>(line, PartitionVerdictJournalJsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void AppendPartitionVerdictJournalEntries(
        string path,
        IReadOnlyList<PartitionVerdictJournalEntry> entries)
    {
        if (entries.Count == 0)
            return;

        lock (PartitionVerdictJournalGate)
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.AppendAllLines(
                path,
                entries.Select(entry => JsonSerializer.Serialize(entry, PartitionVerdictJournalJsonOptions)));
        }
    }

    private static IReadOnlyList<PartitionVerdictJournalEntry> BuildPartitionVerdictJournalEntries(
        PartitionVerdictCacheContext cacheContext,
        string receipt,
        string aggregateVerdict,
        int attemptCount,
        DateTimeOffset summaryRecordedAt)
    {
        var entries = cacheContext.FreshRecords
            .Select(record => new PartitionVerdictJournalEntry(
                $"{record.CacheKey}:partition-verdict",
                new GoalId(record.GoalId),
                PartitionVerdictJournalOperation,
                record.Passed ? "Completed" : "Failed",
                record.RecordedAt,
                $"partition {record.PartitionId} verdict {record.Verdict}",
                record.CandidateTreeSha,
                record.MainSha,
                PartitionVerdictCacheKey: record.CacheKey,
                PartitionPairKey: cacheContext.PairKey,
                PartitionId: record.PartitionId,
                PartitionFilterHash: record.PartitionFilterHash,
                PartitionVerdict: record.Verdict,
                PartitionAttemptId: record.AttemptId,
                PartitionTestResultPaths: record.TestResultPaths))
            .ToList();
        entries.Add(new PartitionVerdictJournalEntry(
            $"{cacheContext.PairKey}:partition-cache:{cacheContext.AttemptId}",
            new GoalId(cacheContext.GoalId),
            PartitionVerdictCacheJournalOperation,
            aggregateVerdict.Equals("GREEN", StringComparison.OrdinalIgnoreCase) ? "Completed" : "Failed",
            summaryRecordedAt,
            receipt,
            cacheContext.CandidateTreeSha,
            cacheContext.MainSha,
            PartitionPairKey: cacheContext.PairKey,
            PartitionVerdict: aggregateVerdict,
            PartitionAttemptId: cacheContext.AttemptId,
            PartitionReuseAttemptCount: attemptCount,
            PartitionForcedFullRerun: cacheContext.ForceFullRerun));
        return entries;
    }

    private static int LatestPartitionReuseAttemptCount(
        PartitionVerdictJournalSnapshot journal,
        string pairKey) =>
        journal.Summaries
            .Where(summary => summary.PairKey.Equals(pairKey, StringComparison.OrdinalIgnoreCase))
            .OrderBy(summary => summary.RecordedAt)
            .LastOrDefault()
            ?.ReuseAttemptCount ?? 0;

    private static string PartitionVerdictPairKey(
        string goalId,
        string candidateTreeSha,
        string mainSha) =>
        $"{goalId}:acceptance:{NormalizeShaToken(candidateTreeSha)}:{NormalizeShaToken(mainSha)}".ToLowerInvariant();

    private static string CurrentAcceptanceAttemptId()
    {
        var prefix = Environment.GetEnvironmentVariable(AcceptanceAttemptTrxPrefixVariable);
        if (!string.IsNullOrWhiteSpace(prefix))
        {
            var name = Path.GetFileName(prefix.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (!string.IsNullOrWhiteSpace(name))
                return name;
        }

        return $"manual-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}";
    }

    private static string ShortHash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value.Trim())))[..16].ToLowerInvariant();

    private static string NormalizeShaToken(string value) => value.Trim().ToLowerInvariant();

    private static bool IsProjectInSolution(string projectPath, string? solutionProject, string? slnContent)
    {
        if (string.IsNullOrWhiteSpace(solutionProject))
            return true;

        if (slnContent is null)
            return true;

        return slnContent.Contains(Path.GetFileName(projectPath), StringComparison.OrdinalIgnoreCase);
    }

    private static bool ProposalValidationApplies(string worktreePath, IReadOnlyList<string>? changedFiles) =>
        changedFiles is { Count: > 0 }
            ? changedFiles.Any(StateEffectProposalParser.IsProposalPath)
            : Directory.Exists(Path.Combine(worktreePath, ".orchestrator-proposals"));

    private static AcceptanceCheckResult RunStateEffectProposalSchemaCheck(
        string worktreePath,
        IReadOnlyList<string>? changedFiles)
    {
        var result = StateEffectProposalParser.ValidateDirectory(worktreePath, changedFiles);
        return new AcceptanceCheckResult(
            "state-effect proposal schema",
            result.Passed,
            result.Passed ? 0 : 1,
            result.Passed ? null : result.Summary,
            ResultSummary: result.Summary);
    }

    private static AcceptanceManifestCheck[] LoadAdvisoryChecks(string worktreePath)
    {
        var path = System.IO.Path.Combine(worktreePath, ".orchestrator", "goal-acceptance-criteria.json");
        if (!File.Exists(path))
            return [];

        try
        {
            var criteria = JsonSerializer.Deserialize<AcceptanceCriterion[]>(
                File.ReadAllText(path),
                CriteriaJsonOptions) ?? [];
            return criteria
                .Where(c => !string.IsNullOrWhiteSpace(c.Type))
                .Select(c => CriterionToManifestCheck(c))
                .ToArray();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static AcceptanceManifestCheck CriterionToManifestCheck(AcceptanceCriterion criterion)
    {
        // For command-exit, the stored Command is the full command line (e.g. "dotnet build Foo.sln -c Release").
        // Split it into executable + arguments for process launch.
        if (criterion.Type.Equals("command-exit", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(criterion.Command))
        {
            var parts = criterion.Command.Split([' '], StringSplitOptions.RemoveEmptyEntries);
            return new AcceptanceManifestCheck
            {
                Name = criterion.Name,
                Type = "command-exit",
                Command = parts[0],
                Arguments = parts.Length > 1 ? parts[1..] : [],
                Advisory = true
            };
        }

        return new AcceptanceManifestCheck
        {
            Name = criterion.Name,
            Type = criterion.Type,
            Command = criterion.Command,
            Pattern = criterion.Pattern,
            FilePath = criterion.Path,
            Advisory = true
        };
    }

    private async Task<(AcceptanceCheckResult Result, bool Retried)> RunCheckAsync(
        AcceptanceManifestCheck check,
        string worktreePath,
        GoalId? goalId,
        int? stableSlotIndex,
        DotnetBuildEnvironmentLease? stableSlotLease,
        DotnetTestBuildPhase? dotnetTestBuildPhase,
        CancellationToken cancellationToken)
    {
        if (check.Type.Equals("no-op", StringComparison.OrdinalIgnoreCase))
        {
            return (new AcceptanceCheckResult(check.Name, true, null, null, Advisory: check.Advisory), false);
        }

        if (check.Type.Equals("grep-absent", StringComparison.OrdinalIgnoreCase))
            return (await RunGrepCheckAsync(check, worktreePath, expectPresent: false, cancellationToken).ConfigureAwait(false), false);

        if (check.Type.Equals("grep-present", StringComparison.OrdinalIgnoreCase))
            return (await RunGrepCheckAsync(check, worktreePath, expectPresent: true, cancellationToken).ConfigureAwait(false), false);

        if (check.Type.Equals("file-exists", StringComparison.OrdinalIgnoreCase))
            return (RunFileExistsCheck(check, worktreePath), false);

        if (check.Type.Equals("command-exit", StringComparison.OrdinalIgnoreCase))
            return await RunCommandCheckAsync(check, worktreePath, goalId, stableSlotIndex, stableSlotLease, dotnetTestBuildPhase, cancellationToken).ConfigureAwait(false);

        return check.Type.Equals("dotnet-test", StringComparison.OrdinalIgnoreCase)
            ? await RunDotnetTestCheckAsync(check, worktreePath, goalId, stableSlotIndex, stableSlotLease, dotnetTestBuildPhase, cancellationToken).ConfigureAwait(false)
            : await RunCommandCheckAsync(check, worktreePath, goalId, stableSlotIndex, stableSlotLease, dotnetTestBuildPhase, cancellationToken).ConfigureAwait(false);
    }

    private async Task<AcceptanceCheckResult> RunGrepCheckAsync(
        AcceptanceManifestCheck check,
        string worktreePath,
        bool expectPresent,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(check.Pattern))
        {
            return new AcceptanceCheckResult(
                check.Name, false, 1,
                "grep check has no pattern configured",
                Advisory: check.Advisory);
        }

        var result = await _runner(
            ["git", "grep", "-q", "--", check.Pattern],
            worktreePath,
            AcceptanceCheckTimeouts.Resolve(check.TimeoutMinutes),
            cancellationToken).ConfigureAwait(false);

        // git grep exit 0 = pattern found, exit 1 = not found
        var patternFound = result.ExitCode == 0;
        var passed = expectPresent ? patternFound : !patternFound;
        var summary = expectPresent
            ? (patternFound ? "pattern found" : "pattern not found")
            : (patternFound ? "pattern still present" : "pattern absent");

        return new AcceptanceCheckResult(
            check.Name,
            passed,
            passed ? 0 : 1,
            passed ? null : $"Advisory check failed: {summary} for pattern '{check.Pattern}'",
            ResultSummary: summary,
            Advisory: check.Advisory);
    }

    private AcceptanceCheckResult RunFileExistsCheck(
        AcceptanceManifestCheck check,
        string worktreePath)
    {
        if (string.IsNullOrWhiteSpace(check.FilePath))
        {
            return new AcceptanceCheckResult(
                check.Name, false, 1,
                "file-exists check has no path configured",
                Advisory: check.Advisory);
        }

        var fullPath = System.IO.Path.Combine(worktreePath, check.FilePath);
        var exists = File.Exists(fullPath);
        return new AcceptanceCheckResult(
            check.Name,
            exists,
            exists ? 0 : 1,
            exists ? null : $"Advisory check failed: file not found: {check.FilePath}",
            ResultSummary: exists ? "file exists" : "file not found",
            Advisory: check.Advisory);
    }

    private async Task<(AcceptanceCheckResult Result, bool Retried)> RunCommandCheckAsync(
        AcceptanceManifestCheck check,
        string worktreePath,
        GoalId? goalId,
        int? stableSlotIndex,
        DotnetBuildEnvironmentLease? stableSlotLease,
        DotnetTestBuildPhase? dotnetTestBuildPhase,
        CancellationToken cancellationToken)
    {
        var arguments = BuildCommandArguments(check);
        if (IsDotnetCommand(arguments))
        {
            if (IsDotnetTestCommand(arguments) && GateUsesStableSlot(stableSlotIndex, stableSlotLease))
            {
                return await RunManagedDotnetTestCheckAsync(
                    check,
                    BuildDotnetTestBuildArguments(arguments),
                    EnsureDotnetTestNoBuildArguments(arguments),
                    worktreePath,
                    goalId,
                    stableSlotIndex,
                    stableSlotLease,
                    dotnetTestBuildPhase,
                    $"acceptance-{Slug(check.Name)}",
                    cancellationToken).ConfigureAwait(false);
            }

            return await RunManagedDotnetCheckAsync(
                check,
                arguments,
                worktreePath,
                goalId,
                stableSlotIndex,
                stableSlotLease,
                $"acceptance-{Slug(check.Name)}",
                cancellationToken).ConfigureAwait(false);
        }

        var result = await RunWithGateHeartbeatAsync(
            arguments,
            worktreePath,
            AcceptanceCheckTimeouts.Resolve(check.TimeoutMinutes),
            CreateGateHeartbeatContext(check, arguments, worktreePath, goalId, stableSlotIndex, null),
            cancellationToken).ConfigureAwait(false);
        if (result.TimedOut)
        {
            return (new AcceptanceCheckResult(
                BuildTimeoutFailureName(check, result),
                false,
                result.ExitCode,
                BuildTimeoutOutput(result),
                ResultSummary: BuildGenericCommandResultSummary(result),
                Advisory: check.Advisory), false);
        }

        return (new AcceptanceCheckResult(
            check.Name,
            result.ExitCode == 0,
            result.ExitCode,
            result.ExitCode == 0 ? null : TailOutput(result.Output),
            ResultSummary: BuildGenericCommandResultSummary(result),
            Advisory: check.Advisory), false);
    }

    private async Task<(AcceptanceCheckResult Result, bool Retried)> RunDotnetTestCheckAsync(
        AcceptanceManifestCheck check,
        string worktreePath,
        GoalId? goalId,
        int? stableSlotIndex,
        DotnetBuildEnvironmentLease? stableSlotLease,
        DotnetTestBuildPhase? dotnetTestBuildPhase,
        CancellationToken cancellationToken)
    {
        if (UsesMicrosoftTestingPlatform(check))
        {
            return await RunMtpTestCheckAsync(
                check,
                worktreePath,
                goalId,
                stableSlotIndex,
                stableSlotLease,
                dotnetTestBuildPhase,
                cancellationToken).ConfigureAwait(false);
        }

        if (!UsesVstestRunner(check))
        {
            return (new AcceptanceCheckResult(
                check.Name,
                false,
                1,
                $"Acceptance check '{check.Name}' has unrecognized runner '{check.Runner}'.",
                ResultSummary: $"unrecognized runner '{check.Runner}'",
                Advisory: check.Advisory), false);
        }

        var noBuild = GateUsesStableSlot(stableSlotIndex, stableSlotLease);
        var testArguments = BuildDotnetTestArguments(check, noBuild);
        if (!noBuild)
        {
            return await RunManagedDotnetCheckAsync(
                check,
                testArguments,
                worktreePath,
                goalId,
                stableSlotIndex,
                stableSlotLease,
                $"acceptance-{Slug(check.Name)}",
                cancellationToken).ConfigureAwait(false);
        }

        return await RunManagedDotnetTestCheckAsync(
            check,
            BuildDotnetTestBuildArguments(check),
            testArguments,
            worktreePath,
            goalId,
            stableSlotIndex,
            stableSlotLease,
            dotnetTestBuildPhase,
            $"acceptance-{Slug(check.Name)}",
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<(AcceptanceCheckResult Result, bool Retried)> RunMtpTestCheckAsync(
        AcceptanceManifestCheck check,
        string worktreePath,
        GoalId? goalId,
        int? stableSlotIndex,
        DotnetBuildEnvironmentLease? stableSlotLease,
        DotnetTestBuildPhase? dotnetTestBuildPhase,
        CancellationToken cancellationToken)
    {
        var attemptName = $"acceptance-{Slug(check.Name)}";
        var buildRun = dotnetTestBuildPhase is null
            ? new DotnetTestBuildPhaseResult(
                await RunManagedDotnetCheckAsync(
                    check,
                    BuildDotnetTestBuildArguments(check),
                    worktreePath,
                    goalId,
                    stableSlotIndex,
                    stableSlotLease,
                    $"{attemptName}-build",
                    cancellationToken).ConfigureAwait(false),
                ContributesToCheck: true)
            : await EnsureDotnetTestBuildPhaseAsync(
                dotnetTestBuildPhase,
                check,
                worktreePath,
                goalId,
                stableSlotIndex,
                stableSlotLease,
                attemptName,
                cancellationToken).ConfigureAwait(false);
        if (!buildRun.Run.Result.Passed)
        {
            return (buildRun.Run.Result with
            {
                Name = check.Name,
                ResultSummary = PrefixResultSummary("build phase failed", buildRun.Run.Result.ResultSummary)
            }, buildRun.Run.Retried);
        }

        var testRun = await RunManagedMtpExecutableCheckAsync(
            check,
            worktreePath,
            goalId,
            stableSlotIndex,
            stableSlotLease,
            attemptName,
            cancellationToken).ConfigureAwait(false);

        var durationMilliseconds = (buildRun.ContributesToCheck ? buildRun.Run.Result.DurationMilliseconds ?? 0 : 0) +
            (testRun.Result.DurationMilliseconds ?? 0);
        var buildPhaseSummary = buildRun.ContributesToCheck &&
            buildRun.Run.Result.ResultSummary?.StartsWith("base-build-cache ", StringComparison.Ordinal) == true
                ? buildRun.Run.Result.ResultSummary
                : null;
        return (testRun.Result with
        {
            DurationMilliseconds = durationMilliseconds,
            LockRemediationApplied = buildRun.Run.Result.LockRemediationApplied || testRun.Result.LockRemediationApplied,
            ResultSummary = buildPhaseSummary is null
                ? testRun.Result.ResultSummary
                : PrefixResultSummary(buildPhaseSummary, testRun.Result.ResultSummary)
        }, buildRun.Run.Retried || testRun.Retried);
    }

    private async Task<(AcceptanceCheckResult Result, bool Retried)> RunManagedMtpExecutableCheckAsync(
        AcceptanceManifestCheck check,
        string worktreePath,
        GoalId? goalId,
        int? stableSlotIndex,
        DotnetBuildEnvironmentLease? stableSlotLease,
        string attemptName,
        CancellationToken cancellationToken)
    {
        var elapsed = Stopwatch.StartNew();
        var environment = stableSlotLease?.Environment ?? (stableSlotIndex.HasValue
            ? DotnetBuildEnvironmentManager.CreateStableSlotAttempt(stableSlotIndex.Value)
            : DotnetBuildEnvironmentManager.CreateAttempt(goalId, attemptName));
        FileStream? leaseLock = null;
        try
        {
            leaseLock = stableSlotLease is null
                ? DotnetBuildEnvironmentManager.AcquireLeaseExecutionLock(environment, cancellationToken)
                : null;

            var telemetry = ResolveTestTelemetry(check, environment);
            var arguments = BuildMtpTestArguments(check, environment, telemetry);
            ReapRecordedGateChildBeforeManagedDotnetCommand(environment, goalId, stableSlotIndex);
            var result = await RunWithGateHeartbeatAsync(
                arguments,
                worktreePath,
                AcceptanceCheckTimeouts.Resolve(check.TimeoutMinutes),
                CreateGateHeartbeatContext(check, arguments, worktreePath, goalId, stableSlotIndex, environment),
                cancellationToken).ConfigureAwait(false);

            elapsed.Stop();
            var passed = !result.TimedOut && result.ExitCode == 0;
            EmitMissingTrxReceiptIfNeeded(passed, telemetry);
            return (new AcceptanceCheckResult(
                result.TimedOut ? BuildTimeoutFailureName(check, result) : check.Name,
                passed,
                result.ExitCode,
                result.TimedOut
                    ? BuildTimeoutOutput(result)
                    : passed ? null : TailOutput(result.Output),
                environment.ArtifactsPath,
                "goal-acceptance-verifier",
                environment.LeaseId,
                (long)elapsed.Elapsed.TotalMilliseconds,
                ResultSummary: BuildGenericCommandResultSummary(result),
                TestResultPaths: telemetry.Paths), false);
        }
        finally
        {
            leaseLock?.Dispose();
        }
    }

    private async Task<(AcceptanceCheckResult Result, bool Retried)> RunManagedDotnetTestCheckAsync(
        AcceptanceManifestCheck check,
        string[] buildArguments,
        string[] testArguments,
        string worktreePath,
        GoalId? goalId,
        int? stableSlotIndex,
        DotnetBuildEnvironmentLease? stableSlotLease,
        DotnetTestBuildPhase? dotnetTestBuildPhase,
        string attemptName,
        CancellationToken cancellationToken)
    {
        var buildRun = dotnetTestBuildPhase is null
            ? new DotnetTestBuildPhaseResult(
                await RunManagedDotnetCheckAsync(
                    check,
                    buildArguments,
                    worktreePath,
                    goalId,
                    stableSlotIndex,
                    stableSlotLease,
                    $"{attemptName}-build",
                    cancellationToken).ConfigureAwait(false),
                ContributesToCheck: true)
            : await EnsureDotnetTestBuildPhaseAsync(
                dotnetTestBuildPhase,
                check,
                worktreePath,
                goalId,
                stableSlotIndex,
                stableSlotLease,
                attemptName,
                cancellationToken).ConfigureAwait(false);
        if (!buildRun.Run.Result.Passed)
        {
            return (buildRun.Run.Result with
            {
                Name = check.Name,
                ResultSummary = PrefixResultSummary("build phase failed", buildRun.Run.Result.ResultSummary)
            }, buildRun.Run.Retried);
        }

        var testRun = await RunManagedDotnetCheckAsync(
            check,
            testArguments,
            worktreePath,
            goalId,
            stableSlotIndex,
            stableSlotLease,
            attemptName,
            cancellationToken).ConfigureAwait(false);

        var lockRemediationApplied = buildRun.Run.Result.LockRemediationApplied || testRun.Result.LockRemediationApplied;
        var durationMilliseconds = (buildRun.ContributesToCheck ? buildRun.Run.Result.DurationMilliseconds ?? 0 : 0) +
            (testRun.Result.DurationMilliseconds ?? 0);
        var buildPhaseSummary = buildRun.ContributesToCheck &&
            buildRun.Run.Result.ResultSummary?.StartsWith("base-build-cache ", StringComparison.Ordinal) == true
                ? buildRun.Run.Result.ResultSummary
                : null;
        return (testRun.Result with
        {
            DurationMilliseconds = durationMilliseconds,
            LockRemediationApplied = lockRemediationApplied,
            ResultSummary = lockRemediationApplied && buildRun.Run.Result.LockRemediationApplied
                ? PrefixResultSummary("build phase remediated", testRun.Result.ResultSummary)
                : buildPhaseSummary is null ? testRun.Result.ResultSummary : PrefixResultSummary(buildPhaseSummary, testRun.Result.ResultSummary)
        }, buildRun.Run.Retried || testRun.Retried);
    }

    private async Task<DotnetTestBuildPhaseResult> EnsureDotnetTestBuildPhaseAsync(
        DotnetTestBuildPhase phase,
        AcceptanceManifestCheck check,
        string worktreePath,
        GoalId? goalId,
        int? stableSlotIndex,
        DotnetBuildEnvironmentLease? stableSlotLease,
        string attemptName,
        CancellationToken cancellationToken)
    {
        if (phase.Run is { } completed)
        {
            return new DotnetTestBuildPhaseResult(completed, ContributesToCheck: false);
        }

        if (phase.CachePlan is not null &&
            await TryRunCachedDotnetTestBuildPhaseAsync(
                phase,
                check,
                worktreePath,
                goalId,
                stableSlotIndex,
                stableSlotLease,
                attemptName,
                cancellationToken).ConfigureAwait(false) is { } cachedRun)
        {
            phase.Run = cachedRun;
            return new DotnetTestBuildPhaseResult(cachedRun, ContributesToCheck: true);
        }

        phase.Run = await RunManagedDotnetCheckAsync(
            check,
            phase.BuildArguments,
            worktreePath,
            goalId,
            stableSlotIndex,
            stableSlotLease,
            $"{attemptName}-build",
            cancellationToken).ConfigureAwait(false);
        return new DotnetTestBuildPhaseResult(phase.Run.Value, ContributesToCheck: true);
    }

    private async Task<(AcceptanceCheckResult Result, bool Retried)?> TryRunCachedDotnetTestBuildPhaseAsync(
        DotnetTestBuildPhase phase,
        AcceptanceManifestCheck check,
        string worktreePath,
        GoalId? goalId,
        int? stableSlotIndex,
        DotnetBuildEnvironmentLease? stableSlotLease,
        string attemptName,
        CancellationToken cancellationToken)
    {
        var plan = phase.CachePlan;
        if (plan is null)
        {
            return null;
        }

        var wall = Stopwatch.StartNew();
        var environment = stableSlotLease?.Environment ?? (stableSlotIndex.HasValue
            ? DotnetBuildEnvironmentManager.CreateStableSlotAttempt(stableSlotIndex.Value)
            : null);
        if (environment is null)
        {
            return null;
        }

        var cache = BaseBuildCacheForTests ?? DotnetBaseBuildCache.Default();
        var restore = cache.Probe(plan.MainSha, plan.RestoreProjects);
        var retried = false;
        var lockRemediationApplied = false;
        (AcceptanceCheckResult Result, bool Retried)? lastRun = null;
        IReadOnlyList<string> builtProjects;
        DotnetBaseBuildCachePublishResult? publish = null;

        if (restore.AllHit)
        {
            builtProjects = plan.BuildProjects;
            var restoredIntoPreparedSlot = false;
            foreach (var project in plan.BuildProjects)
            {
                var projectRun = await RunManagedDotnetCheckAsync(
                    check,
                    BuildDotnetProjectBuildArguments(project, phase.BuildArguments),
                    worktreePath,
                    goalId,
                    stableSlotIndex,
                    stableSlotLease,
                    $"{attemptName}-build-{Slug(ProjectLabel(project))}",
                    cancellationToken,
                    afterLeasePrepared: preparedEnvironment =>
                    {
                        if (restoredIntoPreparedSlot)
                        {
                            return;
                        }

                        restore = cache.Restore(plan.MainSha, preparedEnvironment.ArtifactsPath, plan.RestoreProjects);
                        restoredIntoPreparedSlot = true;
                    }).ConfigureAwait(false);
                retried |= projectRun.Retried;
                lockRemediationApplied |= projectRun.Result.LockRemediationApplied;
                lastRun = projectRun;
                if (!projectRun.Result.Passed)
                {
                    break;
                }
            }
        }
        else
        {
            builtProjects = CacheableProjects;
            lastRun = await RunManagedDotnetCheckAsync(
                check,
                phase.BuildArguments,
                worktreePath,
                goalId,
                stableSlotIndex,
                stableSlotLease,
                $"{attemptName}-build",
                cancellationToken).ConfigureAwait(false);
            retried |= lastRun.Value.Retried;
            lockRemediationApplied |= lastRun.Value.Result.LockRemediationApplied;
            if (lastRun.Value.Result.Passed)
            {
                publish = cache.Publish(plan.MainSha, environment.ArtifactsPath, plan.RestoreProjects);
            }
        }

        wall.Stop();
        if (lastRun is null)
        {
            return null;
        }

        var receiptSummary = BuildBaseBuildCacheSummary(
            plan,
            restore,
            publish,
            builtProjects,
            (long)wall.Elapsed.TotalMilliseconds);
        EmitBaseBuildCacheReceipt(receiptSummary);
        return (lastRun.Value.Result with
        {
            DurationMilliseconds = (long)wall.Elapsed.TotalMilliseconds,
            LockRemediationApplied = lockRemediationApplied,
            ResultSummary = PrefixResultSummary(receiptSummary, lastRun.Value.Result.ResultSummary)
        }, retried);
    }

    private static string BuildBaseBuildCacheSummary(
        DotnetBaseBuildCachePlan plan,
        DotnetBaseBuildCacheRestoreResult restore,
        DotnetBaseBuildCachePublishResult? publish,
        IReadOnlyList<string> builtProjects,
        long buildPhaseMilliseconds)
    {
        var projectReceipts = CacheableProjects
            .Select(project =>
            {
                var restored = restore.Projects.FirstOrDefault(receipt =>
                    receipt.Project.Equals(project, StringComparison.OrdinalIgnoreCase));
                if (restored is not null)
                {
                    return $"{ProjectLabel(project)}={restored.Status}";
                }

                var published = publish?.Projects.FirstOrDefault(receipt =>
                    receipt.Project.Equals(project, StringComparison.OrdinalIgnoreCase));
                return published is not null
                    ? $"{ProjectLabel(project)}=miss,published"
                    : $"{ProjectLabel(project)}=changed";
            });
        var evictions = restore.Evictions.Concat(publish?.Evictions ?? []).ToArray();
        return
            $"base-build-cache main_sha={plan.MainSha} build_phase_ms={buildPhaseMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture)} " +
            $"projects={string.Join(",", projectReceipts)} " +
            $"built_projects={string.Join(",", builtProjects.Select(ProjectLabel))} " +
            $"evictions={(evictions.Length == 0 ? "none" : string.Join(",", evictions))}";
    }

    private static void EmitBaseBuildCacheReceipt(string summary)
    {
        Console.WriteLine($"BASE_BUILD_CACHE {summary}");
        Console.Out.Flush();
    }

    private static string[] BuildDotnetProjectBuildArguments(string project, string[] templateBuildArguments)
    {
        var args = new List<string> { "dotnet", "build", project };
        var startIndex = templateBuildArguments.Length > 2 && !templateBuildArguments[2].StartsWith("-", StringComparison.Ordinal)
            ? 3
            : 2;
        for (var index = startIndex; index < templateBuildArguments.Length; index++)
        {
            var argument = templateBuildArguments[index];
            if (!IsBuildCompatibleDotnetArgument(argument))
            {
                if (ArgumentExpectsValue(argument))
                {
                    index++;
                }

                continue;
            }

            args.Add(argument);
            if (ArgumentExpectsValue(argument) && index + 1 < templateBuildArguments.Length)
            {
                args.Add(templateBuildArguments[++index]);
            }
        }

        return [.. args];
    }

    private async Task<(AcceptanceCheckResult Result, bool Retried)> RunManagedDotnetCheckAsync(
        AcceptanceManifestCheck check,
        string[] arguments,
        string worktreePath,
        GoalId? goalId,
        int? stableSlotIndex,
        DotnetBuildEnvironmentLease? stableSlotLease,
        string attemptName,
        CancellationToken cancellationToken,
        Action<DotnetBuildEnvironment>? afterLeasePrepared = null)
    {
        var elapsed = Stopwatch.StartNew();
        var environment = stableSlotLease?.Environment ?? (stableSlotIndex.HasValue
            ? DotnetBuildEnvironmentManager.CreateStableSlotAttempt(stableSlotIndex.Value)
            : DotnetBuildEnvironmentManager.CreateAttempt(goalId, attemptName));
        FileStream? leaseLock = null;
        try
        {
            leaseLock = stableSlotLease is null
                ? DotnetBuildEnvironmentManager.AcquireLeaseExecutionLock(
                    environment,
                    cancellationToken,
                    _timeProvider,
                    _leaseSleep)
                : null;
            afterLeasePrepared?.Invoke(environment);

            var lockRemediationApplied = false;
            var result = await RunManagedDotnetCommandAsync(
                check,
                arguments,
                environment,
                worktreePath,
                goalId,
                stableSlotIndex,
                AcceptanceCheckTimeouts.Resolve(check.TimeoutMinutes),
                cancellationToken).ConfigureAwait(false);

            if (IsBuildLockFailure(result, environment, out var attribution))
            {
                (result, lockRemediationApplied) = await RemediateBuildLockAndRetryAsync(
                    arguments,
                    worktreePath,
                    check,
                    goalId,
                    stableSlotIndex,
                    stableSlotLease,
                    environment,
                    attemptName,
                    attribution,
                    nextEnvironment =>
                    {
                        if (stableSlotLease is not null)
                        {
                            return;
                        }

                        leaseLock?.Dispose();
                        leaseLock = null;
                        environment = nextEnvironment;
                        leaseLock = DotnetBuildEnvironmentManager.AcquireLeaseExecutionLock(
                            environment,
                            cancellationToken,
                            _timeProvider,
                            _leaseSleep);
                    },
                    cancellationToken).ConfigureAwait(false);
            }

            if (IsBuildLockFailure(result, environment, out var finalAttribution))
            {
                throw new BuildLockBlockedException(finalAttribution);
            }

            elapsed.Stop();
            // A testhost can exit non-zero on SHUTDOWN ("host process exited unexpectedly") even after every
            // test passed. Honor the run's own Passed!/Failed:0 summary so a benign shutdown abort does not
            // block a green goal, while never masking a build/compile failure and still surfacing the tail.
            var reportedAllPassed = !result.TimedOut && result.ExitCode != 0 && TestRunReportsAllPassed(result.Output);
            var passed = !result.TimedOut && (result.ExitCode == 0 || reportedAllPassed);
            var telemetry = ResolveDotnetTestTelemetry(arguments, check, environment);
            EmitMissingTrxReceiptIfNeeded(passed, telemetry);
            return (new AcceptanceCheckResult(
                result.TimedOut ? BuildTimeoutFailureName(check, result) : check.Name,
                passed,
                result.ExitCode,
                result.TimedOut
                    ? BuildTimeoutOutput(result)
                    : passed && result.ExitCode == 0 ? null : TailOutput(result.Output),
                environment.ArtifactsPath,
                "goal-acceptance-verifier",
                environment.LeaseId,
                (long)elapsed.Elapsed.TotalMilliseconds,
                lockRemediationApplied,
                BuildManagedDotnetResultSummary(result, lockRemediationApplied),
                TestResultPaths: telemetry?.Paths), lockRemediationApplied);
        }
        catch (Exception ex) when (IsBuildArtifactIoException(ex) &&
            ex is not DotnetBuildSlotsBusyException and not BuildLockBlockedException)
        {
            var lockedPath = TryExtractPathFromException(ex) ?? environment.ArtifactsPath;
            var attribution = AttributeBuildLock(lockedPath, worktreePath, "acceptance-check", check.Name);
            var (result, _) = await RemediateBuildLockAndRetryAsync(
                arguments,
                worktreePath,
                check,
                goalId,
                stableSlotIndex,
                stableSlotLease,
                environment,
                attemptName,
                attribution,
                nextEnvironment =>
                {
                    if (stableSlotLease is not null)
                    {
                        return;
                    }

                    leaseLock?.Dispose();
                    leaseLock = null;
                    environment = nextEnvironment;
                    leaseLock = DotnetBuildEnvironmentManager.AcquireLeaseExecutionLock(
                        environment,
                        cancellationToken,
                        _timeProvider,
                        _leaseSleep);
                },
                cancellationToken).ConfigureAwait(false);

            if (IsBuildLockFailure(result, environment, out var finalAttribution))
            {
                throw new BuildLockBlockedException(finalAttribution);
            }

            elapsed.Stop();
            var reportedAllPassed = !result.TimedOut && result.ExitCode != 0 && TestRunReportsAllPassed(result.Output);
            var passed = !result.TimedOut && (result.ExitCode == 0 || reportedAllPassed);
            var telemetry = ResolveDotnetTestTelemetry(arguments, check, environment);
            EmitMissingTrxReceiptIfNeeded(passed, telemetry);
            return (new AcceptanceCheckResult(
                result.TimedOut ? BuildTimeoutFailureName(check, result) : check.Name,
                passed,
                result.ExitCode,
                result.TimedOut
                    ? BuildTimeoutOutput(result)
                    : passed && result.ExitCode == 0 ? null : TailOutput(result.Output),
                environment.ArtifactsPath,
                "goal-acceptance-verifier",
                environment.LeaseId,
                (long)elapsed.Elapsed.TotalMilliseconds,
                true,
                BuildManagedDotnetResultSummary(result, transientCompilerLockRetried: true),
                TestResultPaths: telemetry?.Paths), true);
        }
        finally
        {
            leaseLock?.Dispose();
        }
    }

    // A dotnet test run whose own summary banner is "Passed!" (zero failed) but which then exits non-zero
    // is a testhost shutdown abort, not a test failure. Treat it as passed so a benign abort does not block
    // a green goal; a build/compile failure ("Build FAILED" / "error CS...") is a real failure, not this.
    private static bool TestRunReportsAllPassed(string output)
    {
        if (output.Contains("Build FAILED", StringComparison.Ordinal) ||
            output.Contains("error CS", StringComparison.Ordinal))
        {
            return false;
        }

        return output.Contains("Passed!", StringComparison.Ordinal) &&
            !output.Contains("Failed!", StringComparison.Ordinal);
    }

    private static bool IsTransientCompilerLockFailure(string output) =>
        (output.Contains("error CS2012", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("MSB3491", StringComparison.OrdinalIgnoreCase)) &&
        output.Contains("being used by another process", StringComparison.OrdinalIgnoreCase);

    private async Task<(CommandResult Result, bool Remediated)> RemediateBuildLockAndRetryAsync(
        string[] arguments,
        string worktreePath,
        AcceptanceManifestCheck check,
        GoalId? goalId,
        int? stableSlotIndex,
        DotnetBuildEnvironmentLease? stableSlotLease,
        DotnetBuildEnvironment currentEnvironment,
        string attemptName,
        BuildLockAttribution attribution,
        Action<DotnetBuildEnvironment> reacquireLease,
        CancellationToken cancellationToken)
    {
        await _runner(
            ["dotnet", "build-server", "shutdown"],
            worktreePath,
            AcceptanceCheckTimeouts.DefaultTimeout,
            cancellationToken).ConfigureAwait(false);

        if (IsTransientNoHolderBuildArtifactLock(attribution, currentEnvironment))
        {
            return await RetryTransientNoHolderBuildLockAsync(
                arguments,
                worktreePath,
                check,
                goalId,
                stableSlotIndex,
                stableSlotLease,
                currentEnvironment,
                attribution,
                reacquireLease,
                cancellationToken).ConfigureAwait(false);
        }

        var retryEnvironment = stableSlotLease?.Environment ?? (stableSlotIndex.HasValue
            ? DotnetBuildEnvironmentManager.CreateStableSlotAttempt(stableSlotIndex.Value)
            : currentEnvironment);
        reacquireLease(retryEnvironment);
        CommandResult? retry = null;
        BuildLockAttribution? retryAttribution = null;
        try
        {
            retry = await RunManagedDotnetCommandAsync(
                check,
                arguments,
                retryEnvironment,
                worktreePath,
                goalId,
                stableSlotIndex,
                AcceptanceCheckTimeouts.Resolve(check.TimeoutMinutes),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsBuildArtifactIoException(ex))
        {
            var lockedPath = TryExtractPathFromException(ex) ?? retryEnvironment.ArtifactsPath;
            retryAttribution = AttributeBuildLock(lockedPath, worktreePath, "acceptance-retry", check.Name);
        }

        if (retry is not null && !IsBuildLockFailure(retry, retryEnvironment, out retryAttribution))
        {
            return (retry, true);
        }

        retryAttribution ??= attribution;
        if (IsTransientNoHolderBuildArtifactLock(retryAttribution, retryEnvironment))
        {
            return await RetryTransientNoHolderBuildLockAsync(
                arguments,
                worktreePath,
                check,
                goalId,
                stableSlotIndex,
                stableSlotLease,
                retryEnvironment,
                retryAttribution,
                reacquireLease,
                cancellationToken).ConfigureAwait(false);
        }

        var killed = false;
        foreach (var holder in attribution.Holders
            .Concat(retryAttribution.Holders)
            .Where(holder => holder.IsOrchestratorOwned && holder.ProcessId.HasValue)
            .DistinctBy(holder => holder.ProcessId!.Value))
        {
            killed |= WorkerProcessJobs.TryKillOrFallbackAndWait(holder.ProcessId!.Value, TimeSpan.FromSeconds(5));
        }

        if (!killed)
        {
            throw new BuildLockBlockedException(retryAttribution);
        }

        var killRetryEnvironment = stableSlotLease?.Environment ?? (stableSlotIndex.HasValue
            ? DotnetBuildEnvironmentManager.CreateStableSlotAttempt(stableSlotIndex.Value)
            : currentEnvironment);
        reacquireLease(killRetryEnvironment);
        CommandResult killRetry;
        try
        {
            killRetry = await RunManagedDotnetCommandAsync(
                check,
                arguments,
                killRetryEnvironment,
                worktreePath,
                goalId,
                stableSlotIndex,
                AcceptanceCheckTimeouts.Resolve(check.TimeoutMinutes),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsBuildArtifactIoException(ex))
        {
            var lockedPath = TryExtractPathFromException(ex) ?? killRetryEnvironment.ArtifactsPath;
            throw new BuildLockBlockedException(AttributeBuildLock(lockedPath, worktreePath, "acceptance-kill-retry", check.Name));
        }

        if (IsBuildLockFailure(killRetry, killRetryEnvironment, out var killRetryAttribution))
        {
            throw new BuildLockBlockedException(killRetryAttribution);
        }

        return (killRetry, true);
    }

    private async Task<(CommandResult Result, bool Remediated)> RetryTransientNoHolderBuildLockAsync(
        string[] arguments,
        string worktreePath,
        AcceptanceManifestCheck check,
        GoalId? goalId,
        int? stableSlotIndex,
        DotnetBuildEnvironmentLease? stableSlotLease,
        DotnetBuildEnvironment currentEnvironment,
        BuildLockAttribution attribution,
        Action<DotnetBuildEnvironment> reacquireLease,
        CancellationToken cancellationToken)
    {
        var retryEnvironment = stableSlotLease?.Environment ?? (stableSlotIndex.HasValue
            ? DotnetBuildEnvironmentManager.CreateStableSlotAttempt(stableSlotIndex.Value)
            : currentEnvironment);
        var cycleAttribution = attribution;
        var maxRetryCycles = Math.Max(1, TransientNoHolderBuildLockMaxRetryCycles);
        for (var cycle = 1; cycle <= maxRetryCycles; cycle++)
        {
            var wait = await WaitForBuildArtifactWriteAccessAsync(
                cycleAttribution.Path,
                TransientNoHolderBuildLockWaitWindow,
                TransientNoHolderBuildLockPollInterval,
                _timeProvider,
                cancellationToken).ConfigureAwait(false);
            EmitTransientNoHolderBuildLockWaitReceipt(cycleAttribution, wait, cycle, maxRetryCycles);
            if (!wait.Released)
            {
                EmitTransientNoHolderBuildLockRetryReceipt(
                    check,
                    cycleAttribution,
                    cycle,
                    maxRetryCycles,
                    "wait-exhausted",
                    exitCode: null,
                    timedOut: false,
                    buildLock: true);
                throw new BuildLockBlockedException(cycleAttribution);
            }

            reacquireLease(retryEnvironment);
            CommandResult retry;
            try
            {
                retry = await RunManagedDotnetCommandAsync(
                    check,
                    arguments,
                    retryEnvironment,
                    worktreePath,
                    goalId,
                    stableSlotIndex,
                    AcceptanceCheckTimeouts.Resolve(check.TimeoutMinutes),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsBuildArtifactIoException(ex))
            {
                var lockedPath = TryExtractPathFromException(ex) ?? retryEnvironment.ArtifactsPath;
                var exceptionAttribution = AttributeBuildLock(lockedPath, worktreePath, "acceptance-transient-retry", check.Name);
                EmitTransientNoHolderBuildLockRetryReceipt(
                    check,
                    exceptionAttribution,
                    cycle,
                    maxRetryCycles,
                    "io-exception",
                    exitCode: null,
                    timedOut: false,
                    buildLock: true);
                if (cycle < maxRetryCycles && IsTransientNoHolderBuildArtifactLock(exceptionAttribution, retryEnvironment))
                {
                    cycleAttribution = exceptionAttribution;
                    continue;
                }

                throw new BuildLockBlockedException(exceptionAttribution);
            }

            if (!IsBuildLockFailure(retry, retryEnvironment, out var retryAttribution))
            {
                EmitTransientNoHolderBuildLockRetryReceipt(
                    check,
                    cycleAttribution,
                    cycle,
                    maxRetryCycles,
                    "completed",
                    retry.ExitCode,
                    retry.TimedOut,
                    buildLock: false);
                return (retry, true);
            }

            EmitTransientNoHolderBuildLockRetryReceipt(
                check,
                retryAttribution,
                cycle,
                maxRetryCycles,
                "blocked",
                retry.ExitCode,
                retry.TimedOut,
                buildLock: true);
            if (cycle < maxRetryCycles && IsTransientNoHolderBuildArtifactLock(retryAttribution, retryEnvironment))
            {
                cycleAttribution = retryAttribution;
                continue;
            }

            throw new BuildLockBlockedException(retryAttribution);
        }

        throw new BuildLockBlockedException(cycleAttribution);
    }

    private static async Task<TransientBuildLockWaitResult> WaitForBuildArtifactWriteAccessAsync(
        string path,
        TimeSpan waitWindow,
        TimeSpan pollInterval,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var startedAt = timeProvider.GetTimestamp();
        if (CanOpenBuildArtifactForWrite(path))
        {
            return new TransientBuildLockWaitResult((long)timeProvider.GetElapsedTime(startedAt).TotalMilliseconds, true);
        }

        var effectivePollInterval = pollInterval <= TimeSpan.Zero
            ? TimeSpan.FromMilliseconds(1)
            : pollInterval;
        while (timeProvider.GetElapsedTime(startedAt) < waitWindow)
        {
            var remaining = waitWindow - timeProvider.GetElapsedTime(startedAt);
            if (remaining > TimeSpan.Zero)
            {
                await Task.Delay(
                        remaining < effectivePollInterval ? remaining : effectivePollInterval,
                        timeProvider,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            if (CanOpenBuildArtifactForWrite(path))
            {
                return new TransientBuildLockWaitResult((long)timeProvider.GetElapsedTime(startedAt).TotalMilliseconds, true);
            }
        }

        return new TransientBuildLockWaitResult((long)timeProvider.GetElapsedTime(startedAt).TotalMilliseconds, false);
    }

    private static bool CanOpenBuildArtifactForWrite(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                var probePath = Path.Combine(path, $".mcg-write-probe-{Guid.NewGuid():N}.tmp");
                using (new FileStream(probePath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                }

                TryDeleteFile(probePath);
                return true;
            }

            if (File.Exists(path))
            {
                using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete))
                {
                }

                return true;
            }

            var directory = Path.GetDirectoryName(path);
            return string.IsNullOrWhiteSpace(directory) ||
                !Directory.Exists(directory) ||
                CanOpenBuildArtifactForWrite(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void EmitTransientNoHolderBuildLockWaitReceipt(
        BuildLockAttribution attribution,
        TransientBuildLockWaitResult wait,
        int cycle,
        int maxCycles)
    {
        var probeMilliseconds = (long)(attribution.ProbeElapsed ?? TimeSpan.Zero).TotalMilliseconds;
        Console.WriteLine(
            $"LOCK_TRANSIENT_WAIT path={QuoteProgressToken(attribution.Path)} " +
            $"cycle={cycle.ToString(System.Globalization.CultureInfo.InvariantCulture)} " +
            $"max-cycles={maxCycles.ToString(System.Globalization.CultureInfo.InvariantCulture)} " +
            $"waited-ms={wait.WaitedMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture)} " +
            $"probe-ms={probeMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture)} " +
            $"released={wait.Released.ToString().ToLowerInvariant()}");
        Console.Out.Flush();
    }

    private static void EmitTransientNoHolderBuildLockRetryReceipt(
        AcceptanceManifestCheck check,
        BuildLockAttribution attribution,
        int cycle,
        int maxCycles,
        string verdict,
        int? exitCode,
        bool timedOut,
        bool buildLock)
    {
        Console.WriteLine(
            $"LOCK_TRANSIENT_RETRY path={QuoteProgressToken(attribution.Path)} " +
            $"check={QuoteProgressToken(check.Name)} " +
            $"cycle={cycle.ToString(System.Globalization.CultureInfo.InvariantCulture)} " +
            $"max-cycles={maxCycles.ToString(System.Globalization.CultureInfo.InvariantCulture)} " +
            $"verdict={verdict} " +
            $"exit-code={(exitCode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none")} " +
            $"timed-out={timedOut.ToString().ToLowerInvariant()} " +
            $"build-lock={buildLock.ToString().ToLowerInvariant()}");
        Console.Out.Flush();
    }

    private async Task<CommandResult> RunManagedDotnetCommandAsync(
        AcceptanceManifestCheck check,
        string[] arguments,
        DotnetBuildEnvironment environment,
        string worktreePath,
        GoalId? goalId,
        int? stableSlotIndex,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var telemetry = ResolveDotnetTestTelemetry(arguments, check, environment);
        var effectiveArguments = WithBuildEnvironmentArguments(telemetry?.Arguments ?? arguments, environment);
        ReapRecordedGateChildBeforeManagedDotnetCommand(environment, goalId, stableSlotIndex);
        return await RunWithGateHeartbeatAsync(
            effectiveArguments,
            worktreePath,
            timeout,
            CreateGateHeartbeatContext(check, effectiveArguments, worktreePath, goalId, stableSlotIndex, environment),
            cancellationToken).ConfigureAwait(false);
    }

    private static bool IsBuildArtifactIoException(Exception ex) =>
        ex is IOException or UnauthorizedAccessException;

    private static BuildLockAttribution AttributeBuildLock(
        string path,
        string? ownershipHint,
        string? phase,
        string? operation)
    {
        var elapsed = Stopwatch.StartNew();
        var attribution = LockAttribution.Attribute(path, ownershipHint, phase, operation);
        elapsed.Stop();
        return attribution with { ProbeElapsed = elapsed.Elapsed };
    }

    private static bool IsBuildLockFailure(CommandResult result, DotnetBuildEnvironment environment, out BuildLockAttribution attribution)
    {
        attribution = null!;
        if (result.TimedOut || result.ExitCode == 0 || DotnetTestRunReportsCompleted(result.Output))
        {
            return false;
        }

        var lockedPath = LockAttribution.TryExtractLockedPath(result.Output);
        if (lockedPath is null && !IsTransientCompilerLockFailure(result.Output))
        {
            return false;
        }

        attribution = AttributeBuildLock(
            lockedPath ?? environment.ArtifactsPath,
            environment.ArtifactsPath,
            "acceptance-output",
            "classify-build-lock");
        attribution = EnrichBuildLockAttributionWithGateContext(attribution, environment);
        EmitBuildLockClassificationContext(attribution, result, environment);
        return true;
    }

    private static BuildLockAttribution EnrichBuildLockAttributionWithGateContext(
        BuildLockAttribution attribution,
        DotnetBuildEnvironment environment)
    {
        var holders = attribution.Holders.ToList();
        var consumedGateContext = false;
        if (HasNoActionableHolder(attribution) &&
            DotnetBuildEnvironmentManager.TryFindActiveSlotArtifactConsumer(environment) is { } activeConsumer)
        {
            consumedGateContext = true;
            holders.Add(activeConsumer);
        }

        var heartbeat = ReadGateHeartbeat(environment);
        consumedGateContext |= heartbeat?.Snapshot is not null;
        foreach (var holder in BuildLiveHeartbeatHolders(heartbeat?.Snapshot, environment))
        {
            if (!holders.Any(existing => existing.ProcessId == holder.ProcessId))
            {
                holders.Add(holder);
            }
        }

        if (!consumedGateContext)
        {
            return attribution;
        }

        var enriched = attribution with
        {
            Holders = holders,
            Source = attribution.Source.Contains("+gate-context", StringComparison.Ordinal)
                ? attribution.Source
                : attribution.Source + "+gate-context"
        };
        LockAttribution.EmitReceipt(enriched);
        return enriched;
    }

    private static IEnumerable<BuildLockHolder> BuildLiveHeartbeatHolders(
        GateHeartbeatSnapshot? snapshot,
        DotnetBuildEnvironment environment)
    {
        if (snapshot is null ||
            snapshot.CommandLine is not null &&
            !snapshot.CommandLine.Contains(environment.ArtifactsPath, StringComparison.OrdinalIgnoreCase))
        {
            yield break;
        }

        var pids = new[] { snapshot.ProcessId, snapshot.ChildPid }
            .Where(pid => pid.HasValue)
            .Select(pid => pid!.Value)
            .Distinct()
            .Where(IsProcessRunning)
            .ToArray();
        var commandLines = ProcessCommandLines.Read(pids);
        foreach (var pid in pids)
        {
            commandLines.TryGetValue(pid, out var commandLine);
            yield return new BuildLockHolder(
                pid,
                TryProcessName(pid),
                string.IsNullOrWhiteSpace(commandLine) ? snapshot.CommandLine : commandLine,
                true,
                TryProcessStartTime(pid));
        }
    }

    private static void EmitBuildLockClassificationContext(
        BuildLockAttribution attribution,
        CommandResult result,
        DotnetBuildEnvironment environment)
    {
        var heartbeat = ReadGateHeartbeat(environment);
        var snapshot = heartbeat?.Snapshot;
        var pidAlive = snapshot?.ProcessId is { } pid && IsProcessRunning(pid);
        var childAlive = snapshot?.ChildPid is { } childPid && IsProcessRunning(childPid);
        var line =
            $"LOCK_CONTEXT path={QuoteProgressToken(attribution.Path)} source={QuoteProgressToken(attribution.Source)} " +
            $"phase={QuoteProgressToken(attribution.Phase ?? "unknown")} operation={QuoteProgressToken(attribution.Operation ?? "unknown")} " +
            $"exit_code={result.ExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture)} " +
            $"elapsed_ms={(long)(result.Elapsed ?? TimeSpan.Zero).TotalMilliseconds} " +
            $"stdout_bytes={result.StdoutBytes.ToString(System.Globalization.CultureInfo.InvariantCulture)} " +
            $"stderr_bytes={result.StderrBytes.ToString(System.Globalization.CultureInfo.InvariantCulture)} " +
            $"stdout={QuoteProgressToken(result.StdoutPath ?? "unknown")} stderr={QuoteProgressToken(result.StderrPath ?? "unknown")} " +
            $"heartbeat={QuoteProgressToken(heartbeat?.Path ?? Path.Combine(environment.ArtifactsPath, GateHeartbeatArtifacts.FileName))} " +
            $"heartbeat_available={(heartbeat?.IsAvailable == true).ToString().ToLowerInvariant()} " +
            $"heartbeat_state={QuoteProgressToken(snapshot?.State ?? heartbeat?.UnavailableReason ?? "unknown")} " +
            $"heartbeat_pid={snapshot?.ProcessId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown"} " +
            $"heartbeat_pid_alive={pidAlive.ToString().ToLowerInvariant()} " +
            $"heartbeat_child_pid={snapshot?.ChildPid?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown"} " +
            $"heartbeat_child_alive={childAlive.ToString().ToLowerInvariant()} " +
            $"heartbeat_output_bytes={snapshot?.OutputBytes.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown"} " +
            $"slot_index={TryGetStableSlotIndex(environment)?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown"}";
        Console.WriteLine(line);
        Console.Out.Flush();
    }

    private static GateHeartbeatStatus? ReadGateHeartbeat(DotnetBuildEnvironment environment)
    {
        var path = Path.Combine(environment.ArtifactsPath, GateHeartbeatArtifacts.FileName);
        if (!File.Exists(path))
        {
            return null;
        }

        if (TryGetStableSlotIndex(environment) is { } stableSlotIndex)
        {
            return GateHeartbeatArtifacts.ReadStableSlot(stableSlotIndex);
        }

        try
        {
            var snapshot = JsonSerializer.Deserialize<GateHeartbeatSnapshot>(
                File.ReadAllText(path),
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
            return snapshot is null
                ? new GateHeartbeatStatus(-1, path, false, "invalid", null, null, null)
                : new GateHeartbeatStatus(
                    -1,
                    path,
                    true,
                    null,
                    snapshot,
                    Positive(DateTimeOffset.UtcNow - snapshot.LastObservedAt),
                    Positive(DateTimeOffset.UtcNow - snapshot.LastProgressAt));
        }
        catch
        {
            return new GateHeartbeatStatus(-1, path, false, "invalid", null, null, null);
        }
    }

    private static bool IsTransientNoHolderBuildArtifactLock(BuildLockAttribution attribution, DotnetBuildEnvironment environment) =>
        HasNoActionableHolder(attribution) &&
        (PathIsUnderDirectory(attribution.Path, environment.ArtifactsPath) || IsBuildArtifactPath(attribution.Path));

    private static bool DotnetTestRunReportsCompleted(string output) =>
        Regex.IsMatch(
            output,
            @"(?:Passed|Failed)!\s*-\s*Failed:\s*\d+,\s*Passed:\s*\d+",
            RegexOptions.IgnoreCase);

    private static bool IsBuildArtifactPath(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".dll", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".exe", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".pdb", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".json", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".trx", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".cache", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".lock", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasNoActionableHolder(BuildLockAttribution attribution) =>
        attribution.Holders.Count == 0 ||
        attribution.Holders.All(holder =>
            holder.ProcessId is null &&
            (string.IsNullOrWhiteSpace(holder.ProcessName) ||
                holder.ProcessName.Equals("unknown", StringComparison.OrdinalIgnoreCase) ||
                holder.ProcessName.Equals("unknown-probe-timeout", StringComparison.OrdinalIgnoreCase)));

    private static void ReapRecordedGateChildBeforeManagedDotnetCommand(
        DotnetBuildEnvironment environment,
        GoalId? goalId,
        int? stableSlotIndex)
    {
        var heartbeatPath = Path.Combine(environment.ArtifactsPath, GateHeartbeatArtifacts.FileName);
        if (!File.Exists(heartbeatPath))
        {
            return;
        }

        GateHeartbeatSnapshot? snapshot;
        try
        {
            snapshot = JsonSerializer.Deserialize<GateHeartbeatSnapshot>(
                File.ReadAllText(heartbeatPath),
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }
        catch
        {
            return;
        }

        if (snapshot?.ChildPid is not { } childPid)
        {
            return;
        }

        if (stableSlotIndex.HasValue && snapshot.SlotIndex != stableSlotIndex)
        {
            return;
        }

        if (goalId is not null &&
            !string.IsNullOrWhiteSpace(snapshot.GoalId) &&
            !snapshot.GoalId.Equals(goalId.Value, StringComparison.Ordinal))
        {
            return;
        }

        if (!snapshot.State.Equals("running", StringComparison.OrdinalIgnoreCase) &&
            DateTimeOffset.UtcNow - snapshot.LastObservedAt > TimeSpan.FromMinutes(5))
        {
            return;
        }

        if (snapshot.CommandLine is not null &&
            !snapshot.CommandLine.Contains(environment.ArtifactsPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (!IsProcessRunning(childPid))
        {
            return;
        }

        WorkerProcessJobs.TryKillRecordedOwnedChildAndWait(childPid, TimeSpan.FromSeconds(5));
    }

    private static bool IsProcessRunning(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    private static string? TryProcessName(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.ProcessName;
        }
        catch
        {
            return null;
        }
    }

    private static DateTimeOffset? TryProcessStartTime(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
        }
        catch
        {
            return null;
        }
    }

    private static int? TryGetStableSlotIndex(DotnetBuildEnvironment environment)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(environment.ArtifactsPath));
        var name = Path.GetFileName(directory);
        if (name is null ||
            !name.StartsWith("slot-", StringComparison.Ordinal) ||
            !int.TryParse(name["slot-".Length..], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var slotIndex))
        {
            return null;
        }

        return slotIndex;
    }

    private static TimeSpan Positive(TimeSpan value) =>
        value < TimeSpan.Zero ? TimeSpan.Zero : value;

    private static bool PathIsUnderDirectory(string path, string directory)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            var fullDirectory = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return fullPath.Equals(fullDirectory, StringComparison.OrdinalIgnoreCase) ||
                fullPath.StartsWith(fullDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                fullPath.StartsWith(fullDirectory + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static string? TryExtractPathFromException(Exception ex)
    {
        for (var current = ex; current is not null; current = current.InnerException!)
        {
            if (LockAttribution.TryExtractLockedPath(current.Message) is { } path)
            {
                return path;
            }
        }

        return null;
    }

    private static string? BuildManagedDotnetResultSummary(CommandResult result, bool transientCompilerLockRetried)
    {
        var summary = AppendResourceReceipt(
            result.TimedOut ? BuildTimeoutSummary(result) : ExtractResultSummary(result.Output),
            result.ResourceAccounting,
            result.ResourceAccountingExpected);
        if (!transientCompilerLockRetried)
        {
            return summary;
        }

        const string remediation = "build artifact lock detected; holder attributed; remediation retried";
        return string.IsNullOrWhiteSpace(summary)
            ? remediation
            : $"{remediation}; {summary}";
    }

    private static string PrefixResultSummary(string prefix, string? summary) =>
        string.IsNullOrWhiteSpace(summary)
            ? prefix
            : $"{prefix}; {summary}";

    private static string? BuildGenericCommandResultSummary(CommandResult result)
    {
        var summary = result.TimedOut ? BuildTimeoutSummary(result) : ExtractResultSummary(result.Output);
        return AppendResourceReceipt(summary, result.ResourceAccounting, result.ResourceAccountingExpected);
    }

    private static string? AppendResourceReceipt(
        string? summary,
        TaskProcessResourceAccounting? accounting,
        bool accountingExpected)
    {
        var receipt = accounting is null
            ? accountingExpected ? "RESOURCE phase=gate cpu_ms=0 peak_mem_bytes=0 io_bytes=0 accounting_source=accounting-unavailable" : null
            : $"RESOURCE phase=gate cpu_ms={accounting.CpuMilliseconds} peak_mem_bytes={accounting.PeakMemoryBytes} io_bytes={accounting.IoBytes} accounting_source={accounting.AccountingSource}";
        if (receipt is null)
        {
            return summary;
        }

        return string.IsNullOrWhiteSpace(summary)
            ? receipt
            : $"{summary}; {receipt}";
    }

    // A testhost that crashes MID-run ("host process exited unexpectedly" / "Test Run Aborted") with no
    // completed all-passed banner and no real test failure is an environmental abort, not a verdict. A
    // build/compile failure, or a completed run WITH real test failures, is NOT this (it is a genuine red).
    internal static bool IsTransientTesthostAbort(string output)
    {
        if (output.Contains("Build FAILED", StringComparison.Ordinal) ||
            output.Contains("error CS", StringComparison.Ordinal) ||
            TestRunReportsRealFailure(output))
        {
            return false;
        }

        return output.Contains("Test Run Aborted", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("host process exited unexpectedly", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("active Test Run was aborted", StringComparison.OrdinalIgnoreCase);
    }

    // True when the run's own summary banner reports >=1 actual test failure (a completed run with real
    // failures), e.g. "Failed:     1, Passed:  1012". Distinguishes a genuine red from a transient abort.
    private static bool TestRunReportsRealFailure(string output) =>
        System.Text.RegularExpressions.Regex.IsMatch(output, @"Failed:\s*[1-9]\d*");

    private async Task<AcceptanceCheckResult> RunForbiddenChangedPathsCheckAsync(
        IReadOnlyList<string> globs,
        string worktreePath,
        CancellationToken cancellationToken)
    {
        var result = await _runner(
            ["git", "diff", "--name-only", "main...HEAD"],
            worktreePath,
            AcceptanceCheckTimeouts.DefaultTimeout,
            cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            return new AcceptanceCheckResult("forbidden changed paths", false, result.ExitCode, TailOutput(result.Output));
        }

        var changedPaths = result.Output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var forbidden = changedPaths
            .Where(path => globs.Any(glob => GlobMatches(glob, path)))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return forbidden.Length == 0
            ? new AcceptanceCheckResult("forbidden changed paths", true, 0, null)
            : new AcceptanceCheckResult("forbidden changed paths", false, 1, string.Join(Environment.NewLine, forbidden));
    }

    private async Task<AcceptanceCheckResult> RunTestTamperCheckAsync(
        string[] testFiles,
        string worktreePath,
        CancellationToken cancellationToken)
    {
        const string CheckName = "test tamper guard";

        var diffArgs = DiffBaseArgs.Concat(testFiles).ToArray();

        var result = await _runner(
            diffArgs,
            worktreePath,
            AcceptanceCheckTimeouts.DefaultTimeout,
            cancellationToken).ConfigureAwait(false);

        if (result.ExitCode != 0)
            return new AcceptanceCheckResult(CheckName, true, 0, null, Advisory: true, ResultSummary: "diff unavailable");

        var signals = AnalyzeTestFileDiff(result.Output);

        if (signals.Count == 0)
            return new AcceptanceCheckResult(CheckName, true, 0, null, Advisory: true, ResultSummary: "no test degradation detected");

        return new AcceptanceCheckResult(
            CheckName, false, 1,
            string.Join(Environment.NewLine, signals),
            Advisory: true,
            ResultSummary: $"{signals.Count} test degradation signal(s)");
    }

    private static bool IsTestFile(string path) =>
        path.Contains("Tests", StringComparison.OrdinalIgnoreCase);

    private static List<string> AnalyzeTestFileDiff(string diff)
    {
        var signals = new List<string>();
        var fileStats = new List<TestFileDiffStats>();
        string? currentFile = null;
        string? pendingFile = null;
        int assertRemoved = 0, assertAdded = 0;
        int testAttrRemoved = 0, testAttrAdded = 0;
        var tautologies = new List<string>();

        void FlushFile()
        {
            if (currentFile is null) return;

            fileStats.Add(new TestFileDiffStats(
                currentFile,
                assertRemoved,
                assertAdded,
                testAttrRemoved,
                testAttrAdded));

            foreach (var t in tautologies)
                signals.Add($"{currentFile}: tautology assertion added: {t}");
        }

        void StartFile(string filePath)
        {
            FlushFile();
            currentFile = filePath;
            assertRemoved = assertAdded = testAttrRemoved = testAttrAdded = 0;
            tautologies.Clear();
        }

        foreach (var rawLine in diff.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');

            if (line.StartsWith("--- a/", StringComparison.Ordinal))
            {
                pendingFile = line[6..];
            }
            else if (line.StartsWith("+++ b/", StringComparison.Ordinal))
            {
                StartFile(line[6..]);
                pendingFile = null;
            }
            else if (line.StartsWith("+++ /dev/null", StringComparison.Ordinal) && pendingFile is not null)
            {
                StartFile(pendingFile);
                pendingFile = null;
            }
            else if (line.Length > 1 && line[0] is '-' or '+' &&
                     !line.StartsWith("--- ", StringComparison.Ordinal) &&
                     !line.StartsWith("+++ ", StringComparison.Ordinal))
            {
                var content = line[1..];
                var trimmed = content.TrimStart();

                if (line[0] == '-')
                {
                    if (trimmed.StartsWith("Assert.", StringComparison.Ordinal))
                        assertRemoved++;
                    if (TestAttrPattern.IsMatch(trimmed))
                        testAttrRemoved++;
                }
                else
                {
                    if (trimmed.StartsWith("Assert.", StringComparison.Ordinal))
                        assertAdded++;
                    if (TestAttrPattern.IsMatch(trimmed))
                        testAttrAdded++;
                    if (TautologyPattern.IsMatch(content))
                        tautologies.Add(trimmed.Length > 80 ? trimmed[..80] + "..." : trimmed);
                }
            }
        }

        FlushFile();

        var totalAssertRemoved = fileStats.Sum(file => file.AssertRemoved);
        var totalAssertAdded = fileStats.Sum(file => file.AssertAdded);
        var netAssertRemoved = totalAssertRemoved - totalAssertAdded;
        if (netAssertRemoved > 0)
        {
            signals.Insert(
                0,
                $"diff-wide net -{netAssertRemoved} assertion(s) removed ({FormatTestFileDiffDetails(fileStats)})");
        }

        var totalTestAttrRemoved = fileStats.Sum(file => file.TestAttrRemoved);
        var totalTestAttrAdded = fileStats.Sum(file => file.TestAttrAdded);
        var netTestAttrRemoved = totalTestAttrRemoved - totalTestAttrAdded;
        if (netTestAttrRemoved > 0)
        {
            signals.Insert(
                netAssertRemoved > 0 ? 1 : 0,
                $"diff-wide {netTestAttrRemoved} test method(s) removed ({FormatTestFileDiffDetails(fileStats)})");
        }

        return signals;
    }

    private static string FormatTestFileDiffDetails(IReadOnlyList<TestFileDiffStats> fileStats)
    {
        var details = fileStats
            .Where(file =>
                file.AssertRemoved != 0 ||
                file.AssertAdded != 0 ||
                file.TestAttrRemoved != 0 ||
                file.TestAttrAdded != 0)
            .Select(file =>
                $"{file.FilePath}: assertions -{file.AssertRemoved}/+{file.AssertAdded}, tests -{file.TestAttrRemoved}/+{file.TestAttrAdded}");

        return "per-file: " + string.Join("; ", details);
    }

    private sealed record TestFileDiffStats(
        string FilePath,
        int AssertRemoved,
        int AssertAdded,
        int TestAttrRemoved,
        int TestAttrAdded);

    private static string[] BuildDotnetTestArguments(AcceptanceManifestCheck check, bool noBuild = false)
    {
        var args = new List<string> { "dotnet", "test" };
        if (!string.IsNullOrWhiteSpace(check.Project))
        {
            args.Add(check.Project);
        }

        var explicitFilter = ExtractFilterArguments(check.Arguments, args);

        // Exclude host-integration tests that spawn a real Kestrel dashboard server (binds a port,
        // needs an interactive firewall allow) — they hang in the unattended, relocated gate. Match
        // both by class name (works on a worktree built before the trait existed) and by the
        // [Trait("Category","HostIntegration")] tag (covers any future such tests). They run in a
        // dedicated lane instead.
        if (string.IsNullOrWhiteSpace(explicitFilter) && NeedsUnattendedHostIntegrationExclusion(check))
        {
            args.Add("--filter");
            args.Add("FullyQualifiedName!~DashboardHostTests&Category!=HostIntegration");
        }

        // Fail a hung test fast and by name before the whole check budget is exhausted. A test that
        // spawns a process which blocks (e.g. on a firewall prompt) and then WaitForExit()s on it
        // can otherwise stall the whole acceptance until the configured command timeout. The
        // inactivity timeout is per-test and distinct from the full check budget.
        args.Add("--blame-hang-timeout");
        args.Add("120s");
        args.Add("--blame-hang-dump-type");
        args.Add("none");
        if (noBuild && !args.Any(argument => argument.Equals("--no-build", StringComparison.OrdinalIgnoreCase)))
        {
            args.Add("--no-build");
        }

        return [.. args];
    }

    private static DotnetTestBuildPhase CreateDotnetTestBuildPhase(
        string worktreePath,
        IReadOnlyList<AcceptanceManifestCheck> checks,
        IReadOnlyList<string>? changedFiles,
        PolicyShardPlan policyShardPlan)
    {
        DotnetBaseBuildCachePlan? cachePlan = TryCreateBaseBuildCachePlan(worktreePath, changedFiles, policyShardPlan);
        var dotnetTestChecks = checks
            .Where(check => check.Type.Equals("dotnet-test", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var solutionCheck = dotnetTestChecks.FirstOrDefault(check =>
            !string.IsNullOrWhiteSpace(check.Project) &&
            check.Project.EndsWith(".sln", StringComparison.OrdinalIgnoreCase));
        if (solutionCheck is not null)
        {
            return new DotnetTestBuildPhase(BuildDotnetTestBuildArguments(solutionCheck), cachePlan);
        }

        if (TryFindRootSolution(worktreePath) is { } solutionPath)
        {
            var template = dotnetTestChecks.FirstOrDefault();
            var templateArguments = template?.Arguments ?? [];
            return new DotnetTestBuildPhase(BuildDotnetTestBuildArguments(
                ["dotnet", "test", solutionPath, .. templateArguments]), cachePlan);
        }

        var firstCheck = dotnetTestChecks.FirstOrDefault();
        return new DotnetTestBuildPhase(firstCheck is null
            ? ["dotnet", "build"]
            : BuildDotnetTestBuildArguments(firstCheck), cachePlan);
    }

    private static DotnetBaseBuildCachePlan? TryCreateBaseBuildCachePlan(
        string worktreePath,
        IReadOnlyList<string>? changedFiles,
        PolicyShardPlan policyShardPlan)
    {
        if (changedFiles is null ||
            changedFiles.Count == 0 ||
            !policyShardPlan.Applies ||
            policyShardPlan.ForceFull)
        {
            return null;
        }

        var buildProjects = policyShardPlan.DependencyClosure
            .Where(project => CacheableProjects.Contains(project, StringComparer.OrdinalIgnoreCase))
            .OrderBy(project => Array.IndexOf(CacheableProjects, project))
            .ToArray();
        if (buildProjects.Length == 0 || buildProjects.Length == CacheableProjects.Length)
        {
            return null;
        }

        var mainSha = ResolveBaseBuildMainShaForTests?.Invoke(worktreePath) ?? ResolveBaseBuildMainSha(worktreePath);
        if (string.IsNullOrWhiteSpace(mainSha))
        {
            return null;
        }

        var restoreProjects = CacheableProjects
            .Where(project => !buildProjects.Contains(project, StringComparer.OrdinalIgnoreCase))
            .ToArray();
        return restoreProjects.Length == 0
            ? null
            : new DotnetBaseBuildCachePlan(mainSha, restoreProjects, buildProjects);
    }

    private static string? ResolveBaseBuildMainSha(string worktreePath)
    {
        return ResolveGitScalar(worktreePath, "merge-base", "HEAD", "main");
    }

    private static string? ResolveGitScalar(string worktreePath, params string[] arguments)
    {
        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = worktreePath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var argument in arguments)
            {
                process.StartInfo.ArgumentList.Add(argument);
            }

            if (!process.Start())
            {
                return null;
            }

            if (!process.WaitForExit(5000))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return null;
            }

            if (process.ExitCode != 0)
            {
                return null;
            }

            var sha = process.StandardOutput.ReadToEnd().Trim();
            return sha.Length >= 7 && sha.All(Uri.IsHexDigit) ? sha : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return null;
        }
    }

    private static string? TryFindRootSolution(string worktreePath)
    {
        var preferred = Path.Combine(worktreePath, "Mcg.AgentOrchestrator.sln");
        if (File.Exists(preferred))
        {
            return Path.GetFileName(preferred);
        }

        try
        {
            return Directory.EnumerateFiles(worktreePath, "*.sln", SearchOption.TopDirectoryOnly)
                .Select(Path.GetFileName)
                .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            return null;
        }
    }

    private static string[] BuildDotnetTestBuildArguments(AcceptanceManifestCheck check)
    {
        var testArguments = BuildDotnetTestArguments(check);
        return BuildDotnetTestBuildArguments(testArguments);
    }

    private static string[] BuildDotnetTestBuildArguments(string[] testArguments)
    {
        var args = new List<string> { "dotnet", "build" };
        var startIndex = 2;
        if (testArguments.Length > 2 && !testArguments[2].StartsWith("-", StringComparison.Ordinal))
        {
            args.Add(testArguments[2]);
            startIndex = 3;
        }

        for (var index = startIndex; index < testArguments.Length; index++)
        {
            var argument = testArguments[index];
            if (argument.Equals("--filter", StringComparison.OrdinalIgnoreCase) ||
                argument.Equals("--logger", StringComparison.OrdinalIgnoreCase) ||
                argument.Equals("--collect", StringComparison.OrdinalIgnoreCase) ||
                argument.Equals("--blame-hang-timeout", StringComparison.OrdinalIgnoreCase) ||
                argument.Equals("--blame-hang-dump-type", StringComparison.OrdinalIgnoreCase))
            {
                index++;
                continue;
            }

            if (argument.Equals("--no-build", StringComparison.OrdinalIgnoreCase) ||
                argument.StartsWith("--blame", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!IsBuildCompatibleDotnetArgument(argument))
            {
                continue;
            }

            args.Add(argument);
            if (ArgumentExpectsValue(argument) && index + 1 < testArguments.Length)
            {
                args.Add(testArguments[++index]);
            }
        }

        return [.. args];
    }

    private static bool NeedsUnattendedHostIntegrationExclusion(AcceptanceManifestCheck check) =>
        string.IsNullOrWhiteSpace(check.Project) ||
        check.Project.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) ||
        IsInfrastructureTestProject(check.Project);

    private static bool GateUsesStableSlot(int? stableSlotIndex, DotnetBuildEnvironmentLease? stableSlotLease) =>
        stableSlotIndex.HasValue || stableSlotLease is not null;

    private static bool UsesMicrosoftTestingPlatform(AcceptanceManifestCheck check) =>
        check.Runner?.Equals("mtp", StringComparison.OrdinalIgnoreCase) == true;

    private static bool UsesVstestRunner(AcceptanceManifestCheck check) =>
        string.IsNullOrWhiteSpace(check.Runner) ||
        check.Runner.Equals("vstest", StringComparison.OrdinalIgnoreCase);

    private static bool IsDotnetTestCommand(string[] arguments) =>
        arguments.Length >= 2 &&
        arguments[0].Equals("dotnet", StringComparison.OrdinalIgnoreCase) &&
        arguments[1].Equals("test", StringComparison.OrdinalIgnoreCase);

    private static string[] EnsureDotnetTestNoBuildArguments(string[] arguments)
    {
        if (arguments.Any(argument => argument.Equals("--no-build", StringComparison.OrdinalIgnoreCase)))
        {
            return arguments;
        }

        return [.. arguments, "--no-build"];
    }

    private static bool IsBuildCompatibleDotnetArgument(string argument) =>
        argument.Equals("--configuration", StringComparison.OrdinalIgnoreCase) ||
        argument.Equals("-c", StringComparison.OrdinalIgnoreCase) ||
        argument.Equals("--framework", StringComparison.OrdinalIgnoreCase) ||
        argument.Equals("-f", StringComparison.OrdinalIgnoreCase) ||
        argument.Equals("--runtime", StringComparison.OrdinalIgnoreCase) ||
        argument.Equals("-r", StringComparison.OrdinalIgnoreCase) ||
        argument.Equals("--verbosity", StringComparison.OrdinalIgnoreCase) ||
        argument.Equals("-v", StringComparison.OrdinalIgnoreCase) ||
        argument.Equals("--no-restore", StringComparison.OrdinalIgnoreCase) ||
        argument.Equals("--nologo", StringComparison.OrdinalIgnoreCase) ||
        argument.Equals("--force", StringComparison.OrdinalIgnoreCase) ||
        argument.Equals("--interactive", StringComparison.OrdinalIgnoreCase) ||
        argument.StartsWith("-p:", StringComparison.OrdinalIgnoreCase) ||
        argument.StartsWith("/p:", StringComparison.OrdinalIgnoreCase) ||
        argument.StartsWith("--property:", StringComparison.OrdinalIgnoreCase);

    private static bool ArgumentExpectsValue(string argument) =>
        argument.Equals("--configuration", StringComparison.OrdinalIgnoreCase) ||
        argument.Equals("-c", StringComparison.OrdinalIgnoreCase) ||
        argument.Equals("--framework", StringComparison.OrdinalIgnoreCase) ||
        argument.Equals("-f", StringComparison.OrdinalIgnoreCase) ||
        argument.Equals("--runtime", StringComparison.OrdinalIgnoreCase) ||
        argument.Equals("-r", StringComparison.OrdinalIgnoreCase) ||
        argument.Equals("--verbosity", StringComparison.OrdinalIgnoreCase) ||
        argument.Equals("-v", StringComparison.OrdinalIgnoreCase);

    private static string? ExtractFilterArguments(IReadOnlyList<string> sourceArguments, List<string> destinationArguments)
    {
        string? filter = null;
        for (var index = 0; index < sourceArguments.Count; index++)
        {
            var argument = sourceArguments[index];
            if (argument.Equals("--filter", StringComparison.OrdinalIgnoreCase))
            {
                if (index + 1 < sourceArguments.Count)
                {
                    filter = sourceArguments[index + 1];
                    index++;
                }

                continue;
            }

            destinationArguments.Add(argument);
        }

        if (!string.IsNullOrWhiteSpace(filter))
        {
            destinationArguments.Add("--filter");
            destinationArguments.Add(filter);
        }

        return filter;
    }

    private static string[] BuildMtpTestArguments(
        AcceptanceManifestCheck check,
        DotnetBuildEnvironment environment,
        DotnetTestTelemetry telemetry)
    {
        var args = new List<string>
        {
            ResolveMtpExecutablePath(check, environment),
            "--no-ansi",
            "--progress",
            "off"
        };
        var filter = ExtractMtpCompatibleArguments(check.Arguments, args);
        if (!string.IsNullOrWhiteSpace(filter))
        {
            args.AddRange(TranslateMtpFilter(filter));
        }

        args.Add("--results-directory");
        args.Add(Path.GetDirectoryName(telemetry.Paths[0]) ?? Path.Combine(environment.ArtifactsPath, "TestResults"));
        args.Add("--report-trx");
        args.Add("--report-trx-filename");
        args.Add(Path.GetFileName(telemetry.Paths[0]));
        if (!args.Any(argument => argument.Equals("--long-running", StringComparison.OrdinalIgnoreCase)))
        {
            args.Add("--long-running");
            args.Add("120");
        }

        return [.. args];
    }

    private static string? ExtractMtpCompatibleArguments(IReadOnlyList<string> sourceArguments, List<string> destinationArguments)
    {
        string? filter = null;
        for (var index = 0; index < sourceArguments.Count; index++)
        {
            var argument = sourceArguments[index];
            if (argument.Equals("--filter", StringComparison.OrdinalIgnoreCase))
            {
                if (index + 1 < sourceArguments.Count)
                {
                    filter = sourceArguments[++index];
                }

                continue;
            }

            if (argument.Equals("--verbosity", StringComparison.OrdinalIgnoreCase) ||
                argument.Equals("-v", StringComparison.OrdinalIgnoreCase) ||
                argument.Equals("--configuration", StringComparison.OrdinalIgnoreCase) ||
                argument.Equals("-c", StringComparison.OrdinalIgnoreCase) ||
                argument.Equals("--framework", StringComparison.OrdinalIgnoreCase) ||
                argument.Equals("-f", StringComparison.OrdinalIgnoreCase) ||
                argument.Equals("--logger", StringComparison.OrdinalIgnoreCase) ||
                argument.Equals("--results-directory", StringComparison.OrdinalIgnoreCase) ||
                argument.Equals("--blame-hang-timeout", StringComparison.OrdinalIgnoreCase) ||
                argument.Equals("--blame-hang-dump-type", StringComparison.OrdinalIgnoreCase))
            {
                index++;
                continue;
            }

            if (argument.Equals("--no-build", StringComparison.OrdinalIgnoreCase) ||
                argument.Equals("--no-restore", StringComparison.OrdinalIgnoreCase) ||
                argument.Equals("--nologo", StringComparison.OrdinalIgnoreCase) ||
                argument.StartsWith("-p:", StringComparison.OrdinalIgnoreCase) ||
                argument.StartsWith("/p:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            destinationArguments.Add(argument);
        }

        return filter;
    }

    private static IEnumerable<string> TranslateMtpFilter(string filter)
    {
        foreach (var rawToken in Regex.Split(filter, @"[&|]"))
        {
            var token = rawToken.Trim();
            if (token.Length == 0)
            {
                continue;
            }

            var fullyQualifiedName = Regex.Match(
                token,
                @"^FullyQualifiedName\s*(?<op>!~|~)\s*(?<value>[A-Za-z_][A-Za-z0-9_.]*)$",
                RegexOptions.IgnoreCase);
            if (fullyQualifiedName.Success)
            {
                yield return fullyQualifiedName.Groups["op"].Value == "!~"
                    ? "--filter-not-class"
                    : "--filter-class";
                yield return fullyQualifiedName.Groups["value"].Value;
                continue;
            }

            var categoryExclusion = Regex.Match(
                token,
                @"^Category\s*!=\s*(?<value>[A-Za-z_][A-Za-z0-9_.-]*)$",
                RegexOptions.IgnoreCase);
            if (categoryExclusion.Success)
            {
                yield return "--filter-not-trait";
                yield return $"Category={categoryExclusion.Groups["value"].Value}";
                continue;
            }

            throw new InvalidOperationException($"MTP test filter '{filter}' contains unsupported token '{token}'.");
        }
    }

    private static string ResolveMtpExecutablePath(AcceptanceManifestCheck check, DotnetBuildEnvironment environment)
    {
        if (string.IsNullOrWhiteSpace(check.Project))
        {
            throw new InvalidOperationException($"Acceptance check '{check.Name}' uses MTP but has no project.");
        }

        var projectName = Path.GetFileNameWithoutExtension(check.Project);
        var extension = OperatingSystem.IsWindows() ? ".exe" : string.Empty;
        return Path.Combine(
            environment.ArtifactsPath,
            "bin",
            projectName,
            "debug",
            $"{projectName}{extension}");
    }

    private static string[] WithBuildEnvironmentArguments(string[] arguments, DotnetBuildEnvironment environment)
    {
        return [.. arguments, .. environment.Arguments];
    }

    private static DotnetTestTelemetry? ResolveDotnetTestTelemetry(
        string[] arguments,
        AcceptanceManifestCheck check,
        DotnetBuildEnvironment environment)
    {
        if (!IsDotnetTestCommand(arguments))
        {
            return null;
        }

        return ResolveTestTelemetry(check, environment) with
        {
            Arguments = AddVstestTelemetryArguments(arguments, check, environment)
        };
    }

    private static DotnetTestTelemetry ResolveTestTelemetry(
        AcceptanceManifestCheck check,
        DotnetBuildEnvironment environment)
    {
        var attemptPrefix = Environment.GetEnvironmentVariable(AcceptanceAttemptTrxPrefixVariable);
        var directory = string.IsNullOrWhiteSpace(attemptPrefix)
            ? Path.Combine(environment.ArtifactsPath, "TestResults")
            : Path.GetDirectoryName(attemptPrefix);
        if (string.IsNullOrWhiteSpace(directory))
        {
            directory = Path.Combine(environment.ArtifactsPath, "TestResults");
        }

        var filePrefix = string.IsNullOrWhiteSpace(attemptPrefix)
            ? $"{environment.LeaseId}.{Slug(check.Name)}"
            : $"{Path.GetFileName(attemptPrefix)}.{Slug(check.Name)}";
        var fileName = $"{SanitizeFileName(filePrefix)}.trx";
        var path = Path.Combine(directory, fileName);
        return new DotnetTestTelemetry([path], []);
    }

    private static string[] AddVstestTelemetryArguments(
        string[] arguments,
        AcceptanceManifestCheck check,
        DotnetBuildEnvironment environment)
    {
        var telemetry = ResolveTestTelemetry(check, environment);
        var directory = Path.GetDirectoryName(telemetry.Paths[0]) ?? Path.Combine(environment.ArtifactsPath, "TestResults");
        var fileName = Path.GetFileName(telemetry.Paths[0]);
        var effectiveArguments = new List<string>(arguments)
        {
            "--logger",
            $"trx;LogFileName={fileName}",
            "--results-directory",
            directory
        };

        return [.. effectiveArguments];
    }

    private static void EmitMissingTrxReceiptIfNeeded(bool passed, DotnetTestTelemetry? telemetry)
    {
        if (!passed || telemetry is null)
        {
            return;
        }

        var missing = telemetry.Paths
            .Where(path => TryGetFileLength(path) <= 0)
            .ToArray();
        if (missing.Length == 0)
        {
            return;
        }

        Console.WriteLine($"TRX_TELEMETRY_UNAVAILABLE paths={QuoteProgressToken(string.Join(";", missing))}");
        Console.Out.Flush();
    }

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new string(value.Select(ch => invalid.Contains(ch) ? '-' : ch).ToArray()).Trim('-', '.');
        return string.IsNullOrWhiteSpace(sanitized) ? "acceptance-test-results" : sanitized;
    }

    private static bool IsDotnetCommand(string[] arguments)
    {
        return arguments.Length > 0 && arguments[0].Equals("dotnet", StringComparison.OrdinalIgnoreCase);
    }

    private static string[] BuildCommandArguments(AcceptanceManifestCheck check)
    {
        if (string.IsNullOrWhiteSpace(check.Command))
        {
            throw new InvalidOperationException($"Acceptance check '{check.Name}' is missing command.");
        }

        return [check.Command, .. check.Arguments];
    }

    private static string Slug(string value)
    {
        var slug = Regex.Replace(value.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        return string.IsNullOrWhiteSpace(slug) ? "check" : slug;
    }

    private static bool GlobMatches(string glob, string path)
    {
        var normalizedGlob = glob.Replace('\\', '/').TrimStart('/');
        var normalizedPath = path.Replace('\\', '/').TrimStart('/');
        var pattern = "^" + Regex.Escape(normalizedGlob)
            .Replace("\\*\\*", ".*", StringComparison.Ordinal)
            .Replace("\\*", "[^/]*", StringComparison.Ordinal) + "$";
        return Regex.IsMatch(normalizedPath, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static string? ExtractResultSummary(string output)
    {
        return output
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault(line =>
                line.Contains("Failed:", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("Passed:", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("Total:", StringComparison.OrdinalIgnoreCase));
    }

    private static string? TailOutput(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return null;
        }

        const int maxChars = 2000;
        const int maxLines = 40;

        var trimmed = output.Trim();
        var lines = trimmed.Split('\n');
        var tail = lines.Length <= maxLines
            ? trimmed
            : string.Join('\n', lines[^maxLines..]);

        return tail.Length <= maxChars ? tail : tail[^maxChars..];
    }

    private static string BuildTimeoutSummary(CommandResult result) =>
        $"elapsed={FormatTimeout(result.Elapsed ?? result.Timeout ?? AcceptanceCheckTimeouts.DefaultTimeout)} budget={FormatTimeout(result.Timeout ?? AcceptanceCheckTimeouts.DefaultTimeout)}";

    private static string BuildTimeoutFailureName(AcceptanceManifestCheck check, CommandResult result) =>
        $"acceptance-check-timeout: {Slug(check.Name)} {BuildTimeoutSummary(result)}";

    private static string BuildTimeoutOutput(CommandResult result)
    {
        var details = new List<string>
        {
            $"Verification command timed out after {BuildTimeoutSummary(result)}.",
        };

        if (!string.IsNullOrWhiteSpace(result.CommandLine))
            details.Add($"Command: {result.CommandLine}");
        if (!string.IsNullOrWhiteSpace(result.StdoutPath))
            details.Add($"stdout: {result.StdoutPath}");
        if (!string.IsNullOrWhiteSpace(result.StderrPath))
            details.Add($"stderr: {result.StderrPath}");

        var tail = TailOutput(result.Output);
        if (!string.IsNullOrWhiteSpace(tail))
        {
            details.Add("Last output:");
            details.Add(tail);
        }

        return string.Join(Environment.NewLine, details);
    }

    private static string FormatTimeout(TimeSpan timeout) =>
        timeout.TotalSeconds >= 60
            ? $"{timeout.TotalMinutes:0.#}m"
            : $"{timeout.TotalSeconds:0.#}s";

    private async Task<CommandResult> RunWithGateHeartbeatAsync(
        string[] arguments,
        string workingDirectory,
        TimeSpan timeout,
        GateHeartbeatContext heartbeatContext,
        CancellationToken cancellationToken)
    {
        var previous = CurrentGateHeartbeatContext.Value;
        CurrentGateHeartbeatContext.Value = heartbeatContext;
        try
        {
            return await _runner(arguments, workingDirectory, timeout, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            CurrentGateHeartbeatContext.Value = previous;
        }
    }

    private static GateHeartbeatContext CreateGateHeartbeatContext(
        AcceptanceManifestCheck check,
        string[] arguments,
        string worktreePath,
        GoalId? goalId,
        int? stableSlotIndex,
        DotnetBuildEnvironment? environment)
    {
        var heartbeatPath = environment is not null
            ? Path.Combine(environment.ArtifactsPath, GateHeartbeatArtifacts.FileName)
            : stableSlotIndex.HasValue
                ? GateHeartbeatArtifacts.GetStableSlotPath(stableSlotIndex.Value)
                : GateHeartbeatArtifacts.GetManualPath(worktreePath);

        return new GateHeartbeatContext(
            goalId?.Value,
            "verification-check",
            check.Name,
            stableSlotIndex,
            heartbeatPath,
            string.Join(' ', arguments.Select(QuoteForDisplay)));
    }

    private static async Task<CommandResult> RunProcessAsync(
        string[] arguments,
        string workingDirectory,
        TimeSpan commandTimeout,
        CancellationToken cancellationToken)
    {
        // Capture output to FILES via the platform shell, not pipes. A test or build can spawn a
        // grandchild that inherits the child's stdout/stderr handle and outlives it; with a
        // redirected PIPE the test runner never reaches EOF while that grandchild holds the write
        // end, so `dotnet test` never exits and the whole command rides the configured timeout to a
        // "A task was canceled". A plain `dotnet test > out 2> err` exits cleanly in that same
        // scenario, so we mirror it: every process exits regardless of a lingering grandchild and
        // we read the files afterward with a shared, delete-tolerant handle.
        var stdoutPath = Path.Combine(Path.GetTempPath(), $"mcg-acc-{Guid.NewGuid():N}.out");
        var stderrPath = Path.Combine(Path.GetTempPath(), $"mcg-acc-{Guid.NewGuid():N}.err");

        var commandLine = string.Join(' ', arguments.Select(QuoteForDisplay));
        var timedOut = false;
        WorkerProcessJobAccounting? killedAccounting = null;
        var elapsed = Stopwatch.StartNew();
        var heartbeatContext = CurrentGateHeartbeatContext.Value;
        CancellationTokenSource? heartbeatCts = null;
        Task? heartbeatTask = null;
        GateHeartbeatRuntime? heartbeat = null;
        var keepOutputFiles = false;
        var startInfo = new ProcessStartInfo
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory
        };

        if (OperatingSystem.IsWindows())
        {
            startInfo.FileName = "cmd.exe";
            // cmd /c strips one surrounding quote pair, so wrap the whole redirected command once.
            startInfo.Arguments = $"/c \"{BuildRedirectedCommand(arguments, stdoutPath, stderrPath, QuoteForCmd)}\"";
        }
        else
        {
            startInfo.FileName = "/bin/sh";
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add(BuildRedirectedCommand(arguments, stdoutPath, stderrPath, QuoteForPosix));
        }

        startInfo.EnvironmentVariables["MCG_ORCHESTRATOR_REPOSITORY_ROOT"] = workingDirectory;

        // The acceptance suite verifies the CODE and must run hermetically — NOT under the operator's
        // live worker-dispatch runtime config. MCG_WORKER_SANDBOX (and friends) control how real
        // workers are launched (low integrity); several tests read WorkerSandboxOptions.FromEnvironment(),
        // so when the operator runs `conduct` with MCG_WORKER_SANDBOX=1 that var is inherited by this
        // child process and flips those tests' expected sandbox mode — failing acceptance INSIDE the
        // watch while the same suite passes when `acceptance` is run standalone (without the var). Strip
        // the worker-dispatch vars so the suite always runs against the default configuration.
        startInfo.EnvironmentVariables.Remove(WorkerSandboxOptions.EnabledVariable);
        startInfo.EnvironmentVariables.Remove(WorkerSandboxOptions.AccountVariable);
        startInfo.EnvironmentVariables.Remove(WorkerSandboxOptions.CredentialTargetVariable);
        startInfo.EnvironmentVariables.Remove(WorkerSandboxOptions.DispatchWorkerVariable);

        int? startedProcessId = null;
        try
        {
            using var process = ProcessTreeGuiSuppression.Start(startInfo);
            startedProcessId = process.Id;
            WorkerProcessJobs.TryRegister(process, $"acceptance:{workingDirectory}");
            if (heartbeatContext is not null)
            {
                heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                heartbeat = new GateHeartbeatRuntime(
                    heartbeatContext,
                    process.Id,
                    stdoutPath,
                    stderrPath,
                    commandTimeout);
                heartbeatTask = WriteGateHeartbeatLoopAsync(heartbeat, heartbeatCts.Token);
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(commandTimeout);

            try
            {
                await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { WorkerProcessJobs.TryKillOrFallback(process.Id, out killedAccounting); } catch { /* best effort */ }
                try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
                if (cancellationToken.IsCancellationRequested)
                    throw;

                timedOut = true;
            }

            var stdout = await ReadFileWithRetryAsync(stdoutPath).ConfigureAwait(false);
            var stderr = await ReadFileWithRetryAsync(stderrPath).ConfigureAwait(false);
            var stdoutBytes = TryGetFileLength(stdoutPath);
            var stderrBytes = TryGetFileLength(stderrPath);
            elapsed.Stop();
            var exitCode = timedOut ? -1 : process.ExitCode;
            keepOutputFiles = timedOut || exitCode != 0;
            WorkerProcessJobs.Release(process.Id, out var accounting);
            if (heartbeat is not null)
            {
                if (heartbeatCts is not null)
                {
                    try { await heartbeatCts.CancelAsync().ConfigureAwait(false); } catch { }
                }

                if (heartbeatTask is not null)
                {
                    try { await heartbeatTask.ConfigureAwait(false); } catch { }
                }

                heartbeat.WriteFinal(timedOut ? "timed-out" : "completed", childPid: process.Id, exitCode: exitCode);
                heartbeatCts?.Dispose();
                heartbeatCts = null;
                heartbeatTask = null;
            }
            accounting ??= killedAccounting;
            startedProcessId = null;
            return new CommandResult(
                exitCode,
                (stdout + stderr).Trim(),
                timedOut,
                commandLine,
                stdoutPath,
                stderrPath,
                commandTimeout,
                elapsed.Elapsed,
                accounting is null
                    ? null
                    : new TaskProcessResourceAccounting(
                        accounting.CpuMilliseconds,
                        accounting.PeakMemoryBytes,
                        accounting.IoBytes,
                        AccountingSource: accounting.AccountingSource),
                ResourceAccountingExpected: OperatingSystem.IsWindows(),
                stdoutBytes,
                stderrBytes);
        }
        finally
        {
            if (heartbeatCts is not null)
            {
                try { await heartbeatCts.CancelAsync().ConfigureAwait(false); } catch { }
                if (heartbeatTask is not null)
                {
                    try { await heartbeatTask.ConfigureAwait(false); } catch { }
                }

                heartbeatCts.Dispose();
            }

            if (startedProcessId is { } processId)
            {
                WorkerProcessJobs.Release(processId);
            }

            if (!keepOutputFiles)
            {
                TryDeleteFile(stdoutPath);
                TryDeleteFile(stderrPath);
            }
        }
    }

    private static async Task WriteGateHeartbeatLoopAsync(GateHeartbeatRuntime heartbeat, CancellationToken cancellationToken)
    {
        heartbeat.WriteRunning(emitProgress: true);
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(HeartbeatInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            heartbeat.WriteRunning(emitProgress: false);
        }
    }

    private static void EmitGateProgress(AcceptanceGateProgress progress)
    {
        var line =
            $"PHASE_PROGRESS goal={FormatNullableToken(progress.GoalId, 8)} phase={progress.Phase} elapsed_ms={(long)progress.Elapsed.TotalMilliseconds} " +
            $"target={QuoteProgressToken(progress.CurrentTarget)} child_pid={progress.ChildProcessId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown"} " +
            $"output_bytes={progress.OutputBytes} heartbeat={QuoteProgressToken(progress.HeartbeatPath)}";
        Console.WriteLine($"{line} ts={progress.LastObservedAt:O}");
        Console.Out.Flush();
        CurrentGateProgressSink.Value?.Invoke(progress);
    }

    private static string FormatNullableToken(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "unknown";
        return value.Length <= maxLength ? value : value[..maxLength];
    }

    private static string QuoteProgressToken(string value) =>
        value.IndexOfAny([' ', '\t', '\r', '\n', '"']) < 0
            ? value
            : $"\"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\"";

    private static string QuoteForDisplay(string value) =>
        value.Contains(' ', StringComparison.Ordinal) || value.Contains('"', StringComparison.Ordinal)
            ? $"\"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\""
            : value;

    private static string BuildRedirectedCommand(
        string[] arguments,
        string stdoutPath,
        string stderrPath,
        Func<string, string> quote)
    {
        var command = string.Join(' ', arguments.Select(quote));
        return $"{command} > {quote(stdoutPath)} 2> {quote(stderrPath)}";
    }

    private static string QuoteForCmd(string value) =>
        $"\"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\"";

    private static string QuoteForPosix(string value) =>
        $"'{value.Replace("'", "'\\''", StringComparison.Ordinal)}'";

    private static async Task<string> ReadFileWithRetryAsync(string path)
    {
        // A reparented grandchild may still hold the file's write handle; open shared and tolerate
        // transient locks. The output we need (the child's own writes) is already flushed on exit.
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                using var stream = new FileStream(
                    path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                return await reader.ReadToEndAsync().ConfigureAwait(false);
            }
            catch (FileNotFoundException)
            {
                return string.Empty;
            }
            catch (IOException)
            {
                await Task.Delay(100).ConfigureAwait(false);
            }
        }

        return string.Empty;
    }

    private static void TryDeleteFile(string path)
    {
        try { File.Delete(path); } catch { /* best effort; lives under the temp dir */ }
    }

    private static long TryGetFileLength(string path)
    {
        try { return File.Exists(path) ? new FileInfo(path).Length : 0L; }
        catch { return 0L; }
    }

    private sealed record PolicyShardPlan(
        bool Applies,
        bool ForceFull,
        string Evidence,
        IReadOnlySet<string> DependencyClosure)
    {
        public static PolicyShardPlan NotApplicable(string evidence) =>
            new(false, false, evidence, new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        public static PolicyShardPlan Full(string evidence, IReadOnlySet<string> dependencyClosure) =>
            new(true, true, evidence, dependencyClosure);

        public static PolicyShardPlan Scoped(string evidence, IReadOnlySet<string> dependencyClosure) =>
            new(true, false, evidence, dependencyClosure);

        public bool IncludesProject(string? project) =>
            ForceFull ||
            !Applies ||
            (!string.IsNullOrWhiteSpace(project) &&
                DependencyClosure.Contains(NormalizePath(project)!));
    }

    private sealed class AcceptanceManifest
    {
        private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

        public int Version { get; init; } = 1;
        public IReadOnlyList<AcceptanceManifestCheck> Checks { get; init; } = [AcceptanceManifestCheck.DefaultDotnetTest];
        public IReadOnlyList<string> ForbiddenChangedPathGlobs { get; init; } = [];

        public static AcceptanceManifest Load(string worktreePath, IReadOnlyList<string>? changedFiles)
        {
            var path = ResolveManifestPath(worktreePath);
            if (!File.Exists(path))
            {
                return changedFiles is null
                    ? new AcceptanceManifest()
                    : FromTestImpactPlan(RepositoryTestImpactPlanner.Plan(changedFiles));
            }

            return JsonSerializer.Deserialize<AcceptanceManifest>(
                File.ReadAllText(path),
                JsonOptions) ?? new AcceptanceManifest();
        }

        private static AcceptanceManifest FromTestImpactPlan(RepositoryTestImpactPlan plan) =>
            new()
            {
                Checks = plan.Checks
                    .Select(ToAcceptanceCheck)
                    .SelectMany(ExpandBroadInfrastructureCheck)
                    .ToArray()
            };

        private static AcceptanceManifestCheck ToAcceptanceCheck(RepositoryTestImpactCheck check)
        {
            if (check.Command.Count == 0)
            {
                return new AcceptanceManifestCheck
                {
                    Name = check.Name,
                    Type = "no-op"
                };
            }

            if (check.Command.Count >= 2 &&
                check.Command[0].Equals("dotnet", StringComparison.OrdinalIgnoreCase) &&
                check.Command[1].Equals("test", StringComparison.OrdinalIgnoreCase))
            {
                var remaining = check.Command.Skip(2).ToArray();
                var project = remaining.Length > 0 && !remaining[0].StartsWith("-", StringComparison.Ordinal)
                    ? remaining[0]
                    : null;
                var arguments = project is null
                    ? remaining
                    : remaining.Skip(1).ToArray();

                return new AcceptanceManifestCheck
                {
                    Name = check.Name,
                    Type = "dotnet-test",
                    Project = project,
                    Arguments = arguments
                };
            }

            return new AcceptanceManifestCheck
            {
                Name = check.Name,
                Type = "command",
                Command = check.Command[0],
                Arguments = check.Command.Skip(1).ToArray()
            };
        }

        private static string ResolveManifestPath(string worktreePath)
        {
            var trackedPath = Path.Combine(worktreePath, "config", "acceptance-manifest.json");
            if (File.Exists(trackedPath))
            {
                return trackedPath;
            }

            return Path.Combine(worktreePath, ".orchestrator", "acceptance-manifest.json");
        }
    }

    private static readonly JsonSerializerOptions CriteriaJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private sealed class AcceptanceManifestCheck
    {
        public static AcceptanceManifestCheck DefaultDotnetTest { get; } = new()
        {
            Name = "dotnet test",
            Type = "dotnet-test"
        };

        public string Name { get; init; } = "acceptance check";
        public string Type { get; init; } = "command";
        public string? Command { get; init; }
        public string? Project { get; init; }
        public IReadOnlyList<string> Arguments { get; init; } = [];
        public string? Pattern { get; init; }
        public string? FilePath { get; init; }
        public int? TimeoutMinutes { get; init; }
        public bool Advisory { get; init; }
        public string? Runner { get; init; } = "vstest";
    }

    private sealed record InfrastructureTestLane(string Name, string Filter);

    private sealed record DotnetTestTelemetry(IReadOnlyList<string> Paths, string[] Arguments);

    private sealed record DotnetBaseBuildCachePlan(
        string MainSha,
        IReadOnlyList<string> RestoreProjects,
        IReadOnlyList<string> BuildProjects);

    private sealed record PartitionVerdictJournalSnapshot(
        IReadOnlyList<PartitionVerdictRecord> Records,
        IReadOnlyList<PartitionVerdictJournalSummary> Summaries);

    private sealed record PartitionVerdictJournalSummary(
        string PairKey,
        int ReuseAttemptCount,
        bool ForcedFullRerun,
        DateTimeOffset RecordedAt);

    private sealed record PartitionVerdictJournalEntry(
        string IdempotencyKey,
        GoalId GoalId,
        string Operation,
        string Status,
        DateTimeOffset At,
        string? Detail = null,
        string? BranchHeadSha = null,
        string? MainHeadSha = null,
        string? AcceptanceOutcome = null,
        string? PartitionVerdictCacheKey = null,
        string? PartitionPairKey = null,
        string? PartitionId = null,
        string? PartitionFilterHash = null,
        string? PartitionVerdict = null,
        string? PartitionAttemptId = null,
        IReadOnlyList<string>? PartitionTestResultPaths = null,
        int? PartitionReuseAttemptCount = null,
        bool? PartitionForcedFullRerun = null);

    private sealed record PartitionVerdictRecord(
        string GoalId,
        string AttemptId,
        string CandidateTreeSha,
        string MainSha,
        string PartitionFilterHash,
        string PartitionId,
        string CacheKey,
        bool Passed,
        string Verdict,
        IReadOnlyList<string> TestResultPaths,
        DateTimeOffset RecordedAt);

    private sealed record PartitionVerdictReuseReceipt(
        string PartitionId,
        string SourceAttemptId,
        string CacheKey);

    private sealed record PartitionVerdictExecutionReceipt(
        string PartitionId,
        string Verdict);

    private sealed class PartitionVerdictCacheContext(
        string goalId,
        string candidateTreeSha,
        string mainSha,
        string verifyingCommitSha,
        string attemptId,
        string pairKey,
        string journalPath,
        PartitionVerdictJournalSnapshot journal,
        int partitionCount,
        int priorReuseAttemptCount,
        bool forceFullRerun)
    {
        public string GoalId { get; } = goalId;
        public string CandidateTreeSha { get; } = candidateTreeSha;
        public string MainSha { get; } = mainSha;
        public string VerifyingCommitSha { get; } = verifyingCommitSha;
        public string AttemptId { get; } = attemptId;
        public string PairKey { get; } = pairKey;
        public string JournalPath { get; } = journalPath;
        public int PartitionCount { get; } = partitionCount;
        public int PriorReuseAttemptCount { get; } = priorReuseAttemptCount;
        public bool ForceFullRerun { get; } = forceFullRerun;
        public List<PartitionVerdictReuseReceipt> Reused { get; } = [];
        public List<PartitionVerdictExecutionReceipt> Executed { get; } = [];
        public List<PartitionVerdictRecord> FreshRecords { get; } = [];

        public PartitionVerdictRecord? TryGetGreen(string cacheKey) =>
            LatestGreenPartitionVerdict(journal, GoalId, cacheKey);
    }

    private sealed class DotnetTestBuildPhase(string[] buildArguments, DotnetBaseBuildCachePlan? cachePlan)
    {
        public string[] BuildArguments { get; } = buildArguments;
        public DotnetBaseBuildCachePlan? CachePlan { get; } = cachePlan;
        public (AcceptanceCheckResult Result, bool Retried)? Run { get; set; }
    }

    private readonly record struct DotnetTestBuildPhaseResult(
        (AcceptanceCheckResult Result, bool Retried) Run,
        bool ContributesToCheck);

    private readonly record struct TransientBuildLockWaitResult(long WaitedMilliseconds, bool Released);

    private sealed record GateHeartbeatContext(
        string? GoalId,
        string Phase,
        string CurrentTarget,
        int? SlotIndex,
        string HeartbeatPath,
        string CommandLine);

    private sealed class GateHeartbeatRuntime
    {
        private readonly GateHeartbeatContext _context;
        private readonly int _processId;
        private readonly string _stdoutPath;
        private readonly string _stderrPath;
        private readonly TimeSpan _timeout;
        private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;
        private DateTimeOffset _lastProgressAt;
        private DateTimeOffset _lastProgressEmittedAt = DateTimeOffset.MinValue;
        private long _lastOutputBytes = -1;

        public GateHeartbeatRuntime(
            GateHeartbeatContext context,
            int processId,
            string stdoutPath,
            string stderrPath,
            TimeSpan timeout)
        {
            _context = context;
            _processId = processId;
            _stdoutPath = stdoutPath;
            _stderrPath = stderrPath;
            _timeout = timeout;
            _lastProgressAt = _startedAt;
        }

        public void WriteRunning(bool emitProgress)
        {
            var snapshot = BuildSnapshot("running", childPid: _processId);
            GateHeartbeatArtifacts.TryWrite(_context.HeartbeatPath, snapshot);
            if (emitProgress || snapshot.LastObservedAt - _lastProgressEmittedAt >= ProgressInterval)
            {
                _lastProgressEmittedAt = snapshot.LastObservedAt;
                EmitGateProgress(ToProgress(snapshot));
            }
        }

        public void WriteFinal(string state, int? childPid, int? exitCode)
        {
            var snapshot = BuildSnapshot(state, childPid, exitCode);
            GateHeartbeatArtifacts.TryWrite(_context.HeartbeatPath, snapshot);
        }

        private GateHeartbeatSnapshot BuildSnapshot(string state, int? childPid, int? exitCode = null)
        {
            var now = DateTimeOffset.UtcNow;
            var stdoutBytes = TryGetLength(_stdoutPath);
            var stderrBytes = TryGetLength(_stderrPath);
            var outputBytes = stdoutBytes + stderrBytes;
            if (outputBytes != _lastOutputBytes)
            {
                _lastOutputBytes = outputBytes;
                _lastProgressAt = now;
            }

            return new GateHeartbeatSnapshot(
                _context.GoalId,
                _context.Phase,
                _context.CurrentTarget,
                _context.SlotIndex,
                _processId,
                childPid,
                state,
                _startedAt,
                now,
                _lastProgressAt,
                stdoutBytes,
                stderrBytes,
                outputBytes,
                _context.CommandLine,
                exitCode,
                _stdoutPath,
                _stderrPath);
        }

        private AcceptanceGateProgress ToProgress(GateHeartbeatSnapshot snapshot) =>
            new(
                snapshot.GoalId,
                snapshot.Phase,
                snapshot.CurrentTarget,
                snapshot.SlotIndex,
                snapshot.ProcessId,
                snapshot.ChildPid,
                snapshot.StartedAt,
                snapshot.LastObservedAt,
                snapshot.LastProgressAt,
                Positive(snapshot.LastObservedAt - snapshot.StartedAt),
                snapshot.OutputBytes,
                _context.HeartbeatPath);

        private static long TryGetLength(string path)
        {
            try
            {
                return File.Exists(path) ? new FileInfo(path).Length : 0L;
            }
            catch
            {
                return 0L;
            }
        }

        private static TimeSpan Positive(TimeSpan value) =>
            value < TimeSpan.Zero ? TimeSpan.Zero : value;
    }

    private sealed class RestoreAction(Action restore) : IDisposable
    {
        public void Dispose() => restore();
    }
}
