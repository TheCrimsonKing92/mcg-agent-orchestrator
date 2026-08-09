using System.Diagnostics;
using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
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
    IReadOnlyList<string>? TestResultPaths = null,
    string? FailureClassification = null,
    string? TestResultAttemptId = null,
    int TestResultRunOrdinal = 0,
    bool TestResultIsExplicitCrossAttemptReuse = false,
    IReadOnlyList<string>? FailingTestIdentities = null);

public static class AcceptanceFailureClassifications
{
    public const string GateEnvironmentInterference = "gate-environment-interference";
    public const string StructuralCoverageFailed = "structural-coverage-failed";
}

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
    IReadOnlyList<AcceptanceCheckResult> Checks,
    FocusedEvidenceCoverage? Coverage = null,
    IReadOnlyList<FocusedEvidenceArmRunResult>? Arms = null,
    FindingEvidenceOutcomeReason? OutcomeReason = null)
{
    public bool IsValidEvidence =>
        Accepted &&
        Passed &&
        OutcomeReason is null or FindingEvidenceOutcomeReason.ValidEvidence;
}

public sealed record FocusedEvidenceArmRunResult(
    FindingEvidenceArm Arm,
    string Sha,
    FindingEvidenceArmDisposition Disposition,
    bool Accepted,
    bool Passed,
    string Summary,
    IReadOnlyList<AcceptanceCheckResult> Checks);

public sealed record FocusedEvidenceTargetCoverage(
    string Target,
    IReadOnlyList<string> CheckNames);

public sealed record FocusedEvidenceCoverage(
    IReadOnlyList<FocusedEvidenceTargetCoverage> TargetToChecks,
    string ExecutionMode = "focused",
    string ExecutionReason = "explicit-focused-mapping");

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
        bool runBaselineArm = false,
        CancellationToken cancellationToken = default);
}

public sealed class GoalAcceptanceVerifier : IGoalAcceptanceVerifier
{
    public sealed record StartupContract(
        int ManifestCheckCount,
        IReadOnlyList<string> InfrastructureLaneNames);

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
        @"^\s*\[\s*(?:Xunit\.)?(?:Fact|Theory)\s*(?:\(|,|\])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex TautologyPattern = new(
        @"Assert\.True\(\s*true\s*\)|Assert\.False\(\s*false\s*\)|Assert\.Equal\(\s*(?<v>\w+)\s*,\s*\k<v>\s*\)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);
    private static readonly Encoding StrictUtf16LittleEndian = new UnicodeEncoding(false, true, true);
    private static readonly Encoding StrictUtf16BigEndian = new UnicodeEncoding(true, true, true);
    private static readonly Encoding StrictUtf32LittleEndian = new UTF32Encoding(false, true, true);
    private static readonly Encoding StrictUtf32BigEndian = new UTF32Encoding(true, true, true);

    private static readonly string[] DiffBaseArgs = ["git", "diff", "--unified=0", "main...HEAD", "--"];
    private const string CoreProject = "src/Mcg.AgentOrchestrator.Core/Mcg.AgentOrchestrator.Core.csproj";
    private const string InfrastructureProject = "src/Mcg.AgentOrchestrator.Infrastructure/Mcg.AgentOrchestrator.Infrastructure.csproj";
    private const string AppProject = "src/Mcg.AgentOrchestrator.App/Mcg.AgentOrchestrator.App.csproj";
    private const string CoreTestsProject = "tests/Mcg.AgentOrchestrator.Core.Tests/Mcg.AgentOrchestrator.Core.Tests.csproj";
    private const string InfrastructureTestsProject = "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj";
    internal const string FocusedEvidenceSupportedProjectForms =
        "Core, Core.Tests, Mcg.AgentOrchestrator.Core.Tests, Infrastructure, Infrastructure.Tests, " +
        "Mcg.AgentOrchestrator.Infrastructure.Tests, or a full .csproj path ending in " +
        "tests/Mcg.AgentOrchestrator.Core.Tests/Mcg.AgentOrchestrator.Core.Tests.csproj or " +
        "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj";
    private const string PartitionVerdictJournalOperation = "acceptance:partition-verdict";
    private const string PartitionVerdictCacheJournalOperation = "acceptance:partition-verdict-cache";
    private const int FocusedEvidenceShortTimeoutTargetLimit = 4;
    public const string AcceptanceAttemptTrxPrefixVariable = "MCG_ACCEPTANCE_GATE_ATTEMPT_TRX_PREFIX";

    private static readonly Dictionary<string, string[]> ReferencingProjectsByProject = new(StringComparer.OrdinalIgnoreCase)
    {
        [CoreProject] = [InfrastructureProject, AppProject, CoreTestsProject, InfrastructureTestsProject],
        [InfrastructureProject] = [AppProject, InfrastructureTestsProject],
        [AppProject] = [InfrastructureTestsProject],
        [CoreTestsProject] = [],
        [InfrastructureTestsProject] = []
    };

    public static StartupContract ValidateStartupContract(string repositoryRoot)
    {
        var manifest = AcceptanceManifest.Load(repositoryRoot, changedFiles: null);
        if (manifest.Checks.Count == 0)
        {
            throw new InvalidOperationException("Acceptance manifest loaded without any checks.");
        }
        foreach (var check in manifest.Checks)
        {
            var type = check.Type.Trim().ToLowerInvariant();
            if (type == "command")
            {
                _ = BuildCommandArguments(check);
            }
            else if (type == "dotnet-test")
            {
                _ = BuildDotnetTestArguments(check);
            }
            else if (type is not ("no-op" or "grep-absent" or "grep-present" or "file-exists" or "command-exit"))
            {
                throw new InvalidOperationException(
                    $"Acceptance manifest check '{check.Name}' has unsupported type '{check.Type}'.");
            }
        }

        var laneNames = AcceptanceGateEngineSettings.Load(repositoryRoot)
            .InfrastructureTestLanes
            .Select(lane => lane.Name)
            .ToArray();
        if (laneNames.Length == 0 || laneNames.Any(string.IsNullOrWhiteSpace))
        {
            throw new InvalidOperationException("Acceptance infrastructure lanes could not be enumerated.");
        }

        return new StartupContract(manifest.Checks.Count, laneNames);
    }
    private readonly Func<string[], string, TimeSpan, CancellationToken, Task<CommandResult>> _runner;
    private readonly TimeProvider _timeProvider;
    private readonly Action<TimeSpan> _leaseSleep;
    private static readonly AsyncLocal<GateHeartbeatContext?> CurrentGateHeartbeatContext = new();
    private static readonly AsyncLocal<Action<AcceptanceGateProgress>?> CurrentGateProgressSink = new();
    private static readonly AsyncLocal<Func<bool>?> CurrentGateCancellationProbe = new();
    private static readonly AsyncLocal<AcceptanceGateEngineSettings?> CurrentGateEngineSettings = new();
    private static readonly AsyncLocal<string?> CurrentAcceptanceAttemptPrefix = new();
    private static readonly AsyncLocal<ManagedRunEnvironmentScope?> CurrentManagedRunEnvironmentScope = new();
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
    private static readonly TimeSpan CaptureDrainTimeout = TimeSpan.FromSeconds(12);
    private const int CappedOutputPreviewBytes = 64 * 1024;
    internal static Func<string, string?>? ResolveBaseBuildMainShaForTests { get; set; }
    internal static Func<string, string?>? ResolvePartitionVerdictCandidateTreeShaForTests { get; set; }
    internal static Func<string, string?>? ResolvePartitionVerdictMainShaForTests { get; set; }
    internal static Func<string, string?>? ResolvePartitionVerdictVerifyingCommitShaForTests { get; set; }
    internal static Func<string, string?>? ResolveMainWorktreePathForTests { get; set; }
    internal static Func<string, string[]>? ResolveDeletedTestFilesForTests { get; set; }
    internal static Func<int>? ResolveShardCoreBudgetForTests { get; set; }
    internal static Action<string>? OnInfrastructureShardResourcesAcquiredForTests { get; set; }
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

    public static IDisposable PushAcceptanceAttemptResultsPrefix(string prefix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        var previous = CurrentAcceptanceAttemptPrefix.Value;
        CurrentAcceptanceAttemptPrefix.Value = Path.GetFullPath(prefix);
        return new RestoreAction(() => CurrentAcceptanceAttemptPrefix.Value = previous);
    }

    private static ManagedRunEnvironmentScope PushManagedRunEnvironmentScope()
    {
        var scope = new ManagedRunEnvironmentScope(CurrentManagedRunEnvironmentScope.Value);
        CurrentManagedRunEnvironmentScope.Value = scope;
        return scope;
    }

    private static string? AcceptanceAttemptResultsPrefix =>
        CurrentAcceptanceAttemptPrefix.Value ??
        Environment.GetEnvironmentVariable(AcceptanceAttemptTrxPrefixVariable);

    internal static string? AcceptanceAttemptResultsPrefixForTests =>
        AcceptanceAttemptResultsPrefix;

    private static IDisposable PushEngineSettings(AcceptanceGateEngineSettings settings)
    {
        var previous = CurrentGateEngineSettings.Value;
        CurrentGateEngineSettings.Value = settings;
        return new RestoreAction(() => CurrentGateEngineSettings.Value = previous);
    }

    private static AcceptanceGateEngineSettings EngineSettings =>
        CurrentGateEngineSettings.Value ?? new AcceptanceGateEngineSettings();

    public async Task<AcceptanceVerificationResult> RunAsync(
        string worktreePath,
        GoalId? goalId = null,
        IReadOnlyList<string>? changedFiles = null,
        int? stableSlotIndex = null,
        DotnetBuildEnvironmentLease? stableSlotLease = null,
        CancellationToken cancellationToken = default)
    {
        var engineSettings = AcceptanceGateEngineSettings.Load(worktreePath);
        using var engineScope = PushEngineSettings(engineSettings);
        using var resultsScope = PushOwnerResultsScope(worktreePath, goalId, "gate");
        using var runEnvironmentScope = PushManagedRunEnvironmentScope();
        if (TryClassifyManifestTrust(worktreePath, changedFiles) is { } manifestTrustFailure)
        {
            return new AcceptanceVerificationResult(
                Passed: false,
                Skipped: false,
                ExitCode: manifestTrustFailure.ExitCode,
                OutputTail: manifestTrustFailure.OutputTail,
                Checks: [manifestTrustFailure]);
        }

        // Shut down build servers to release file locks before running tests.
        await _runner(
            ["dotnet", "build-server", "shutdown"],
            worktreePath,
            engineSettings.ResolveBuildServerShutdownTimeout(),
            cancellationToken).ConfigureAwait(false);

        var manifest = AcceptanceManifest.Load(worktreePath, changedFiles);
        var shardCoreBudget =
            ResolveShardCoreBudgetForTests?.Invoke() ?? Math.Max(1, Environment.ProcessorCount / 2);
        var shardConcurrencyBudget = Math.Min(
            engineSettings.MaxConcurrentShards,
            shardCoreBudget);
        var infrastructureTestLanes = engineSettings.InfrastructureTestLanes;
        var policyShardPlan = BuildPolicyShardPlan(changedFiles);

        // Apply policy-required checks from the change scope. Focused project checks replace
        // matching unfiltered project checks so a narrow App change does not still run the full
        // Infrastructure project suite from the tracked manifest.
        var policyRequiredChecks = BuildRequiredPolicyChecks(changedFiles);
        var policyEffectiveChecks = BuildPolicyEffectiveChecks(
            manifest.Checks,
            changedFiles,
            policyRequiredChecks,
            policyShardPlan,
            infrastructureTestLanes);
        var structuralCoverageApplies = StructuralCoverageApplies(engineSettings, changedFiles);
        var structurallyCompleteChecks = structuralCoverageApplies
            ? EnsureTrustedStructuralCoverageExecutionChecks(
                policyEffectiveChecks,
                manifest.Checks,
                worktreePath)
            : policyEffectiveChecks;
        var effectiveChecks = ExpandBroadInfrastructureChecks(structurallyCompleteChecks, infrastructureTestLanes);
        var partitionVerdictCache = CreatePartitionVerdictCacheContext(
            worktreePath,
            goalId,
            effectiveChecks,
            engineSettings.PartitionVerdictFullRerunEveryN);
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

        var scopedChecks = BuildChangeScopedChecks(solutionCheck, effectiveChecks, changedFiles, policyShardPlan, infrastructureTestLanes);
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
                var batch = await RunCheckBatchAsync(
                    effectiveChecks.Where(c =>
                        c.Type.Equals("dotnet-test", StringComparison.OrdinalIgnoreCase) &&
                        scopedNames.Contains(c.Name)).ToArray(),
                    partitionVerdictCache,
                    worktreePath,
                    goalId,
                    stableSlotIndex,
                    stableSlotLease,
                    dotnetTestBuildPhase,
                    shardConcurrencyBudget,
                    cancellationToken).ConfigureAwait(false);
                checks.AddRange(batch.Results);
                retried |= batch.Retried;
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
                    var batch = await RunCheckBatchAsync(
                        effectiveChecks.Where(c =>
                            c.Type.Equals("dotnet-test", StringComparison.OrdinalIgnoreCase) &&
                            !c.Name.Equals(solutionCheck!.Name, StringComparison.Ordinal) &&
                            !deferredNames.Contains(c.Name)).ToArray(),
                        partitionVerdictCache,
                        worktreePath,
                        goalId,
                        stableSlotIndex,
                        stableSlotLease,
                        dotnetTestBuildPhase,
                        shardConcurrencyBudget,
                        cancellationToken).ConfigureAwait(false);
                    checks.AddRange(batch.Results);
                    retried |= batch.Retried;
                }
            }
        }
        else
        {
            var batch = await RunCheckBatchAsync(
                effectiveChecks,
                partitionVerdictCache,
                worktreePath,
                goalId,
                stableSlotIndex,
                stableSlotLease,
                dotnetTestBuildPhase,
                shardConcurrencyBudget,
                cancellationToken).ConfigureAwait(false);
            checks.AddRange(batch.Results);
            retried |= batch.Retried;
        }

        if (FinalizePartitionVerdictCache(partitionVerdictCache, checks) is { } partitionCacheReceipt)
        {
            checks.Add(partitionCacheReceipt);
        }

        AddCoveredBroadInfrastructureResults(checks, policyRequiredChecks, changedFiles, infrastructureTestLanes);
        AddCoveredBroadInfrastructureResults(checks, manifest.Checks, changedFiles, infrastructureTestLanes);
        AddCoveredPolicyAliasResults(checks, policyRequiredChecks, effectiveChecks);
        AddPolicyShardReceiptResults(checks, manifest.Checks, effectiveChecks, policyShardPlan);

        if (checks.All(check => check.Passed) && manifest.ForbiddenChangedPathGlobs.Count > 0)
        {
            checks.Add(await RunForbiddenChangedPathsCheckAsync(manifest.ForbiddenChangedPathGlobs, worktreePath, cancellationToken).ConfigureAwait(false));
        }

        if (checks.All(check => check.Passed) && structuralCoverageApplies)
        {
            checks.Add(await RunStructuralCoverageCheckAsync(
                effectiveChecks,
                checks,
                worktreePath,
                changedFiles,
                stableSlotIndex,
                stableSlotLease,
                partitionVerdictCache?.AttemptId,
                cancellationToken).ConfigureAwait(false));
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

        var verification = new AcceptanceVerificationResult(
            Passed: failedCheck is null,
            Skipped: false,
            ExitCode: failedCheck?.ExitCode ?? 0,
            OutputTail: failedCheck?.OutputTail,
            Retried: retried,
            ArtifactsPath: artifactsPath,
            Checks: checks,
            TestResultPaths: testResultPaths);
        if (verification.Passed)
        {
            runEnvironmentScope.MarkSuccessful();
        }

        return verification;
    }

    public async Task<FocusedEvidenceRunResult> RunFocusedEvidenceAsync(
        string worktreePath,
        GoalId? goalId,
        string request,
        int? stableSlotIndex = null,
        DotnetBuildEnvironmentLease? stableSlotLease = null,
        bool runBaselineArm = false,
        CancellationToken cancellationToken = default)
    {
        var engineSettings = AcceptanceGateEngineSettings.Load(worktreePath);
        using var engineScope = PushEngineSettings(engineSettings);
        using var resultsScope = PushOwnerResultsScope(worktreePath, goalId, "pre-review");
        using var runEnvironmentScope = PushManagedRunEnvironmentScope();

        if (!TryBuildFocusedEvidenceChecks(
                request,
                out var focusedChecks,
                out var coverage,
                out var rejection))
        {
            return new FocusedEvidenceRunResult(
                request,
                Accepted: false,
                Passed: false,
                Summary: rejection,
                Checks: []);
        }

        var candidateSha = ResolveGitScalar(worktreePath, "rev-parse", "HEAD") ?? "unavailable";
        FocusedEvidenceArmRunResult candidate;
        if (runBaselineArm)
        {
            EmitFocusedEvidenceArmStarted(FindingEvidenceArm.Candidate, candidateSha);
        }

        try
        {
            candidate = await RunFocusedEvidenceArmAsync(
                FindingEvidenceArm.Candidate,
                candidateSha,
                worktreePath,
                goalId,
                focusedChecks,
                coverage,
                stableSlotIndex,
                stableSlotLease,
                executionEnvironment: null,
                shutdownBuildServers: true,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (runBaselineArm)
        {
            EmitFocusedEvidenceArmFailed(FindingEvidenceArm.Candidate, candidateSha, ex);
            throw;
        }

        if (runBaselineArm)
        {
            EmitFocusedEvidenceArmResolved(candidate);
        }

        if (!runBaselineArm)
        {
            if (candidate.Disposition == FindingEvidenceArmDisposition.Green)
            {
                runEnvironmentScope.MarkSuccessful();
            }

            return new FocusedEvidenceRunResult(
                request,
                Accepted: true,
                Passed: candidate.Disposition == FindingEvidenceArmDisposition.Green,
                Summary: candidate.Summary,
                Checks: candidate.Checks,
                Coverage: coverage,
                Arms: [candidate]);
        }

        var baselineSha = ResolveGitScalar(worktreePath, "merge-base", "HEAD", "main");
        EmitFocusedEvidenceArmStarted(FindingEvidenceArm.Baseline, baselineSha ?? "unavailable");
        FocusedEvidenceArmRunResult baseline;
        try
        {
            baseline = await RunBaselineFocusedEvidenceArmAsync(
                worktreePath,
                baselineSha,
                goalId,
                focusedChecks,
                coverage,
                stableSlotIndex,
                stableSlotLease,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            EmitFocusedEvidenceArmFailed(FindingEvidenceArm.Baseline, baselineSha ?? "unavailable", ex);
            throw;
        }

        EmitFocusedEvidenceArmResolved(baseline);
        var outcomeReason = ClassifyFocusedEvidenceExperiment(candidate, baseline);
        EmitFocusedEvidenceClassification(candidate, baseline, outcomeReason);
        var summary =
            $"focused evidence {FindingEvidenceOutcomeReasonJsonConverter.ToWireValue(outcomeReason)}; " +
            $"candidate={ArmDispositionWireValue(candidate.Disposition)} ({candidate.Summary}); " +
            $"baseline={ArmDispositionWireValue(baseline.Disposition)} ({baseline.Summary})";
        var evidence = new FocusedEvidenceRunResult(
            request,
            Accepted: true,
            Passed: candidate.Disposition == FindingEvidenceArmDisposition.Green,
            Summary: summary,
            Checks: candidate.Checks,
            Coverage: coverage,
            Arms: [candidate, baseline],
            OutcomeReason: outcomeReason);
        if (candidate.Disposition == FindingEvidenceArmDisposition.Green)
        {
            runEnvironmentScope.MarkSuccessful();
        }

        return evidence;
    }

    private async Task<FocusedEvidenceArmRunResult> RunFocusedEvidenceArmAsync(
        FindingEvidenceArm arm,
        string sha,
        string worktreePath,
        GoalId? goalId,
        IReadOnlyList<AcceptanceManifestCheck> focusedChecks,
        FocusedEvidenceCoverage coverage,
        int? stableSlotIndex,
        DotnetBuildEnvironmentLease? stableSlotLease,
        DotnetBuildEnvironment? executionEnvironment,
        bool shutdownBuildServers,
        CancellationToken cancellationToken)
    {
        using var armResultsScope = PushFocusedEvidenceArmResultsScope(arm);
        var engineSettings = EngineSettings;
        if (shutdownBuildServers)
        {
            await _runner(
                ["dotnet", "build-server", "shutdown"],
                worktreePath,
                engineSettings.ResolveBuildServerShutdownTimeout(),
                cancellationToken).ConfigureAwait(false);
        }

        var dotnetTestBuildPhase = CreateDotnetTestBuildPhase(
            worktreePath,
            focusedChecks,
            changedFiles: null,
            PolicyShardPlan.NotApplicable("focused evidence"));
        dotnetTestBuildPhase.BuildEnvironment = executionEnvironment;
        var shardCoreBudget =
            ResolveShardCoreBudgetForTests?.Invoke() ?? Math.Max(1, Environment.ProcessorCount / 2);
        var shardConcurrencyBudget = Math.Min(engineSettings.MaxConcurrentShards, shardCoreBudget);
        var batch = await RunCheckBatchAsync(
            focusedChecks,
            cacheContext: null,
            worktreePath,
            goalId,
            stableSlotIndex,
            stableSlotLease,
            dotnetTestBuildPhase,
            shardConcurrencyBudget,
            cancellationToken).ConfigureAwait(false);
        var checks = batch.Results;
        var disposition = ClassifyFocusedEvidenceArm(checks);
        var failed = checks.FirstOrDefault(check => !check.Passed);
        var receiptPaths = checks
            .SelectMany(check => check.TestResultPaths ?? [])
            .Concat(checks.Select(check => check.ArtifactsPath ?? string.Empty))
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var planSummary = FormatFocusedEvidencePlanSummary(coverage);
        var summary = failed is null
            ? $"{checks.Count} check(s) passed; {planSummary}; receipts: {FormatReceiptPaths(receiptPaths)}"
            : $"{failed.Name} exit {failed.ExitCode}; {planSummary}; receipts: {FormatReceiptPaths(receiptPaths)}";
        return new FocusedEvidenceArmRunResult(
            arm,
            sha,
            disposition,
            Accepted: true,
            Passed: disposition == FindingEvidenceArmDisposition.Green,
            summary,
            checks);
    }

    private async Task<FocusedEvidenceArmRunResult> RunBaselineFocusedEvidenceArmAsync(
        string candidateWorktreePath,
        string? baselineSha,
        GoalId? goalId,
        IReadOnlyList<AcceptanceManifestCheck> focusedChecks,
        FocusedEvidenceCoverage coverage,
        int? stableSlotIndex,
        DotnetBuildEnvironmentLease? stableSlotLease,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(baselineSha))
        {
            return InconclusiveBaseline("baseline merge-base could not be resolved");
        }

        var baselineRoot = Path.Combine(Path.GetTempPath(), "mcg-focused-evidence-baselines");
        Directory.CreateDirectory(baselineRoot);
        var baselinePath = Path.Combine(
            baselineRoot,
            $"{(goalId?.Value ?? "operator")[..Math.Min(8, (goalId?.Value ?? "operator").Length)]}-{Guid.NewGuid():N}");
        var add = GitCli.Run(
            candidateWorktreePath,
            "worktree", "add", "--detach", baselinePath, baselineSha);
        if (!add.Succeeded)
        {
            TryDeleteFocusedEvidenceBaselineDirectory(baselineRoot, baselinePath);
            return InconclusiveBaseline(
                $"baseline worktree could not be created: {TrimForReceipt(add.Error)}",
                baselineSha);
        }

        DotnetBuildEnvironment? baselineEnvironment = null;
        GoalId? baselineEnvironmentId = null;
        try
        {
            // The baseline owns a fresh artifact environment. Sharing candidate artifacts could make
            // a structurally broken baseline look like a meaningful RED arm.
            baselineEnvironmentId = GoalId.New();
            baselineEnvironment = DotnetBuildEnvironmentManager.CreateAttempt(
                baselineEnvironmentId,
                $"focused-evidence-baseline-{baselineSha[..Math.Min(8, baselineSha.Length)]}");
            return await RunFocusedEvidenceArmAsync(
                FindingEvidenceArm.Baseline,
                baselineSha,
                baselinePath,
                // Ownerless execution gets an invocation-local build environment. Passing the goal
                // id here would reuse candidate artifacts and invalidate the negative control.
                null,
                focusedChecks,
                coverage,
                stableSlotIndex,
                stableSlotLease,
                baselineEnvironment,
                shutdownBuildServers: true,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _ = GitCli.Run(candidateWorktreePath, "worktree", "remove", "--force", baselinePath);
            TryDeleteFocusedEvidenceBaselineDirectory(baselineRoot, baselinePath);
            if (baselineEnvironment is not null)
            {
                DotnetBuildEnvironmentManager.TryCleanupSuccessfulRun(baselineEnvironment);
            }

            if (baselineEnvironmentId is not null)
            {
                DotnetBuildEnvironmentManager.TryDeleteGoalArtifacts(baselineEnvironmentId);
            }
        }
    }

    private static FindingEvidenceArmDisposition ClassifyFocusedEvidenceArm(
        IReadOnlyList<AcceptanceCheckResult> checks)
    {
        if (checks.Count > 0 && checks.All(check => check.Passed))
        {
            return FindingEvidenceArmDisposition.Green;
        }

        return checks
            .Where(check => !check.Passed)
            .Any(check => check.FailingTestIdentities is { Count: > 0 })
                ? FindingEvidenceArmDisposition.Red
                : FindingEvidenceArmDisposition.Inconclusive;
    }

    private static FindingEvidenceOutcomeReason ClassifyFocusedEvidenceExperiment(
        FocusedEvidenceArmRunResult candidate,
        FocusedEvidenceArmRunResult baseline) =>
        candidate.Disposition switch
        {
            FindingEvidenceArmDisposition.Red => FindingEvidenceOutcomeReason.CandidateRed,
            FindingEvidenceArmDisposition.Inconclusive => FindingEvidenceOutcomeReason.CandidateInconclusive,
            _ => baseline.Disposition switch
            {
                FindingEvidenceArmDisposition.Green => FindingEvidenceOutcomeReason.VacuousEvidence,
                FindingEvidenceArmDisposition.Red => FindingEvidenceOutcomeReason.ValidEvidence,
                _ => FindingEvidenceOutcomeReason.BaselineInconclusive
            }
        };

    private static void EmitFocusedEvidenceArmStarted(FindingEvidenceArm arm, string sha) =>
        EmitFocusedEvidenceDiagnostic(
            $"FOCUSED_EVIDENCE_ARM arm={arm.ToString().ToLowerInvariant()} state=started sha={QuoteProgressToken(sha)}");

    private static void EmitFocusedEvidenceArmResolved(FocusedEvidenceArmRunResult arm)
    {
        var failingTestCount = arm.Checks.Sum(check => check.FailingTestIdentities?.Count ?? 0);
        var failed = arm.Checks.FirstOrDefault(check => !check.Passed);
        var failureDetail = failed?.OutputTail is { Length: > 0 } outputTail
            ? $" detail={QuoteProgressToken(TrimForReceipt(outputTail))}"
            : string.Empty;
        EmitFocusedEvidenceDiagnostic(
            $"FOCUSED_EVIDENCE_ARM arm={arm.Arm.ToString().ToLowerInvariant()} state=resolved " +
            $"sha={QuoteProgressToken(arm.Sha)} disposition={ArmDispositionWireValue(arm.Disposition)} " +
            $"accepted={arm.Accepted.ToString().ToLowerInvariant()} passed={arm.Passed.ToString().ToLowerInvariant()} " +
            $"checks={arm.Checks.Count} failing_tests={failingTestCount} summary={QuoteProgressToken(arm.Summary)}" +
            failureDetail);
    }

    private static void EmitFocusedEvidenceArmFailed(
        FindingEvidenceArm arm,
        string sha,
        Exception exception) =>
        EmitFocusedEvidenceDiagnostic(
            $"FOCUSED_EVIDENCE_ARM arm={arm.ToString().ToLowerInvariant()} state=failed " +
            $"sha={QuoteProgressToken(sha)} exception={exception.GetType().Name} " +
            $"detail={QuoteProgressToken(TrimForReceipt(exception.Message))}");

    private static void EmitFocusedEvidenceClassification(
        FocusedEvidenceArmRunResult candidate,
        FocusedEvidenceArmRunResult baseline,
        FindingEvidenceOutcomeReason outcomeReason) =>
        EmitFocusedEvidenceDiagnostic(
            $"FOCUSED_EVIDENCE_EXPERIMENT state=classified " +
            $"outcome={FindingEvidenceOutcomeReasonJsonConverter.ToWireValue(outcomeReason)} " +
            $"candidate={ArmDispositionWireValue(candidate.Disposition)} " +
            $"baseline={ArmDispositionWireValue(baseline.Disposition)}");

    private static void EmitFocusedEvidenceDiagnostic(string line)
    {
        Console.WriteLine(line);
        Console.Out.Flush();
    }

    private static FocusedEvidenceArmRunResult InconclusiveBaseline(string summary, string sha = "unavailable") =>
        new(
            FindingEvidenceArm.Baseline,
            sha,
            FindingEvidenceArmDisposition.Inconclusive,
            Accepted: false,
            Passed: false,
            summary,
            Checks: []);

    private static IDisposable PushFocusedEvidenceArmResultsScope(FindingEvidenceArm arm)
    {
        var prefix = AcceptanceAttemptResultsPrefix;
        return string.IsNullOrWhiteSpace(prefix)
            ? new RestoreAction(static () => { })
            : PushAcceptanceAttemptResultsPrefix($"{prefix}-{arm.ToString().ToLowerInvariant()}");
    }

    private static string ArmDispositionWireValue(FindingEvidenceArmDisposition disposition) =>
        disposition switch
        {
            FindingEvidenceArmDisposition.Green => "green",
            FindingEvidenceArmDisposition.Red => "red",
            _ => "inconclusive"
        };

    private static string TrimForReceipt(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length <= 500 ? trimmed : trimmed[..500];
    }

    private static void TryDeleteFocusedEvidenceBaselineDirectory(string baselineRoot, string baselinePath)
    {
        try
        {
            var resolvedRoot = Path.GetFullPath(baselineRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var resolvedPath = Path.GetFullPath(baselinePath);
            if (resolvedPath.StartsWith(resolvedRoot, StringComparison.OrdinalIgnoreCase) && Directory.Exists(resolvedPath))
            {
                Directory.Delete(resolvedPath, recursive: true);
            }
        }
        catch (IOException)
        {
            // The git worktree removal is authoritative; cleanup is best-effort for partial creation.
        }
        catch (UnauthorizedAccessException)
        {
            // Preserve the evidence result when a stale handle delays temp-directory cleanup.
        }
    }

    private static bool ShouldStopAfterFailedCheck(
        PartitionVerdictCacheContext? cacheContext,
        AcceptanceManifestCheck check) =>
        cacheContext is null ||
        !TryGetInfrastructurePartitionId(check, out _, out _);

    private async Task<CheckBatchResult> RunCheckBatchAsync(
        IReadOnlyList<AcceptanceManifestCheck> batchChecks,
        PartitionVerdictCacheContext? cacheContext,
        string worktreePath,
        GoalId? goalId,
        int? stableSlotIndex,
        DotnetBuildEnvironmentLease? stableSlotLease,
        DotnetTestBuildPhase? dotnetTestBuildPhase,
        int maxConcurrentShards,
        CancellationToken cancellationToken)
    {
        var results = new List<AcceptanceCheckResult>(batchChecks.Count);
        var retried = false;
        for (var index = 0; index < batchChecks.Count;)
        {
            var check = batchChecks[index];
            if (TryGetInfrastructurePartitionId(check, out _, out _) &&
                stableSlotIndex.HasValue &&
                stableSlotLease is not null &&
                maxConcurrentShards > 1)
            {
                var shardChecks = batchChecks
                    .Skip(index)
                    .TakeWhile(candidate => TryGetInfrastructurePartitionId(candidate, out _, out _))
                    .ToArray();
                var shardBatch = await RunInfrastructureShardBatchAsync(
                    shardChecks,
                    cacheContext,
                    worktreePath,
                    goalId,
                    stableSlotIndex.Value,
                    stableSlotLease,
                    dotnetTestBuildPhase,
                    maxConcurrentShards,
                    cancellationToken).ConfigureAwait(false);
                results.AddRange(shardBatch.Results);
                retried |= shardBatch.Retried;
                index += shardChecks.Length;
                continue;
            }

            var checkResult = await RunCheckWithPartitionVerdictCacheAsync(
                check,
                cacheContext,
                worktreePath,
                goalId,
                stableSlotIndex,
                stableSlotLease,
                dotnetTestBuildPhase,
                cancellationToken).ConfigureAwait(false);
            retried |= checkResult.Retried;
            results.Add(checkResult.Result);
            index++;
            if (!checkResult.Result.Passed && ShouldStopAfterFailedCheck(cacheContext, check))
            {
                break;
            }
        }

        return new CheckBatchResult(results, retried);
    }

    private async Task<CheckBatchResult> RunInfrastructureShardBatchAsync(
        IReadOnlyList<AcceptanceManifestCheck> shardChecks,
        PartitionVerdictCacheContext? cacheContext,
        string worktreePath,
        GoalId? goalId,
        int primarySlotIndex,
        DotnetBuildEnvironmentLease primaryLease,
        DotnetTestBuildPhase? primaryBuildPhase,
        int maxConcurrentShards,
        CancellationToken cancellationToken)
    {
        var allShardsUseMtp = shardChecks.All(UsesMicrosoftTestingPlatform);
        if (allShardsUseMtp && primaryBuildPhase is not null)
        {
            var prebuild = await EnsureDotnetTestBuildPhaseAsync(
                primaryBuildPhase,
                shardChecks[0],
                worktreePath,
                goalId,
                primarySlotIndex,
                primaryLease,
                "acceptance-infrastructure-shards-prebuild",
                cancellationToken).ConfigureAwait(false);
            if (prebuild.Run.Result.Passed)
            {
                VerifyPrebuiltMtpExecutables(shardChecks, primaryBuildPhase);
                primaryLease.ReleaseExecutionLock();
            }
        }

        var wallClock = Stopwatch.StartNew();
        var orderedShards = shardChecks
            .Select((check, index) => new IndexedShard(index, check))
            .OrderByDescending(shard => shard.Check.EstimatedSerialSeconds)
            .ThenBy(shard => shard.Index)
            .ToArray();
        var outcomes = new ShardRunOutcome?[shardChecks.Count];
        var maxConcurrentExecutions = allShardsUseMtp
            ? Math.Min(maxConcurrentShards, shardChecks.Count)
            : 1;
        var pendingShards = orderedShards.ToList();
        var activeResourceKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var activeShards = new List<(Task Task, IReadOnlyList<string> ResourceKeys)>();
        var failures = new List<Exception>();

        async Task RunShardAsync(IndexedShard shard)
        {
            OnInfrastructureShardResourcesAcquiredForTests?.Invoke(shard.Check.Name);
            var shardClock = Stopwatch.StartNew();
            var worker = new ShardWorkerLease(
                primarySlotIndex,
                primaryLease,
                primaryBuildPhase);
            var shardResultsDirectory = ResolveInfrastructureShardResultsDirectory(
                worker.Lease.Environment);
            var run = await RunCheckWithPartitionVerdictCacheAsync(
                shard.Check,
                cacheContext,
                worktreePath,
                goalId,
                worker.SlotIndex,
                worker.Lease,
                worker.BuildPhase,
                cancellationToken,
                shardResultsDirectory).ConfigureAwait(false);
            shardClock.Stop();
            outcomes[shard.Index] = new ShardRunOutcome(run.Result, run.Retried);
            EmitShardTimingProgress(
                goalId,
                "shard-complete",
                shard.Check.Name,
                worker.SlotIndex,
                shardClock.Elapsed);
        }

        while (pendingShards.Count > 0 || activeShards.Count > 0)
        {
            while (!cancellationToken.IsCancellationRequested &&
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

            if (activeShards.Count == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw new InvalidOperationException(
                    "Infrastructure shard scheduler has pending work but no runnable or active shard.");
            }

            var completedTask = await Task.WhenAny(
                activeShards.Select(active => active.Task)).ConfigureAwait(false);
            var completedIndex = activeShards.FindIndex(active => active.Task == completedTask);
            var completedResourceKeys = activeShards[completedIndex].ResourceKeys;
            activeShards.RemoveAt(completedIndex);
            try
            {
                await completedTask.ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                failures.Add(exception);
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
        }

        if (failures.Count > 0)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failures[0]).Throw();
        }

        cancellationToken.ThrowIfCancellationRequested();
        wallClock.Stop();
        if (outcomes.Any(outcome => outcome is null))
        {
            throw new InvalidOperationException(
                "Concurrent infrastructure shard execution completed without a verdict for every shard.");
        }

        EmitShardTimingProgress(
            goalId,
            "shards-complete",
            $"{shardChecks.Count}-infrastructure-shards",
            primarySlotIndex,
            wallClock.Elapsed);
        var completed = outcomes.Select(outcome => outcome!).ToArray();
        return new CheckBatchResult(
            completed.Select(outcome => outcome.Result).ToArray(),
            completed.Any(outcome => outcome.Retried));
    }

    internal static string ResolveInfrastructureShardResultsDirectory(
        DotnetBuildEnvironment environment)
    {
        var attemptPrefix = AcceptanceAttemptResultsPrefix;
        var attemptDirectory = string.IsNullOrWhiteSpace(attemptPrefix)
            ? null
            : Path.GetDirectoryName(attemptPrefix);
        return string.IsNullOrWhiteSpace(attemptDirectory)
            ? Path.Combine(environment.ArtifactsPath, "TestResults")
            : attemptDirectory;
    }

    internal static IReadOnlyList<string> OrderExclusiveResourceKeys(
        IEnumerable<string> resourceKeys) =>
        resourceKeys
            .Select(key => key.Trim())
            .Order(StringComparer.OrdinalIgnoreCase)
            .ThenBy(key => key, StringComparer.Ordinal)
            .ToArray();

    private void VerifyPrebuiltMtpExecutables(
        IReadOnlyList<AcceptanceManifestCheck> shardChecks,
        DotnetTestBuildPhase buildPhase)
    {
        var buildEnvironment = buildPhase.BuildEnvironment
            ?? throw new InvalidOperationException(
                "Infrastructure shard prebuild completed without recording its build environment.");
        foreach (var executablePath in shardChecks
            .Select(check => EngineSettings.ResolveMtpInvocation(check.Project).ResolveExecutablePath(buildEnvironment))
            .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(executablePath))
            {
                throw new InvalidDataException(
                    $"Infrastructure shard prebuild did not produce configured MTP executable '{executablePath}'.");
            }
        }
    }

    private static void EmitShardTimingProgress(
        GoalId? goalId,
        string phase,
        string target,
        int slotIndex,
        TimeSpan elapsed)
    {
        var now = DateTimeOffset.UtcNow;
        EmitGateProgress(new AcceptanceGateProgress(
            goalId?.Value,
            phase,
            target,
            slotIndex,
            Environment.ProcessId,
            null,
            now - elapsed,
            now,
            now,
            elapsed,
            0,
            GateHeartbeatArtifacts.GetStableSlotPath(slotIndex)));
    }

    private async Task<(AcceptanceCheckResult Result, bool Retried)> RunCheckWithPartitionVerdictCacheAsync(
        AcceptanceManifestCheck check,
        PartitionVerdictCacheContext? cacheContext,
        string worktreePath,
        GoalId? goalId,
        int? stableSlotIndex,
        DotnetBuildEnvironmentLease? stableSlotLease,
        DotnetTestBuildPhase? dotnetTestBuildPhase,
        CancellationToken cancellationToken,
        string? testResultsDirectoryOverride = null)
    {
        if (cacheContext is not null &&
            TryBuildPartitionCacheKey(cacheContext, check, out var partitionId, out var filterHash, out var cacheKey) &&
            !cacheContext.ForceFullRerun &&
            cacheContext.TryGetGreen(cacheKey) is { } cached &&
            HasReusableStructuralCoverageEvidence(cached))
        {
            var reused = new PartitionVerdictReuseReceipt(partitionId, cached.AttemptId, cacheKey);
            cacheContext.RecordReuse(reused);
            return (new AcceptanceCheckResult(
                check.Name,
                true,
                0,
                null,
                ResultSummary:
                    $"partition-verdict-cache reused source_attempt_id={cached.AttemptId} cache_key={cacheKey}",
                TestResultPaths: cached.TestResultPaths,
                TestResultAttemptId: cached.AttemptId,
                TestResultIsExplicitCrossAttemptReuse: true), false);
        }

        var fresh = await RunCheckWithCancellationProbeAsync(
            check,
            worktreePath,
            goalId,
            stableSlotIndex,
            stableSlotLease,
            dotnetTestBuildPhase,
            cancellationToken,
            testResultsDirectoryOverride).ConfigureAwait(false);
        var currentAttemptId = cacheContext?.AttemptId ?? CurrentAcceptanceAttemptIdOrNull();
        fresh = (fresh.Result with
        {
            TestResultAttemptId = currentAttemptId,
            TestResultRunOrdinal = 0
        }, fresh.Retried);

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
                cancellationToken,
                testResultsDirectoryOverride).ConfigureAwait(false);
            fresh = (rerun.Result with
            {
                TestResultAttemptId = currentAttemptId,
                TestResultRunOrdinal = 1
            }, true);
        }

        if (cacheContext is not null &&
            TryBuildPartitionCacheKey(cacheContext, check, out partitionId, out filterHash, out cacheKey))
        {
            cacheContext.RecordExecution(
                new PartitionVerdictExecutionReceipt(
                    partitionId,
                    fresh.Result.Passed ? "GREEN" : "RED"),
                new PartitionVerdictRecord(
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
        CancellationToken cancellationToken,
        string? testResultsDirectoryOverride = null)
    {
        ThrowIfGateCancellationRequested(cancellationToken);
        var result = await RunCheckAsync(
            check,
            worktreePath,
            goalId,
            stableSlotIndex,
            stableSlotLease,
            dotnetTestBuildPhase,
            cancellationToken,
            testResultsDirectoryOverride).ConfigureAwait(false);
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
        out FocusedEvidenceCoverage coverage,
        out string rejection)
    {
        checks = [];
        coverage = new FocusedEvidenceCoverage(TargetToChecks: []);
        rejection = string.Empty;
        var items = request
            .Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (items.Length == 0)
        {
            rejection = "empty evidence request";
            return false;
        }

        var validated = new List<(string Target, string Project, string? Filter)>();
        var totalTargets = 0;
        foreach (var item in items)
        {
            var project = InfrastructureTestsProject;
            var expression = item;
            var hasExplicitProject = false;
            var separator = item.IndexOf(':', StringComparison.Ordinal);
            if (separator >= 0)
            {
                hasExplicitProject = true;
                var alias = item[..separator].Trim();
                expression = item[(separator + 1)..].Trim();
                if (!TryResolveFocusedEvidenceProject(alias, out project))
                {
                    rejection = $"unsupported evidence request project alias '{alias}'";
                    return false;
                }
            }

            if (!TryNormalizeFocusedEvidenceFilter(
                    expression,
                    allowMappedProject: hasExplicitProject,
                    out var filter,
                    out var targetCount,
                    out rejection))
            {
                return false;
            }

            totalTargets += targetCount;
            validated.Add((item, project, filter));
        }

        // Historical rule: more than four focused targets collapsed to one whole-project check per
        // project (and 18 Infrastructure lanes). It optimized process/check count and replaced the
        // earlier hard rejection of mappings over four targets; no filter-length or runner failure
        // was recorded. Keep every validated filter, while retaining the old path's manifest-default
        // timeout for wide mappings so serial focused execution does not lose its completion budget.
        // Performance prediction pending the operator-owned like-for-like run: one build phase is
        // created per arm, but each request item starts a serial MTP process with fixed cost F~=9-11s.
        // Thus the six-target Infrastructure shape is B+R6+kF: 9-11s of process cost when packed into
        // one item, or 54-66s when split across k=6 items, versus the 1775.9s/3367-test collapsed
        // baseline. Fixed process cost alone crosses 1775.9s at k=162 (F=11), 178 (F=10), or 198
        // (F=9); build plus filtered runtime B+R6 moves that crossing earlier. The two-target,
        // cross-project shape remains B+R2+2F (18-22s of process cost), so predict its existing
        // 231.6s/220-test focused result within ordinary run variance. RunCheckBatchAsync only shards
        // named Infrastructure acceptance lanes, so mapped focused items do not recover the former
        // four-lane concurrency. The operator receipt must replace these predictions with measured
        // wall-clock and tests_executed for both exact target sets.
        var built = new List<AcceptanceManifestCheck>();
        var targetToChecks = new List<FocusedEvidenceTargetCoverage>();
        foreach (var item in validated)
        {
            var check = new AcceptanceManifestCheck
            {
                Name = item.Filter is null
                    ? $"reviewer mapped project evidence: {ProjectLabel(item.Project)}"
                    : $"reviewer focused evidence: {ProjectLabel(item.Project)} {item.Filter}",
                Type = "dotnet-test",
                // Both focused-evidence target projects (Core.Tests, Infrastructure.Tests) are MTP;
                // without this the check defaults to the VSTest runner and fails on .NET 10 with
                // "VSTest target is no longer supported", making every reviewer evidence run fail.
                Runner = "mtp",
                Project = item.Project,
                Arguments = item.Filter is null
                    ? ["--verbosity", "minimal"]
                    : ["--verbosity", "minimal", "--filter", item.Filter],
                TimeoutMinutes = totalTargets <= FocusedEvidenceShortTimeoutTargetLimit ? 10 : null
            };
            built.Add(check);
            targetToChecks.Add(new FocusedEvidenceTargetCoverage(item.Target, [check.Name]));
        }

        var hasFocusedFilters = validated.Any(item => item.Filter is not null);
        var hasMappedProjects = validated.Any(item => item.Filter is null);
        checks = built;
        coverage = new FocusedEvidenceCoverage(
            TargetToChecks: targetToChecks,
            ExecutionMode: hasFocusedFilters && hasMappedProjects
                ? "mixed"
                : hasFocusedFilters ? "focused" : "project",
            ExecutionReason: hasFocusedFilters && hasMappedProjects
                ? "explicit-focused-and-mapped-project-request"
                : hasFocusedFilters ? "explicit-focused-mapping" : "explicit-mapped-project-request");
        return true;
    }

    internal static bool TryResolveFocusedEvidenceProject(string alias, out string project)
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
        bool allowMappedProject,
        out string? filter,
        out int targetCount,
        out string rejection)
    {
        filter = null;
        targetCount = 0;
        rejection = string.Empty;
        var trimmed = expression.Trim();
        if (trimmed.Length == 0)
        {
            rejection = "empty focused evidence filter";
            return false;
        }

        if (trimmed.Equals("mapped-project", StringComparison.OrdinalIgnoreCase))
        {
            if (!allowMappedProject)
            {
                rejection = "mapped-project evidence requires an explicit supported project alias";
                return false;
            }

            // This is the bounded representation of a deterministic planner check that has no
            // narrower class filter. It runs one known test project, never the solution-level
            // acceptance manifest, and counts as one broker target.
            targetCount = 1;
            return true;
        }

        if (trimmed.Equals("all", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("full", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("full-suite", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains(".sln", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains('*', StringComparison.Ordinal))
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
            try
            {
                _ = TranslateMtpFilter(filter).ToArray();
            }
            catch (InvalidOperationException)
            {
                rejection = "focused evidence filter contains an unsupported token";
                filter = null;
                targetCount = 0;
                return false;
            }

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

    private static string FormatFocusedEvidencePlanSummary(FocusedEvidenceCoverage coverage) =>
        $"mode={coverage.ExecutionMode} reason={coverage.ExecutionReason}";

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
        PolicyShardPlan policyShardPlan,
        IReadOnlyList<AcceptanceTestLane> infrastructureTestLanes)
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
            foreach (var scopedCheck in ExpandBroadInfrastructureCheck(plannedManifestCheck, infrastructureTestLanes))
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
        PolicyShardPlan? policyShardPlan = null,
        IReadOnlyList<AcceptanceTestLane>? infrastructureTestLanes = null)
    {
        if (changedFiles is null || changedFiles.Count == 0)
            return manifestChecks;

        policyShardPlan ??= BuildPolicyShardPlan(changedFiles);
        var requiredPolicyChecks = policyRequiredChecks ?? BuildRequiredPolicyChecks(changedFiles);
        var plannedChecks = requiredPolicyChecks
            .Where(check => !policyShardPlan.ForceFull ||
                !check.Type.Equals("dotnet-test", StringComparison.OrdinalIgnoreCase))
            .SelectMany(check => ExpandBroadInfrastructureCheck(check, infrastructureTestLanes))
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

    internal static bool StructuralCoverageApplies(
        AcceptanceGateEngineSettings engineSettings,
        IReadOnlyList<string>? changedFiles) =>
        engineSettings.EnforceStructuralCoverage &&
        (changedFiles is null ||
            changedFiles.Count == 0 ||
            !RepositoryChangeClassifier.Classify(changedFiles).IsDocsOnly);

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

    internal static string ProjectLabel(string project) =>
        project.Equals(CoreProject, StringComparison.OrdinalIgnoreCase) ? "Core" :
        project.Equals(InfrastructureProject, StringComparison.OrdinalIgnoreCase) ? "Infrastructure" :
        project.Equals(AppProject, StringComparison.OrdinalIgnoreCase) ? "App" :
        project.Equals(CoreTestsProject, StringComparison.OrdinalIgnoreCase) ? "Core.Tests" :
        project.Equals(InfrastructureTestsProject, StringComparison.OrdinalIgnoreCase) ? "Infrastructure.Tests" :
        project;

    private static List<AcceptanceManifestCheck> ExpandBroadInfrastructureChecks(
        IReadOnlyList<AcceptanceManifestCheck> manifestChecks,
        IReadOnlyList<AcceptanceTestLane> infrastructureTestLanes)
    {
        var effective = new List<AcceptanceManifestCheck>();
        foreach (var check in manifestChecks)
        {
            if (!IsBroadInfrastructureTestCheck(check))
            {
                effective.Add(check);
                continue;
            }

            effective.AddRange(ExpandBroadInfrastructureCheck(check, infrastructureTestLanes));
        }

        return effective;
    }

    private static IEnumerable<AcceptanceManifestCheck> ExpandBroadInfrastructureCheck(
        AcceptanceManifestCheck check,
        IReadOnlyList<AcceptanceTestLane>? infrastructureTestLanes = null)
    {
        if (!IsBroadInfrastructureTestCheck(check))
        {
            yield return check;
            yield break;
        }

        foreach (var lane in infrastructureTestLanes ?? EngineSettings.InfrastructureTestLanes)
        {
            yield return BuildInfrastructureShardCheck(check, lane);
        }
    }

    private static AcceptanceManifestCheck BuildInfrastructureShardCheck(
        AcceptanceManifestCheck check,
        AcceptanceTestLane lane) =>
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
            Runner = check.Runner,
            EstimatedSerialSeconds = lane.EstimatedSerialSeconds,
            ExclusiveResourceKeys = lane.ExclusiveResourceKeys
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
            Arguments = arguments,
            Runner = ResolveDotnetTestRunner(project)
        };
    }

    // Core.Tests and Infrastructure.Tests are Microsoft.Testing.Platform projects; synthesized
    // dotnet-test checks for them must carry runner=mtp or they default to the VSTest runner and
    // fail on .NET 10 with "VSTest target is no longer supported".
    private static string ResolveDotnetTestRunner(string? project) =>
        !string.IsNullOrWhiteSpace(project) &&
        (IsCoreTestProject(project) || IsInfrastructureTestProject(project))
            ? "mtp"
            : "vstest";

    private static string[] SplitCommandLine(string commandLine) =>
        commandLine.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static void AddCoveredBroadInfrastructureResults(
        List<AcceptanceCheckResult> checks,
        IReadOnlyList<AcceptanceManifestCheck> policyEffectiveChecks,
        IReadOnlyList<string>? changedFiles,
        IReadOnlyList<AcceptanceTestLane> infrastructureTestLanes)
    {
        if (changedFiles is null || changedFiles.Count == 0)
            return;

        foreach (var broadCheck in policyEffectiveChecks.Where(IsBroadInfrastructureTestCheck))
        {
            if (checks.Any(result => result.Name.Equals(broadCheck.Name, StringComparison.OrdinalIgnoreCase)))
                continue;

            var shardResults = ExpandBroadInfrastructureCheck(broadCheck, infrastructureTestLanes)
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

            if (shardResults.Length != infrastructureTestLanes.Count)
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
        IReadOnlyList<AcceptanceManifestCheck> effectiveChecks,
        int fullRerunEveryN)
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
        var forceFullRerun = reusableGreenExists && priorReuseAttemptCount + 1 >= fullRerunEveryN;

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

        var standardPrefix = "infrastructure tests: ";
        string? partitionName = null;
        if (check.Name.StartsWith(standardPrefix, StringComparison.OrdinalIgnoreCase))
        {
            partitionName = check.Name[standardPrefix.Length..];
        }

        if (string.IsNullOrWhiteSpace(partitionName))
        {
            return false;
        }

        partitionId = Slug(partitionName);
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
        if (CurrentAcceptanceAttemptIdOrNull() is { } attemptId)
        {
            return attemptId;
        }

        return $"manual-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}";
    }

    private static string? CurrentAcceptanceAttemptIdOrNull()
    {
        var prefix = AcceptanceAttemptResultsPrefix;
        if (string.IsNullOrWhiteSpace(prefix))
        {
            return null;
        }

        var name = Path.GetFileName(prefix.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return string.IsNullOrWhiteSpace(name) ? null : name;
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
        CancellationToken cancellationToken,
        string? testResultsDirectoryOverride = null)
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
            ? await RunDotnetTestCheckAsync(
                check,
                worktreePath,
                goalId,
                stableSlotIndex,
                stableSlotLease,
                dotnetTestBuildPhase,
                cancellationToken,
                testResultsDirectoryOverride).ConfigureAwait(false)
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
            EngineSettings.ResolveCheckTimeout(check.TimeoutMinutes),
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
            EngineSettings.ResolveCheckTimeout(check.TimeoutMinutes),
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
        CancellationToken cancellationToken,
        string? testResultsDirectoryOverride = null)
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
                cancellationToken,
                testResultsDirectoryOverride).ConfigureAwait(false);
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
        CancellationToken cancellationToken,
        string? testResultsDirectoryOverride = null)
    {
        var attemptName = $"acceptance-{Slug(check.Name)}";
        var effectiveBuildPhase = dotnetTestBuildPhase ??
            new DotnetTestBuildPhase(BuildDotnetTestBuildArguments(check), cachePlan: null);
        var buildRun = await EnsureDotnetTestBuildPhaseAsync(
            effectiveBuildPhase,
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
            effectiveBuildPhase.BuildEnvironment,
            attemptName,
            cancellationToken,
            testResultsDirectoryOverride).ConfigureAwait(false);

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
        DotnetBuildEnvironment? executableEnvironment,
        string attemptName,
        CancellationToken cancellationToken,
        string? testResultsDirectoryOverride = null)
    {
        var elapsed = Stopwatch.StartNew();
        var environment = executableEnvironment ?? ResolveExecutionEnvironment(
            goalId,
            attemptName,
            stableSlotIndex,
            stableSlotLease);
        stableSlotLease?.ReleaseExecutionLock();

        var telemetry = ResolveTestTelemetry(
            check,
            environment,
            testResultsDirectoryOverride);
        PrepareTestTelemetryForRun(telemetry);
        var arguments = BuildMtpTestArguments(check, executableEnvironment ?? environment, telemetry);
        ReapRecordedGateChildBeforeManagedDotnetCommand(check, environment, goalId, stableSlotIndex);
        var result = await RunWithGateHeartbeatAsync(
            arguments,
            worktreePath,
            EngineSettings.ResolveCheckTimeout(check.TimeoutMinutes),
            CreateGateHeartbeatContext(check, arguments, worktreePath, goalId, stableSlotIndex, environment),
            cancellationToken).ConfigureAwait(false);

        elapsed.Stop();
        var passed = !result.TimedOut && result.ExitCode == 0;
        EmitMissingTrxReceiptIfNeeded(passed, telemetry);
        IReadOnlyList<string> failingTestIdentities = passed
            ? []
            : ExtractTrxFailureIdentities(telemetry.Paths);
        var durableTestResultPaths = CopyCompletedTestReceiptsToAttemptFolder(telemetry.Paths);
        return (new AcceptanceCheckResult(
            result.TimedOut ? BuildTimeoutFailureName(check, result) : check.Name,
            passed,
            result.ExitCode,
            passed ? null : BuildMtpFailureOutput(check.Name, result, telemetry),
            environment.ArtifactsPath,
            "goal-acceptance-verifier",
            environment.LeaseId,
            (long)elapsed.Elapsed.TotalMilliseconds,
            ResultSummary: BuildGenericCommandResultSummary(result),
            TestResultPaths: durableTestResultPaths,
            FailingTestIdentities: failingTestIdentities), false);
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
            if (phase.BuildEnvironment is null)
            {
                throw new InvalidOperationException(
                    "A completed acceptance build phase has no recorded build environment.");
            }

            return new DotnetTestBuildPhaseResult(completed, ContributesToCheck: false);
        }

        phase.BuildEnvironment ??= ResolveExecutionEnvironment(
            goalId,
            $"{attemptName}-build",
            stableSlotIndex,
            stableSlotLease);
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
            cancellationToken,
            executionEnvironment: phase.BuildEnvironment).ConfigureAwait(false);
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
        var environment = ResolveExecutionEnvironment(
            goalId,
            $"{attemptName}-cache",
            stableSlotIndex,
            stableSlotLease);
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
        Action<DotnetBuildEnvironment>? afterLeasePrepared = null,
        DotnetBuildEnvironment? executionEnvironment = null)
    {
        var elapsed = Stopwatch.StartNew();
        var environment = executionEnvironment ?? ResolveExecutionEnvironment(
            goalId,
            attemptName,
            stableSlotIndex,
            stableSlotLease);
        DotnetBuildEnvironmentLease? leaseLock = null;
        try
        {
            leaseLock = stableSlotLease?.IsExecutionLockHeld == true
                ? null
                : DotnetBuildEnvironmentManager.AcquireLeaseExecutionPermit(
                    environment,
                    cancellationToken,
                    _timeProvider,
                    _leaseSleep);
            afterLeasePrepared?.Invoke(environment);

            var lockRemediationApplied = false;
            var result = await RunManagedDotnetCommandAsync(
                check,
                arguments,
                environment,
                worktreePath,
                goalId,
                stableSlotIndex,
                EngineSettings.ResolveCheckTimeout(check.TimeoutMinutes),
                cancellationToken).ConfigureAwait(false);

            if (IsBuildLockFailure(result, environment, check, out var attribution))
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
                        if (stableSlotLease?.IsExecutionLockHeld == true)
                        {
                            return;
                        }

                        leaseLock?.Dispose();
                        leaseLock = null;
                        environment = nextEnvironment;
                        leaseLock = DotnetBuildEnvironmentManager.AcquireLeaseExecutionPermit(
                            environment,
                            cancellationToken,
                            _timeProvider,
                            _leaseSleep);
                    },
                    cancellationToken).ConfigureAwait(false);
            }

            if (IsBuildLockFailure(result, environment, check, out var finalAttribution))
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
            IReadOnlyList<string> failingTestIdentities = passed
                ? []
                : ExtractTrxFailureIdentities(telemetry?.Paths);
            var durableTestResultPaths = CopyCompletedTestReceiptsToAttemptFolder(telemetry?.Paths);
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
                TestResultPaths: durableTestResultPaths,
                FailingTestIdentities: failingTestIdentities), lockRemediationApplied);
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
                    if (stableSlotLease?.IsExecutionLockHeld == true)
                    {
                        return;
                    }

                    leaseLock?.Dispose();
                    leaseLock = null;
                    environment = nextEnvironment;
                    leaseLock = DotnetBuildEnvironmentManager.AcquireLeaseExecutionPermit(
                        environment,
                        cancellationToken,
                        _timeProvider,
                        _leaseSleep);
                },
                cancellationToken).ConfigureAwait(false);

            if (IsBuildLockFailure(result, environment, check, out var finalAttribution))
            {
                throw new BuildLockBlockedException(finalAttribution);
            }

            elapsed.Stop();
            var reportedAllPassed = !result.TimedOut && result.ExitCode != 0 && TestRunReportsAllPassed(result.Output);
            var passed = !result.TimedOut && (result.ExitCode == 0 || reportedAllPassed);
            var telemetry = ResolveDotnetTestTelemetry(arguments, check, environment);
            EmitMissingTrxReceiptIfNeeded(passed, telemetry);
            IReadOnlyList<string> failingTestIdentities = passed
                ? []
                : ExtractTrxFailureIdentities(telemetry?.Paths);
            var durableTestResultPaths = CopyCompletedTestReceiptsToAttemptFolder(telemetry?.Paths);
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
                TestResultPaths: durableTestResultPaths,
                FailingTestIdentities: failingTestIdentities), true);
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
            EngineSettings.ResolveBuildServerShutdownTimeout(),
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

        var retryEnvironment = currentEnvironment;
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
                EngineSettings.ResolveCheckTimeout(check.TimeoutMinutes),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsBuildArtifactIoException(ex))
        {
            var lockedPath = TryExtractPathFromException(ex) ?? retryEnvironment.ArtifactsPath;
            retryAttribution = AttributeBuildLock(lockedPath, worktreePath, "acceptance-retry", check.Name);
        }

        if (retry is not null &&
            !IsBuildLockFailure(retry, retryEnvironment, check, out retryAttribution))
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

        var killRetryEnvironment = currentEnvironment;
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
                EngineSettings.ResolveCheckTimeout(check.TimeoutMinutes),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsBuildArtifactIoException(ex))
        {
            var lockedPath = TryExtractPathFromException(ex) ?? killRetryEnvironment.ArtifactsPath;
            throw new BuildLockBlockedException(AttributeBuildLock(lockedPath, worktreePath, "acceptance-kill-retry", check.Name));
        }

        if (IsBuildLockFailure(
            killRetry,
            killRetryEnvironment,
            check,
            out var killRetryAttribution))
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
        var retryEnvironment = currentEnvironment;
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
                var exhaustedAttribution = AttributeBuildLock(
                    cycleAttribution.Path,
                    worktreePath,
                    "acceptance-transient-exhaustion",
                    check.Name);
                EmitTransientNoHolderBuildLockRetryReceipt(
                    check,
                    exhaustedAttribution,
                    cycle,
                    maxRetryCycles,
                    "wait-exhausted",
                    exitCode: null,
                    timedOut: false,
                    buildLock: true);
                throw new BuildLockBlockedException(exhaustedAttribution);
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
                    EngineSettings.ResolveCheckTimeout(check.TimeoutMinutes),
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

                var exhaustedAttribution = AttributeBuildLock(
                    exceptionAttribution.Path,
                    worktreePath,
                    "acceptance-transient-exhaustion",
                    check.Name);
                throw new BuildLockBlockedException(exhaustedAttribution);
            }

            if (!IsBuildLockFailure(retry, retryEnvironment, check, out var retryAttribution))
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

            var finalAttribution = cycle == maxRetryCycles
                ? AttributeBuildLock(
                    retryAttribution.Path,
                    worktreePath,
                    "acceptance-transient-exhaustion",
                    check.Name)
                : retryAttribution;
            EmitTransientNoHolderBuildLockRetryReceipt(
                check,
                finalAttribution,
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

            throw new BuildLockBlockedException(finalAttribution);
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
        var holder = attribution.Holders.FirstOrDefault();
        Console.WriteLine(
            $"LOCK_TRANSIENT_RETRY path={QuoteProgressToken(attribution.Path)} " +
            $"check={QuoteProgressToken(check.Name)} " +
            $"cycle={cycle.ToString(System.Globalization.CultureInfo.InvariantCulture)} " +
            $"max-cycles={maxCycles.ToString(System.Globalization.CultureInfo.InvariantCulture)} " +
            $"verdict={verdict} " +
            $"exit-code={(exitCode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none")} " +
            $"timed-out={timedOut.ToString().ToLowerInvariant()} " +
            $"build-lock={buildLock.ToString().ToLowerInvariant()} " +
            $"holder-pid={holder?.ProcessId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none"} " +
            $"holder-name={QuoteProgressToken(holder?.ProcessName ?? "none")} " +
            $"attribution-source={QuoteProgressToken(attribution.Source)}");
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
        ReapRecordedGateChildBeforeManagedDotnetCommand(check, environment, goalId, stableSlotIndex);
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

    private static bool IsBuildLockFailure(
        CommandResult result,
        DotnetBuildEnvironment environment,
        AcceptanceManifestCheck check,
        out BuildLockAttribution attribution)
    {
        attribution = null!;
        if (result.TimedOut || result.ExitCode == 0 || DotnetTestRunReportsCompleted(result.Output))
        {
            return false;
        }

        if (!OutputDescribesBuildLock(result.Output))
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
        attribution = EnrichBuildLockAttributionWithGateContext(attribution, environment, check);
        EmitBuildLockClassificationContext(attribution, result, environment, check);
        return true;
    }

    private static bool OutputDescribesBuildLock(string output) =>
        output.Contains("being used by another process", StringComparison.OrdinalIgnoreCase) ||
        output.Contains("file is locked", StringComparison.OrdinalIgnoreCase) ||
        output.Contains("locked by another process", StringComparison.OrdinalIgnoreCase);

    private static BuildLockAttribution EnrichBuildLockAttributionWithGateContext(
        BuildLockAttribution attribution,
        DotnetBuildEnvironment environment,
        AcceptanceManifestCheck check)
    {
        var holders = attribution.Holders.ToList();
        var consumedGateContext = false;
        if (HasNoActionableHolder(attribution) &&
            DotnetBuildEnvironmentManager.TryFindActiveSlotArtifactConsumer(environment) is { } activeConsumer)
        {
            consumedGateContext = true;
            holders.Add(activeConsumer);
        }

        var heartbeat = ReadGateHeartbeat(environment, check);
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
        DotnetBuildEnvironment environment,
        AcceptanceManifestCheck check)
    {
        var heartbeat = ReadGateHeartbeat(environment, check);
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

    private static GateHeartbeatStatus? ReadGateHeartbeat(
        DotnetBuildEnvironment environment,
        AcceptanceManifestCheck check)
    {
        var stableSlotIndex = TryGetStableSlotIndex(environment);
        var path = ResolveGateHeartbeatPath(
            check,
            environment,
            stableSlotIndex,
            worktreePath: null);
        if (!File.Exists(path))
        {
            return null;
        }

        if (stableSlotIndex.HasValue &&
            path.Equals(
                GateHeartbeatArtifacts.GetStableSlotPath(stableSlotIndex.Value),
                StringComparison.OrdinalIgnoreCase))
        {
            return GateHeartbeatArtifacts.ReadStableSlot(stableSlotIndex.Value);
        }

        try
        {
            var snapshot = JsonSerializer.Deserialize<GateHeartbeatSnapshot>(
                File.ReadAllText(path),
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
            return snapshot is null
                ? new GateHeartbeatStatus(stableSlotIndex ?? -1, path, false, "invalid", null, null, null)
                : new GateHeartbeatStatus(
                    stableSlotIndex ?? -1,
                    path,
                    true,
                    null,
                    snapshot,
                    Positive(DateTimeOffset.UtcNow - snapshot.LastObservedAt),
                    Positive(DateTimeOffset.UtcNow - snapshot.LastProgressAt));
        }
        catch
        {
            return new GateHeartbeatStatus(
                stableSlotIndex ?? -1,
                path,
                false,
                "invalid",
                null,
                null,
                null);
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
        AcceptanceManifestCheck check,
        DotnetBuildEnvironment environment,
        GoalId? goalId,
        int? stableSlotIndex)
    {
        var heartbeatPath = ResolveGateHeartbeatPath(check, environment, stableSlotIndex, worktreePath: null);
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
        const string prefix = "run-build-";
        if (!environment.LeaseId.StartsWith(prefix, StringComparison.Ordinal) ||
            !int.TryParse(
                environment.LeaseId[prefix.Length..],
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture,
                out var slotIndex))
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
            EngineSettings.ResolveCheckTimeout(null),
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

    private static bool HasReusableStructuralCoverageEvidence(PartitionVerdictRecord cached) =>
        !EngineSettings.EnforceStructuralCoverage ||
        cached.TestResultPaths is { Count: > 0 } &&
        cached.TestResultPaths.All(path => TryGetFileLength(path) > 0);

    private async Task<AcceptanceCheckResult> RunStructuralCoverageCheckAsync(
        IReadOnlyList<AcceptanceManifestCheck> effectiveChecks,
        IReadOnlyList<AcceptanceCheckResult> completedChecks,
        string worktreePath,
        IReadOnlyList<string>? changedFiles,
        int? stableSlotIndex,
        DotnetBuildEnvironmentLease? stableSlotLease,
        string? currentAttemptId,
        CancellationToken cancellationToken)
    {
        var mainWorktreePath = ResolveMainWorktreePath(worktreePath);
        if (string.IsNullOrWhiteSpace(mainWorktreePath))
        {
            return new AcceptanceCheckResult(
                "structural test coverage",
                false,
                1,
                "Trusted main worktree could not be resolved for cross-generation discovery.",
                ResultSummary: "main discovery unavailable");
        }

        var broadChecks = DiscoverTrustedTestProjects(worktreePath, mainWorktreePath)
            .Select(project => BuildTrustedStructuralCoverageCheck(project, effectiveChecks))
            .ToArray();
        if (broadChecks.Length == 0)
        {
            return new AcceptanceCheckResult(
                "structural test coverage",
                false,
                1,
                "Structural coverage is enabled but trusted discovery found no test projects.",
                ResultSummary: "no trusted test project");
        }

        var environment = ResolveExecutionEnvironment(
            null,
            "acceptance-coverage-discovery",
            stableSlotIndex,
            stableSlotLease);
        var allSummaries = new List<string>();
        foreach (var broadCheck in broadChecks)
        {
            var deletedTestFiles = ResolveDeletedTestFiles(worktreePath, broadCheck.Project!);
            var candidateDiscoveryArguments = BuildUnattendedDiscoveryArguments(
                broadCheck,
                EngineSettings,
                environment);
            var candidateDiscovery = await _runner(
                candidateDiscoveryArguments,
                worktreePath,
                EngineSettings.ResolveDiscoveryTimeout(),
                cancellationToken).ConfigureAwait(false);
            if (candidateDiscovery.ExitCode != 0)
            {
                return new AcceptanceCheckResult(
                    $"structural test coverage: {broadCheck.Name}",
                    false,
                    candidateDiscovery.ExitCode,
                    TailOutput(candidateDiscovery.Output),
                    ResultSummary: "candidate trusted discovery failed");
            }

            var discoveryDecodeFault = candidateDiscovery.Output.Contains('\uFFFD', StringComparison.Ordinal);
            IReadOnlySet<string>? mainDiscoveredTests = null;
            var mainProjectPath = Path.Combine(
                mainWorktreePath,
                broadCheck.Project!.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(mainProjectPath))
            {
                var mainArtifactsPath = Path.Combine(environment.ArtifactsPath, "main-coverage-baseline");
                var mainBuildArguments = new[]
                {
                    "dotnet",
                    "build",
                    broadCheck.Project,
                    "--artifacts-path",
                    mainArtifactsPath,
                    "--verbosity",
                    "minimal"
                };
                var mainBuildPermit = stableSlotLease?.IsExecutionLockHeld == true
                    ? null
                    : DotnetBuildEnvironmentManager.AcquireLeaseExecutionPermit(
                        environment,
                        cancellationToken,
                        _timeProvider,
                        _leaseSleep);
                CommandResult mainBuild;
                try
                {
                    mainBuild = await _runner(
                        mainBuildArguments,
                        mainWorktreePath,
                        EngineSettings.ResolveCheckTimeout(broadCheck.TimeoutMinutes),
                        cancellationToken)
                        .ConfigureAwait(false);
                }
                finally
                {
                    mainBuildPermit?.Dispose();
                }

                if (mainBuild.ExitCode != 0)
                {
                    return new AcceptanceCheckResult(
                        $"structural test coverage: {broadCheck.Name}",
                        false,
                        mainBuild.ExitCode,
                        TailOutput(mainBuild.Output),
                        ResultSummary: "trusted main baseline build failed");
                }

                var mainDiscoveryArguments = BuildUnattendedDiscoveryArguments(
                    broadCheck,
                    EngineSettings,
                    environment with { ArtifactsPath = mainArtifactsPath });
                var mainDiscovery = await _runner(
                    mainDiscoveryArguments,
                    mainWorktreePath,
                    EngineSettings.ResolveDiscoveryTimeout(),
                    cancellationToken).ConfigureAwait(false);
                if (mainDiscovery.ExitCode != 0)
                {
                    return new AcceptanceCheckResult(
                        $"structural test coverage: {broadCheck.Name}",
                        false,
                        mainDiscovery.ExitCode,
                        TailOutput(mainDiscovery.Output),
                        ResultSummary: "trusted main discovery failed");
                }

                discoveryDecodeFault |= mainDiscovery.Output.Contains('\uFFFD', StringComparison.Ordinal);
                mainDiscoveredTests = TestCoverageInvariant.ParseDiscoveredTests(
                    mainDiscovery.Output,
                    bareTestList: UsesMicrosoftTestingPlatform(broadCheck));
            }

            IEnumerable<AcceptanceManifestCheck> partitionChecks = IsBroadInfrastructureTestCheck(broadCheck)
                ? ExpandBroadInfrastructureCheck(broadCheck)
                : [broadCheck];
            var partitions = partitionChecks
                .Select(shard =>
                {
                    var result = completedChecks.LastOrDefault(candidate =>
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
            var coverage = TestCoverageInvariant.Evaluate(
                TestCoverageInvariant.ParseDiscoveredTests(
                    candidateDiscovery.Output,
                    bareTestList: UsesMicrosoftTestingPlatform(broadCheck)),
                partitions,
                mainDiscoveredTests,
                DeletedTestFilesForProject(deletedTestFiles, broadCheck.Project!),
                currentAttemptId);
            if (!coverage.Passed)
            {
                var details = new List<string>
                {
                    $"classification: {coverage.FailureClassification}",
                    coverage.Summary
                };
                if (discoveryDecodeFault)
                {
                    details.Add(
                        "discovery output contains Unicode replacement characters; captured process output decoding is corrupt");
                }
                details.AddRange(coverage.EmptyPartitions.Take(10).Select(name => $"empty partition: {name}"));
                details.AddRange(coverage.MissingTests.Take(10).Select(name => $"missing test: {name}"));
                return new AcceptanceCheckResult(
                    $"structural test coverage: {broadCheck.Name}",
                    false,
                    1,
                    string.Join(Environment.NewLine, details),
                    ResultSummary: coverage.Summary,
                    FailureClassification: coverage.FailureClassification);
            }

            allSummaries.Add(coverage.Summary);
        }

        return new AcceptanceCheckResult(
            "structural test coverage",
            true,
            0,
            null,
            ResultSummary: string.Join("; ", allSummaries));
    }

    internal static IReadOnlyList<string> DiscoverTrustedTestProjects(string worktreePath)
    {
        var testsRoot = Path.Combine(worktreePath, "tests");
        if (!Directory.Exists(testsRoot))
        {
            return [];
        }

        return Directory.EnumerateFiles(testsRoot, "*.csproj", SearchOption.AllDirectories)
            .Where(path =>
                Path.GetFileNameWithoutExtension(path).EndsWith(".Tests", StringComparison.OrdinalIgnoreCase) ||
                File.ReadAllText(path).Contains("<IsTestProject>true</IsTestProject>", StringComparison.OrdinalIgnoreCase))
            .Select(path => NormalizePath(Path.GetRelativePath(worktreePath, path))!)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    internal static IReadOnlyList<string> DiscoverTrustedTestProjects(
        string candidateWorktreePath,
        string mainWorktreePath) =>
        DiscoverTrustedTestProjects(candidateWorktreePath)
            .Concat(DiscoverTrustedTestProjects(mainWorktreePath))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static IReadOnlyList<AcceptanceManifestCheck> EnsureTrustedStructuralCoverageExecutionChecks(
        IReadOnlyList<AcceptanceManifestCheck> effectiveChecks,
        IReadOnlyList<AcceptanceManifestCheck> manifestChecks,
        string worktreePath)
    {
        var mainWorktreePath = ResolveMainWorktreePath(worktreePath);
        var trustedProjects = string.IsNullOrWhiteSpace(mainWorktreePath)
            ? DiscoverTrustedTestProjects(worktreePath)
            : DiscoverTrustedTestProjects(worktreePath, mainWorktreePath);
        var completed = effectiveChecks.ToList();
        foreach (var project in trustedProjects)
        {
            if (completed.Any(check =>
                    IsBroadTestProjectCheck(check) &&
                    string.Equals(NormalizePath(check.Project), project, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            completed.RemoveAll(check =>
                check.Type.Equals("dotnet-test", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(NormalizePath(check.Project), project, StringComparison.OrdinalIgnoreCase));
            completed.Add(BuildTrustedStructuralCoverageCheck(project, manifestChecks));
        }

        return completed;
    }

    internal static IReadOnlyList<string> DeletedTestFilesForProject(
        IReadOnlyList<string> deletedTestFiles,
        string project)
    {
        var normalizedProject = NormalizePath(project)!;
        var projectDirectory = NormalizePath(Path.GetDirectoryName(normalizedProject))?.TrimEnd('/');
        if (string.IsNullOrWhiteSpace(projectDirectory))
        {
            return [];
        }

        return deletedTestFiles
            .Where(path =>
            {
                var normalizedPath = NormalizePath(path);
                return normalizedPath?.StartsWith(
                    $"{projectDirectory}/",
                    StringComparison.OrdinalIgnoreCase) == true;
            })
            .ToArray();
    }

    private static bool IsBroadTestProjectCheck(AcceptanceManifestCheck check) =>
        check.Type.Equals("dotnet-test", StringComparison.OrdinalIgnoreCase) &&
        !string.IsNullOrWhiteSpace(check.Project) &&
        check.Project.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) &&
        !check.Arguments.Any(argument => argument.Equals("--filter", StringComparison.OrdinalIgnoreCase));

    private static AcceptanceManifestCheck BuildTrustedStructuralCoverageCheck(
        string project,
        IReadOnlyList<AcceptanceManifestCheck> effectiveChecks)
    {
        var matchingChecks = effectiveChecks
            .Where(check =>
                check.Type.Equals("dotnet-test", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(NormalizePath(check.Project), project, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (matchingChecks.FirstOrDefault(IsBroadTestProjectCheck) is { } broadCheck)
        {
            return broadCheck;
        }

        if (matchingChecks.FirstOrDefault() is { } filteredCheck)
        {
            var laneSeparator = filteredCheck.Name.LastIndexOf(": ", StringComparison.Ordinal);
            return new AcceptanceManifestCheck
            {
                Name = laneSeparator > 0 ? filteredCheck.Name[..laneSeparator] : filteredCheck.Name,
                Type = filteredCheck.Type,
                Project = filteredCheck.Project,
                Arguments = RemoveTestFilter(filteredCheck.Arguments),
                TimeoutMinutes = filteredCheck.TimeoutMinutes,
                Runner = filteredCheck.Runner
            };
        }

        return new AcceptanceManifestCheck
        {
            Name = $"trusted structural discovery: {Path.GetFileNameWithoutExtension(project)}",
            Type = "dotnet-test",
            Project = project,
            Runner = EngineSettings.HasMtpInvocation(project) ? "mtp" : "vstest"
        };
    }

    private static IReadOnlyList<string> RemoveTestFilter(IReadOnlyList<string> arguments)
    {
        var unfiltered = new List<string>();
        for (var index = 0; index < arguments.Count; index++)
        {
            if (arguments[index].Equals("--filter", StringComparison.OrdinalIgnoreCase))
            {
                index++;
                continue;
            }

            unfiltered.Add(arguments[index]);
        }

        return unfiltered;
    }

    private static string[] BuildDiscoveryArguments(
        AcceptanceManifestCheck check,
        AcceptanceGateEngineSettings engineSettings,
        DotnetBuildEnvironment environment)
    {
        if (string.IsNullOrWhiteSpace(check.Project))
        {
            throw new InvalidDataException($"Acceptance check '{check.Name}' has no discovery project.");
        }

        if (UsesMicrosoftTestingPlatform(check))
        {
            var invocation = engineSettings.ResolveMtpInvocation(check.Project);
            // Discovery must mirror the execution-side unattended exclusion: lanes filter out
            // Category=HostIntegration tests, so listing them here would make the structural
            // coverage invariant report by-design-excluded tests as missing on every attempt.
            return UseDotnetHostForManagedExecutable(
            [
                invocation.ResolveExecutablePath(environment),
                "--no-ansi",
                "--progress",
                "off",
                "--list-tests",
                "--filter-not-trait",
                "Category=HostIntegration"
            ]);
        }

        var arguments = new List<string>
        {
            "dotnet",
            "test",
            check.Project,
            "--no-build",
            "--list-tests",
            "--filter",
            "Category!=HostIntegration"
        };
        return WithBuildEnvironmentArguments([.. arguments], environment);
    }

    private static string[] BuildUnattendedDiscoveryArguments(
        AcceptanceManifestCheck check,
        AcceptanceGateEngineSettings engineSettings,
        DotnetBuildEnvironment environment)
    {
        var arguments = BuildDiscoveryArguments(check, engineSettings, environment).ToList();
        if (UsesMicrosoftTestingPlatform(check))
        {
            arguments.Add("--filter-not-trait");
            arguments.Add("Category=AcceptanceOptIn");
        }
        else
        {
            var filterIndex = arguments.IndexOf("--filter");
            if (filterIndex < 0 || filterIndex == arguments.Count - 1)
            {
                throw new InvalidDataException(
                    $"Acceptance check '{check.Name}' produced no discovery filter.");
            }

            arguments[filterIndex + 1] += "&Category!=AcceptanceOptIn";
        }

        return [.. arguments];
    }

    private static string? ResolveMainWorktreePath(string worktreePath)
    {
        if (ResolveMainWorktreePathForTests is not null)
        {
            return ResolveMainWorktreePathForTests(worktreePath);
        }

        var output = ResolveGitText(worktreePath, "worktree", "list", "--porcelain");
        if (string.IsNullOrWhiteSpace(output))
        {
            return null;
        }

        string? currentPath = null;
        foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.StartsWith("worktree ", StringComparison.Ordinal))
            {
                currentPath = line["worktree ".Length..].Trim();
            }
            else if (line.Equals("branch refs/heads/main", StringComparison.Ordinal) &&
                !string.IsNullOrWhiteSpace(currentPath))
            {
                return currentPath;
            }
        }

        return null;
    }

    private static string[] ResolveDeletedTestFiles(string worktreePath, string project)
    {
        if (ResolveDeletedTestFilesForTests is not null)
        {
            return ResolveDeletedTestFilesForTests(worktreePath);
        }

        var output = ResolveGitText(worktreePath, "diff", "--name-status", "main...HEAD", "--");
        if (string.IsNullOrWhiteSpace(output))
        {
            return [];
        }

        return ParseDeletedTestFiles(
            output,
            project,
            destination => ResolveOwningProject(worktreePath, destination));
    }

    internal static string[] ParseDeletedTestFilesForTests(
        string output,
        string project,
        Func<string, string?> resolveOwningProject) =>
        ParseDeletedTestFiles(output, project, resolveOwningProject);

    private static string[] ParseDeletedTestFiles(
        string output,
        string project,
        Func<string, string?> resolveOwningProject)
    {
        var normalizedProject = NormalizePath(project);
        return output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split('\t'))
            .Where(parts =>
            {
                if (parts.Length < 2 || !IsTestFile(parts[1]))
                {
                    return false;
                }

                if (parts[0].Equals("D", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                if (parts.Length < 3 ||
                    !parts[0].StartsWith("R", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                var destinationProject = NormalizePath(resolveOwningProject(parts[2]));
                return !string.IsNullOrWhiteSpace(destinationProject) &&
                    !string.Equals(destinationProject, normalizedProject, StringComparison.OrdinalIgnoreCase);
            })
            .Select(parts => NormalizePath(parts[1])!)
            .ToArray();
    }

    private static string? ResolveOwningProject(string worktreePath, string path)
    {
        var root = Path.GetFullPath(worktreePath);
        var fullPath = Path.GetFullPath(Path.Combine(
            root,
            NormalizePath(path)!.Replace('/', Path.DirectorySeparatorChar)));
        var relativePath = Path.GetRelativePath(root, fullPath);
        if (Path.IsPathRooted(relativePath) ||
            relativePath.Equals("..", StringComparison.Ordinal) ||
            relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
        {
            return null;
        }

        var directory = Path.GetDirectoryName(fullPath);
        while (!string.IsNullOrWhiteSpace(directory))
        {
            if (Directory.Exists(directory))
            {
                var project = Directory.EnumerateFiles(directory, "*.csproj", SearchOption.TopDirectoryOnly)
                    .OrderBy(candidate => candidate, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault();
                if (project is not null)
                {
                    return NormalizePath(Path.GetRelativePath(root, project));
                }
            }

            if (directory.Equals(root, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            directory = Path.GetDirectoryName(directory);
        }

        return null;
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
            EngineSettings.ResolveCheckTimeout(null),
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
        // [Trait("Category","HostIntegration")] tag (covers any future such tests).
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

    private static AcceptanceCheckResult? TryClassifyManifestTrust(
        string worktreePath,
        IReadOnlyList<string>? changedFiles)
    {
        if (changedFiles is null ||
            !changedFiles.Any(path =>
                NormalizePath(path).Equals("config/acceptance-manifest.json", StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        var candidatePath = Path.Combine(worktreePath, "config", "acceptance-manifest.json");
        var trustedJson = ResolveGitText(worktreePath, "show", "main:config/acceptance-manifest.json");
        if (!File.Exists(candidatePath) || string.IsNullOrWhiteSpace(trustedJson))
        {
            return new AcceptanceCheckResult(
                "acceptance manifest trusted dimensions",
                false,
                1,
                "Trusted main acceptance manifest could not be compared; refusing candidate engine settings.",
                ResultSummary: "trusted manifest comparison unavailable");
        }

        var decision = RepositoryChangeClassifier.ClassifyAcceptanceManifestChange(
            trustedJson,
            File.ReadAllText(candidatePath));
        return decision.RequiresTrustedReview
            ? new AcceptanceCheckResult(
                "acceptance manifest trusted dimensions",
                false,
                1,
                decision.Evidence,
                ResultSummary: "operator review required")
            : null;
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

    private static DotnetBuildEnvironment ResolveExecutionEnvironment(
        GoalId? goalId,
        string attemptName,
        int? stableSlotIndex,
        DotnetBuildEnvironmentLease? buildLease)
    {
        // The lease limits concurrent builds; it no longer owns build or test artifacts.
        // Goal-scoped artifacts remain reusable across attempts, while ownerless commands use
        // an invocation-local environment.
        DotnetBuildEnvironment environment;
        if (buildLease is not null)
        {
            environment = buildLease.Environment;
        }
        else if (goalId is not null)
        {
            environment = DotnetBuildEnvironmentManager.ResolveGoalEnvironment(goalId);
        }
        else
        {
            environment = stableSlotIndex.HasValue
                ? DotnetBuildEnvironmentManager.CreateStableSlotAttempt(
                    stableSlotIndex.Value)
                : DotnetBuildEnvironmentManager.CreateAttempt(
                    null,
                    attemptName);
        }

        if (buildLease is null)
        {
            CurrentManagedRunEnvironmentScope.Value?.Track(environment);
        }

        return environment;
    }

    private static IDisposable PushOwnerResultsScope(
        string worktreePath,
        GoalId? goalId,
        string ownerKind)
    {
        if (!string.IsNullOrWhiteSpace(AcceptanceAttemptResultsPrefix))
        {
            return new RestoreAction(static () => { });
        }

        var repositoryRoot = Environment.GetEnvironmentVariable("MCG_ORCHESTRATOR_REPOSITORY_ROOT");
        repositoryRoot = ResolveOwnerResultsRepositoryRoot(
            string.IsNullOrWhiteSpace(repositoryRoot) ? worktreePath : repositoryRoot);

        var owner = goalId?.Value ?? "operator";
        var attemptRoot = OwnerResultsAttemptRoot(ownerKind);
        var directory = Path.Combine(
            repositoryRoot,
            ".orchestrator",
            attemptRoot,
            owner);
        Directory.CreateDirectory(directory);
        var prefix = Path.Combine(
            directory,
            $"{owner[..Math.Min(8, owner.Length)]}-{ownerKind}-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}");
        return PushAcceptanceAttemptResultsPrefix(prefix);
    }

    internal static string OwnerResultsAttemptRoot(string ownerKind) =>
        ownerKind.Equals("pre-review", StringComparison.Ordinal)
            ? "pre-review-evidence-attempts"
            : "acceptance-gate-attempts";

    internal static string ResolveOwnerResultsRepositoryRoot(string worktreePath)
    {
        var fullPath = Path.GetFullPath(worktreePath);
        var worktreeMarker =
            $"{Path.DirectorySeparatorChar}.orchestrator-worktrees{Path.DirectorySeparatorChar}";
        var markerIndex = fullPath.IndexOf(worktreeMarker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex > 0)
        {
            return fullPath[..markerIndex];
        }

        var candidate = new DirectoryInfo(fullPath);
        while (candidate is not null)
        {
            if (Directory.Exists(Path.Combine(candidate.FullName, ".git")))
            {
                return candidate.FullName;
            }

            candidate = candidate.Parent;
        }

        return Path.Combine(Path.GetTempPath(), "mcg-acceptance-owner-results");
    }

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
        if (string.IsNullOrWhiteSpace(check.Project))
        {
            throw new InvalidDataException($"Acceptance check '{check.Name}' uses MTP but has no project.");
        }

        var invocation = EngineSettings.ResolveMtpInvocation(check.Project);
        var executablePath = invocation.ResolveExecutablePath(environment);
        var resultsDirectory = Path.GetDirectoryName(telemetry.Paths[0])
            ?? Path.Combine(environment.ArtifactsPath, "TestResults");
        var trxFileName = Path.GetFileName(telemetry.Paths[0]);
        var args = invocation.Arguments
            .Select(argument => argument
                .Replace("{executable}", executablePath, StringComparison.Ordinal)
                .Replace("{resultsDirectory}", resultsDirectory, StringComparison.Ordinal)
                .Replace("{trxFileName}", trxFileName, StringComparison.Ordinal))
            .ToList();
        var filter = ExtractMtpCompatibleArguments(check.Arguments, args);
        if (!string.IsNullOrWhiteSpace(filter))
        {
            args.AddRange(TranslateMtpFilter(filter));
        }

        return UseDotnetHostForManagedExecutable(args);
    }

    private static string[] UseDotnetHostForManagedExecutable(IReadOnlyList<string> arguments) =>
        arguments.Count > 0 && arguments[0].EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
            ? ["dotnet", .. arguments]
            : [.. arguments];

    internal static string? ResolveGitText(string worktreePath, params string[] arguments)
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

            // Drain both pipes concurrently BEFORE waiting for exit. Waiting first deadlocks as soon
            // as the child's output exceeds the pipe buffer (~4KB): the child blocks writing, the
            // 5s wait expires, and the caller sees null. The acceptance manifest crossed that size
            // when the engine section landed, which turned every trusted-manifest read into a refusal.
            var standardOutputTask = process.StandardOutput.ReadToEndAsync();
            var standardErrorTask = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(5000))
            {
                try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
                try { process.WaitForExit(1000); } catch { /* best effort */ }
                try { Task.WhenAll(standardOutputTask, standardErrorTask).Wait(1000); } catch { /* best effort */ }
                return null;
            }

            Task.WhenAll(standardOutputTask, standardErrorTask).GetAwaiter().GetResult();
            return process.ExitCode == 0 ? standardOutputTask.Result : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return null;
        }
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

    internal static IEnumerable<string> TranslateMtpFilter(string filter)
    {
        foreach (var rawToken in Regex.Split(filter, @"[&|]"))
        {
            // A focused filter can be a parenthesized disjunction such as
            // "(FullyQualifiedName~A|FullyQualifiedName~B)|(FullyQualifiedName~C&Category!=X)".
            // MTP unions repeated --filter-class and intersects --filter-not-class/--filter-not-trait
            // exclusions, so the grouping parens carry no additional meaning at the token level and
            // are stripped before matching. (A per-group Category!= therefore widens to the whole
            // union, which is harmless because such traits are unique to a single mapped class.)
            var token = rawToken.Trim().Trim('(', ')').Trim();
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
                yield return $"*{fullyQualifiedName.Groups["value"].Value}*";
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

    private static string[] WithBuildEnvironmentArguments(string[] arguments, DotnetBuildEnvironment environment)
    {
        // The build-environment arguments are MSBuild-shaped (--artifacts-path, -maxcpucount,
        // -p:BuildInParallel=false) and only mean something to dotnet-driven commands. Appending them to a
        // direct MTP test-executable invocation corrupts the run: every post-landing gate produced an empty
        // <TestRun /> core-tests receipt and failed structural coverage (backlog 8af9b957, 2026-07-25).
        if (arguments.Length == 0 ||
            !arguments[0].Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            return arguments;
        }

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
        DotnetBuildEnvironment environment,
        string? resultsDirectoryOverride = null)
    {
        var attemptPrefix = AcceptanceAttemptResultsPrefix;
        var directory = !string.IsNullOrWhiteSpace(resultsDirectoryOverride)
            ? resultsDirectoryOverride
            : string.IsNullOrWhiteSpace(attemptPrefix)
                ? Path.Combine(environment.ArtifactsPath, "TestResults")
                : Path.GetDirectoryName(attemptPrefix);
        if (string.IsNullOrWhiteSpace(directory))
        {
            directory = Path.Combine(environment.ArtifactsPath, "TestResults");
        }

        var filePrefix = string.IsNullOrWhiteSpace(attemptPrefix)
            ? $"{environment.LeaseId}.{Slug(check.Name)}"
            : $"{Path.GetFileName(attemptPrefix)}.{Slug(check.Name)}";
        var fileName = BoundFileName(SanitizeFileName(filePrefix), ".trx");
        var path = Path.Combine(directory, fileName);
        return new DotnetTestTelemetry([path], []);
    }

    private static void PrepareTestTelemetryForRun(DotnetTestTelemetry telemetry)
    {
        foreach (var path in telemetry.Paths)
        {
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new IOException($"Unable to clear stale TRX before the test run: {path}", ex);
            }

            if (File.Exists(path))
            {
                throw new IOException($"Unable to clear stale TRX before the test run: {path}");
            }
        }
    }

    internal static IReadOnlyList<string>? CopyCompletedTestReceiptsToAttemptFolder(
        IReadOnlyList<string>? sourcePaths)
    {
        if (sourcePaths is null)
        {
            return null;
        }

        var attemptPrefix = Environment.GetEnvironmentVariable(AcceptanceAttemptTrxPrefixVariable);
        if (string.IsNullOrWhiteSpace(attemptPrefix))
        {
            return sourcePaths;
        }

        var receiptDirectory = Path.GetDirectoryName(attemptPrefix);
        if (string.IsNullOrWhiteSpace(receiptDirectory))
        {
            throw new InvalidOperationException(
                $"Acceptance attempt TRX prefix '{attemptPrefix}' does not identify an attempt receipt folder.");
        }

        var durablePaths = new string[sourcePaths.Count];
        for (var index = 0; index < sourcePaths.Count; index++)
        {
            var sourcePath = sourcePaths[index];
            if (!File.Exists(sourcePath))
            {
                durablePaths[index] = sourcePath;
                continue;
            }

            var destinationPath = Path.Combine(receiptDirectory, Path.GetFileName(sourcePath));
            try
            {
                Directory.CreateDirectory(receiptDirectory);
                if (!sourcePath.Equals(destinationPath, StringComparison.OrdinalIgnoreCase))
                {
                    var temporaryPath = $"{destinationPath}.{Guid.NewGuid():N}.tmp";
                    File.Copy(sourcePath, temporaryPath, overwrite: true);
                    File.Move(temporaryPath, destinationPath, overwrite: true);
                }

                durablePaths[index] = destinationPath;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new IOException(
                    $"Failed to preserve completed test receipt from '{sourcePath}' to attempt folder '{destinationPath}'.",
                    ex);
            }
        }

        return durablePaths;
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

    private static string BuildMtpFailureOutput(
        string checkName,
        CommandResult result,
        DotnetTestTelemetry telemetry)
    {
        var details = new List<string>();
        var commandOutput = result.TimedOut
            ? BuildTimeoutOutput(result)
            : TailOutput(result.Output);
        if (!string.IsNullOrWhiteSpace(commandOutput))
        {
            details.Add(commandOutput);
        }

        var trxPaths = telemetry.Paths.Where(File.Exists).ToArray();
        if (trxPaths.Length == 0)
        {
            details.Add(
                $"[FAIL] {checkName}: failed — no TRX produced (shard was killed or crashed before reporter flushed)");
            return string.Join(Environment.NewLine, details);
        }

        var failures = new List<string>();
        foreach (var trxPath in trxPaths)
        {
            try
            {
                failures.AddRange(ExtractTrxFailureEvidence(trxPath));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
            {
                details.Add(
                    $"[FAIL] {checkName}: failed — TRX found but could not be read ({FirstNonEmptyLine(ex.Message)})");
                return string.Join(Environment.NewLine, details);
            }
        }

        if (failures.Count == 0)
        {
            details.Add(
                $"[FAIL] {checkName}: failed — TRX found but contained no failure records (process may have exited before tests ran)");
        }
        else
        {
            details.AddRange(failures);
        }

        return string.Join(Environment.NewLine, details);
    }

    internal static IReadOnlyList<string> ExtractTrxFailureEvidence(string trxPath)
    {
        var document = XDocument.Load(trxPath, LoadOptions.None);
        var definitionsByTestId = document
            .Descendants()
            .Where(element =>
                element.Name.LocalName.Equals("UnitTest", StringComparison.Ordinal) &&
                !string.IsNullOrWhiteSpace(element.Attribute("id")?.Value))
            .GroupBy(element => element.Attribute("id")!.Value, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        return document
            .Descendants()
            .Where(element =>
                element.Name.LocalName.Equals("UnitTestResult", StringComparison.Ordinal) &&
                string.Equals(
                    element.Attribute("outcome")?.Value,
                    "Failed",
                    StringComparison.OrdinalIgnoreCase))
            .Select(result =>
            {
                definitionsByTestId.TryGetValue(
                    result.Attribute("testId")?.Value ?? string.Empty,
                    out var definition);
                var testName = ResolveTrxTestName(result, definition);
                var message = result
                    .Descendants()
                    .FirstOrDefault(element =>
                        element.Name.LocalName.Equals("Message", StringComparison.Ordinal) &&
                        element.Ancestors().Any(ancestor =>
                            ancestor.Name.LocalName.Equals("ErrorInfo", StringComparison.Ordinal)))
                    ?.Value;
                return $"[FAIL] {testName}: {FirstNonEmptyLine(message) ?? "failure message unavailable"}";
            })
            .ToArray();
    }

    internal static IReadOnlyList<string> ExtractTrxFailureIdentities(string trxPath)
    {
        var document = XDocument.Load(trxPath, LoadOptions.None);
        var definitionsByTestId = document
            .Descendants()
            .Where(element =>
                element.Name.LocalName.Equals("UnitTest", StringComparison.Ordinal) &&
                !string.IsNullOrWhiteSpace(element.Attribute("id")?.Value))
            .GroupBy(element => element.Attribute("id")!.Value, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        return document
            .Descendants()
            .Where(element =>
                element.Name.LocalName.Equals("UnitTestResult", StringComparison.Ordinal) &&
                string.Equals(
                    element.Attribute("outcome")?.Value,
                    "Failed",
                    StringComparison.OrdinalIgnoreCase))
            .Select(result =>
            {
                definitionsByTestId.TryGetValue(
                    result.Attribute("testId")?.Value ?? string.Empty,
                    out var definition);
                return ResolveTrxTestName(result, definition);
            })
            .Where(identity => !string.IsNullOrWhiteSpace(identity))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static IReadOnlyList<string> ExtractTrxFailureIdentities(IEnumerable<string>? trxPaths)
    {
        if (trxPaths is null)
        {
            return [];
        }

        var identities = new List<string>();
        foreach (var trxPath in trxPaths.Where(File.Exists))
        {
            try
            {
                identities.AddRange(ExtractTrxFailureIdentities(trxPath));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
            {
                // An unreadable receipt is evidence-inconclusive. Never infer a code failure from its output tail.
            }
        }

        return identities
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static string ResolveTrxTestName(XElement result, XElement? definition)
    {
        var testName = result.Attribute("testName")?.Value?.Trim();
        var displayName = result.Descendants()
            .Concat(definition?.Descendants() ?? [])
            .Where(element =>
                element.Name.LocalName.Equals("DisplayName", StringComparison.Ordinal) ||
                element.Name.LocalName.Equals("Description", StringComparison.Ordinal))
            .Select(element => element.Value.Trim())
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        displayName ??= definition?.Attribute("name")?.Value?.Trim();

        if (!string.IsNullOrWhiteSpace(displayName) &&
            (string.IsNullOrWhiteSpace(testName) ||
                (LooksLikeQualifiedTestName(testName) && displayName.Length < testName.Length)))
        {
            return displayName;
        }

        if (!string.IsNullOrWhiteSpace(testName))
        {
            return testName;
        }

        var testMethod = definition?.Descendants()
            .FirstOrDefault(element => element.Name.LocalName.Equals("TestMethod", StringComparison.Ordinal));
        var className = testMethod?.Attribute("className")?.Value?.Trim();
        var methodName = testMethod?.Attribute("name")?.Value?.Trim();
        if (!string.IsNullOrWhiteSpace(className) && !string.IsNullOrWhiteSpace(methodName))
        {
            return $"{className}.{methodName}";
        }

        return result.Attribute("testId")?.Value?.Trim() ?? "unknown test";
    }

    private static bool LooksLikeQualifiedTestName(string value) =>
        value.Contains('+', StringComparison.Ordinal) ||
        value.Count(ch => ch == '.') >= 2;

    private static string? FirstNonEmptyLine(string? value) =>
        value?
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(line => !string.IsNullOrWhiteSpace(line));

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

    // A check names its own artifacts, and a focused-evidence check is named after its entire filter
    // expression, so the generated name grows with the request rather than staying bounded. Windows raises
    // ERROR_INVALID_NAME - "The filename, directory name, or volume label syntax is incorrect" - once a single
    // path component passes the 255-character NTFS limit, and a four-class evidence request produced a
    // 324-character TRX name. That killed the run inside MTP's TRX writer, where the IOException is unhandled:
    // the host exited 0xE0434352 and the gate booked the crash as a test failure. Bound every generated
    // component here rather than at the callers, and keep a stable hash of the full name so two long names
    // still land on two files.
    //
    // The 200 leaves headroom under 255 for suffixes callers append to a resolved path, the widest being the
    // ".{guid:N}.tmp" staging name used when copying receipts into an attempt folder.
    private const int MaxGeneratedFileNameLength = 200;

    private static string BoundFileName(string stem, string suffix)
    {
        if (stem.Length + suffix.Length <= MaxGeneratedFileNameLength)
        {
            return stem + suffix;
        }

        var hash = ShortHash(stem);
        var keep = MaxGeneratedFileNameLength - suffix.Length - hash.Length - 1;
        return keep <= 0
            ? hash + suffix
            : $"{stem[..keep].TrimEnd('-', '.')}-{hash}{suffix}";
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
        $"elapsed={FormatTimeout(result.Elapsed ?? result.Timeout ?? EngineSettings.ResolveCheckTimeout(null))} budget={FormatTimeout(result.Timeout ?? EngineSettings.ResolveCheckTimeout(null))}";

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
        var heartbeatPath = ResolveGateHeartbeatPath(
            check,
            environment,
            stableSlotIndex,
            worktreePath);

        return new GateHeartbeatContext(
            goalId?.Value,
            "verification-check",
            check.Name,
            stableSlotIndex,
            heartbeatPath,
            ResolveStableSlotHeartbeatMirrorPath(environment, heartbeatPath),
            string.Join(' ', arguments.Select(QuoteForDisplay)),
            environment?.RootPath);
    }

    // An attempt can run several checks after releasing its build permit, and unrelated goals can hash to
    // the same permit index. Mirror each live run to a path keyed by its attempt-scoped primary heartbeat
    // so terminal updates can never overwrite a sibling. Gate-status enumerates these run-scoped mirrors.
    private static string? ResolveStableSlotHeartbeatMirrorPath(
        DotnetBuildEnvironment? environment,
        string primaryHeartbeatPath)
    {
        if (environment?.BuildPermitIndex is not { } slotIndex ||
            slotIndex < 0 ||
            slotIndex >= DotnetBuildEnvironmentManager.StableSlotCount)
        {
            return null;
        }

        var stableSlotPath = GateHeartbeatArtifacts.GetStableSlotPath(slotIndex);
        return primaryHeartbeatPath.Equals(stableSlotPath, StringComparison.OrdinalIgnoreCase)
            ? null
            : GateHeartbeatArtifacts.GetRunScopedStableSlotPath(slotIndex, primaryHeartbeatPath);
    }

    private static string ResolveGateHeartbeatPath(
        AcceptanceManifestCheck check,
        DotnetBuildEnvironment? environment,
        int? stableSlotIndex,
        string? worktreePath)
    {
        var attemptPrefix = AcceptanceAttemptResultsPrefix;
        if (!string.IsNullOrWhiteSpace(attemptPrefix))
        {
            var attemptDirectory = Path.GetDirectoryName(attemptPrefix);
            var stem = $"{Path.GetFileName(attemptPrefix)}.{Slug(check.Name)}-{ShortHash(check.Name)}";
            var heartbeatFileName = BoundFileName(stem, $".{GateHeartbeatArtifacts.FileName}");
            return string.IsNullOrWhiteSpace(attemptDirectory)
                ? heartbeatFileName
                : Path.Combine(attemptDirectory, heartbeatFileName);
        }

        if (environment is not null)
        {
            return Path.Combine(environment.ArtifactsPath, GateHeartbeatArtifacts.FileName);
        }

        return stableSlotIndex.HasValue
            ? GateHeartbeatArtifacts.GetStableSlotPath(stableSlotIndex.Value)
            : GateHeartbeatArtifacts.GetManualPath(
                worktreePath ?? Path.Combine(Path.GetTempPath(), "mcg-acceptance-owner-results"));
    }

    internal static string ResolveGateHeartbeatPathForTests(
        string checkName,
        DotnetBuildEnvironment environment,
        int? stableSlotIndex = null) =>
        ResolveGateHeartbeatPath(
            new AcceptanceManifestCheck { Name = checkName },
            environment,
            stableSlotIndex,
            worktreePath: null);

    internal static string ResolveTrxPathForTests(string checkName, DotnetBuildEnvironment environment) =>
        ResolveTestTelemetry(new AcceptanceManifestCheck { Name = checkName }, environment).Paths[0];

    // Test seam: drive one real gate-heartbeat "running" beat (and optionally a terminal beat) through
    // the production context creation + runtime writer, so a test can prove that gate-status's
    // stable-slot read path (GateHeartbeatArtifacts.ReadStableSlots) observes a live gate. Returns the
    // primary (attempt-scoped) heartbeat path and the stable-slot mirror path the context resolved.
    internal static (string PrimaryPath, string? StableSlotPath) WriteGateHeartbeatBeatForTests(
        string checkName,
        GoalId? goalId,
        DotnetBuildEnvironment environment,
        int processId,
        string stdoutPath,
        string stderrPath,
        string? finalState = null)
    {
        var context = CreateGateHeartbeatContext(
            new AcceptanceManifestCheck { Name = checkName },
            ["dotnet", "test"],
            Path.GetTempPath(),
            goalId,
            stableSlotIndex: null,
            environment);
        var runtime = new GateHeartbeatRuntime(
            context,
            processId,
            stdoutPath,
            stderrPath,
            TimeSpan.FromMinutes(1));
        runtime.WriteRunning(emitProgress: false);
        if (finalState is not null)
        {
            runtime.WriteFinal(finalState, childPid: processId, exitCode: 0);
        }

        return (context.HeartbeatPath, context.StableSlotHeartbeatPath);
    }

    // The acceptance suite verifies the CODE and must run hermetically — NOT under whatever runtime config
    // the operator's conductor happened to be launched with. Anything surviving here is an ambient input the
    // gate silently depends on, which makes its verdict a statement about the launcher. Proven leaks include:
    //
    // 1. Worker-dispatch vars (MCG_WORKER_SANDBOX and friends) control how real workers launch (low
    //    integrity). Several tests read WorkerSandboxOptions.FromEnvironment(), so running `conduct` with
    //    MCG_WORKER_SANDBOX=1 flipped those tests' expected sandbox mode — failing acceptance INSIDE the
    //    watch while the same suite passed when `acceptance` ran standalone.
    // 2. Handoff coordination vars made CLI grandchildren skip startup cleanup, masking a real backlog-list
    //    regression. Acceptance-scope overrides then demonstrated why extending that deny-list is insufficient:
    //    either override could bypass the build-system-file rule and false-green its regression test.
    // Identity for commits a verification child creates (the pre-landing rebase replays commits). Fixed and
    // attributable on purpose: a gate must not author commits as the operator, and it must not depend on the
    // operator having configured git at all.
    private const string HermeticGitIdentityName = "MCG Acceptance Gate";
    private const string HermeticGitIdentityEmail = "acceptance-gate@localhost";
    internal const string LegacyOwnedStartNegativeControlVariable =
        "MCG_TEST_ONLY_ACCEPTANCE_LEGACY_START_THEN_ATTACH";

    internal static void ConfigureHermeticVerificationEnvironment(
        IDictionary<string, string?> environment,
        string repositoryRoot,
        string? buildEnvironmentRoot = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        environment.TryGetValue("NUGET_PACKAGES", out var nugetPackages);
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        var allowed = environment
            .Where(pair => IsInheritedHermeticVerificationEnvironmentVariable(pair.Key))
            .ToArray();

        // This is deliberately an allow-list, not another enumeration of known-dangerous inputs. Verification
        // children have accumulated behavior switches, provider credentials, control-plane tokens, and attempt
        // identity outside MCG_*; missing any one of them can turn a real failure green or emit real traffic.
        environment.Clear();
        foreach (var pair in allowed)
        {
            environment[pair.Key] = pair.Value;
        }

        var profileRoot = Path.Combine(Path.GetTempPath(), "mcg-hvp");
        Directory.CreateDirectory(profileRoot);
        nugetPackages = string.IsNullOrWhiteSpace(nugetPackages)
            ? Path.Combine(string.IsNullOrWhiteSpace(userProfile) ? profileRoot : userProfile, ".nuget", "packages")
            : nugetPackages;
        environment["HOME"] = profileRoot;
        environment["USERPROFILE"] = profileRoot;
        environment["DOTNET_CLI_HOME"] = profileRoot;

        // Git resolves user.name/user.email from $HOME/.gitconfig, and the profile root above is empty, so a
        // verification child inherits NO GIT IDENTITY. Read-only git is unaffected, which is why this hid for
        // so long - but the pre-landing rebase REPLAYS COMMITS, and git exits 128 ("unable to auto-detect
        // email address") the moment it needs an identity. That surfaced as
        // "Rebase of goal/X onto main failed ... (exit=128; stderr=Rebasing (1/3)" on every landing attempt:
        // git printed its progress, hit the first commit, and died. A manual rebase always worked because an
        // operator shell still has the real HOME.
        //
        // Supply a DETERMINISTIC identity rather than borrowing the operator's global config. Pointing
        // GIT_CONFIG_GLOBAL at the real ~/.gitconfig would restore identity but reintroduce exactly the
        // ambient-input class this function exists to forbid - any [core], [merge], [rebase] or [alias] the
        // operator later adds would silently steer gate behaviour - and it would still miss a host that keeps
        // identity under $XDG_CONFIG_HOME. These four variables outrank every config file, so identity is
        // guaranteed regardless of where the operator's happens to live. Same shape as the canary's inline
        // -c user.name/-c user.email.
        environment["GIT_AUTHOR_NAME"] = HermeticGitIdentityName;
        environment["GIT_AUTHOR_EMAIL"] = HermeticGitIdentityEmail;
        environment["GIT_COMMITTER_NAME"] = HermeticGitIdentityName;
        environment["GIT_COMMITTER_EMAIL"] = HermeticGitIdentityEmail;
        // A relocated DOTNET_CLI_HOME reads as a first use, and the SDK responds by PERSISTING
        // "$DOTNET_CLI_HOME\.dotnet\tools" into the operator's HKCU\Environment\Path. That is ambient
        // state written by a function whose entire purpose is to write none, so it must be suppressed
        // here rather than cleaned up afterwards. DOTNET_SKIP_FIRST_TIME_EXPERIENCE does NOT suppress
        // it - measured on .NET 10, the entry is still written; the SDK dropped that variable years ago.
        environment["DOTNET_ADD_GLOBAL_TOOLS_TO_PATH"] = "0";
        environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        environment["DOTNET_GENERATE_ASPNET_CERTIFICATE"] = "false";
        environment["DOTNET_NOLOGO"] = "1";
        environment["NUGET_PACKAGES"] = nugetPackages;
        if (!string.IsNullOrWhiteSpace(buildEnvironmentRoot))
        {
            // NUGET_PACKAGES isolates restored packages, but NuGet's HTTP cache is derived from
            // LOCALAPPDATA on Windows. Verification deliberately preserves the real LOCALAPPDATA, so
            // concurrent arms otherwise race on %LOCALAPPDATA%\NuGet\v3-cache (notably vuln_index.dat-new).
            // Keep the cache inside the build lease so candidate and baseline restores cannot share locks,
            // and so invocation-local baseline cleanup removes it with the rest of the build environment.
            var nugetHttpCachePath = Path.Combine(buildEnvironmentRoot, "nuget-http-cache");
            Directory.CreateDirectory(nugetHttpCachePath);
            environment["NUGET_HTTP_CACHE_PATH"] = nugetHttpCachePath;
        }
        if (OperatingSystem.IsWindows())
        {
            var profileRootPath = Path.GetPathRoot(profileRoot) ?? string.Empty;
            // APPDATA/LOCALAPPDATA stay pinned to the REAL per-user locations, and are set explicitly rather
            // than left to derive from the repointed USERPROFILE above. Moving them into the profile broke
            // three different things in one day, each in its own way, because everything Windows derives from
            // LOCALAPPDATA moved with it: the per-user temp location (%LOCALAPPDATA%\Temp) vanished, paths
            // built through it outgrew MAX_PATH and failed git object writes, and the PowerShell 7 execution
            // alias under %LOCALAPPDATA%\Microsoft\WindowsApps stopped resolving so callers silently degraded
            // to Windows PowerShell 5.1. Isolation of credentials and caches is already achieved by HOME,
            // USERPROFILE, DOTNET_CLI_HOME and NUGET_PACKAGES; relocating LOCALAPPDATA added no isolation the
            // others do not, and its blast radius is every path anything derives from it.
            // This function MUST BE IDEMPOTENT, because it runs at two nested levels: the conductor
            // configures the __acceptance-gate-attempt child, and that child - already living inside the
            // hermetic environment, with USERPROFILE repointed at the profile root - configures each lane in
            // turn. GetFolderPath expands the REG_EXPAND_SZ literal "%USERPROFILE%\AppData\Local" against the
            // CALLING process, so at the inner level it no longer returns the real per-user folder: it
            // returns <profile-root>\AppData\Local, and the lanes inherited THAT as their LOCALAPPDATA. Every
            // path derived from it then nested one level deeper per hop, which is where the observed
            // ...\mcg-hvp\AppData\Local\Temp\Low\mcg-tests came from, and it is why gate lanes kept resolving
            // Windows PowerShell 5.1 instead of the per-user pwsh 7 install even after WorkerShell learned to
            // read the variable - the variable it read was already redirected.
            //
            // Prefer the INHERITED variable, which the outer level pinned to the real location, and fall back
            // to the known folder only at the outermost level where no variable exists yet.
            var (appData, localAppData) = ResolvePerUserFolders(
                Environment.GetEnvironmentVariable("APPDATA"),
                Environment.GetEnvironmentVariable("LOCALAPPDATA"),
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
            // Repointing LOCALAPPDATA silently moves every path DERIVED from it, and the Windows per-user
            // temp location is %LOCALAPPDATA%\Temp. Callers that resolve their own temp root that way - the
            // test assembly's temp redirect does exactly this - then land under a directory that exists only
            // if we make it. Leaving it absent produced
            // "unable to write file ...\mcg-hvp\AppData\Local\Temp" and failed
            // whole lanes on environment construction rather than on the code being verified. The parent
            // dirs above are created for the same reason; this is the one that was missed.
            Directory.CreateDirectory(Path.Combine(localAppData, "Temp"));
            environment["HOMEDRIVE"] = profileRootPath.TrimEnd(Path.DirectorySeparatorChar);
            environment["HOMEPATH"] = Path.DirectorySeparatorChar +
                profileRoot[profileRootPath.Length..].TrimStart(Path.DirectorySeparatorChar);
            environment["APPDATA"] = appData;
            environment["LOCALAPPDATA"] = localAppData;
            var powershellDirectory = Path.Combine(profileRoot, "powershell");
            Directory.CreateDirectory(powershellDirectory);
            environment["PSModuleAnalysisCachePath"] = Path.Combine(powershellDirectory, "ModuleAnalysisCache");
        }

        environment["MCG_ORCHESTRATOR_REPOSITORY_ROOT"] = Path.GetFullPath(repositoryRoot);
    }

    /// <summary>
    /// Chooses the per-user APPDATA/LOCALAPPDATA this environment should carry, preferring the INHERITED
    /// variables over the known-folder API. Pure so the nested-level behaviour is assertable: the known-folder
    /// values cannot be varied in-process (Windows resolves them from the token, not from a mutated
    /// USERPROFILE), so a test that tried to simulate nesting by setting environment variables would pass
    /// with or without the fix.
    /// </summary>
    internal static (string AppData, string LocalAppData) ResolvePerUserFolders(
        string? appDataVariable,
        string? localAppDataVariable,
        string appDataKnownFolder,
        string localAppDataKnownFolder) =>
        (string.IsNullOrWhiteSpace(appDataVariable) ? appDataKnownFolder : appDataVariable,
         string.IsNullOrWhiteSpace(localAppDataVariable) ? localAppDataKnownFolder : localAppDataVariable);

    internal static bool IsHermeticVerificationEnvironmentVariable(string name) =>
        IsInheritedHermeticVerificationEnvironmentVariable(name) ||
        name.Equals("HOME", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("USERPROFILE", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("HOMEDRIVE", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("HOMEPATH", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("APPDATA", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("LOCALAPPDATA", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("PSModuleAnalysisCachePath", StringComparison.OrdinalIgnoreCase) ||
        // The four deterministic git identity variables this function sets. They are DECLARED here rather
        // than inherited: the allow-list is what the hermetic environment is permitted to contain, so every
        // variable ConfigureHermeticVerificationEnvironment writes must be nameable here or the gate's own
        // hermeticity assertion fails on it.
        name.Equals("GIT_AUTHOR_NAME", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("GIT_AUTHOR_EMAIL", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("GIT_COMMITTER_NAME", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("GIT_COMMITTER_EMAIL", StringComparison.OrdinalIgnoreCase) ||
        name.Equals(AcceptanceAttemptTrxPrefixVariable, StringComparison.OrdinalIgnoreCase) ||
        name.Equals("MCG_ORCHESTRATOR_REPOSITORY_ROOT", StringComparison.OrdinalIgnoreCase);

    private static bool IsInheritedHermeticVerificationEnvironmentVariable(string name) =>
        name.Equals("PATH", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("PATHEXT", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("SystemRoot", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("WINDIR", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("COMSPEC", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("ProgramFiles", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("ProgramFiles(x86)", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("ProgramW6432", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("TEMP", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("TMP", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("TMPDIR", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("DOTNET_", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("NUGET_", StringComparison.OrdinalIgnoreCase);

    private static async Task<CommandResult> RunProcessAsync(
        string[] arguments,
        string workingDirectory,
        TimeSpan commandTimeout,
        CancellationToken cancellationToken)
    {
        // Keep the shell command semantics, but own the capture file offsets in this process. The
        // drain keeps consuming after the cap so a noisy child cannot block or grow the files.
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
        var stdoutPipeName = OperatingSystem.IsWindows() ? $"mcg-acc-{Guid.NewGuid():N}-out" : null;
        var stderrPipeName = OperatingSystem.IsWindows() ? $"mcg-acc-{Guid.NewGuid():N}-err" : null;
        var startInfo = BuildAcceptanceProcessStartInfo(
            arguments,
            workingDirectory,
            stdoutPipeName is null ? null : $@"\\.\pipe\{stdoutPipeName}",
            stderrPipeName is null ? null : $@"\\.\pipe\{stderrPipeName}");
        CancellationTokenSource? captureDrainCts = null;
        Task<CaptureLimitResult>[]? captureDrains = null;
        Stream[]? captureSources = null;

        ConfigureHermeticVerificationEnvironment(
            startInfo.Environment,
            workingDirectory,
            heartbeatContext?.BuildEnvironmentRoot);

        int? startedProcessId = null;
        try
        {
            captureDrainCts = new CancellationTokenSource();
            Task[]? captureConnections = null;
            if (stdoutPipeName is not null && stderrPipeName is not null)
            {
                var stdoutPipe = CreateCapturePipe(stdoutPipeName);
                var stderrPipe = CreateCapturePipe(stderrPipeName);
                captureSources = [stdoutPipe, stderrPipe];
                var stdoutConnection = stdoutPipe.WaitForConnectionAsync(captureDrainCts.Token);
                var stderrConnection = stderrPipe.WaitForConnectionAsync(captureDrainCts.Token);
                captureConnections = [stdoutConnection, stderrConnection];
                // Begin accepting and draining before process start. The test-only legacy
                // start-then-attach control waits for the child to exit inside
                // StartAcceptanceProcess; delaying the drains until that method returned could
                // fill the named-pipe buffer and deadlock the child before the expected attach
                // failure was observed.
                captureDrains =
                [
                    ConnectAndDrainCappedCaptureAsync(
                        stdoutPipe,
                        stdoutConnection,
                        stdoutPath,
                        EngineSettings.OutputCaptureLimitBytes,
                        () => DateTimeOffset.UtcNow,
                        onLimitReached: null,
                        captureDrainCts.Token),
                    ConnectAndDrainCappedCaptureAsync(
                        stderrPipe,
                        stderrConnection,
                        stderrPath,
                        EngineSettings.OutputCaptureLimitBytes,
                        () => DateTimeOffset.UtcNow,
                        onLimitReached: null,
                        captureDrainCts.Token)
                ];
            }

            using var process = StartAcceptanceProcess(startInfo, workingDirectory);
            startedProcessId = process.Id;
            if (captureConnections is not null)
            {
                await Task.WhenAll(captureConnections)
                    .WaitAsync(CaptureDrainTimeout, cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                captureSources =
                [
                    process.StandardOutput.BaseStream,
                    process.StandardError.BaseStream
                ];
                captureDrains =
                [
                    DrainCappedCaptureAsync(
                        captureSources[0],
                        stdoutPath,
                        EngineSettings.OutputCaptureLimitBytes,
                        () => DateTimeOffset.UtcNow,
                        onLimitReached: null,
                        cancellationToken: captureDrainCts.Token),
                    DrainCappedCaptureAsync(
                        captureSources[1],
                        stderrPath,
                        EngineSettings.OutputCaptureLimitBytes,
                        () => DateTimeOffset.UtcNow,
                        onLimitReached: null,
                        cancellationToken: captureDrainCts.Token)
                ];
            }
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

            var captureResults = await CompleteCaptureDrainsAsync(
                process,
                captureDrains,
                captureDrainCts,
                captureSources).ConfigureAwait(false);
            captureDrains = null;
            captureDrainCts.Dispose();
            captureDrainCts = null;
            DisposeCaptureSources(captureSources);
            captureSources = null;

            foreach (var capture in captureResults.Where(result => result.LimitReached))
            {
                EmitCaptureLimitReached(heartbeatContext, capture.Path, EngineSettings.OutputCaptureLimitBytes);
            }

            var cappedPaths = captureResults
                .Where(result => result.LimitReached)
                .Select(result => result.Path)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var stdout = await ReadCapturedFileWithRetryAsync(
                stdoutPath,
                cappedPaths.Contains(stdoutPath)).ConfigureAwait(false);
            var stderr = await ReadCapturedFileWithRetryAsync(
                stderrPath,
                cappedPaths.Contains(stderrPath)).ConfigureAwait(false);
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
                heartbeat = null;
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
            if (captureDrainCts is not null)
            {
                await CancelCaptureDrainsAsync(
                    captureDrainCts,
                    captureDrains,
                    captureSources).ConfigureAwait(false);
                captureDrainCts.Dispose();
            }

            if (heartbeatCts is not null)
            {
                try { await heartbeatCts.CancelAsync().ConfigureAwait(false); } catch { }
                if (heartbeatTask is not null)
                {
                    try { await heartbeatTask.ConfigureAwait(false); } catch { }
                }

                heartbeatCts.Dispose();
            }

            heartbeat?.WriteFinal("failed", childPid: startedProcessId, exitCode: null);

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

    private static Process StartAcceptanceProcess(ProcessStartInfo startInfo, string workingDirectory)
    {
        if (OperatingSystem.IsWindows() &&
            string.Equals(
                Environment.GetEnvironmentVariable(LegacyOwnedStartNegativeControlVariable),
                "wait-for-fast-exit",
                StringComparison.Ordinal))
        {
            // Cross-process rule-(l) control only: replay the former production sequence through the
            // __acceptance-gate-attempt child, and deterministically expose its start-then-attach window.
            // The external test sets this variable only on that one child process.
            var legacyProcess = ProcessTreeGuiSuppression.Start(startInfo);
            try
            {
                if (!legacyProcess.WaitForExit(TimeSpan.FromSeconds(30)))
                {
                    throw new TimeoutException("Legacy owned-start negative-control child did not exit.");
                }

                WorkerProcessJobs.RegisterOrThrow(
                    legacyProcess,
                    $"acceptance:{workingDirectory}");
                return legacyProcess;
            }
            catch
            {
                legacyProcess.Dispose();
                throw;
            }
        }

        return WorkerProcessJobs.StartRegisteredOrThrow(
            startInfo,
            $"acceptance:{workingDirectory}");
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

    private static string BuildShellCommand(string[] arguments, Func<string, string> quote) =>
        string.Join(' ', arguments.Select(quote));

    private static string QuoteForCmd(string value) =>
        $"\"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\"";

    private static string QuoteForPosix(string value) =>
        $"'{value.Replace("'", "'\\''", StringComparison.Ordinal)}'";

    internal static async Task<string> ReadFileWithRetryAsync(string path, int? maximumBytes = null)
    {
        // A reparented grandchild may still hold the file's write handle; open shared and tolerate
        // transient locks. The output we need (the child's own writes) is already flushed on exit.
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                using var stream = new FileStream(
                    path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var buffer = new MemoryStream();
                if (maximumBytes is { } limit && stream.Length > limit)
                {
                    const string spliceMarker = "\n[... captured output omitted ...]\n";
                    var markerBytes = Encoding.UTF8.GetByteCount(spliceMarker);
                    var contentBytes = Math.Max(2, limit - markerBytes);
                    var headBytes = Math.Max(1, contentBytes * 3 / 4);
                    var tailBytes = Math.Max(1, contentBytes - headBytes);
                    using var head = new MemoryStream();
                    using var tail = new MemoryStream();
                    await CopyAtMostAsync(stream, head, headBytes).ConfigureAwait(false);
                    stream.Seek(-Math.Min(tailBytes, stream.Length), SeekOrigin.End);
                    await CopyAtMostAsync(stream, tail, tailBytes).ConfigureAwait(false);
                    return DecodeCapturedWindow(head.GetBuffer().AsSpan(0, checked((int)head.Length)), trimLeading: false) +
                        spliceMarker +
                        DecodeCapturedWindow(tail.GetBuffer().AsSpan(0, checked((int)tail.Length)), trimLeading: true);
                }
                else
                {
                    await stream.CopyToAsync(buffer).ConfigureAwait(false);
                }
                return DecodeCapturedOutput(buffer.GetBuffer().AsSpan(0, checked((int)buffer.Length)));
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

    internal static Task<string> ReadCapturedFileWithRetryAsync(string path, bool captureLimitReached) =>
        ReadFileWithRetryAsync(path, captureLimitReached ? CappedOutputPreviewBytes : null);

    internal static ProcessStartInfo BuildAcceptanceProcessStartInfo(
        string[] arguments,
        string workingDirectory,
        string? stdoutRedirectTarget = null,
        string? stderrRedirectTarget = null)
    {
        var startInfo = new ProcessStartInfo
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory
        };

        if (OperatingSystem.IsWindows())
        {
            if (string.IsNullOrWhiteSpace(stdoutRedirectTarget) != string.IsNullOrWhiteSpace(stderrRedirectTarget))
            {
                throw new ArgumentException("Both capture redirection targets must be provided together.");
            }

            startInfo.FileName = "cmd.exe";
            var command = BuildShellCommand(arguments, QuoteForCmd);
            if (!string.IsNullOrWhiteSpace(stdoutRedirectTarget))
            {
                command = $"{command} > {QuoteForCmd(stdoutRedirectTarget)} 2> {QuoteForCmd(stderrRedirectTarget!)}";
            }
            else
            {
                // Retain the managed-pipe form for focused capture tests and non-owned callers.
                startInfo.RedirectStandardOutput = true;
                startInfo.RedirectStandardError = true;
            }

            // cmd /c strips one surrounding quote pair, so wrap the whole command once.
            startInfo.Arguments = $"/c \"{command}\"";
        }
        else
        {
            startInfo.RedirectStandardOutput = true;
            startInfo.RedirectStandardError = true;
            startInfo.FileName = "/bin/sh";
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add(BuildShellCommand(arguments, QuoteForPosix));
        }

        return startInfo;
    }

    private static NamedPipeServerStream CreateCapturePipe(string pipeName) =>
        new(
            pipeName,
            PipeDirection.In,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);

    private static async Task<CaptureLimitResult> ConnectAndDrainCappedCaptureAsync(
        NamedPipeServerStream source,
        Task connection,
        string path,
        long limitBytes,
        Func<DateTimeOffset> utcNow,
        Action? onLimitReached,
        CancellationToken cancellationToken)
    {
        await connection.ConfigureAwait(false);
        return await DrainCappedCaptureAsync(
            source,
            path,
            limitBytes,
            utcNow,
            onLimitReached,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<CaptureLimitResult>> CompleteCaptureDrainsAsync(
        Process process,
        Task<CaptureLimitResult>[] captureDrains,
        CancellationTokenSource captureDrainCts,
        IReadOnlyList<Stream> captureSources)
    {
        try
        {
            return await Task.WhenAll(captureDrains)
                .WaitAsync(CaptureDrainTimeout)
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            await captureDrainCts.CancelAsync().ConfigureAwait(false);
            try { WorkerProcessJobs.TryKillOrFallback(process.Id, out _); } catch { /* best effort */ }
            try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
            DisposeCaptureSources(captureSources);
            return await Task.WhenAll(captureDrains)
                .WaitAsync(CaptureDrainTimeout)
                .ConfigureAwait(false);
        }
    }

    private static async Task CancelCaptureDrainsAsync(
        CancellationTokenSource captureDrainCts,
        Task<CaptureLimitResult>[]? captureDrains,
        IReadOnlyList<Stream>? captureSources)
    {
        try { await captureDrainCts.CancelAsync().ConfigureAwait(false); } catch { }
        DisposeCaptureSources(captureSources);
        if (captureDrains is null)
        {
            return;
        }

        try
        {
            await Task.WhenAll(captureDrains)
                .WaitAsync(CaptureDrainTimeout)
                .ConfigureAwait(false);
        }
        catch
        {
            // Cleanup is best effort, but it is always time-bounded. A descendant can retain a
            // copied pipe handle even after process-tree termination fails.
        }
    }

    private static void DisposeCaptureSources(IReadOnlyList<Stream>? captureSources)
    {
        if (captureSources is null)
        {
            return;
        }

        foreach (var source in captureSources)
        {
            try { source.Dispose(); } catch { }
        }
    }

    internal static async Task<CaptureLimitResult> DrainCappedCaptureAsync(
        Stream source,
        string path,
        long limitBytes,
        Func<DateTimeOffset> utcNow,
        Action? onLimitReached,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        long writtenBytes = 0;
        long persistedBytes = 0;
        var limitReached = false;
        await using (var destination = new FileStream(
            path,
            FileMode.Create,
            FileAccess.Write,
            FileShare.ReadWrite | FileShare.Delete,
            buffer.Length,
            useAsync: true))
        {
            try
            {
                while (true)
                {
                    int read;
                    try
                    {
                        read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                    }
                    catch (IOException ex) when (IsClosedPipe(ex))
                    {
                        break;
                    }
                    if (read == 0)
                        break;

                    writtenBytes = writtenBytes > long.MaxValue - read
                        ? long.MaxValue
                        : writtenBytes + read;
                    var persist = checked((int)Math.Min(read, Math.Max(0, limitBytes - persistedBytes)));
                    if (persist > 0)
                    {
                        await destination.WriteAsync(buffer.AsMemory(0, persist), cancellationToken)
                            .ConfigureAwait(false);
                        persistedBytes += persist;
                    }

                    if (!limitReached && writtenBytes >= limitBytes)
                    {
                        limitReached = true;
                        onLimitReached?.Invoke();
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // A descendant may inherit the pipe after the shell exits. Cancellation ends the
                // bounded drain; bytes already observed remain valid capture evidence.
            }
            catch (Exception ex) when (
                cancellationToken.IsCancellationRequested &&
                ex is IOException or ObjectDisposedException)
            {
                // Closing the pipe reader is the reliable cancellation mechanism for synchronous
                // redirected FileStreams on Windows; it can surface either exception.
            }

            await destination.FlushAsync(CancellationToken.None).ConfigureAwait(false);
        }

        if (limitReached)
        {
            await FinalizeCappedCaptureAsync(
                path,
                limitBytes,
                writtenBytes,
                utcNow()).ConfigureAwait(false);
        }

        return new CaptureLimitResult(path, writtenBytes, limitReached);
    }

    private static bool IsClosedPipe(IOException exception)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        var nativeErrorCode = exception.HResult & 0xffff;
        return nativeErrorCode is 109 or 232 or 233; // broken pipe, no data, pipe not connected
    }

    internal static async Task FinalizeCappedCaptureAsync(
        string path,
        long limitBytes,
        long writtenBytes,
        DateTimeOffset timestamp)
    {
        var terminator = Encoding.UTF8.GetBytes(
            $"\n[ACCEPTANCE_CAPTURE_LIMIT_REACHED cap_bytes={limitBytes} written_bytes={writtenBytes} timestamp={timestamp:O}]\n");
        var reserved = Math.Min(terminator.Length, checked((int)Math.Min(limitBytes, int.MaxValue)));
        await using var destination = new FileStream(
            path,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.ReadWrite | FileShare.Delete,
            4096,
            useAsync: true);
        destination.SetLength(Math.Max(0, limitBytes - reserved));
        destination.Position = destination.Length;
        await destination.WriteAsync(terminator.AsMemory(terminator.Length - reserved, reserved)).ConfigureAwait(false);
        await destination.FlushAsync().ConfigureAwait(false);
    }

    private static async Task CopyAtMostAsync(Stream source, Stream destination, int maximumBytes)
    {
        var buffer = new byte[Math.Min(16 * 1024, maximumBytes)];
        var remaining = maximumBytes;
        while (remaining > 0)
        {
            var read = await source.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, remaining))).ConfigureAwait(false);
            if (read == 0)
                break;
            await destination.WriteAsync(buffer.AsMemory(0, read)).ConfigureAwait(false);
            remaining -= read;
        }
    }

    private static string DecodeCapturedWindow(ReadOnlySpan<byte> bytes, bool trimLeading)
    {
        if (trimLeading)
        {
            while (!bytes.IsEmpty && (bytes[0] & 0xc0) == 0x80)
                bytes = bytes[1..];
        }

        // A byte window may end in the middle of a UTF-8 sequence. Remove only the incomplete
        // suffix; complete non-UTF8 data still follows the existing OEM/Latin-1 fallback below.
        for (var trim = 0; trim < Math.Min(3, bytes.Length); trim++)
        {
            try
            {
                return StrictUtf8.GetString(bytes[..(bytes.Length - trim)]);
            }
            catch (DecoderFallbackException)
            {
                // Try one fewer trailing byte before falling back to the established decoder.
            }
        }

        return DecodeCapturedOutput(bytes);
    }

    private static void EmitCaptureLimitReached(
        GateHeartbeatContext? context,
        string path,
        long capBytes) =>
        EmitCaptureLimitReached(
            context?.GoalId,
            CurrentAcceptanceAttemptPrefix.Value,
            path,
            capBytes,
            Console.Out);

    internal static void EmitCaptureLimitReached(
        string? goalId,
        string? attemptPrefix,
        string path,
        long capBytes,
        TextWriter writer)
    {
        var runId = Path.GetFileName(
            attemptPrefix ?? Path.GetFileNameWithoutExtension(path));
        writer.WriteLine(
            $"ACCEPTANCE_CAPTURE_LIMIT_REACHED goal={FormatNullableToken(goalId, 8)} run={QuoteProgressToken(runId)} path={QuoteProgressToken(path)} cap_bytes={capBytes}");
        writer.Flush();
    }

    internal static string DecodeCapturedOutput(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
        {
            return string.Empty;
        }

        if (bytes.StartsWith(Encoding.UTF8.Preamble))
        {
            return StrictUtf8.GetString(bytes[Encoding.UTF8.Preamble.Length..]);
        }

        if (bytes.StartsWith(Encoding.UTF32.Preamble))
        {
            return StrictUtf32LittleEndian.GetString(bytes[Encoding.UTF32.Preamble.Length..]);
        }

        if (bytes.StartsWith(StrictUtf32BigEndian.Preamble))
        {
            return StrictUtf32BigEndian.GetString(bytes[StrictUtf32BigEndian.Preamble.Length..]);
        }

        if (bytes.StartsWith(Encoding.Unicode.Preamble))
        {
            return StrictUtf16LittleEndian.GetString(bytes[Encoding.Unicode.Preamble.Length..]);
        }

        if (bytes.StartsWith(Encoding.BigEndianUnicode.Preamble))
        {
            return StrictUtf16BigEndian.GetString(bytes[Encoding.BigEndianUnicode.Preamble.Length..]);
        }

        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException) when (OperatingSystem.IsWindows())
        {
            return DecodeWindowsOem(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Latin1.GetString(bytes);
        }
    }

    private static string DecodeWindowsOem(ReadOnlySpan<byte> bytes)
    {
        const uint CpOem = 1;
        var source = bytes.ToArray();
        var charCount = MultiByteToWideChar(CpOem, 0, source, source.Length, null, 0);
        if (charCount <= 0)
        {
            throw new InvalidOperationException(
                $"Unable to decode captured output with the Windows OEM code page. Win32Error={Marshal.GetLastWin32Error()}.");
        }

        var chars = new char[charCount];
        var converted = MultiByteToWideChar(CpOem, 0, source, source.Length, chars, chars.Length);
        if (converted != charCount)
        {
            throw new InvalidOperationException(
                $"Unable to decode captured output with the Windows OEM code page. Win32Error={Marshal.GetLastWin32Error()}.");
        }

        return new string(chars);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern int MultiByteToWideChar(
        uint codePage,
        uint flags,
        byte[] multiByteText,
        int byteCount,
        [Out] char[]? wideText,
        int charCount);

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
                    .SelectMany(check => ExpandBroadInfrastructureCheck(check))
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
                    Arguments = arguments,
                    Runner = ResolveDotnetTestRunner(project)
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
        public double EstimatedSerialSeconds { get; init; }
        public IReadOnlyList<string> ExclusiveResourceKeys { get; init; } = [];
    }

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
        private readonly object _gate = new();

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

        public void RecordReuse(PartitionVerdictReuseReceipt receipt)
        {
            lock (_gate)
            {
                Reused.Add(receipt);
            }
        }

        public void RecordExecution(
            PartitionVerdictExecutionReceipt execution,
            PartitionVerdictRecord record)
        {
            lock (_gate)
            {
                Executed.Add(execution);
                FreshRecords.Add(record);
            }
        }
    }

    private sealed class DotnetTestBuildPhase(string[] buildArguments, DotnetBaseBuildCachePlan? cachePlan)
    {
        public string[] BuildArguments { get; } = buildArguments;
        public DotnetBaseBuildCachePlan? CachePlan { get; } = cachePlan;
        public DotnetBuildEnvironment? BuildEnvironment { get; set; }
        public (AcceptanceCheckResult Result, bool Retried)? Run { get; set; }
    }

    private sealed record IndexedShard(int Index, AcceptanceManifestCheck Check);

    private sealed record ShardRunOutcome(AcceptanceCheckResult Result, bool Retried);

    private sealed record ShardWorkerLease(
        int SlotIndex,
        DotnetBuildEnvironmentLease Lease,
        DotnetTestBuildPhase? BuildPhase);

    private sealed record CheckBatchResult(
        IReadOnlyList<AcceptanceCheckResult> Results,
        bool Retried);

    private readonly record struct DotnetTestBuildPhaseResult(
        (AcceptanceCheckResult Result, bool Retried) Run,
        bool ContributesToCheck);

    private readonly record struct TransientBuildLockWaitResult(long WaitedMilliseconds, bool Released);

    internal readonly record struct CaptureLimitResult(string Path, long WrittenBytes, bool LimitReached);

    private sealed record GateHeartbeatContext(
        string? GoalId,
        string Phase,
        string CurrentTarget,
        int? SlotIndex,
        string HeartbeatPath,
        string? StableSlotHeartbeatPath,
        string CommandLine,
        string? BuildEnvironmentRoot);

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
            MirrorToStableSlot(snapshot);
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
            if (_context.StableSlotHeartbeatPath is { } stableSlotPath)
            {
                GateHeartbeatArtifacts.TryDelete(stableSlotPath);
            }
        }

        // Mirror only the live beat. WriteFinal removes this run's uniquely keyed mirror, leaving any
        // concurrent sibling visible and preventing completed state from accumulating indefinitely.
        private void MirrorToStableSlot(GateHeartbeatSnapshot snapshot)
        {
            if (_context.StableSlotHeartbeatPath is { } stableSlotPath)
            {
                GateHeartbeatArtifacts.TryWrite(stableSlotPath, snapshot);
            }
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

    private sealed class ManagedRunEnvironmentScope(
        ManagedRunEnvironmentScope? previous) : IDisposable
    {
        private readonly ConcurrentDictionary<string, DotnetBuildEnvironment> _environments =
            new(StringComparer.OrdinalIgnoreCase);
        private int _successful;
        private int _disposed;

        public void Track(DotnetBuildEnvironment environment)
        {
            if (environment.LeaseMetadataPath is null)
            {
                _environments.TryAdd(environment.RootPath, environment);
            }
        }

        public void MarkSuccessful() => Interlocked.Exchange(ref _successful, 1);

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            CurrentManagedRunEnvironmentScope.Value = previous;
            if (Volatile.Read(ref _successful) == 0)
            {
                return;
            }

            foreach (var environment in _environments.Values)
            {
                DotnetBuildEnvironmentManager.TryCleanupSuccessfulRun(environment);
            }
        }
    }

    private sealed class RestoreAction(Action restore) : IDisposable
    {
        public void Dispose() => restore();
    }
}
