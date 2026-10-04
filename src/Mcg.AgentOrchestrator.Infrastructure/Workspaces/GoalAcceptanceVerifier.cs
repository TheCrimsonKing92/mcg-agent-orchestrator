using System.Diagnostics;
using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Mcg.AgentOrchestrator.Core;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Mcg.AgentOrchestrator.Infrastructure.AcceptanceCheckCommandBuilder;
using static Mcg.AgentOrchestrator.Infrastructure.AcceptancePolicyShardPlanner;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed partial record AcceptanceCheckResult(
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
    IReadOnlyList<string>? FailingTestIdentities = null,
    int? ExecutedTestCount = null,
    int? DiscoveredTestCount = null,
    AcceptanceShardCompletionDecision? CompletionDecision = null,
    string? ProcessStderrPath = null,
    string? ProcessStderr = null,
    string? GateHeartbeatPath = null,
    AcceptanceFailureCauseEvidence? FailureCauseEvidence = null,
    string? TestProjectPath = null,
    IReadOnlyList<AcceptanceTestFailureAttribution>? FailingTestAttributions = null,
    int? ChildProcessId = null,
    DateTimeOffset? ChildProcessStartedAt = null,
    IReadOnlyList<string>? CoveredBy = null);

public enum AcceptanceTestFailureOrigin
{
    Introduced,
    Inherited,
    Unattributed,
    UnconfirmedIntroduced
}

public sealed record AcceptanceTestFailureAttribution(
    string TestIdentity,
    AcceptanceTestFailureOrigin Origin,
    string Evidence,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] CandidateFailureRerunEvidence? CandidateRerun = null);

public sealed record CandidateFailureRerunEvidence(
    string Outcome,
    string? ReceiptPointer,
    string? Error = null);

public sealed record AcceptanceShardCompletionDecision(
    bool Passed,
    string? FailedPredicate,
    bool TimedOut,
    int? ExitCode,
    int? DiscoveredTestCount,
    int? ExecutedTestCount,
    string TrxOutcome,
    string? PolicySignal = null,
    int? NotExecutedTestCount = null);

public static class AcceptanceShardCompletionPredicates
{
    public const string TimedOut = "timed-out";
    public const string NonzeroExit = "nonzero-exit";
    public const string MissingTrx = "missing-trx";
    public const string MalformedTrx = "malformed-trx";
    public const string ZeroTests = "zero-tests";
    public const string IncompleteExecution = "incomplete-execution";
    public const string FailingTrx = "failing-trx";
    public const string AssemblyCleanupFailure = AcceptanceFailureClassifications.AssemblyCleanupFailure;
    public const string CheckFailed = "check-failed";
    public const string RetryEvidenceRetentionFailed = "retry-evidence-retention-failed";
}

public sealed record AcceptanceFailureCauseEvidence(
    AcceptanceFailureCause Cause,
    string Evidence,
    string? CheckName = null,
    string? SourceClassification = null);

internal sealed record AcceptanceFailureCauseReceiptV1(
    int ContractVersion,
    string Kind,
    string Owner,
    string ProbeClassification,
    bool ProcessStarted,
    int? ExitCode,
    long StandardOutputByteCount,
    long StandardErrorByteCount,
    bool DrainTimedOut,
    bool TimedOut,
    bool DrainFailed,
    string RepositoryHeadState,
    string Check,
    string FixtureAttemptId,
    int ProbeOrdinal);

internal static class AcceptanceFailureCauseReceiptCodec
{
    internal const string Prefix = "MCG_ACCEPTANCE_CAUSE_V1:";
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
    private static readonly Regex Marker = new(
        Regex.Escape(Prefix) + "(?<payload>[A-Za-z0-9+/=]+)",
        RegexOptions.CultureInvariant);

    internal static string Format(AcceptanceFailureCauseReceiptV1 receipt) =>
        Prefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(receipt, Options)));

    internal static bool TryParse(string message, out AcceptanceFailureCauseReceiptV1 receipt)
    {
        receipt = null!;
        var matches = Marker.Matches(message ?? string.Empty);
        if (matches.Count != 1)
        {
            return false;
        }

        try
        {
            receipt = JsonSerializer.Deserialize<AcceptanceFailureCauseReceiptV1>(
                Convert.FromBase64String(matches[0].Groups["payload"].Value),
                Options)!;
            return IsEnvironmentalApparatus(receipt);
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            receipt = null!;
            return false;
        }
    }

    private static bool IsEnvironmentalApparatus(AcceptanceFailureCauseReceiptV1? receipt)
    {
        if (receipt is null ||
            receipt.ContractVersion != 1 ||
            !receipt.Kind.Equals("seeded-dispatch-repository-git-probe", StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(receipt.Check) ||
            string.IsNullOrWhiteSpace(receipt.FixtureAttemptId) ||
            receipt.FixtureAttemptId.Equals("not-assigned", StringComparison.Ordinal) ||
            receipt.ProbeOrdinal <= 0)
        {
            return false;
        }

        return receipt.Owner switch
        {
            "ProcessOutputApparatus" => IsProcessOutputApparatus(receipt),
            "FixturePublication" => IsFixturePublicationApparatus(receipt),
            _ => false
        };
    }

    private static bool IsProcessOutputApparatus(AcceptanceFailureCauseReceiptV1 receipt)
    {
        if (receipt.RepositoryHeadState is not (
            "ValidLooseReference" or "ValidPackedReference" or "ValidDetachedHead"))
        {
            return false;
        }

        return IsProcessFaultClassification(receipt);
    }

    private static bool IsFixturePublicationApparatus(AcceptanceFailureCauseReceiptV1 receipt)
    {
        if (receipt.RepositoryHeadState is not (
            "RepositoryMissing" or "GitMetadataMissing" or "HeadMissing" or "HeadInvalid" or
            "ReferenceMissing" or "ReferenceInvalid"))
        {
            return false;
        }

        return receipt.ProbeClassification switch
        {
            "NotRun" => !receipt.ProcessStarted && receipt.ExitCode is null &&
                receipt.StandardOutputByteCount == 0 &&
                !receipt.DrainTimedOut && !receipt.TimedOut && !receipt.DrainFailed,
            "NonZeroExit" => receipt.ProcessStarted && receipt.ExitCode is not null and not 0 &&
                !receipt.DrainTimedOut && !receipt.TimedOut && !receipt.DrainFailed,
            "InvalidRequiredOutput" => receipt.ProcessStarted && receipt.ExitCode == 0 &&
                receipt.StandardOutputByteCount > 0 &&
                !receipt.DrainTimedOut && !receipt.TimedOut && !receipt.DrainFailed,
            _ => IsProcessFaultClassification(receipt)
        };
    }

    private static bool IsProcessFaultClassification(AcceptanceFailureCauseReceiptV1 receipt) =>
        receipt.ProbeClassification switch
        {
            "EmptyRequiredOutput" => receipt.ProcessStarted && receipt.ExitCode == 0 &&
                receipt.StandardOutputByteCount == 0 &&
                !receipt.DrainTimedOut && !receipt.TimedOut && !receipt.DrainFailed,
            "LaunchFailure" => !receipt.ProcessStarted && receipt.ExitCode is null &&
                receipt.StandardOutputByteCount == 0 &&
                receipt.StandardErrorByteCount > 0 &&
                !receipt.DrainTimedOut && !receipt.TimedOut && !receipt.DrainFailed,
            "ProcessTimeout" => receipt.ProcessStarted && receipt.TimedOut,
            "DrainTimeout" => receipt.ProcessStarted && !receipt.TimedOut && receipt.DrainTimedOut,
            "DrainFailure" => receipt.ProcessStarted && !receipt.TimedOut &&
                !receipt.DrainTimedOut && receipt.DrainFailed,
            "ProcessObservationFailure" => receipt.ProcessStarted &&
                receipt.StandardOutputByteCount == 0 &&
                receipt.StandardErrorByteCount > 0 &&
                !receipt.DrainTimedOut && !receipt.TimedOut && !receipt.DrainFailed,
            _ => false
        };
}

public static class AcceptanceShardCompletionSignals
{
    public const string MissingTrxInjectedRunnerCompatibility = "missing-trx-injected-runner-compatibility";
}

internal sealed record AcceptanceProcessCleanupObservation(
    int ProcessId,
    string Stage,
    bool RegistryActive,
    bool JobActive,
    bool NativeHandleOpen,
    bool ProcessDisposed);

public static class AcceptanceFailureClassifications
{
    public const string GateEnvironmentInterference = "gate-environment-interference";
    public const string InheritedBaselineApparatus = "inherited-baseline-apparatus";
    public const string StructuralCoverageFailed = "structural-coverage-failed";
    public const string FocusedSelectionApparatusFailure = "focused-selection-apparatus-failure";
    public const string FocusedSelectionAbsentAtBaseline = "focused-selection-absent-at-baseline";
    public const string RetryEvidenceRetentionFailed = "retry-evidence-retention-failed";
    public const string FocusedSelectionReceiptUnreadable = "focused-selection-receipt-unreadable";
    public const string SeededRepositoryProcessOutputApparatus = "seeded-repository-process-output-apparatus";
    public const string SeededRepositoryApparatus = "seeded-repository-apparatus";
    public const string SharedGateApparatusInvalidated = "shared-gate-apparatus-invalidated";
    public const string AssemblyCleanupFailure = "assembly-cleanup-failure";

    public static bool IsEnvironmentalApparatus(string? classification) =>
        classification is GateEnvironmentInterference or
            InheritedBaselineApparatus or
            FocusedSelectionApparatusFailure or
            FocusedSelectionReceiptUnreadable or
            SeededRepositoryProcessOutputApparatus or
            SeededRepositoryApparatus or
            AssemblyCleanupFailure or
            SharedGateApparatusInvalidated;
}

public enum FocusedEvidenceRejectionCode
{
    EmptyRequest,
    UnsupportedProject,
    UnsafeFilter,
    OversizedFilter,
    UnsupportedToken,
    UnresolvableSelection,
    SourceDiscoveryFailure
}

public sealed record FocusedEvidenceRejection(
    FocusedEvidenceRejectionCode Code,
    string OffendingToken,
    string Detail);

public sealed class AcceptanceInfrastructureDeferredException : Exception
{
    public AcceptanceInfrastructureDeferredException(
        string reasonCode,
        int? exitCode,
        string? outputTail,
        BuildLockAttribution? buildLockAttribution = null)
        : base(BuildMessage(reasonCode, exitCode, outputTail, buildLockAttribution))
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reasonCode);
        ReasonCode = reasonCode;
        ExitCode = exitCode;
        OutputTail = outputTail;
        BuildLockAttribution = buildLockAttribution;
    }

    public string ReasonCode { get; }

    public int? ExitCode { get; }

    public string? OutputTail { get; }

    public BuildLockAttribution? BuildLockAttribution { get; }

    private static string BuildMessage(
        string reasonCode,
        int? exitCode,
        string? outputTail,
        BuildLockAttribution? buildLockAttribution)
    {
        var detail = buildLockAttribution is null
            ? outputTail
            : $"path={buildLockAttribution.Path}; source={buildLockAttribution.Source}";
        return $"Acceptance infrastructure deferred: reason={reasonCode}; " +
            $"exit-code={exitCode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none"}" +
            (string.IsNullOrWhiteSpace(detail) ? string.Empty : $"; detail={detail}");
    }
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

public enum DotnetShardDisposition
{
    RunDotnetShards,
    KnownEmptyCandidate,
    DocsTreeOnlyCandidate
}

public sealed record FocusedEvidenceRunResult(
    string Request,
    bool Accepted,
    bool Passed,
    string Summary,
    IReadOnlyList<AcceptanceCheckResult> Checks,
    FocusedEvidenceCoverage? Coverage = null,
    IReadOnlyList<FocusedEvidenceArmRunResult>? Arms = null,
    FindingEvidenceOutcomeReason? OutcomeReason = null,
    FocusedEvidenceRejection? Rejection = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    [property: System.Text.Json.Serialization.JsonConverter(typeof(FindingEvidenceNegativeControlOutcomeJsonConverter))]
    FindingEvidenceNegativeControlOutcome? NegativeControlOutcome = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    [property: System.Text.Json.Serialization.JsonConverter(typeof(FindingEvidenceRevertPathsRejectionJsonConverter))]
    FindingEvidenceRevertPathsRejection? RevertPathsRejection = null)
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
    IReadOnlyList<AcceptanceCheckResult> Checks,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    [property: System.Text.Json.Serialization.JsonConverter(typeof(FindingEvidenceRevertPathsRejectionJsonConverter))]
    FindingEvidenceRevertPathsRejection? RevertPathsRejection = null);

public sealed record FocusedEvidenceTargetCoverage(
    string Target,
    IReadOnlyList<string> CheckNames);

public sealed record FocusedEvidenceCoverage(
    IReadOnlyList<FocusedEvidenceTargetCoverage> TargetToChecks,
    string ExecutionMode = "focused",
    string ExecutionReason = "explicit-focused-mapping");

public interface IGoalAcceptanceVerifier
{
    string ComputeEffectivePlanIdentity(
        string worktreePath,
        IReadOnlyList<string>? changedFiles = null) =>
        GoalAcceptanceVerifier.ComputeEffectiveAcceptancePlanIdentity(worktreePath, changedFiles);

    Task<AcceptanceVerificationResult> RunOwnedAsync(
        string worktreePath,
        GoalId? goalId,
        IReadOnlyList<string>? changedFiles,
        int? stableSlotIndex,
        DotnetBuildEnvironmentLease? stableSlotLease,
        IAcceptanceAttemptExecutionOwner executionOwner);

    Task<FocusedEvidenceRunResult> RunFocusedEvidenceOwnedAsync(
        string worktreePath,
        GoalId? goalId,
        string request,
        IAcceptanceFocusedVerificationOwner executionOwner,
        int? stableSlotIndex = null,
        DotnetBuildEnvironmentLease? stableSlotLease = null,
        bool runBaselineArm = false);

    Task<FocusedEvidenceRunResult> RunNegativeControlFocusedEvidenceOwnedAsync(
        string worktreePath, GoalId? goalId, string request,
        IAcceptanceFocusedVerificationOwner executionOwner,
        FindingEvidenceNegativeControl negativeControl,
        int? stableSlotIndex = null, DotnetBuildEnvironmentLease? stableSlotLease = null,
        bool runBaselineArm = false, IReadOnlyList<string>? revertPaths = null, FindingEvidenceMutation? mutation = null) => throw new NotSupportedException("Negative-control focused evidence is not supported.");
}

public sealed partial class GoalAcceptanceVerifier : IGoalAcceptanceVerifier
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
        long StderrBytes = 0,
        string? Stderr = null,
        int? ChildProcessId = null,
        DateTimeOffset? ChildProcessStartedAt = null,
        bool CaptureLimited = false,
        long? CaptureLimitBytes = null);

    internal static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);
    internal static readonly Encoding StrictUtf16LittleEndian = new UnicodeEncoding(false, true, true);
    internal static readonly Encoding StrictUtf16BigEndian = new UnicodeEncoding(true, true, true);
    internal static readonly Encoding StrictUtf32LittleEndian = new UTF32Encoding(false, true, true);
    internal static readonly Encoding StrictUtf32BigEndian = new UTF32Encoding(true, true, true);

    internal const int MaxFocusedEvidenceFilterLength = 1024;
    internal static string FocusedEvidenceSupportedProjectForms(
        AcceptanceGateEngineSettings? engineSettings) =>
        DeclaredTestProjectInventory.DescribeSupportedProjectForms(engineSettings);
    internal const int FocusedEvidenceShortTimeoutTargetLimit = 4;
    internal const int MaxFailureAttributionFocusedEvidenceIdentities = FocusedEvidenceShortTimeoutTargetLimit;
    public const string AcceptanceAttemptTrxPrefixVariable = "MCG_ACCEPTANCE_GATE_ATTEMPT_TRX_PREFIX";

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
    private readonly DotnetBuildStorageRoot _storageRoot;
    private readonly AcceptanceStructuralCoverageEvaluator _structuralCoverageEvaluator;
    private readonly TimeProvider _timeProvider;
    private readonly Action<TimeSpan> _leaseSleep;
    private readonly IAcceptanceRunExecutionContext? _executionContext;
    private readonly AcceptanceInvocationContext? _invocationContext;
    private readonly GoalAcceptanceVerifierTestOverrides _testOverrideSource, _testOverrides;
    // Injected runners return only CommandResult; missing TRX remains a typed compatibility signal for that seam.
    private readonly bool _requiresTestTelemetryReceipt;
    private static readonly TimeSpan DefaultHeartbeatInterval = TimeSpan.FromSeconds(5), DefaultProgressInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DefaultTransientNoHolderBuildLockWaitWindow = TimeSpan.FromSeconds(75), DefaultTransientNoHolderBuildLockPollInterval = TimeSpan.FromMilliseconds(250);
    private const int DefaultTransientNoHolderBuildLockMaxRetryCycles = 2;
    internal static readonly TimeSpan CaptureDrainTimeout = TimeSpan.FromSeconds(12);
    internal const int CappedOutputPreviewBytes = 64 * 1024;
    // A failed partition reruns once as a probe; Retried=true records the probe without changing its verdict.
    // Tests that assert exact partition run counts disable this within-attempt companion to the verdict cache.
    private static readonly string[] CacheableProjects =
    [
        CoreProject,
        ProvidersProject,
        OperatorCommsProject,
        InfrastructureProject,
        AppProject,
        CoreTestsProject,
        InfrastructureTestsProject,
        AcceptanceTestsProject,
        TestSupportProject,
        ProviderEnvironmentTestsProject,
        CliTestsProject
    ];
    public GoalAcceptanceVerifier() : this(DotnetBuildEnvironmentManager.CaptureStorageRoot()) { }
    public GoalAcceptanceVerifier(DotnetBuildStorageRoot storageRoot) : this(new GoalAcceptanceVerifierTestOverrides(), storageRoot) { }
    public GoalAcceptanceVerifier(DotnetBuildStorageRoot storageRoot, string ownerPolicyDecisionStoreDirectory) : this(storageRoot)
    {
        _ownerPolicyDecisionStoreDirectory = Path.GetFullPath(ownerPolicyDecisionStoreDirectory);
    }
    internal GoalAcceptanceVerifier(GoalAcceptanceVerifierTestOverrides testOverrides, DotnetBuildStorageRoot? storageRoot = null)
        : this(
            (arguments, workingDirectory, timeout, cancellationToken) => RunProcessAsync(
                arguments, workingDirectory, timeout,
                forceUtf8ConsoleOutput: false,
                cancellationToken,
                heartbeatInterval: testOverrides.HeartbeatInterval,
                progressInterval: testOverrides.ProgressInterval),
            (arguments, workingDirectory, timeout, cancellationToken) => RunProcessAsync(
                arguments, workingDirectory, timeout,
                forceUtf8ConsoleOutput: true,
                cancellationToken,
                heartbeatInterval: testOverrides.HeartbeatInterval,
                progressInterval: testOverrides.ProgressInterval),
            TimeProvider.System,
            requiresTestTelemetryReceipt: true,
            testOverrides: testOverrides,
            storageRoot: storageRoot)
    { }
    internal GoalAcceptanceVerifier(
        GoalAcceptanceVerifierTestOverrides testOverrides,
        Func<string[], string, CancellationToken, Task<CommandResult>> runner,
        TimeProvider? timeProvider = null,
        Action<TimeSpan>? leaseSleep = null)
        : this(runner, timeProvider ?? TimeProvider.System, leaseSleep, testOverrides)
    { }
    internal GoalAcceptanceVerifier(
        GoalAcceptanceVerifierTestOverrides testOverrides,
        Func<string[], string, TimeSpan, CancellationToken, Task<CommandResult>> runner,
        TimeProvider? timeProvider = null,
        Action<TimeSpan>? leaseSleep = null)
        : this(runner, timeProvider ?? TimeProvider.System, leaseSleep, testOverrides)
    { }
    internal GoalAcceptanceVerifier(Func<string[], string, CancellationToken, Task<CommandResult>> runner)
        : this(runner, TimeProvider.System)
    { }
    internal GoalAcceptanceVerifier(
        Func<string[], string, CancellationToken, Task<CommandResult>> runner,
        TimeProvider timeProvider,
        Action<TimeSpan>? leaseSleep = null,
        GoalAcceptanceVerifierTestOverrides? testOverrides = null)
        : this((arguments, workingDirectory, _, cancellationToken) =>
            runner(arguments, workingDirectory, cancellationToken), timeProvider, leaseSleep, testOverrides)
    { }
    internal GoalAcceptanceVerifier(Func<string[], string, TimeSpan, CancellationToken, Task<CommandResult>> runner)
        : this(runner, TimeProvider.System)
    { }
    internal GoalAcceptanceVerifier(
        Func<string[], string, TimeSpan, CancellationToken, Task<CommandResult>> runner,
        TimeProvider timeProvider,
        Action<TimeSpan>? leaseSleep = null,
        GoalAcceptanceVerifierTestOverrides? testOverrides = null)
        : this(runner, runner, timeProvider, leaseSleep, requiresTestTelemetryReceipt: false, testOverrides)
    { }
    private GoalAcceptanceVerifier(
        Func<string[], string, TimeSpan, CancellationToken, Task<CommandResult>> runner,
        Func<string[], string, TimeSpan, CancellationToken, Task<CommandResult>> discoveryRunner,
        TimeProvider timeProvider,
        Action<TimeSpan>? leaseSleep = null,
        bool requiresTestTelemetryReceipt = false,
        GoalAcceptanceVerifierTestOverrides? testOverrides = null,
        DotnetBuildStorageRoot? storageRoot = null)
    {
        _runner = runner;
        _storageRoot = storageRoot ?? testOverrides?.BuildStorageRootForTests ?? DotnetBuildEnvironmentManager.CaptureStorageRoot();
        _structuralCoverageEvaluator = new AcceptanceStructuralCoverageEvaluator(discoveryRunner, IsBuildArtifactIoException);
        _timeProvider = timeProvider;
        _leaseSleep = leaseSleep ?? Thread.Sleep;
        _requiresTestTelemetryReceipt = requiresTestTelemetryReceipt;
        _testOverrideSource = testOverrides ?? new GoalAcceptanceVerifierTestOverrides();
        _testOverrides = _testOverrideSource.Snapshot();
    }
    internal GoalAcceptanceVerifier(GoalAcceptanceVerifier source, IAcceptanceRunExecutionContext executionContext)
    {
        _runner = source._runner;
        _storageRoot = source._storageRoot;
        _ownerPolicyDecisionStoreDirectory = source._ownerPolicyDecisionStoreDirectory;
        _structuralCoverageEvaluator = source._structuralCoverageEvaluator;
        _timeProvider = source._timeProvider;
        _leaseSleep = source._leaseSleep;
        _requiresTestTelemetryReceipt = source._requiresTestTelemetryReceipt;
        _executionContext = executionContext;
        _testOverrideSource = source._testOverrideSource; _testOverrides = source._testOverrides;
    }
    private GoalAcceptanceVerifier(GoalAcceptanceVerifier source, AcceptanceInvocationContext invocationContext)
    {
        _runner = source._runner;
        _storageRoot = source._storageRoot;
        _ownerPolicyDecisionStoreDirectory = source._ownerPolicyDecisionStoreDirectory;
        _structuralCoverageEvaluator = source._structuralCoverageEvaluator;
        _timeProvider = source._timeProvider;
        _leaseSleep = source._leaseSleep;
        _requiresTestTelemetryReceipt = source._requiresTestTelemetryReceipt;
        _executionContext = source._executionContext;
        _invocationContext = invocationContext;
        _testOverrideSource = source._testOverrideSource;
        _testOverrides = source._testOverrides;
    }
    private AcceptanceInvocationContext AllocateTestTelemetryInvocation(AcceptanceManifestCheck check)
    {
        var attemptPrefix = AcceptanceAttemptResultsPrefix;
        var stem = string.IsNullOrWhiteSpace(attemptPrefix)
            ? Slug(check.Name)
            : $"{Path.GetFileName(attemptPrefix)}.{Slug(check.Name)}";
        return (_executionContext ?? throw new InvalidOperationException("Test invocation allocation requires an execution owner."))
            .CreateInvocation(stem);
    }
    private string AppendTestTelemetryInvocationSuffix(string stem) =>
        AppendTestTelemetryInvocationSuffix(stem, _invocationContext?.Ordinal ?? 0);
    private static string AppendTestTelemetryInvocationSuffix(string stem, int ordinal) =>
        ordinal > 0
            ? $"{stem}-run-{ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture)}"
            : stem;
    private string? AcceptanceAttemptResultsPrefix => _executionContext is AcceptanceAttemptExecutionOwner attemptOwner
        ? attemptOwner.ArtifactResultsPrefix
        : _executionContext?.ResultsPrefix;
    private AcceptanceGateEngineSettings EngineSettings =>
        _executionContext?.Settings ?? new AcceptanceGateEngineSettings();
    public static string ComputeEffectiveAcceptancePlanIdentity(
        string worktreePath,
        IReadOnlyList<string>? changedFiles = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(worktreePath);
        var engineSettings = AcceptanceGateEngineSettings.Load(worktreePath);
        var plan = CreateEffectiveGatePlan(worktreePath, changedFiles, engineSettings);
        return ComputeEffectiveAcceptanceManifestIdentity(plan.Checks, engineSettings);
    }

    internal static IReadOnlyList<AcceptanceManifestCheck> BuildEffectiveAcceptanceChecksForTests(
        string worktreePath,
        IReadOnlyList<string>? changedFiles = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(worktreePath);
        var engineSettings = AcceptanceGateEngineSettings.Load(worktreePath);
        return CreateEffectiveGatePlan(worktreePath, changedFiles, engineSettings).Checks;
    }

    internal static IReadOnlyList<AcceptanceManifestCheck> MapRequiredPolicyChecksForTests(
        string worktreePath,
        VerificationPolicy policy) => MapRequiredPolicyChecks(worktreePath, policy);

    internal async Task<AcceptanceVerificationResult> RunAsync(
        string worktreePath,
        GoalId? goalId = null,
        IReadOnlyList<string>? changedFiles = null,
        int? stableSlotIndex = null,
        DotnetBuildEnvironmentLease? stableSlotLease = null,
        CancellationToken cancellationToken = default)
    {
        var engineSettings = AcceptanceGateEngineSettings.Load(worktreePath);
        var executionOwner = AcceptanceExecutionOwners.CreateAttemptForVerifierCompatibility(
            worktreePath,
            goalId,
            stableSlotIndex,
            cancellationToken,
            engineSettings,
            new AcceptanceAttemptIdentityResolvers(
                path => _testOverrides.ResolvePartitionVerdictCandidateTreeShaForTests?.Invoke(path),
                path => _testOverrides.ResolvePartitionVerdictMainShaForTests?.Invoke(path),
                path => _testOverrides.ResolvePartitionVerdictVerifyingCommitShaForTests?.Invoke(path)));
        await using (executionOwner.ConfigureAwait(false))
        {
            return await RunOwnedAsync(
                worktreePath, goalId, changedFiles, stableSlotIndex, stableSlotLease,
                executionOwner).ConfigureAwait(false);
        }
    }

    public Task<AcceptanceVerificationResult> RunOwnedAsync(
        string worktreePath,
        GoalId? goalId,
        IReadOnlyList<string>? changedFiles,
        int? stableSlotIndex,
        DotnetBuildEnvironmentLease? stableSlotLease,
        IAcceptanceAttemptExecutionOwner executionOwner)
    {
        EnsureTestOverridesUnchanged();
        return executionOwner is AcceptanceAttemptExecutionOwner owner
            ? owner.ExecuteAsync(this, worktreePath, goalId, changedFiles, stableSlotIndex, stableSlotLease)
            : throw new ArgumentException("The supplied owner is not an acceptance-attempt execution owner.", nameof(executionOwner));
    }
    internal async Task<AcceptanceVerificationResult> RunOwnedAcceptanceAsync(
        string worktreePath,
        GoalId? goalId = null,
        IReadOnlyList<string>? changedFiles = null,
        int? stableSlotIndex = null,
        DotnetBuildEnvironmentLease? stableSlotLease = null,
        CancellationToken cancellationToken = default)
    {
        using var phaseAccountant = AcceptanceGatePhaseAccountant.Start(
            _timeProvider, goalId?.Value, EmitGateProgress, cancellationToken);
        phaseAccountant.BindHostHealthLedger(worktreePath,
            (_executionContext as AcceptanceAttemptExecutionOwner)?.Identity.AttemptId);
        AcceptanceGatePhaseAccountant.RecordCurrentSlotWait(stableSlotLease?.SlotWaitDuration);
        var untrackedSnapshot = CaptureGateWorktreeUntrackedSnapshot(worktreePath, goalId);
        try
        {
        phaseAccountant.TransitionTo(AcceptanceGatePhaseNames.GatePlan);
        var sourceSizePreflight = SourceSizeRatchetPreflight.Evaluate(worktreePath);
        if (sourceSizePreflight.HasBlockingViolation)
        {
            phaseAccountant.MarkCompleted(passed: false);
            var check = new AcceptanceCheckResult(
                SourceSizeRatchetPreflight.CheckName, false, 1, sourceSizePreflight.Message);
            return new AcceptanceVerificationResult(
                Passed: false,
                Skipped: false,
                ExitCode: 1,
                OutputTail: sourceSizePreflight.Message,
                Checks: [check]);
        }

        var executionOwner = (AcceptanceAttemptExecutionOwner)(_executionContext ?? throw new InvalidOperationException(
            "Acceptance execution context was not supplied."));
        var engineSettings = executionOwner.Settings;
        using var laneDurationScope = AcceptanceLaneDurationStore.PushRecordingScope(worktreePath);
        var ownerProtectedDecision = EvaluateOwnerProtectedConfiguration(
            worktreePath, goalId, changedFiles, executionOwner.OwnerProtectedCohortMembers);
        if (ownerProtectedDecision.Failure is { } manifestTrustFailure)
        {
            phaseAccountant.MarkCompleted(passed: false);
            return new AcceptanceVerificationResult(
                Passed: false,
                Skipped: false,
                ExitCode: manifestTrustFailure.ExitCode,
                OutputTail: manifestTrustFailure.OutputTail,
                Checks: [manifestTrustFailure]);
        }

        phaseAccountant.TransitionTo(AcceptanceGatePhaseNames.PlanConstruction);
        var shardCoreBudget =
            _testOverrides.ResolveShardCoreBudgetForTests?.Invoke() ?? Math.Max(1, Environment.ProcessorCount / 2);
        var shardConcurrencyBudget = Math.Min(
            engineSettings.MaxConcurrentShards,
            shardCoreBudget);
        var effectivePlan = CreateEffectiveGatePlan(worktreePath, changedFiles, engineSettings);
        if (effectivePlan.UndeclaredTestProjects.Count > 0)
        {
            var message = "Structural coverage requires every discovered test project to be declared by a dotnet-test check in config/acceptance-manifest.json:" +
                Environment.NewLine + string.Join(Environment.NewLine, effectivePlan.UndeclaredTestProjects);
            phaseAccountant.MarkCompleted(passed: false);
            var check = new AcceptanceCheckResult(
                "structural coverage declaration", false, 1, message,
                FailureClassification: AcceptanceFailureClassifications.StructuralCoverageFailed);
            return new AcceptanceVerificationResult(false, false, 1, message, Checks: [check]);
        }

        var manifest = effectivePlan.Manifest;
        var infrastructureTestLanes = effectivePlan.InfrastructureTestLanes;
        var policyShardPlan = effectivePlan.PolicyShardPlan;
        var policyRequiredChecks = effectivePlan.PolicyRequiredChecks;
        var structuralCoverageApplies = effectivePlan.StructuralCoverageApplies;
        var effectiveChecks = effectivePlan.Checks;
        var partitionVerdictCache = AcceptancePartitionVerdictCache.Create(
            new AcceptancePartitionVerdictCacheOptions(
                goalId,
                worktreePath,
                effectiveChecks,
                engineSettings.PartitionVerdictFullRerunEveryN,
                _testOverrides.PartitionVerdictWithinAttemptRerunEnabled,
                path => _testOverrides.ResolvePartitionVerdictCandidateTreeShaForTests?.Invoke(path) ??
                    ResolveGitScalar(path, "rev-parse", "HEAD^{tree}"),
                path => _testOverrides.ResolvePartitionVerdictMainShaForTests?.Invoke(path) ??
                    ResolveGitScalar(path, "rev-parse", "main"),
                path => _testOverrides.ResolvePartitionVerdictVerifyingCommitShaForTests?.Invoke(path) ??
                    ResolveGitScalar(path, "rev-parse", "HEAD"),
                () => executionOwner.Identity.AttemptId,
                () => ComputeEffectiveAcceptanceManifestIdentity(effectiveChecks, engineSettings),
                () => EngineSettings.EnforceStructuralCoverage,
                () => TempRootApparatusLossReceiptStore.Read(executionOwner.ApparatusReceiptPath)));
        var dotnetTestBuildPhase = GateUsesStableSlot(stableSlotIndex, stableSlotLease)
            ? CreateDotnetTestBuildPhase(worktreePath, effectiveChecks, changedFiles, policyShardPlan)
            : null;

        var advisoryChecks = LoadAdvisoryChecks(worktreePath);
        var sanctionedRemovedTests = LoadSanctionedTestRemovals(worktreePath);
        if (structuralCoverageApplies)
        {
            _coverageHasIndependentChecks = effectiveChecks.Any(check =>
                !check.Type.Equals("dotnet-test", StringComparison.OrdinalIgnoreCase));
            _coveragePreparationFactory = token => PrepareStructuralCoverageCheckAsync(
                effectiveChecks, infrastructureTestLanes, worktreePath, goalId, stableSlotIndex,
                stableSlotLease, partitionVerdictCache?.AttemptId, sanctionedRemovedTests,
                manifest.Checks, engineSettings, ownerProtectedDecision.OwnerApprovalSatisfied, token);
        }

        var checks = new List<AcceptanceCheckResult>();
        checks.AddRange(effectivePlan.SemanticDeduplications.Select(receipt =>
            new AcceptanceCheckResult(
                $"semantic execution deduplication: {receipt.PartitionId}",
                true,
                0,
                null,
                ResultSummary:
                    $"kept_index={receipt.RetainedPlanIndex} dropped_index={receipt.DroppedPlanIndex} " +
                    $"semantic_key_sha256={receipt.SemanticKeySha256}",
                Advisory: true)));
        var retried = false;
        phaseAccountant.TransitionTo(AcceptanceGatePhaseNames.CheckExecution);

        // When the manifest has both a solution-wide dotnet-test check and granular
        // per-project .csproj checks covered by it, run the solution once and synthesize
        // results for the granular checks so the policy gate finds all required names
        // without re-running the full test suite.
        var solutionCheck = effectiveChecks.FirstOrDefault(c =>
            c.Type.Equals("dotnet-test", StringComparison.OrdinalIgnoreCase) &&
            (string.IsNullOrWhiteSpace(c.Project) || c.Project.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)));

        var scopedChecks = BuildChangeScopedChecks(worktreePath, solutionCheck, effectiveChecks, changedFiles, policyShardPlan, infrastructureTestLanes);
        var runSolutionCheck = solutionCheck is not null && scopedChecks is null;
        var deferredChecks = runSolutionCheck
            ? BuildDeferredChecks(solutionCheck, effectiveChecks, worktreePath)
            : [];

        if (scopedChecks is not null)
        {
            var scopedNames = scopedChecks.Select(c => c.Name).ToHashSet(StringComparer.Ordinal);

            var batch = await RunCheckBatchAsync(
                effectiveChecks.Where(c =>
                    !c.Type.Equals("dotnet-test", StringComparison.OrdinalIgnoreCase) || scopedNames.Contains(c.Name)).ToArray(),
                partitionVerdictCache,
                worktreePath,
                goalId,
                stableSlotIndex,
                stableSlotLease,
                dotnetTestBuildPhase,
                shardConcurrencyBudget,
                executionOwner.CancellationToken,
                executionOwner: executionOwner).ConfigureAwait(false);
            checks.AddRange(batch.Results);
            retried |= batch.Retried;
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
                        $"covered by: {solutionCheck!.Name}",
                        CoveredBy: [solutionCheck.Name]));
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
                        executionOwner.CancellationToken,
                        executionOwner: executionOwner).ConfigureAwait(false);
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
                executionOwner.CancellationToken,
                executionOwner: executionOwner).ConfigureAwait(false);
            checks.AddRange(batch.Results);
            retried |= batch.Retried;
        }

        phaseAccountant.TransitionTo(AcceptanceGatePhaseNames.PolicySynthesis);
        if (partitionVerdictCache?.CompleteAttempt() is { } partitionCacheReceipt)
        {
            checks.Add(partitionCacheReceipt);
        }
        partitionVerdictCache?.RecordSemanticDeduplications(effectivePlan.SemanticDeduplications);

        if (effectivePlan.DotnetShardDisposition == DotnetShardDisposition.RunDotnetShards)
        {
            AddCoveredBroadInfrastructureResults(checks, policyRequiredChecks, changedFiles, infrastructureTestLanes);
            AddCoveredBroadInfrastructureResults(checks, manifest.Checks, changedFiles, infrastructureTestLanes);
            AddCoveredPolicyAliasResults(checks, policyRequiredChecks, effectiveChecks);
            AddPolicyShardReceiptResults(checks, manifest.Checks, effectiveChecks, policyShardPlan);
        }

        if (checks.All(check => check.Passed) && manifest.ForbiddenChangedPathGlobs.Count > 0)
        {
            checks.Add(await RunForbiddenChangedPathsCheckAsync(manifest.ForbiddenChangedPathGlobs, worktreePath, cancellationToken).ConfigureAwait(false));
        }

        phaseAccountant.TransitionTo(AcceptanceGatePhaseNames.StructuralCoverage);
        _testOverrides.OnStructuralCoverageStartedForTests?.Invoke();
        if (checks.All(check => check.Passed) && structuralCoverageApplies)
        {
            var (prepared, preparationDuration, preparationWait) =
                await AwaitStructuralCoveragePreparationAsync(cancellationToken).ConfigureAwait(false);
            phaseAccountant.RecordStructuralCoveragePreparation(preparationDuration, preparationWait);
            var structuralCoverage = await EvaluateStructuralCoverageCheckAsync(
                prepared, checks, cancellationToken).ConfigureAwait(false);
            checks.Add(structuralCoverage);
            retried |= structuralCoverage.LockRemediationApplied;
        }
        else if (structuralCoverageApplies)
        {
            var preparationDuration = await DiscardStructuralCoveragePreparationAsync().ConfigureAwait(false);
            phaseAccountant.RecordStructuralCoveragePreparation(preparationDuration, TimeSpan.Zero);
        }

        phaseAccountant.TransitionTo(AcceptanceGatePhaseNames.AdvisoryAndTamper);
        if (checks.All(check => check.Passed) && ProposalValidationApplies(worktreePath, changedFiles))
        {
            checks.Add(RunStateEffectProposalSchemaCheck(worktreePath, changedFiles));
        }

        // Advisory checks: always run, failures are recorded but do not affect overall Passed.
        foreach (var advisoryCheck in advisoryChecks)
        {
            if (effectivePlan.DotnetShardDisposition != DotnetShardDisposition.RunDotnetShards &&
                advisoryCheck.Type.Equals("dotnet-test", StringComparison.OrdinalIgnoreCase))
            {
                checks.Add(new AcceptanceCheckResult(
                    advisoryCheck.Name,
                    true,
                    null,
                    null,
                    ResultSummary:
                    $"advisory-skip=true; dotnet-shard-disposition={DotnetShardDispositionReason(effectivePlan.DotnetShardDisposition)}",
                    Advisory: true));
                continue;
            }

            var checkResult = await RunCheckWithCancellationProbeAsync(advisoryCheck, worktreePath, goalId, stableSlotIndex, stableSlotLease, dotnetTestBuildPhase, cancellationToken).ConfigureAwait(false);
            checks.Add(checkResult.Result with { Advisory = true });
        }

        var testFileChanges = changedFiles?.Where(TestTamperAnalysis.IsTestFile).ToArray();
        if (testFileChanges is { Length: > 0 })
        {
            checks.Add(await TestTamperAnalysis.RunTestTamperCheckAsync(
                _runner,
                EngineSettings,
                worktreePath,
                sanctionedRemovedTests,
                cancellationToken).ConfigureAwait(false));
        }

        phaseAccountant.TransitionTo(AcceptanceGatePhaseNames.Finalize);
        var failureAttributionBudget = new AcceptanceFailureAttributionPlanner.FocusedInvocationBudget(
            MaxFailureAttributionFocusedEvidenceIdentities);
        var failureAttributionRequests = new (AcceptanceCheckResult Check, AcceptanceManifestCheck? ManifestCheck)[checks.Count];
        for (var index = 0; index < checks.Count; index++)
        {
            var check = AttachFailureCauseEvidence(checks[index]);
            var manifestCheck = effectiveChecks.FirstOrDefault(candidate =>
                candidate.Name.Equals(check.Name, StringComparison.Ordinal));
            check = manifestCheck?.Project is { Length: > 0 } project
                ? check with { TestProjectPath = project }
                : check;
            checks[index] = check;
            failureAttributionRequests[index] = (check, manifestCheck);
        }

        var attributedChecks = await AttachTestFailureAttributionsBatchAsync(
            failureAttributionRequests,
            engineSettings,
            worktreePath,
            goalId,
            stableSlotIndex,
            stableSlotLease,
            cancellationToken,
            failureAttributionBudget,
            changedFiles).ConfigureAwait(false);
        for (var index = 0; index < attributedChecks.Count; index++)
        {
            checks[index] = attributedChecks[index];
        }

        if (ownerProtectedDecision.Pass is { } ownerProtectedPass) checks.Add(ownerProtectedPass);
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
            AcceptanceLaneDurationStore.Flush();
            executionOwner.MarkSuccessful();
        }

        phaseAccountant.MarkCompleted(verification.Passed);
        return verification;
        }
        catch (Exception exception) when (ShouldCaptureGateEngineFault(exception))
        {
            throw AcceptanceGateEngineException.Capture(exception, phaseAccountant.Snapshot);
        }
        finally
        {
            try
            {
                await DiscardStructuralCoveragePreparationAsync().ConfigureAwait(false);
            }
            finally
            {
                RemoveGateCreatedUntrackedPaths(untrackedSnapshot, goalId, phaseAccountant.LastStartedTarget);
                _coveragePreparationCancellation?.Dispose();
            }
        }
    }

    internal async Task<AcceptanceCheckResult> AttachTestFailureAttributionsAsync(
        AcceptanceCheckResult check,
        AcceptanceManifestCheck? manifestCheck,
        AcceptanceGateEngineSettings engineSettings,
        string worktreePath,
        GoalId? goalId,
        int? stableSlotIndex,
        DotnetBuildEnvironmentLease? stableSlotLease,
        IAcceptanceAttemptExecutionOwner executionOwner,
        CancellationToken cancellationToken,
        AcceptanceFailureAttributionPlanner.FocusedInvocationBudget? invocationBudget = null,
        IReadOnlyList<string>? changedFiles = null)
    {
        var owner = executionOwner as AcceptanceAttemptExecutionOwner ?? throw new ArgumentException(
            "Failure attribution requires an acceptance-attempt execution owner.", nameof(executionOwner));
        var attributed = await new GoalAcceptanceVerifier(this, owner).AttachTestFailureAttributionsBatchAsync(
            [(check, manifestCheck)],
            engineSettings,
            worktreePath,
            goalId,
            stableSlotIndex,
            stableSlotLease,
            cancellationToken,
            invocationBudget,
            changedFiles).ConfigureAwait(false);
        return attributed[0];
    }

    private async Task<IReadOnlyList<AcceptanceCheckResult>> AttachTestFailureAttributionsBatchAsync(
        IReadOnlyList<(AcceptanceCheckResult Check, AcceptanceManifestCheck? ManifestCheck)> requests,
        AcceptanceGateEngineSettings engineSettings,
        string worktreePath,
        GoalId? goalId,
        int? stableSlotIndex,
        DotnetBuildEnvironmentLease? stableSlotLease,
        CancellationToken cancellationToken,
        AcceptanceFailureAttributionPlanner.FocusedInvocationBudget? invocationBudget = null,
        IReadOnlyList<string>? changedFiles = null)
    {
        invocationBudget ??= new AcceptanceFailureAttributionPlanner.FocusedInvocationBudget(
            MaxFailureAttributionFocusedEvidenceIdentities);
        var results = requests.Select(request => request.Check).ToArray();
        var prepared = new List<CandidateRerunPreparedCheck>();
        for (var index = 0; index < requests.Count; index++)
        {
            var (check, manifestCheck) = requests[index];
            if (check.Passed || check.Advisory ||
                check.FailingTestIdentities is not { Count: > 0 } failingTestIdentities)
            {
                continue;
            }

            var identitySelection = invocationBudget.Select(failingTestIdentities);
            var identities = identitySelection.All;
            if (identities.Count == 0)
            {
                continue;
            }

            if (identitySelection.Selected.Count == 0)
            {
                results[index] = check with
                {
                    FailingTestAttributions = AcceptanceFailureAttributionPlanner.IncludeOmittedAsUnattributed(
                        [],
                        identitySelection.Omitted,
                        MaxFailureAttributionFocusedEvidenceIdentities)
                };
                continue;
            }

            if (manifestCheck is null ||
                !manifestCheck.Type.Equals("dotnet-test", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(manifestCheck.Project))
            {
                results[index] = Unattributed(
                    check,
                    identities,
                    "acceptance check has no exact dotnet-test project mapping");
                continue;
            }

            var selectors = identitySelection.Selected
                .Select(AcceptanceFailureAttributionPlanner.NormalizeIdentity)
                .ToArray();
            if (selectors.Any(selector => selector.Length == 0 ||
                    !Regex.IsMatch(selector, @"^[A-Za-z_][A-Za-z0-9_.]*$")))
            {
                results[index] = Unattributed(
                    check,
                    identities,
                    "failing test identity cannot be represented by an exact focused selector");
                continue;
            }

            var selectionPlan = AcceptanceFailureAttributionPlanner.BuildCandidateSelections(
                ProjectLabel(manifestCheck.Project), selectors, engineSettings, worktreePath);
            if (!selectionPlan.Succeeded)
            {
                results[index] = Unattributed(check, identities, selectionPlan.FailureEvidence!);
                continue;
            }

            invocationBudget.Consume(identitySelection);
            prepared.Add(new CandidateRerunPreparedCheck(index, check, identitySelection, selectors, selectionPlan));
        }

        if (prepared.Count == 0)
        {
            return results;
        }

        var focusedChecks = prepared
            .SelectMany(item => item.SelectionPlan.Checks)
            .GroupBy(check => check.Name, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();
        var focusedCoverage = new FocusedEvidenceCoverage(
            prepared
                .SelectMany(item => item.SelectionPlan.Coverage.TargetToChecks)
                .GroupBy(target => target.Target, StringComparer.Ordinal)
                .Select(group => new FocusedEvidenceTargetCoverage(
                    group.Key,
                    group.SelectMany(target => target.CheckNames)
                        .Distinct(StringComparer.Ordinal)
                        .ToArray()))
                .ToArray(),
            ExecutionReason: "failure-attribution-gate-batch");
        var baselineSha = ResolveGitScalar(worktreePath, "merge-base", "HEAD", "main");
        FocusedEvidenceArmRunResult baseline;
        try
        {
            baseline = await RunBaselineFocusedEvidenceArmAsync(
                worktreePath,
                baselineSha,
                goalId,
                focusedChecks,
                focusedCoverage,
                stableSlotIndex,
                stableSlotLease,
                cancellationToken,
                classifyMissingSelectionsAsAbsent: true).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            foreach (var item in prepared)
            {
                results[item.Index] = Unattributed(
                    item.Check,
                    item.IdentitySelection.All,
                    $"focused baseline execution failed: {ex.GetType().Name}");
            }

            return results;
        }

        foreach (var item in prepared)
        {
            var attributions = AcceptanceFailureAttributionPlanner.IncludeOmittedAsUnattributed(
                AcceptanceFailureAttributionPlanner.ClassifyBaselineFailures(
                    item.IdentitySelection.Selected,
                    item.Selectors,
                    item.SelectionPlan.CheckNamesBySelector,
                    baseline),
                item.IdentitySelection.Omitted,
                MaxFailureAttributionFocusedEvidenceIdentities);
            results[item.Index] = item.Check with { FailingTestAttributions = attributions };
        }

        await ApplyCandidateRerunToChecksAsync(prepared, results, baselineSha, worktreePath,
            goalId, stableSlotIndex, stableSlotLease, changedFiles, invocationBudget,
            cancellationToken).ConfigureAwait(false);

        return results;

        static AcceptanceCheckResult Unattributed(
            AcceptanceCheckResult check,
            IReadOnlyList<string> identities,
            string evidence) => check with
        {
            FailingTestAttributions = identities
                .Select(identity => new AcceptanceTestFailureAttribution(
                    identity,
                    AcceptanceTestFailureOrigin.Unattributed,
                    evidence))
                .ToArray()
        };
    }

    internal static AcceptanceCheckResult AttachFailureCauseEvidence(AcceptanceCheckResult check)
    {
        ArgumentNullException.ThrowIfNull(check);

        if (check.Passed)
        {
            return check with { FailureCauseEvidence = null };
        }

        var trxCause = ExtractTrxFailureCauseEvidence(check.TestResultPaths, check.Name);
        if (check.FailureCauseEvidence is not null && trxCause is not null &&
            (check.FailureCauseEvidence.Cause != trxCause.Cause ||
             !string.Equals(
                 check.FailureCauseEvidence.SourceClassification,
                 trxCause.SourceClassification,
                 StringComparison.Ordinal)))
        {
            return check with { FailureCauseEvidence = null };
        }

        if (string.IsNullOrWhiteSpace(check.FailureClassification) &&
            check.FailureCauseEvidence is null &&
            trxCause is not null)
        {
            check = check with
            {
                FailureClassification = trxCause.SourceClassification,
                FailureCauseEvidence = trxCause
            };
        }

        check = AcceptanceAssemblyCleanupEvidence.AttachCleanupCause(check);
        var classification = string.IsNullOrWhiteSpace(check.FailureClassification)
            ? null
            : check.FailureClassification.Trim();
        var classifiedCause = classification switch
        {
            AcceptanceFailureClassifications.GateEnvironmentInterference or
            AcceptanceFailureClassifications.FocusedSelectionApparatusFailure or
            AcceptanceFailureClassifications.FocusedSelectionReceiptUnreadable or
            AcceptanceFailureClassifications.SeededRepositoryProcessOutputApparatus or
            AcceptanceFailureClassifications.AssemblyCleanupFailure or
            AcceptanceFailureClassifications.SeededRepositoryApparatus =>
                AcceptanceFailureCause.EnvironmentalApparatus,
            _ => (AcceptanceFailureCause?)null
        };

        var supplied = check.FailureCauseEvidence;
        if (supplied is not null &&
            (!Enum.IsDefined(supplied.Cause) ||
             supplied.Cause == AcceptanceFailureCause.NotClassified ||
             string.IsNullOrWhiteSpace(supplied.Evidence) ||
             supplied.CheckName is not null &&
             !supplied.CheckName.Equals(check.Name, StringComparison.Ordinal) ||
             supplied.SourceClassification is not null &&
             (classification is null ||
              !supplied.SourceClassification.Equals(classification, StringComparison.Ordinal))))
        {
            return check with { FailureCauseEvidence = null };
        }

        if (classifiedCause is null)
        {
            return check;
        }

        if (supplied is not null && supplied.Cause != classifiedCause)
        {
            return check with { FailureCauseEvidence = null };
        }

        var evidence = supplied?.Evidence.Trim() ??
            $"check={JsonSerializer.Serialize(check.Name)}; failureClassification={JsonSerializer.Serialize(classification)}";
        return check with
        {
            FailureCauseEvidence = new AcceptanceFailureCauseEvidence(
                classifiedCause.Value,
                evidence,
                check.Name,
                classification)
        };
    }

    private static AcceptanceFailureCauseEvidence? ExtractTrxFailureCauseEvidence(
        IEnumerable<string>? trxPaths,
        string checkName)
    {
        if (trxPaths is null)
        {
            return null;
        }

        var receipts = new List<AcceptanceFailureCauseReceiptV1>();
        var failedResultCount = 0;
        foreach (var trxPath in trxPaths.Where(File.Exists))
        {
            try
            {
                var failedResults = XDocument.Load(trxPath, LoadOptions.None)
                    .Descendants()
                    .Where(element =>
                    {
                        if (!element.Name.LocalName.Equals("UnitTestResult", StringComparison.Ordinal))
                        {
                            return false;
                        }

                        var outcome = element.Attribute("outcome")?.Value;
                        return !string.Equals(outcome, "Passed", StringComparison.OrdinalIgnoreCase) &&
                            !string.Equals(outcome, "NotExecuted", StringComparison.OrdinalIgnoreCase);
                    })
                    .ToArray();
                foreach (var result in failedResults)
                {
                    failedResultCount++;
                    var message = result.Descendants()
                        .FirstOrDefault(element =>
                            element.Name.LocalName.Equals("Message", StringComparison.Ordinal) &&
                            element.Ancestors().Any(ancestor =>
                                ancestor.Name.LocalName.Equals("ErrorInfo", StringComparison.Ordinal)))
                        ?.Value;
                    if (message is null ||
                        !AcceptanceFailureCauseReceiptCodec.TryParse(message, out var receipt))
                    {
                        return null;
                    }

                    receipts.Add(receipt);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
            {
                return null;
            }
        }

        if (failedResultCount == 0 || receipts.Count != failedResultCount)
        {
            return null;
        }

        var evidence = string.Join(
            " | ",
            receipts.Select(AcceptanceFailureCauseReceiptCodec.Format).Distinct(StringComparer.Ordinal));
        if (evidence.Length > 4096)
        {
            evidence = evidence[..4096];
        }

        var sourceClassification = receipts.All(receipt =>
            receipt.Owner.Equals("ProcessOutputApparatus", StringComparison.Ordinal))
                ? AcceptanceFailureClassifications.SeededRepositoryProcessOutputApparatus
                : AcceptanceFailureClassifications.SeededRepositoryApparatus;
        return new AcceptanceFailureCauseEvidence(
            AcceptanceFailureCause.EnvironmentalApparatus,
            evidence,
            checkName,
            sourceClassification);
    }

    private static bool ShouldCaptureGateEngineFault(Exception exception) =>
        exception is not AcceptanceGateEngineException and
        not AcceptanceInfrastructureDeferredException and
        not DotnetBuildSlotsBusyException and
        not BuildLockBlockedException and
        not OperationCanceledException;

    internal Task<FocusedEvidenceRunResult> RunFocusedEvidenceAsync(
        string worktreePath,
        GoalId? goalId,
        string request,
        int? stableSlotIndex = null,
        DotnetBuildEnvironmentLease? stableSlotLease = null,
        bool runBaselineArm = false,
        CancellationToken cancellationToken = default,
        FindingEvidenceNegativeControl? negativeControl = null, IReadOnlyList<string>? revertPaths = null, FindingEvidenceMutation? mutation = null) =>
        FocusedEvidenceExecution.RunFocusedEvidenceAsync(
            RunNegativeControlFocusedEvidenceOwnedAsync, RunFocusedEvidenceOwnedAsync,
            worktreePath, goalId, request, stableSlotIndex, stableSlotLease, runBaselineArm,
            cancellationToken, negativeControl, revertPaths, mutation);

    public Task<FocusedEvidenceRunResult> RunFocusedEvidenceOwnedAsync(
        string worktreePath,
        GoalId? goalId,
        string request,
        IAcceptanceFocusedVerificationOwner executionOwner,
        int? stableSlotIndex = null,
        DotnetBuildEnvironmentLease? stableSlotLease = null,
        bool runBaselineArm = false) =>
        FocusedEvidenceExecution.RunFocusedEvidenceOwnedAsync(
            EnsureTestOverridesUnchanged,
            (owner, w, g, r, i, l, b) => owner.ExecuteAsync(this, w, g, r, i, l, b),
            worktreePath, goalId, request, executionOwner, stableSlotIndex, stableSlotLease, runBaselineArm);

    private void EnsureTestOverridesUnchanged()
    {
        if (_testOverrideSource != _testOverrides)
            throw new InvalidOperationException(
                "GoalAcceptanceVerifier test overrides changed after the verifier snapshot was created.");
    }

    internal Task<FocusedEvidenceRunResult> RunOwnedFocusedEvidenceAsync(
        string worktreePath,
        GoalId? goalId,
        string request,
        int? stableSlotIndex = null,
        DotnetBuildEnvironmentLease? stableSlotLease = null,
        bool runBaselineArm = false,
        CancellationToken cancellationToken = default,
        FindingEvidenceNegativeControl? negativeControl = null, IReadOnlyList<string>? revertPaths = null, FindingEvidenceMutation? mutation = null) =>
        FocusedEvidenceExecution.RunOwnedFocusedEvidenceAsync(
            _executionContext, RunFocusedEvidenceArmAsync, RunBaselineFocusedEvidenceArmAsync,
            AddSourceRevertedEvidenceAsync, ResolveFocusedEvidenceMergeBase,
            worktreePath, goalId, request, stableSlotIndex, stableSlotLease, runBaselineArm,
            cancellationToken, negativeControl, revertPaths, mutation);

    private Task<FocusedEvidenceArmRunResult> RunFocusedEvidenceArmAsync(
        FindingEvidenceArm arm,
        string sha,
        string worktreePath,
        GoalId? goalId,
        IReadOnlyList<AcceptanceManifestCheck> focusedChecks,
        FocusedEvidenceCoverage coverage,
        int? stableSlotIndex,
        DotnetBuildEnvironmentLease? stableSlotLease,
        DotnetBuildEnvironment? executionEnvironment,
        CancellationToken cancellationToken,
        bool continueAfterFailure = false,
        IAcceptanceRunExecutionContext? executionOwner = null,
        bool armContextApplied = false) =>
        FocusedEvidenceExecution.RunFocusedEvidenceArmAsync(
            armContext => new GoalAcceptanceVerifier(this, armContext).RunFocusedEvidenceArmAsync,
            RunFocusedEvidenceChecksAsync, CaptureLimitDetail,
            arm, sha, worktreePath, goalId, focusedChecks, coverage, stableSlotIndex, stableSlotLease,
            executionEnvironment, cancellationToken, continueAfterFailure, executionOwner, armContextApplied);

    private Task<FocusedEvidenceArmRunResult> RunBaselineFocusedEvidenceArmAsync(
        string candidateWorktreePath,
        string? baselineSha,
        GoalId? goalId,
        IReadOnlyList<AcceptanceManifestCheck> focusedChecks,
        FocusedEvidenceCoverage coverage,
        int? stableSlotIndex,
        DotnetBuildEnvironmentLease? stableSlotLease,
        CancellationToken cancellationToken,
        bool classifyMissingSelectionsAsAbsent = false,
        IAcceptanceRunExecutionContext? executionOwner = null,
        bool partitionCandidateOnlySelections = false) =>
        FocusedEvidenceExecution.RunBaselineFocusedEvidenceArmAsync(
            RunFocusedEvidenceArmAsync, GetFocusedEvidenceBaselineRoot, TryLabelFocusedEvidenceBaselineWorktree,
            SelectBaselineFocusedChecks, CombineFindingBaselineArm, _storageRoot,
            candidateWorktreePath, baselineSha, goalId, focusedChecks, coverage, stableSlotIndex,
            stableSlotLease, cancellationToken, classifyMissingSelectionsAsAbsent, executionOwner,
            partitionCandidateOnlySelections);

    private static string GetFocusedEvidenceBaselineRoot()
    {
        var baselineRoot = Path.Combine(Path.GetTempPath(), FocusedEvidenceBaselinesRootDirectoryName);
        return baselineRoot;
    }

    internal static FindingEvidenceOutcomeReason ClassifyFocusedEvidenceExperiment(
        FocusedEvidenceArmRunResult candidate,
        FocusedEvidenceArmRunResult baseline) =>
        FocusedEvidenceExecution.ClassifyFocusedEvidenceExperiment(
            candidate, baseline);

    private static void EmitFocusedEvidenceArmStarted(FindingEvidenceArm arm, string sha) =>
        FocusedEvidenceExecution.EmitFocusedEvidenceArmStarted(
            arm, sha);

    private static void EmitFocusedEvidenceArmResolved(FocusedEvidenceArmRunResult arm) =>
        FocusedEvidenceExecution.EmitFocusedEvidenceArmResolved(
            arm);

    internal static string TrimForReceipt(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length <= 500 ? trimmed : trimmed[..500];
    }

    private static void TryDeleteFocusedEvidenceBaselineDirectory(string baselineRoot, string baselinePath) =>
        FocusedEvidenceExecution.TryDeleteFocusedEvidenceBaselineDirectory(
            baselineRoot, baselinePath);

    private async Task<IReadOnlyList<AcceptanceCheckResult>> RunFocusedEvidenceChecksAsync(
        string worktreePath,
        IReadOnlyList<AcceptanceManifestCheck> focusedChecks,
        GoalId? goalId,
        int? stableSlotIndex,
        DotnetBuildEnvironmentLease? stableSlotLease,
        DotnetBuildEnvironment? executionEnvironment,
        CancellationToken cancellationToken,
        bool continueAfterFailure,
        IAcceptanceRunExecutionContext? executionOwner)
    {
        var dotnetTestBuildPhase = CreateDotnetTestBuildPhase(
            worktreePath,
            focusedChecks,
            changedFiles: null,
            PolicyShardPlan.NotApplicable("focused evidence"));
        dotnetTestBuildPhase.BuildEnvironment = executionEnvironment;
        var shardCoreBudget =
            _testOverrides.ResolveShardCoreBudgetForTests?.Invoke() ?? Math.Max(1, Environment.ProcessorCount / 2);
        var shardConcurrencyBudget = Math.Min(EngineSettings.MaxConcurrentShards, shardCoreBudget);
        var batch = await RunCheckBatchAsync(
            focusedChecks,
            cacheContext: null,
            worktreePath,
            goalId,
            stableSlotIndex,
            stableSlotLease,
            dotnetTestBuildPhase,
            shardConcurrencyBudget,
            cancellationToken,
            continueAfterFailure,
            executionOwner).ConfigureAwait(false);
        return batch.Results;
    }

    private static bool ShouldStopAfterFailedCheck(
        AcceptancePartitionVerdictCache? cacheContext,
        AcceptanceManifestCheck check) =>
        cacheContext is null ||
        !TryGetInfrastructurePartitionId(check, out _, out _);

    private async Task<CheckBatchResult> RunCheckBatchAsync(
        IReadOnlyList<AcceptanceManifestCheck> batchChecks,
        AcceptancePartitionVerdictCache? cacheContext,
        string worktreePath,
        GoalId? goalId,
        int? stableSlotIndex,
        DotnetBuildEnvironmentLease? stableSlotLease,
        DotnetTestBuildPhase? dotnetTestBuildPhase,
        int maxConcurrentShards,
        CancellationToken cancellationToken,
        bool continueAfterFailure = false,
        IAcceptanceRunExecutionContext? executionOwner = null)
    {
        var independentChecks = batchChecks.Where(check => !check.Type.Equals("dotnet-test", StringComparison.OrdinalIgnoreCase)).ToArray();
        var laneChecks = batchChecks.Where(check => check.Type.Equals("dotnet-test", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (independentChecks.Length > 0 && laneChecks.Length > 0)
        {
            var overlapped = await AcceptanceOverlappedCheckRunner.RunAsync(
                async () =>
                {
                    var independent = await RunCheckBatchAsync(independentChecks, cacheContext, worktreePath,
                        goalId, stableSlotIndex, stableSlotLease, dotnetTestBuildPhase, 1,
                        cancellationToken, continueAfterFailure, executionOwner).ConfigureAwait(false);
                    if (independent.Results.All(result => result.Passed))
                        SignalStructuralCoveragePrechecksPassed();
                    return independent;
                },
                () => RunCheckBatchAsync(laneChecks, cacheContext, worktreePath, goalId, stableSlotIndex,
                    stableSlotLease, dotnetTestBuildPhase, maxConcurrentShards, cancellationToken, continueAfterFailure, executionOwner),
                executionOwner).ConfigureAwait(false);
            return new CheckBatchResult([.. overlapped.Independent.Results, .. overlapped.Lanes.Results],
                overlapped.Independent.Retried || overlapped.Lanes.Retried);
        }
        if (!_coverageHasIndependentChecks && independentChecks.Length == 0 && laneChecks.Length > 0)
            SignalStructuralCoveragePrechecksPassed();
        if (CanOverlapNonPartitionDotnetTests(batchChecks, stableSlotIndex, stableSlotLease,
                dotnetTestBuildPhase, maxConcurrentShards))
            return await RunOverlappedDotnetTestBatchAsync(batchChecks, cacheContext, worktreePath,
                goalId, stableSlotIndex!.Value, stableSlotLease!, dotnetTestBuildPhase!,
                maxConcurrentShards, cancellationToken, continueAfterFailure).ConfigureAwait(false);
        var results = new List<AcceptanceCheckResult>(batchChecks.Count);
        var retried = false;
        for (var index = 0; index < batchChecks.Count;)
        {
            if (cacheContext?.SharedApparatusInvalidation is not null)
            {
                break;
            }

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
            if (cacheContext?.SharedApparatusInvalidation is not null)
            {
                break;
            }
            if (!checkResult.Result.Passed &&
                !continueAfterFailure &&
                ShouldStopAfterFailedCheck(cacheContext, check))
            {
                break;
            }
        }

        return new CheckBatchResult(
            cacheContext?.ApplySharedApparatusInvalidation(results) ?? results,
            retried);
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
                primaryLease.ReleaseExecutionLock();
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
        var shardConcurrency = new GateShardConcurrencyCounter();
        var failures = new List<Exception>();
        using var sharedApparatusCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, stopToken);

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
                shardResultsDirectory).ConfigureAwait(false);
            var shardElapsed = _timeProvider.GetElapsedTime(shardStarted);
            AcceptanceGatePhaseAccountant.RecordCurrentLaneSample(shardElapsed);
            outcomes[shard.Index] = new ShardRunOutcome(run.Result, run.Retried);
            AcceptanceLaneDurationStore.Record(shard.Check, run.Result, shardElapsed);
            EmitShardTimingProgress(
                goalId,
                "shard-complete",
                shard.Check.Name,
                worker.SlotIndex,
                shardElapsed,
                shardConcurrency.Count,
                _storageRoot, EmitGateProgress);
        }

        while (pendingShards.Count > 0 || activeShards.Count > 0)
        {
            if (stopToken.IsCancellationRequested)
                pendingShards.Clear();
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

            if (activeShards.Count == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (stopToken.IsCancellationRequested)
                    break;
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

    internal static string ResolveInfrastructureShardResultsDirectory(
        DotnetBuildEnvironment environment,
        string? attemptPrefix = null)
    {
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

    private void VerifyPrebuiltMtpArtifacts(
        IReadOnlyList<AcceptanceManifestCheck> shardChecks,
        DotnetTestBuildPhase buildPhase)
    {
        var buildEnvironment = buildPhase.BuildEnvironment
            ?? throw new InvalidOperationException("Infrastructure shard prebuild completed without recording its build environment.");
        foreach (var artifactPath in shardChecks
            .Select(check => EngineSettings.ResolveMtpInvocation(check.Project))
            .DistinctBy(invocation => invocation.Project, StringComparer.OrdinalIgnoreCase)
            .SelectMany(invocation => invocation.ResolveRequiredBuildArtifacts(buildEnvironment))
            .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(artifactPath))
            {
                throw new InvalidDataException($"Infrastructure shard prebuild did not produce configured MTP artifact '{artifactPath}'.");
            }
        }
    }

    internal static void EmitShardTimingProgress(
        GoalId? goalId,
        string phase,
        string target,
        int slotIndex,
        TimeSpan elapsed,
        int concurrentShardCount,
        DotnetBuildStorageRoot storageRoot,
        Action<AcceptanceGateProgress>? progressSink)
    {
        var now = DateTimeOffset.UtcNow;
        progressSink?.Invoke(new AcceptanceGateProgress(
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
            GateHeartbeatArtifacts.GetStableSlotPath(slotIndex, storageRoot),
            GateLoadContextProbe.Capture(concurrentShardCount)));
    }

    private async Task<(AcceptanceCheckResult Result, bool Retried)> RunCheckWithPartitionVerdictCacheAsync(
        AcceptanceManifestCheck check,
        AcceptancePartitionVerdictCache? cacheContext,
        string worktreePath,
        GoalId? goalId,
        int? stableSlotIndex,
        DotnetBuildEnvironmentLease? stableSlotLease,
        DotnetTestBuildPhase? dotnetTestBuildPhase,
        CancellationToken cancellationToken,
        string? testResultsDirectoryOverride = null)
    {
        if (cacheContext?.TryReuse(check) is { } reused)
        {
            return (reused, false);
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
        var currentAttemptId = cacheContext?.AttemptId ??
            (_executionContext as AcceptanceAttemptExecutionOwner)?.Identity.AttemptId;
        var completionDecision = fresh.Result.CompletionDecision ?? InferPartitionCompletionDecision(fresh.Result);
        fresh = (fresh.Result with
        {
            TestResultAttemptId = currentAttemptId,
            CompletionDecision = completionDecision,
            FailureClassification = fresh.Result.FailureClassification ?? completionDecision.FailedPredicate
        }, fresh.Retried);

        // A failed infrastructure partition may run once more as a classification probe. The first run
        // remains the verdict; the probe only records whether the failure was confirmed as intermittent.
        fresh = (cacheContext?.ObserveSharedApparatusEvidence(check, fresh.Result) ?? fresh.Result, fresh.Retried);
        if (cacheContext?.ShouldRerunWithinAttempt(check, fresh.Result.CompletionDecision) == true)
        {
            var original = fresh.Result;
            var originalInvocationId = BuildInvocationIdentity(check, original.TestResultRunOrdinal, currentAttemptId);
            AcceptanceRetainedDiagnostic? retainedDiagnostic = null;
            try
            {
                retainedDiagnostic = AcceptanceAttemptArtifactCustody.RetainRetryDiagnostic(
                    AcceptanceAttemptResultsPrefix,
                    original.ArtifactsPath,
                    $"{Slug(check.Name)}-run-{original.TestResultRunOrdinal.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
                    original.ProcessStderrPath,
                    original.ProcessStderr,
                    JsonSerializer.Serialize(original.CompletionDecision));
                if (string.IsNullOrWhiteSpace(original.GateHeartbeatPath))
                {
                    throw new IOException(
                        $"Retry-driving result '{originalInvocationId}' has no original heartbeat identity.");
                }

                GateHeartbeatArtifacts.AttachRetainedStderr(original.GateHeartbeatPath, retainedDiagnostic);
            }
            catch (IOException ex)
            {
                var evidenceFailure = BuildRetryEvidenceRetentionFailure(original, retainedDiagnostic, ex);
                cacheContext.RecordExecution(check, evidenceFailure);
                return (evidenceFailure, false);
            }

            var retryInvocation = AllocateTestTelemetryInvocation(check);
            var retryInvocationId = BuildInvocationIdentity(check, retryInvocation.Ordinal, currentAttemptId);
            cacheContext.RecordWithinAttemptRetry(
                check,
                original,
                originalInvocationId,
                retryInvocationId,
                retainedDiagnostic);
            var rerun = await RunCheckWithCancellationProbeAsync(
                check,
                worktreePath,
                goalId,
                stableSlotIndex,
                stableSlotLease,
                dotnetTestBuildPhase,
                cancellationToken,
                testResultsDirectoryOverride,
                retryInvocation).ConfigureAwait(false);
            var rerunDecision = rerun.Result.CompletionDecision ?? InferPartitionCompletionDecision(rerun.Result);
            var rerunResult = rerun.Result with
            {
                TestResultAttemptId = currentAttemptId,
                CompletionDecision = rerunDecision,
                FailureClassification = rerun.Result.FailureClassification ?? rerunDecision.FailedPredicate
            };
            fresh = (DecorateVerdictWithGateChildReap(
                cacheContext.SelectPartitionVerdict(check, original, rerunResult), rerunResult), true);
        }

        cacheContext?.RecordExecution(check, fresh.Result);

        return fresh;
    }

    private static AcceptanceCheckResult BuildRetryEvidenceRetentionFailure(
        AcceptanceCheckResult original,
        AcceptanceRetainedDiagnostic? retainedDiagnostic,
        IOException exception)
    {
        var detail =
            $"Within-attempt retry refused: {AcceptanceFailureClassifications.RetryEvidenceRetentionFailed}; " +
            $"{exception.Message}" +
            (retainedDiagnostic is null
                ? string.Empty
                : $" Retained diagnostic remains at '{retainedDiagnostic.Path}' (sha256={retainedDiagnostic.Sha256}).");
        return original with
        {
            Passed = false,
            OutputTail = string.IsNullOrWhiteSpace(original.OutputTail)
                ? detail
                : $"{original.OutputTail}{Environment.NewLine}{detail}",
            ResultSummary = PrefixResultSummary(detail, original.ResultSummary),
            FailureClassification = AcceptanceFailureClassifications.RetryEvidenceRetentionFailed,
            CompletionDecision = original.CompletionDecision is null
                ? null
                : original.CompletionDecision with
                {
                    PolicySignal = AcceptanceFailureClassifications.RetryEvidenceRetentionFailed
                },
            ProcessStderrPath = retainedDiagnostic?.Path ?? original.ProcessStderrPath
        };
    }

    private async Task<(AcceptanceCheckResult Result, bool Retried)> RunCheckWithCancellationProbeAsync(
        AcceptanceManifestCheck check,
        string worktreePath,
        GoalId? goalId,
        int? stableSlotIndex,
        DotnetBuildEnvironmentLease? stableSlotLease,
        DotnetTestBuildPhase? dotnetTestBuildPhase,
        CancellationToken cancellationToken,
        string? testResultsDirectoryOverride = null,
        AcceptanceInvocationContext? invocationOverride = null)
    {
        ThrowIfGateCancellationRequested(cancellationToken);
        var invocation = invocationOverride ?? AllocateTestTelemetryInvocation(check);
        var invocationVerifier = new GoalAcceptanceVerifier(this, invocation);
        var invocationTask = AcceptanceGateCancellationMonitor.RunAsync(
                activeToken => invocationVerifier.RunCheckAsync(check,
                    worktreePath,
                    goalId,
                    stableSlotIndex,
                    stableSlotLease,
                    dotnetTestBuildPhase,
                    activeToken,
                    testResultsDirectoryOverride),
                _executionContext?.ResolveCancellationProbe(boundary: false), cancellationToken);
        var result = await (_executionContext?.TrackStarted(invocationTask) ?? invocationTask).ConfigureAwait(false);
        ThrowIfGateCancellationRequested(cancellationToken);
        return result;
    }

    internal Task<(AcceptanceCheckResult Result, bool Retried)> RunInvocationForOwnerTestsAsync(
        IAcceptanceRunExecutionContext owner,
        AcceptanceInvocationContext invocation,
        AcceptanceManifestCheck check,
        string worktreePath,
        CancellationToken cancellationToken) =>
        new GoalAcceptanceVerifier(this, owner).RunCheckWithCancellationProbeAsync(
            check, worktreePath, null, null, null, null, cancellationToken,
            invocationOverride: invocation);

    private static string BuildInvocationIdentity(
        AcceptanceManifestCheck check,
        int ordinal,
        string? attemptId) =>
        $"{attemptId ?? "manual"}:{Slug(check.Name)}:{ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture)}";

    private static AcceptanceShardCompletionDecision InferPartitionCompletionDecision(AcceptanceCheckResult result)
    {
        if (result.Passed)
        {
            return new AcceptanceShardCompletionDecision(
                true,
                null,
                false,
                result.ExitCode,
                result.DiscoveredTestCount,
                result.ExecutedTestCount,
                "not-applicable",
                result.FailureClassification);
        }

        var predicate = result.FailureClassification ??
            (result.Name.StartsWith("acceptance-check-timeout:", StringComparison.Ordinal) ||
             IsCaptureLimitFailureName(result.Name)
                ? AcceptanceShardCompletionPredicates.TimedOut
                : result.ExitCode != 0
                    ? AcceptanceShardCompletionPredicates.NonzeroExit
                    : AcceptanceShardCompletionPredicates.CheckFailed);
        return new AcceptanceShardCompletionDecision(
            false,
            predicate,
            predicate.Equals(AcceptanceShardCompletionPredicates.TimedOut, StringComparison.Ordinal),
            result.ExitCode,
            result.DiscoveredTestCount,
            result.ExecutedTestCount,
            "not-available",
            result.FailureClassification);
    }

    private void ThrowIfGateCancellationRequested(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_executionContext?.ResolveCancellationProbe(boundary: true)?.Invoke() == true)
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

    internal static bool TryBuildFocusedEvidenceChecks(
        string request,
        AcceptanceGateEngineSettings engineSettings,
        string worktreePath,
        out IReadOnlyList<AcceptanceManifestCheck> checks,
        out FocusedEvidenceCoverage coverage,
        out FocusedEvidenceRejection rejection) =>
        FocusedEvidenceRequestResolver.TryBuildFocusedEvidenceChecks(request, engineSettings, worktreePath, out checks, out coverage, out rejection);

    internal static bool TryResolveFocusedEvidenceProject(
        string alias,
        out string project) =>
        FocusedEvidenceRequestResolver.TryResolveFocusedEvidenceProject(alias, out project);

    internal static bool TryResolveFocusedEvidenceProject(
        string alias,
        AcceptanceGateEngineSettings? engineSettings,
        out string project) =>
        FocusedEvidenceRequestResolver.TryResolveFocusedEvidenceProject(alias, engineSettings, out project);

    internal static string FormatReceiptPaths(IReadOnlyList<string> paths) =>
        paths.Count == 0 ? "none" : string.Join(", ", paths);

    internal static string FormatFocusedEvidencePlanSummary(FocusedEvidenceCoverage coverage) =>
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
        string worktreePath,
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

        var plan = RepositoryTestImpactPlanner.Plan(summary, worktreePath);
        if (!plan.RequiresBuild ||
            plan.RequiresBroadVerification ||
            plan.Checks.Any(check => check.Command.Count == 0))
        {
            return null;
        }

        var scoped = new List<AcceptanceManifestCheck>();
        foreach (var plannedCheck in plan.Checks)
        {
            var plannedManifestCheck = PolicyCheckToManifestCheck(worktreePath, plannedCheck);
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

    private static IReadOnlyList<AcceptanceManifestCheck> BuildPolicyEffectiveChecks(
        string worktreePath,
        IReadOnlyList<AcceptanceManifestCheck> manifestChecks,
        IReadOnlyList<string>? changedFiles,
        IReadOnlyList<AcceptanceManifestCheck>? policyRequiredChecks = null,
        PolicyShardPlan? policyShardPlan = null,
        IReadOnlyList<AcceptanceTestLane>? infrastructureTestLanes = null)
    {
        policyShardPlan ??= BuildPolicyShardPlan(changedFiles, null,
            CandidateTreeProbe.ForRepositoryRoot(worktreePath), manifestChecks.Select(check => check.Project).OfType<string>());
        if (changedFiles is null || changedFiles.Count == 0)
            return manifestChecks.Where(check => !IsSkippedPolicyShardCheck(check, policyShardPlan)).ToArray();

        var requiredPolicyChecks = policyRequiredChecks ?? BuildRequiredPolicyChecks(worktreePath, changedFiles);
        var plannedChecks = requiredPolicyChecks
            .Where(check => !check.Type.Equals("dotnet-test", StringComparison.OrdinalIgnoreCase) || !policyShardPlan.IsAbsentProject(check.Project))
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
        AcceptancePolicyShardPlanner.StructuralCoverageApplies(engineSettings, changedFiles);

    internal static DotnetShardDisposition ClassifyDotnetShardDisposition(
        IReadOnlyList<string>? changedFiles) =>
        AcceptancePolicyShardPlanner.ClassifyDotnetShardDisposition(changedFiles);

    private static EffectiveGatePlan CreateEffectiveGatePlan(
        string worktreePath,
        IReadOnlyList<string>? changedFiles,
        AcceptanceGateEngineSettings engineSettings)
    {
        var manifest = AcceptanceManifest.Load(worktreePath, changedFiles);
        var dotnetShardDisposition = ClassifyDotnetShardDisposition(changedFiles);
        var policyShardPlan = BuildPolicyShardPlan(changedFiles, null,
            CandidateTreeProbe.ForRepositoryRoot(worktreePath), manifest.Checks.Select(check => check.Project).OfType<string>());
        if (dotnetShardDisposition == DotnetShardDisposition.RunDotnetShards &&
            !policyShardPlan.ForceFull &&
            manifest.Checks.Any(check =>
                check.Type.Equals("dotnet-test", StringComparison.OrdinalIgnoreCase)) &&
            !manifest.Checks.Any(check =>
                check.Type.Equals("dotnet-test", StringComparison.OrdinalIgnoreCase) &&
                policyShardPlan.IncludesProject(check.Project)))
        {
            policyShardPlan = policyShardPlan with
            {
                Applies = true,
                ForceFull = true,
                Evidence = $"strict candidate path disposition requires dotnet shards; {policyShardPlan.Evidence}"
            };
        }

        var infrastructureTestLanes = SelectInfrastructureTestLanes(
            ResolveOwnedCollectionLanes(engineSettings, worktreePath),
            changedFiles,
            policyShardPlan);

        // This is the single owner of the environment- and scope-expanded gate plan. Cohort
        // identity, partition-cache identity, and execution all hash or consume this exact plan.
        var policyRequiredChecks = BuildRequiredPolicyChecks(worktreePath, changedFiles);
        var policyEffectiveChecks = BuildPolicyEffectiveChecks(
            worktreePath,
            manifest.Checks,
            changedFiles,
            policyRequiredChecks,
            policyShardPlan,
            infrastructureTestLanes);
        var structuralCoverageApplies = StructuralCoverageApplies(engineSettings, changedFiles);
        var structuralCoverageDeclarations = structuralCoverageApplies
            ? EnsureTrustedStructuralCoverageDeclarations(
                policyEffectiveChecks,
                manifest.Checks,
                worktreePath)
            : new StructuralCoverageDeclarationPlan(policyEffectiveChecks, []);
        var semanticDeduplication = DeduplicateSemanticExecutionChecks(ApplyDotnetShardDisposition(
            ExpandBroadInfrastructureChecks(
                structuralCoverageDeclarations.Checks,
                infrastructureTestLanes),
            dotnetShardDisposition));
        return new EffectiveGatePlan(
            manifest,
            semanticDeduplication.Checks,
            infrastructureTestLanes,
            policyShardPlan,
            policyRequiredChecks,
            structuralCoverageDeclarations.UndeclaredProjects,
            structuralCoverageApplies,
            dotnetShardDisposition,
            semanticDeduplication.Receipts);
    }

    private static IReadOnlyList<AcceptanceManifestCheck> BuildRequiredPolicyChecks(
        string worktreePath,
        IReadOnlyList<string>? changedFiles)
    {
        if (changedFiles is null || changedFiles.Count == 0)
            return [];

        var policy = VerificationPolicyCompiler.Compile(
            AgentRole.Reviewer,
            goalObjective: string.Empty,
            taskDescription: string.Empty,
            verificationPlan: null,
            changedFiles, RepositoryTestImpactPlanner.Plan(changedFiles, worktreePath));
        return MapRequiredPolicyChecks(worktreePath, policy);
    }

    private static IReadOnlyList<AcceptanceManifestCheck> MapRequiredPolicyChecks(
        string worktreePath,
        VerificationPolicy policy) =>
        policy.Checks
            .Where(c => c.Required)
            .Select(check => PolicyCheckToManifestCheck(worktreePath, check))
            .Where(c => c is not null)
            .Select(c => c!)
            .ToArray();

    internal static string ProjectLabel(string project) =>
        AcceptancePolicyShardPlanner.ProjectLabel(project);

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

    private static SemanticExecutionDeduplicationPlan DeduplicateSemanticExecutionChecks(
        IReadOnlyList<AcceptanceManifestCheck> checks)
    {
        var keys = new Dictionary<string, (AcceptanceManifestCheck Check, int Index)>(StringComparer.OrdinalIgnoreCase);
        var unique = new List<AcceptanceManifestCheck>(checks.Count);
        var receipts = new List<SemanticExecutionDeduplicationReceipt>();
        for (var index = 0; index < checks.Count; index++)
        {
            var check = checks[index];
            var key = SemanticExecutionKey(check);
            if (!TryGetInfrastructurePartitionId(check, out var partitionId, out _) ||
                !keys.TryGetValue(key, out var retained))
            {
                keys[key] = (check, index);
                unique.Add(check);
                continue;
            }

            receipts.Add(new SemanticExecutionDeduplicationReceipt(
                partitionId,
                retained.Check.Name,
                check.Name,
                retained.Index,
                index,
                Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)))));
        }

        return new SemanticExecutionDeduplicationPlan(unique, receipts);
    }

    private static string SemanticExecutionKey(AcceptanceManifestCheck check) =>
        JsonSerializer.Serialize(new
        {
            check.Name,
            check.Type,
            check.Command,
            Project = NormalizePath(check.Project),
            check.Arguments,
            check.Pattern,
            FilePath = NormalizePath(check.FilePath),
            check.TimeoutMinutes,
            check.Advisory,
            check.Runner,
            check.EstimatedSerialSeconds,
            ExclusiveResourceKeys = check.ExclusiveResourceKeys
                .OrderBy(key => key, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            check.IsFocusedEvidenceSelection,
            check.FocusedEvidenceTokens,
            check.FocusedEvidenceSelections
        });

    internal static string SemanticExecutionKeyForTests(AcceptanceManifestCheck check) =>
        SemanticExecutionKey(check);

    private static IEnumerable<AcceptanceManifestCheck> ExpandBroadInfrastructureCheck(
        AcceptanceManifestCheck check,
        IReadOnlyList<AcceptanceTestLane>? infrastructureTestLanes = null)
    {
        if (!IsBroadInfrastructureTestCheck(check))
        {
            yield return check;
            yield break;
        }

        foreach (var lane in infrastructureTestLanes ?? new AcceptanceGateEngineSettings().InfrastructureTestLanes)
        {
            yield return BuildInfrastructureShardCheck(check, lane);
        }
    }

    private static string ManifestCheckKey(AcceptanceManifestCheck check) =>
        $"{check.Type}:{check.Command}:{NormalizePath(check.Project)}:{string.Join('\u001f', check.Arguments)}";

    private static AcceptanceManifestCheck? PolicyCheckToManifestCheck(
        string worktreePath,
        VerificationPolicyCheck check)
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

            return DotnetCommandToManifestCheck(worktreePath, check.Name, parts);
        }

        return null;
    }

    private static AcceptanceManifestCheck PolicyCheckToManifestCheck(
        string worktreePath,
        RepositoryTestImpactCheck check)
    {
        // Command format: ["dotnet", "test", "--project", <project>, ...args]
        // or the legacy positional form: ["dotnet", "test", <optional project>, ...args]
        return DotnetCommandToManifestCheck(worktreePath, check.Name, [.. check.Command]);
    }

    private static AcceptanceManifestCheck DotnetCommandToManifestCheck(
        string worktreePath,
        string name,
        string[] command)
    {
        var remaining = command.Skip(2).ToArray();
        var usesNativeProjectOption = remaining.Length > 1 &&
            remaining[0].Equals("--project", StringComparison.OrdinalIgnoreCase);
        var project = usesNativeProjectOption
            ? remaining[1]
            : remaining.Length > 0 && !remaining[0].StartsWith("-", StringComparison.Ordinal)
                ? remaining[0]
                : null;
        var arguments = usesNativeProjectOption
            ? remaining.Skip(2).ToArray()
            : project is null
                ? remaining
                : remaining.Skip(1).ToArray();
        return new AcceptanceManifestCheck
        {
            Name = name,
            Type = "dotnet-test",
            Project = project,
            Arguments = arguments,
            Runner = ResolveDotnetTestRunner(worktreePath, project)
        };
    }

    internal static string ResolveDotnetTestRunner(
        string worktreePath,
        string? project,
        Func<string, XDocument>? loadProject = null)
    {
        if (string.IsNullOrWhiteSpace(worktreePath) ||
            string.IsNullOrWhiteSpace(project) ||
            !project.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
        {
            return "mtp";
        }

        try
        {
            var rootPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(worktreePath));
            var projectPath = Path.GetFullPath(Path.Combine(rootPath, project));
            var relativePath = Path.GetRelativePath(rootPath, projectPath);
            if (Path.IsPathRooted(relativePath) ||
                relativePath.Equals("..", StringComparison.Ordinal) ||
                relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
                relativePath.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal))
            {
                return "mtp";
            }

            var document = (loadProject ?? (path => XDocument.Load(path, LoadOptions.None)))(projectPath);
            var declarations = document
                .Descendants()
                .Where(element => element.Name.LocalName.Equals(
                    "UseMicrosoftTestingPlatformRunner",
                    StringComparison.Ordinal))
                .ToArray();
            if (declarations.Length != 1)
            {
                return "mtp";
            }

            var declaration = declarations[0];
            var propertyGroup = declaration.Parent;
            var isUnconditionalTopLevelProperty =
                propertyGroup is not null &&
                propertyGroup.Name.LocalName.Equals("PropertyGroup", StringComparison.Ordinal) &&
                ReferenceEquals(propertyGroup.Parent, document.Root) &&
                !propertyGroup.Attributes().Any(attribute =>
                    attribute.Name.LocalName.Equals("Condition", StringComparison.OrdinalIgnoreCase)) &&
                !declaration.Attributes().Any(attribute =>
                    attribute.Name.LocalName.Equals("Condition", StringComparison.OrdinalIgnoreCase));
            return isUnconditionalTopLevelProperty &&
                bool.TryParse(declaration.Value.Trim(), out var useMtp) &&
                !useMtp
                    ? "vstest"
                    : "mtp";
        }
        catch (Exception ex) when (
            ex is IOException or
                UnauthorizedAccessException or
                System.Security.SecurityException or
                System.Xml.XmlException or
                ArgumentException or
                NotSupportedException)
        {
            return "mtp";
        }
    }

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
                    $"covered by failed partition: {failedShard.Name}",
                    CoveredBy: [failedShard.Name]));
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
                $"covered by {shardResults.Length} partitioned checks",
                CoveredBy: shardResults.Select(result => result.Name).ToArray()));
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
                $"covered by: {coveringCheck.Name}",
                CoveredBy: [coveringCheck.Name]));
        }
    }

    private static string ComputeEffectiveAcceptanceManifestIdentity(
        IReadOnlyList<AcceptanceManifestCheck> effectiveChecks,
        AcceptanceGateEngineSettings? engineSettings = null)
    {
        var canonicalChecks = effectiveChecks.Select(check => new
        {
            check.Name,
            check.Type,
            check.Command,
            check.Project,
            Arguments = check.Arguments.ToArray(),
            check.Pattern,
            check.FilePath,
            check.TimeoutMinutes,
            check.Advisory,
            check.Runner,
            check.EstimatedSerialSeconds,
            ExclusiveResourceKeys = check.ExclusiveResourceKeys.ToArray()
        }).ToArray();
        var settings = engineSettings ?? new AcceptanceGateEngineSettings();
        var canonicalPlan = new
        {
            Checks = canonicalChecks,
            ChangeScopedAcceptanceEnabled = AcceptancePolicyShardPlanner.ChangeScopedAcceptanceEnabled(),
            Engine = new
            {
                settings.MaxConcurrentShards,
                settings.EnforceStructuralCoverage,
                settings.PartitionVerdictFullRerunEveryN,
                settings.OutputCaptureLimitBytes,
                Timeouts = new
                {
                    settings.Timeouts.DefaultMinutes,
                    settings.Timeouts.BuildServerShutdownMinutes,
                    settings.Timeouts.DiscoveryMinutes
                },
                MtpInvocations = settings.MtpInvocations
                    .OrderBy(invocation => invocation.Project, StringComparer.OrdinalIgnoreCase)
                    .Select(invocation => new
                    {
                        invocation.Project,
                        invocation.ExecutablePathTemplate,
                        Arguments = invocation.Arguments.ToArray()
                    })
                    .ToArray()
            }
        };
        var canonicalJson = JsonSerializer.Serialize(canonicalPlan);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonicalJson));
        return $"effective-manifest-sha256-{Convert.ToHexStringLower(hash)}";
    }

    internal static bool TryGetInfrastructurePartitionId(
        AcceptanceManifestCheck check,
        out string partitionId,
        out string filter)
    {
        partitionId = string.Empty;
        filter = string.Empty;
        if (!check.Type.Equals("dotnet-test", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(check.Project) ||
            !ProjectMatches(check.Project, InfrastructureTestsProject) ||
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

    internal static string ShortHash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value.Trim())))[..16].ToLowerInvariant();

    internal static string NormalizeShaToken(string value) => value.Trim().ToLowerInvariant();

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
                .Where(c =>
                    !string.IsNullOrWhiteSpace(c.Type) &&
                    !c.Type.Equals("test-removal", StringComparison.OrdinalIgnoreCase))
                .Select(c => CriterionToManifestCheck(c))
                .ToArray();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string[] LoadSanctionedTestRemovals(string worktreePath)
    {
        var path = System.IO.Path.Combine(worktreePath, ".orchestrator", "goal-acceptance-criteria.json");
        if (!File.Exists(path))
            return [];

        try
        {
            return (JsonSerializer.Deserialize<AcceptanceCriterion[]>(
                    File.ReadAllText(path),
                    CriteriaJsonOptions) ?? [])
                .Where(c => string.Equals(c.Type, "test-removal", StringComparison.OrdinalIgnoreCase))
                .Select(c => c.TestIdentity?.Trim())
                .Where(identity => !string.IsNullOrWhiteSpace(identity))
                .Cast<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
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
        using var targetScope = AcceptanceGatePhaseAccountant.BeginCurrentTarget(check.Name);
        try
        {
        if (check.Type.Equals("no-op", StringComparison.OrdinalIgnoreCase))
        {
            return (new AcceptanceCheckResult(
                check.Name,
                true,
                null,
                null,
                ResultSummary: check.Arguments.Count == 0 ? null : string.Join("; ", check.Arguments),
                Advisory: check.Advisory), false);
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
        catch (Exception exception) when (ShouldCaptureGateEngineFault(exception))
        {
            throw AcceptanceGateEngineException.Capture(
                exception,
                AcceptanceGatePhaseAccountant.CurrentSnapshot);
        }
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
        if (IsInterrupted(result))
        {
            return (new AcceptanceCheckResult(
                BuildInterruptedFailureName(check, result),
                false,
                result.ExitCode,
                BuildInterruptedOutput(result),
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

    private async Task<(AcceptanceCheckResult Result, bool Retried)> RunManagedMtpExecutableCheckCoreAsync(
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

        var telemetry = GoalAcceptanceVerifierTestTelemetry.ResolveTestTelemetry(
            check,
            environment,
            AcceptanceAttemptResultsPrefix,
            _invocationContext,
            testResultsDirectoryOverride);
        GoalAcceptanceVerifierTestTelemetry.PrepareTestTelemetryForRun(telemetry);
        var arguments = BuildMtpTestArguments(check, EngineSettings, executableEnvironment ?? environment, telemetry, NeedsUnattendedHostIntegrationExclusion(check), TranslateCheckMtpFilter);
        ReapRecordedGateChildBeforeManagedDotnetCommand(check, environment, goalId, stableSlotIndex);
        var result = await RunLaneTestHostWithShardPermitAsync(
            arguments,
            worktreePath,
            EngineSettings.ResolveCheckTimeout(check.TimeoutMinutes),
            CreateGateHeartbeatContext(check, arguments, worktreePath, goalId, stableSlotIndex, environment),
            cancellationToken, isMtpLane: true).ConfigureAwait(false);

        elapsed.Stop();
        var processPassed = !IsInterrupted(result) && result.ExitCode == 0;
        GoalAcceptanceVerifierTestTelemetry.EmitMissingTrxReceiptIfNeeded(processPassed, telemetry);
        var trxEvidence = GoalAcceptanceVerifierTestTelemetry.InspectTrxCompletionEvidence(telemetry.Paths);
        var executedTestCount = trxEvidence.ExecutedTestCount;
        var selectionCoverage = check.IsFocusedEvidenceSelection && executedTestCount != 0
            ? GoalAcceptanceVerifierTestTelemetry.InspectFocusedEvidenceSelectionCoverage(check.FocusedEvidenceSelections, telemetry.Paths)
            : FocusedEvidenceSelectionCoverage.Empty;
        var uncoveredSelections = selectionCoverage.UncoveredSelections;
        var unreadableReceipts = selectionCoverage.UnreadableReceiptPaths;
        var zeroTestApparatusFailure = check.IsFocusedEvidenceSelection &&
            (executedTestCount == 0 || uncoveredSelections.Count > 0 || unreadableReceipts.Count > 0);
        var policyFailure = zeroTestApparatusFailure
            ? unreadableReceipts.Count > 0
                ? AcceptanceFailureClassifications.FocusedSelectionReceiptUnreadable
                : AcceptanceFailureClassifications.FocusedSelectionApparatusFailure
            : null;
        var completionDecision = GoalAcceptanceVerifierTestTelemetry.DecideTestShardCompletion(
            result,
            trxEvidence,
            policyFailure,
            requireTrxEvidence: _requiresTestTelemetryReceipt);
        var passed = completionDecision.Passed;
        IReadOnlyList<string> failingTestIdentities = passed || zeroTestApparatusFailure
            ? []
            : GoalAcceptanceVerifierTestTelemetry.ExtractTrxFailureIdentities(telemetry.Paths);
        var durableTestResultPaths = GoalAcceptanceVerifierTestTelemetry.CopyCompletedTestReceiptsToAttemptFolder(
            telemetry.Paths,
            _executionContext?.ResultsPrefix);
        PreserveLaunchLockSummary(result, telemetry.Paths[0], AcceptanceAttemptResultsPrefix);
        var apparatusDetail = unreadableReceipts.Count > 0
            ? $"'{check.Name}' could not read test receipt(s): {string.Join(", ", unreadableReceipts)}."
            : executedTestCount == 0
            ? $"'{check.Name}' executed 0 tests."
            : $"'{check.Name}' had selection(s) matching 0 tests: {string.Join(", ", uncoveredSelections)}.";
        var outputTail = zeroTestApparatusFailure
            ? $"Focused evidence selection apparatus failure: {apparatusDetail}"
            : passed
                ? null
                : BuildMtpFailureOutput(check.Name, result, telemetry, trxEvidence, completionDecision);
        var resultSummary = zeroTestApparatusFailure
            ? PrefixResultSummary(
                $"{(unreadableReceipts.Count > 0 ? "focused-selection-receipt-unreadable" : "focused-selection-apparatus-failure")} executed={executedTestCount?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown"}",
                BuildGenericCommandResultSummary(result))
            : BuildGenericCommandResultSummary(result);
        return (new AcceptanceCheckResult(
            IsInterrupted(result) ? BuildInterruptedFailureName(check, result) : check.Name,
            passed,
            result.ExitCode,
            outputTail,
            environment.ArtifactsPath,
            "goal-acceptance-verifier",
            environment.LeaseId,
            (long)elapsed.Elapsed.TotalMilliseconds,
            ResultSummary: resultSummary,
            TestResultPaths: durableTestResultPaths,
            FailureClassification: policyFailure,
            TestResultRunOrdinal: _invocationContext?.Ordinal ?? 0,
            FailingTestIdentities: failingTestIdentities,
            ExecutedTestCount: executedTestCount,
            DiscoveredTestCount: trxEvidence.DiscoveredTestCount,
            CompletionDecision: completionDecision,
            ProcessStderrPath: result.StderrPath,
            ProcessStderr: result.Stderr,
            GateHeartbeatPath: ResolveGateHeartbeatPath(
                check,
                environment,
                stableSlotIndex,
                worktreePath,
                _invocationContext?.Ordinal ?? 0),
            ChildProcessId: result.ChildProcessId,
            ChildProcessStartedAt: result.ChildProcessStartedAt), false);
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
        var cache = _testOverrides.BaseBuildCacheForTests ?? DotnetBaseBuildCache.Default(_storageRoot);
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
            builtProjects = plan.CacheableProjects;
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
        var projectReceipts = plan.CacheableProjects
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

    private DotnetBuildEnvironmentLease AcquireCheckPermit(DotnetBuildEnvironment environment, GoalId? goalId, bool wait, CancellationToken ct) =>
        wait ? StructuralCoveragePermitWait.Acquire(environment, goalId, _storageRoot, _testOverrides, EmitGateProgress,
            _timeProvider, _leaseSleep, _executionContext?.ArtifactCustody, ct, GateHeartbeatRunClass.ResolveRunId(_executionContext)) :
        DotnetBuildEnvironmentManager.AcquireLeaseExecutionPermit(environment, ct, _timeProvider, _leaseSleep,
            _executionContext?.ArtifactCustody);

    private async Task<(AcceptanceCheckResult Result, bool Retried)> RunManagedDotnetCheckCoreAsync(
        AcceptanceManifestCheck check,
        string[] arguments,
        string worktreePath,
        GoalId? goalId,
        int? stableSlotIndex,
        DotnetBuildEnvironmentLease? stableSlotLease,
        string attemptName,
        CancellationToken cancellationToken,
        Action<DotnetBuildEnvironment>? afterLeasePrepared = null,
        DotnetBuildEnvironment? executionEnvironment = null,
        bool waitForPermit = false)
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
                : AcquireCheckPermit(environment, goalId, waitForPermit, cancellationToken);
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
                    stableSlotLease ?? leaseLock,
                    IsTransientCompilerLockFailure(result.Output),
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
                        leaseLock = AcquireCheckPermit(environment, goalId, waitForPermit, cancellationToken);
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
            var telemetry = GoalAcceptanceVerifierTestTelemetry.ResolveDotnetTestTelemetry(arguments, check, environment, AcceptanceAttemptResultsPrefix, _invocationContext);
            var reportedAllPassed = telemetry is not null &&
                !IsInterrupted(result) && result.ExitCode != 0 && TestRunReportsAllPassed(result.Output);
            GoalAcceptanceVerifierTestTelemetry.EmitMissingTrxReceiptIfNeeded(!IsInterrupted(result) && (result.ExitCode == 0 || reportedAllPassed), telemetry);
            var trxEvidence = GoalAcceptanceVerifierTestTelemetry.InspectTrxCompletionEvidence(telemetry?.Paths);
            var completionDecision = telemetry is null
                ? GoalAcceptanceVerifierTestTelemetry.DecideNonTestCommandCompletion(result)
                : GoalAcceptanceVerifierTestTelemetry.DecideTestShardCompletion(
                    result,
                    trxEvidence,
                    policySignal: reportedAllPassed ? "testhost-shutdown-all-passed" : null,
                    allowNonzeroExit: reportedAllPassed,
                    requireTrxEvidence: _requiresTestTelemetryReceipt);
            var passed = completionDecision.Passed;
            IReadOnlyList<string> failingTestIdentities = passed
                ? []
                : GoalAcceptanceVerifierTestTelemetry.ExtractTrxFailureIdentities(telemetry?.Paths);
            var durableTestResultPaths = GoalAcceptanceVerifierTestTelemetry.CopyCompletedTestReceiptsToAttemptFolder(
                telemetry?.Paths,
                _executionContext?.ResultsPrefix);
            return (new AcceptanceCheckResult(
                IsInterrupted(result) ? BuildInterruptedFailureName(check, result) : check.Name,
                passed,
                result.ExitCode,
                IsInterrupted(result)
                    ? BuildInterruptedOutput(result)
                    : passed && result.ExitCode == 0 ? null : TailOutput(result.Output),
                environment.ArtifactsPath,
                "goal-acceptance-verifier",
                environment.LeaseId,
                (long)elapsed.Elapsed.TotalMilliseconds,
                lockRemediationApplied,
                BuildManagedDotnetResultSummary(result, lockRemediationApplied),
                TestResultPaths: durableTestResultPaths,
                TestResultRunOrdinal: _invocationContext?.Ordinal ?? 0,
                FailingTestIdentities: failingTestIdentities,
                ExecutedTestCount: trxEvidence.ExecutedTestCount,
                DiscoveredTestCount: trxEvidence.DiscoveredTestCount,
                CompletionDecision: completionDecision,
                ProcessStderrPath: result.StderrPath,
                ProcessStderr: result.Stderr,
                GateHeartbeatPath: ResolveGateHeartbeatPath(
                    check,
                    environment,
                    stableSlotIndex,
                    worktreePath,
                    _invocationContext?.Ordinal ?? 0),
                ChildProcessId: result.ChildProcessId,
                ChildProcessStartedAt: result.ChildProcessStartedAt), lockRemediationApplied);
        }
        catch (Exception ex) when (IsBuildArtifactIoException(ex) &&
            ex is not DotnetBuildSlotsBusyException and not BuildLockBlockedException)
        {
            var lockedPath = TryExtractPathFromException(ex) ?? environment.ArtifactsPath;
            var attribution = AttributeBuildLock(lockedPath, environment.ArtifactsPath, "acceptance-check", check.Name);
            var (result, _) = await RemediateBuildLockAndRetryAsync(
                arguments,
                worktreePath,
                check,
                goalId,
                stableSlotIndex,
                stableSlotLease,
                stableSlotLease ?? leaseLock,
                compilerLockRemediationRequired: false,
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
                    _testOverrides.OnBuildArtifactIoRetryLeaseReleasedForTests?.Invoke();
                    leaseLock = AcquireCheckPermit(environment, goalId, waitForPermit, cancellationToken);
                },
                cancellationToken).ConfigureAwait(false);

            if (IsBuildLockFailure(result, environment, check, out var finalAttribution))
            {
                throw new BuildLockBlockedException(finalAttribution);
            }

            elapsed.Stop();
            var telemetry = GoalAcceptanceVerifierTestTelemetry.ResolveDotnetTestTelemetry(arguments, check, environment, AcceptanceAttemptResultsPrefix, _invocationContext);
            var reportedAllPassed = telemetry is not null &&
                !IsInterrupted(result) && result.ExitCode != 0 && TestRunReportsAllPassed(result.Output);
            GoalAcceptanceVerifierTestTelemetry.EmitMissingTrxReceiptIfNeeded(!IsInterrupted(result) && (result.ExitCode == 0 || reportedAllPassed), telemetry);
            var trxEvidence = GoalAcceptanceVerifierTestTelemetry.InspectTrxCompletionEvidence(telemetry?.Paths);
            var completionDecision = telemetry is null
                ? GoalAcceptanceVerifierTestTelemetry.DecideNonTestCommandCompletion(result)
                : GoalAcceptanceVerifierTestTelemetry.DecideTestShardCompletion(
                    result,
                    trxEvidence,
                    policySignal: reportedAllPassed ? "testhost-shutdown-all-passed" : null,
                    allowNonzeroExit: reportedAllPassed,
                    requireTrxEvidence: _requiresTestTelemetryReceipt);
            var passed = completionDecision.Passed;
            IReadOnlyList<string> failingTestIdentities = passed
                ? []
                : GoalAcceptanceVerifierTestTelemetry.ExtractTrxFailureIdentities(telemetry?.Paths);
            var durableTestResultPaths = GoalAcceptanceVerifierTestTelemetry.CopyCompletedTestReceiptsToAttemptFolder(
                telemetry?.Paths,
                _executionContext?.ResultsPrefix);
            return (new AcceptanceCheckResult(
                IsInterrupted(result) ? BuildInterruptedFailureName(check, result) : check.Name,
                passed,
                result.ExitCode,
                IsInterrupted(result)
                    ? BuildInterruptedOutput(result)
                    : passed && result.ExitCode == 0 ? null : TailOutput(result.Output),
                environment.ArtifactsPath,
                "goal-acceptance-verifier",
                environment.LeaseId,
                (long)elapsed.Elapsed.TotalMilliseconds,
                true,
                BuildManagedDotnetResultSummary(result, transientCompilerLockRetried: true),
                TestResultPaths: durableTestResultPaths,
                TestResultRunOrdinal: _invocationContext?.Ordinal ?? 0,
                FailingTestIdentities: failingTestIdentities,
                ExecutedTestCount: trxEvidence.ExecutedTestCount,
                DiscoveredTestCount: trxEvidence.DiscoveredTestCount,
                CompletionDecision: completionDecision,
                ProcessStderrPath: result.StderrPath,
                ProcessStderr: result.Stderr,
                GateHeartbeatPath: ResolveGateHeartbeatPath(
                    check,
                    environment,
                    stableSlotIndex,
                    worktreePath,
                    _invocationContext?.Ordinal ?? 0),
                ChildProcessId: result.ChildProcessId,
                ChildProcessStartedAt: result.ChildProcessStartedAt), true);
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

    internal static bool IsTransientCompilerLockFailure(string output) =>
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
        DotnetBuildEnvironmentLease? recoveryLease,
        bool compilerLockRemediationRequired,
        DotnetBuildEnvironment currentEnvironment,
        string attemptName,
        BuildLockAttribution attribution,
        Action<DotnetBuildEnvironment> reacquireLease,
        CancellationToken cancellationToken)
    {
        if (compilerLockRemediationRequired)
        {
            recoveryLease?.MarkCompilerLockRemediationRequired();
            stableSlotLease?.ReleaseExecutionLock();
        }
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
            retryAttribution = AttributeBuildLock(lockedPath, retryEnvironment.ArtifactsPath, "acceptance-retry", check.Name);
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
            .Where(holder =>
                holder.IsOrchestratorOwned &&
                holder.ProcessId.HasValue &&
                holder.ProcessId.Value != Environment.ProcessId)
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
            throw new BuildLockBlockedException(AttributeBuildLock(lockedPath, killRetryEnvironment.ArtifactsPath, "acceptance-kill-retry", check.Name));
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
        var maxRetryCycles = Math.Max(
            1,
            _testOverrides.TransientNoHolderBuildLockMaxRetryCycles ?? DefaultTransientNoHolderBuildLockMaxRetryCycles);
        for (var cycle = 1; cycle <= maxRetryCycles; cycle++)
        {
            var wait = await WaitForBuildArtifactWriteAccessAsync(
                cycleAttribution.Path,
                _testOverrides.TransientNoHolderBuildLockWaitWindow ?? DefaultTransientNoHolderBuildLockWaitWindow,
                _testOverrides.TransientNoHolderBuildLockPollInterval ?? DefaultTransientNoHolderBuildLockPollInterval,
                _timeProvider,
                cancellationToken).ConfigureAwait(false);
            EmitTransientNoHolderBuildLockWaitReceipt(cycleAttribution, wait, cycle, maxRetryCycles);
            if (!wait.Released)
            {
                var exhaustedAttribution = AttributeBuildLock(
                    cycleAttribution.Path,
                    retryEnvironment.ArtifactsPath,
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
                var exceptionAttribution = AttributeBuildLock(lockedPath, retryEnvironment.ArtifactsPath, "acceptance-transient-retry", check.Name);
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
                    retryEnvironment.ArtifactsPath,
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
                    retryEnvironment.ArtifactsPath,
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

    private static Task<TransientBuildLockWaitResult> WaitForBuildArtifactWriteAccessAsync(
        string path,
        TimeSpan waitWindow,
        TimeSpan pollInterval,
        TimeProvider timeProvider,
        CancellationToken cancellationToken) =>
        GoalAcceptanceVerifierBuildArtifactLock.WaitForBuildArtifactWriteAccessAsync(path, waitWindow, pollInterval, timeProvider, cancellationToken);

    private static void EmitTransientNoHolderBuildLockWaitReceipt(
        BuildLockAttribution attribution,
        TransientBuildLockWaitResult wait,
        int cycle,
        int maxCycles) =>
        GoalAcceptanceVerifierBuildArtifactLock.EmitTransientNoHolderBuildLockWaitReceipt(attribution, wait, cycle, maxCycles);

    private static void EmitTransientNoHolderBuildLockRetryReceipt(
        AcceptanceManifestCheck check,
        BuildLockAttribution attribution,
        int cycle,
        int maxCycles,
        string verdict,
        int? exitCode,
        bool timedOut,
        bool buildLock) =>
        GoalAcceptanceVerifierBuildArtifactLock.EmitTransientNoHolderBuildLockRetryReceipt(check, attribution, cycle, maxCycles, verdict, exitCode, timedOut, buildLock);

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
        var telemetry = GoalAcceptanceVerifierTestTelemetry.ResolveDotnetTestTelemetry(arguments, check, environment, AcceptanceAttemptResultsPrefix, _invocationContext);
        var effectiveArguments = WithBuildEnvironmentArguments(telemetry?.Arguments ?? arguments, environment);
        ReapRecordedGateChildBeforeManagedDotnetCommand(check, environment, goalId, stableSlotIndex);
        return await RunLaneTestHostWithShardPermitAsync(
            effectiveArguments,
            worktreePath,
            timeout,
            CreateGateHeartbeatContext(check, effectiveArguments, worktreePath, goalId, stableSlotIndex, environment),
            cancellationToken).ConfigureAwait(false);
    }

    private static bool IsBuildArtifactIoException(Exception ex) =>
        GoalAcceptanceVerifierBuildArtifactLock.IsBuildArtifactIoException(ex);

    private BuildLockAttribution AttributeBuildLock(
        string path,
        string? ownershipHint,
        string? phase,
        string? operation) =>
        GoalAcceptanceVerifierBuildArtifactLock.AttributeBuildLock(path, ownershipHint, phase, operation, _timeProvider);

    private bool IsBuildLockFailure(
        CommandResult result,
        DotnetBuildEnvironment environment,
        AcceptanceManifestCheck check,
        out BuildLockAttribution attribution) =>
        GoalAcceptanceVerifierBuildArtifactLock.IsBuildLockFailure(result, environment, check, out attribution, _timeProvider, _storageRoot, this);

    private static bool IsTransientNoHolderBuildArtifactLock(BuildLockAttribution attribution, DotnetBuildEnvironment environment) =>
        GoalAcceptanceVerifierBuildArtifactLock.IsTransientNoHolderBuildArtifactLock(attribution, environment);

    internal static bool DotnetTestRunReportsCompleted(string output) =>
        Regex.IsMatch(
            output,
            @"(?:Passed|Failed)!\s*-\s*Failed:\s*\d+,\s*Passed:\s*\d+",
            RegexOptions.IgnoreCase);

    internal static bool IsProcessRunning(int processId)
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

    internal static int? TryGetStableSlotIndex(DotnetBuildEnvironment environment)
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

    internal static TimeSpan Positive(TimeSpan value) =>
        value < TimeSpan.Zero ? TimeSpan.Zero : value;

    internal static bool PathIsUnderDirectory(string path, string directory)
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

    private static IReadOnlyList<string> DiscoverTrustedStructuralCoverageProjects(
        string candidateWorktreePath,
        string mainWorktreePath)
    {
        var candidateProjects = DiscoverTrustedTestProjects(candidateWorktreePath);
        IReadOnlyList<string> mainProjects;
        try
        {
            mainProjects = DiscoverTrustedTestProjects(mainWorktreePath);
        }
        catch (Exception ex) when (IsBuildArtifactIoException(ex))
        {
            throw new AcceptanceInfrastructureDeferredException(
                "trusted-main-project-discovery-io",
                exitCode: null,
                outputTail: ex.Message);
        }

        return candidateProjects
            .Concat(mainProjects)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    internal static StructuralCoverageDeclarationPlan EnsureTrustedStructuralCoverageDeclarations(
        IReadOnlyList<AcceptanceManifestCheck> effectiveChecks,
        IReadOnlyList<AcceptanceManifestCheck> manifestChecks,
        string worktreePath)
    {
        var completed = effectiveChecks.ToList();
        var undeclared = new List<string>();
        foreach (var project in DiscoverTrustedTestProjects(worktreePath))
        {
            var declarations = manifestChecks.Where(check =>
                check.Type.Equals("dotnet-test", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(NormalizePath(check.Project), project, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (declarations.Length == 0)
            {
                undeclared.Add(project);
                continue;
            }

            if (completed.Any(check =>
                    IsBroadTestProjectCheck(check) &&
                    string.Equals(NormalizePath(check.Project), project, StringComparison.OrdinalIgnoreCase)))
                continue;

            if (declarations.FirstOrDefault(IsBroadTestProjectCheck) is { } umbrella)
                completed.Add(umbrella);
        }

        return new StructuralCoverageDeclarationPlan(completed, undeclared);
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
        string worktreePath,
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
            Runner = ResolveDotnetTestRunner(worktreePath, project)
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
                invocation.ResolveManagedAssemblyPath(environment),
                "--no-ansi",
                "--progress",
                "off",
                "--list-tests",
                "json",
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

    private string? ResolveMainWorktreePath(string worktreePath)
    {
        if (_testOverrides.ResolveMainWorktreePathForTests is not null)
        {
            return _testOverrides.ResolveMainWorktreePathForTests(worktreePath);
        }

        return AcceptanceContainedGenerationBaseline.ResolveMainWorktreePath(
            worktreePath,
            AcceptanceGitTextResolver.Resolve);
    }

    internal static string? ResolveMainWorktreePathWithGitForTests(
        string worktreePath,
        Func<string, string[], string?> resolveGitText) =>
        AcceptanceContainedGenerationBaseline.ResolveMainWorktreePath(worktreePath, resolveGitText);

    private string[] ResolveDeletedTestFiles(string worktreePath, string project)
    {
        if (_testOverrides.ResolveDeletedTestFilesForTests is not null)
        {
            return _testOverrides.ResolveDeletedTestFilesForTests(worktreePath);
        }

        return ResolveDeletedTestFilesCore(worktreePath, project, AcceptanceGitTextResolver.Resolve);
    }

    internal static string[] ResolveDeletedTestFilesWithGitForTests(
        string worktreePath,
        string project,
        Func<string, string[], string?> resolveGitText) =>
        ResolveDeletedTestFilesCore(worktreePath, project, resolveGitText);

    private static string[] ResolveDeletedTestFilesCore(
        string worktreePath,
        string project,
        Func<string, string[], string?> resolveGitText)
    {
        var output = resolveGitText(worktreePath, ["diff", "--name-status", "main...HEAD", "--"]);
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

    internal static string? ResolveOwningProjectForTests(string worktreePath, string path) =>
        ResolveOwningProject(worktreePath, path);

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
                if (parts.Length < 2 || !TestTamperAnalysis.IsTestFile(parts[1]))
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
        var normalizedPath = NormalizePath(path);
        if (string.IsNullOrWhiteSpace(normalizedPath))
        {
            return null;
        }

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(worktreePath));
        var fullPath = Path.GetFullPath(Path.Combine(
            root,
            normalizedPath.Replace('/', Path.DirectorySeparatorChar)));
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

    internal static string FilterTestFileDiffSections(string diff) => TestTamperAnalysis.FilterTestFileDiffSections(diff);

    private DotnetTestBuildPhase CreateDotnetTestBuildPhase(
        string worktreePath,
        IReadOnlyList<AcceptanceManifestCheck> checks,
        IReadOnlyList<string>? changedFiles,
        PolicyShardPlan policyShardPlan)
    {
        var dotnetTestChecks = checks
            .Where(check => check.Type.Equals("dotnet-test", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var cacheableProjects = CacheableProjects
            .Where(project =>
                !ProjectMatches(project, AcceptanceTestsProject) ||
                dotnetTestChecks.Any(check => ProjectMatches(check.Project, AcceptanceTestsProject)))
            .ToArray();
        DotnetBaseBuildCachePlan? cachePlan = TryCreateBaseBuildCachePlan(
            worktreePath,
            changedFiles,
            policyShardPlan,
            cacheableProjects);
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

    private DotnetBaseBuildCachePlan? TryCreateBaseBuildCachePlan(
        string worktreePath,
        IReadOnlyList<string>? changedFiles,
        PolicyShardPlan policyShardPlan,
        string[] cacheableProjects)
    {
        if (changedFiles is null ||
            changedFiles.Count == 0 ||
            !policyShardPlan.Applies ||
            policyShardPlan.ForceFull)
        {
            return null;
        }

        var buildProjects = policyShardPlan.DependencyClosure
            .Where(project => cacheableProjects.Contains(project, StringComparer.OrdinalIgnoreCase))
            .OrderBy(project => Array.IndexOf(cacheableProjects, project))
            .ToArray();
        if (buildProjects.Length == 0 || buildProjects.Length == cacheableProjects.Length)
        {
            return null;
        }

        var mainSha = _testOverrides.ResolveBaseBuildMainShaForTests?.Invoke(worktreePath) ?? ResolveBaseBuildMainSha(worktreePath);
        if (string.IsNullOrWhiteSpace(mainSha))
        {
            return null;
        }

        var restoreProjects = cacheableProjects
            .Where(project => !buildProjects.Contains(project, StringComparer.OrdinalIgnoreCase))
            .ToArray();
        return restoreProjects.Length == 0
            ? null
            : new DotnetBaseBuildCachePlan(mainSha, restoreProjects, buildProjects, cacheableProjects);
    }

    private static string? ResolveBaseBuildMainSha(string worktreePath)
    {
        return ResolveGitScalar(worktreePath, "merge-base", "HEAD", "main");
    }

    internal static string? ResolveGitScalar(string worktreePath, params string[] arguments)
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

            if (!process.WaitForExit(GitCli.DefaultTimeoutMilliseconds))
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

    private static bool GateUsesStableSlot(int? stableSlotIndex, DotnetBuildEnvironmentLease? stableSlotLease) =>
        stableSlotIndex.HasValue || stableSlotLease is not null;

    internal DotnetBuildEnvironment ResolveExecutionEnvironment(
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
            environment = DotnetBuildEnvironmentManager.ResolveGoalEnvironment(goalId, _storageRoot);
        }
        else
        {
            environment = stableSlotIndex.HasValue
                ? DotnetBuildEnvironmentManager.CreateStableSlotAttempt(
                    stableSlotIndex.Value,
                    storageRoot: _storageRoot)
                : DotnetBuildEnvironmentManager.CreateAttempt(
                    null,
                    attemptName,
                    storageRoot: _storageRoot);
        }

        if (buildLease is null)
        {
            _executionContext?.TrackEnvironment(environment);
        }

        return environment;
    }

    internal static string? ResolveGitScalarForExecutionOwner(string worktreePath, params string[] arguments) =>
        ResolveGitScalar(worktreePath, arguments);

    internal static string ResolveOwnerResultsPrefix(
        string worktreePath,
        GoalId? goalId,
        string ownerKind)
    {
        var repositoryRoot = ResolveOwnerResultsRootForCandidate(worktreePath);
        var owner = goalId?.Value ?? "operator";
        var directory = Path.Combine(
            repositoryRoot,
            ".orchestrator",
            OwnerResultsAttemptRoot(ownerKind),
            owner);
        CreateOwnerResultsDirectory(directory);
        return Path.Combine(
            directory,
            $"{owner[..Math.Min(8, owner.Length)]}-{ownerKind}-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}");
    }

    internal static string OwnerResultsAttemptRoot(string ownerKind) =>
        ownerKind.Equals("pre-review", StringComparison.Ordinal)
            ? "pre-review-evidence-attempts"
            : "acceptance-gate-attempts";

    internal static string ResolveOwnerResultsRepositoryRoot(string worktreePath)
    {
        var fullPath = Path.GetFullPath(worktreePath);
        var candidate = new DirectoryInfo(fullPath);
        while (candidate is not null)
        {
            if (Directory.Exists(Path.Combine(candidate.FullName, ".git")))
            {
                return candidate.FullName;
            }

            if (candidate.Name.Equals(".orchestrator-worktrees", StringComparison.OrdinalIgnoreCase) &&
                candidate.Parent is not null)
            {
                return candidate.Parent.FullName;
            }

            candidate = candidate.Parent;
        }

        return Path.Combine(Path.GetTempPath(), OwnerResultsRootDirectoryName);
    }

    private static bool UsesMicrosoftTestingPlatform(AcceptanceManifestCheck check) =>
        AcceptanceCheckCommandBuilder.UsesMicrosoftTestingPlatform(check);

    internal static bool IsDotnetTestCommand(string[] arguments) =>
        AcceptanceCheckCommandBuilder.IsDotnetTestCommand(arguments);

    private static string[] UseDotnetHostForManagedExecutable(IReadOnlyList<string> arguments) =>
        AcceptanceCheckCommandBuilder.UseDotnetHostForManagedExecutable(arguments);

    internal static string? ResolveGitText(string worktreePath, params string[] arguments) =>
        AcceptanceGitTextResolver.Resolve(worktreePath, arguments);

    internal static IEnumerable<string> TranslateMtpFilter(string filter) =>
        AcceptanceCheckCommandBuilder.TranslateMtpFilter(filter);

    private static string[] WithBuildEnvironmentArguments(string[] arguments, DotnetBuildEnvironment environment) =>
        AcceptanceCheckCommandBuilder.WithBuildEnvironmentArguments(arguments, environment);

    internal static DotnetTestTelemetry ResolveTestTelemetryCore(
        AcceptanceManifestCheck check,
        DotnetBuildEnvironment environment,
        string? attemptPrefix,
        int invocationOrdinal,
        string? resultsDirectoryOverride = null)
    {
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
        filePrefix = AppendTestTelemetryInvocationSuffix(filePrefix, invocationOrdinal);
        var fileName = BoundFileName(SanitizeFileName(filePrefix), ".trx");
        var path = Path.Combine(directory, fileName);
        return new DotnetTestTelemetry([path], []);
    }

    internal static IReadOnlyList<string>? CopyCompletedTestReceiptsToAttemptFolder(
        IReadOnlyList<string>? sourcePaths,
        string? attemptPrefix) =>
        GoalAcceptanceVerifierTestTelemetry.CopyCompletedTestReceiptsToAttemptFolder(sourcePaths, attemptPrefix);

    internal static IReadOnlyList<string> ExtractTrxFailureEvidence(string trxPath) =>
        AcceptanceTrxOutcomeTaxonomy.ExtractTrxFailureEvidence(trxPath);

    internal static IReadOnlyList<string> ExtractTrxFailureIdentities(string trxPath) =>
        AcceptanceTrxTestIdentityResolver.ExtractTrxFailureIdentities(trxPath);

    private static TrxCompletionEvidence InspectTrxCompletionEvidence(IEnumerable<string>? trxPaths) =>
        GoalAcceptanceVerifierTestTelemetry.InspectTrxCompletionEvidence(trxPaths);

    private static AcceptanceShardCompletionDecision DecideTestShardCompletion(
        CommandResult result,
        TrxCompletionEvidence trx,
        string? policyFailure = null,
        string? policySignal = null,
        bool allowNonzeroExit = false,
        bool requireTrxEvidence = true) =>
        GoalAcceptanceVerifierTestTelemetry.DecideTestShardCompletion(result, trx, policyFailure, policySignal, allowNonzeroExit, requireTrxEvidence);

    internal static AcceptanceShardCompletionDecision DecideTestShardCompletionForTests(
        CommandResult result,
        int? discoveredTestCount,
        int? executedTestCount,
        string trxOutcome,
        string? trxFailedPredicate = null,
        string? policyFailure = null,
        string? policySignal = null,
        bool allowNonzeroExit = false,
        int? notExecutedTestCount = 0) =>
        DecideTestShardCompletion(
            result,
            new TrxCompletionEvidence(
                discoveredTestCount,
                executedTestCount,
                notExecutedTestCount,
                trxOutcome,
                trxOutcome.Equals("passed", StringComparison.OrdinalIgnoreCase) ||
                    trxOutcome.Equals("completed", StringComparison.OrdinalIgnoreCase),
                trxFailedPredicate),
            policyFailure,
            policySignal,
            allowNonzeroExit);

    internal static AcceptanceShardCompletionDecision DecideTestShardCompletionFromTrxForTests(
        CommandResult result,
        IEnumerable<string> trxPaths) =>
        DecideTestShardCompletion(result, InspectTrxCompletionEvidence(trxPaths));

    internal static string? FirstNonEmptyLine(string? value) =>
        value?
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(line => !string.IsNullOrWhiteSpace(line));

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new string(value.Select(ch => invalid.Contains(ch) ? '-' : ch).ToArray()).Trim('-', '.');
        return string.IsNullOrWhiteSpace(sanitized) ? "acceptance-test-results" : sanitized;
    }

    private static bool IsDotnetCommand(string[] arguments) =>
        AcceptanceCheckCommandBuilder.IsDotnetCommand(arguments);

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

    private static string BuildTimeoutSummary(CommandResult result)
    {
        var fallback = new AcceptanceGateEngineSettings().ResolveCheckTimeout(null);
        return $"elapsed={FormatTimeout(result.Elapsed ?? result.Timeout ?? fallback)} budget={FormatTimeout(result.Timeout ?? fallback)}";
    }

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
        return _requiresTestTelemetryReceipt
            ? await RunProcessAsync(
                arguments,
                workingDirectory,
                timeout,
                forceUtf8ConsoleOutput: false,
                cancellationToken,
                heartbeatContext: heartbeatContext,
                progressSink: EmitGateProgress,
                engineSettings: EngineSettings,
                attemptResultsPrefix: AcceptanceAttemptResultsPrefix,
                gateInvocationId: _executionContext?.RunId,
                apparatusReceiptPath: _executionContext?.ApparatusReceiptPath,
                heartbeatInterval: _testOverrides.HeartbeatInterval, progressInterval: _testOverrides.ProgressInterval,
                capturePublicationInterval: _testOverrides.CapturePublicationInterval,
                stopOnCaptureLimit: StopsOnCaptureLimit(heartbeatContext)).ConfigureAwait(false)
            : await _runner(arguments, workingDirectory, timeout, cancellationToken).ConfigureAwait(false);
    }
    private GateHeartbeatContext CreateGateHeartbeatContext(
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
            ResolveStableSlotHeartbeatMirrorPath(environment, heartbeatPath, _storageRoot),
            string.Join(' ', arguments.Select(QuoteForDisplay)),
            environment?.RootPath) { RunClass = GateHeartbeatRunClass.Classify(_executionContext), RunId = GateHeartbeatRunClass.ResolveRunId(_executionContext) };
    }

    // An attempt can run several checks after releasing its build permit, and unrelated goals can hash to
    // the same permit index. Mirror each live run to a path keyed by its attempt-scoped primary heartbeat
    // so terminal updates can never overwrite a sibling. Gate-status enumerates these run-scoped mirrors.
    private static string? ResolveStableSlotHeartbeatMirrorPath(
        DotnetBuildEnvironment? environment,
        string primaryHeartbeatPath,
        DotnetBuildStorageRoot storageRoot)
    {
        if (environment?.BuildPermitIndex is not { } slotIndex ||
            slotIndex < 0 ||
            slotIndex >= DotnetBuildEnvironmentManager.StableSlotCount)
        {
            return null;
        }

        var stableSlotPath = GateHeartbeatArtifacts.GetStableSlotPath(slotIndex, storageRoot);
        return primaryHeartbeatPath.Equals(stableSlotPath, StringComparison.OrdinalIgnoreCase)
            ? null
            : GateHeartbeatArtifacts.GetRunScopedStableSlotPath(slotIndex, primaryHeartbeatPath, storageRoot);
    }

    internal string ResolveGateHeartbeatPath(
        AcceptanceManifestCheck check,
        DotnetBuildEnvironment? environment,
        int? stableSlotIndex,
        string? worktreePath,
        int? invocationOrdinal = null) =>
        ResolveGateHeartbeatPathCore(
            check,
            environment,
            stableSlotIndex,
            worktreePath,
            AcceptanceAttemptResultsPrefix,
            invocationOrdinal ?? _invocationContext?.Ordinal ?? 0,
            _storageRoot);

    private static string ResolveGateHeartbeatPathCore(
        AcceptanceManifestCheck check,
        DotnetBuildEnvironment? environment,
        int? stableSlotIndex,
        string? worktreePath,
        string? attemptPrefix,
        int invocationOrdinal,
        DotnetBuildStorageRoot storageRoot)
    {
        if (!string.IsNullOrWhiteSpace(attemptPrefix))
        {
            var attemptDirectory = Path.GetDirectoryName(attemptPrefix);
            var stem = $"{Path.GetFileName(attemptPrefix)}.{Slug(check.Name)}-{ShortHash(check.Name)}";
            stem = AppendTestTelemetryInvocationSuffix(
                stem,
                invocationOrdinal);
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
            ? GateHeartbeatArtifacts.GetStableSlotPath(stableSlotIndex.Value, storageRoot)
            : GateHeartbeatArtifacts.GetManualPath(
                worktreePath ?? Path.Combine(Path.GetTempPath(), OwnerResultsRootDirectoryName));
    }

    internal static string ResolveGateHeartbeatPathForTests(
        string checkName,
        DotnetBuildEnvironment environment,
        int? stableSlotIndex = null,
        string? attemptResultsPrefix = null) =>
        ResolveGateHeartbeatPathCore(
            new AcceptanceManifestCheck { Name = checkName },
            environment,
            stableSlotIndex,
            worktreePath: null,
            attemptPrefix: attemptResultsPrefix,
            invocationOrdinal: 0,
            DotnetBuildEnvironmentManager.CaptureStorageRoot());

    internal static string ResolveGateHeartbeatPathForTests(
        string checkName,
        string worktreePath,
        int invocationOrdinal,
        string? attemptResultsPrefix = null) =>
        ResolveGateHeartbeatPathCore(
            new AcceptanceManifestCheck { Name = checkName },
            environment: null,
            stableSlotIndex: null,
            worktreePath,
            attemptPrefix: attemptResultsPrefix,
            invocationOrdinal,
            DotnetBuildEnvironmentManager.CaptureStorageRoot());

    internal static string ResolveTrxPathForTests(
        string checkName,
        DotnetBuildEnvironment environment,
        string? attemptResultsPrefix = null) =>
        ResolveTestTelemetryCore(
            new AcceptanceManifestCheck { Name = checkName },
            environment,
            attemptPrefix: attemptResultsPrefix,
            invocationOrdinal: 0).Paths[0];

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
        string? finalState = null,
        string? attemptResultsPrefix = null, string? runClass = null, string? runId = null)
    {
        var check = new AcceptanceManifestCheck { Name = checkName };
        var heartbeatPath = ResolveGateHeartbeatPathCore(
            check,
            environment,
            stableSlotIndex: null,
            Path.GetTempPath(),
            attemptPrefix: attemptResultsPrefix,
            invocationOrdinal: 0,
            DotnetBuildEnvironmentManager.CaptureStorageRoot());
        var context = new GateHeartbeatContext(
            goalId?.Value,
            "verification-check",
            check.Name,
            null,
            heartbeatPath,
            ResolveStableSlotHeartbeatMirrorPath(
                environment,
                heartbeatPath,
                DotnetBuildEnvironmentManager.CaptureStorageRoot()),
            "dotnet test",
            environment.RootPath) { RunClass = runClass, RunId = runId };
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

    public static void ConfigureHermeticVerificationEnvironment(
        IDictionary<string, string?> environment,
        string repositoryRoot,
        string? buildEnvironmentRoot = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        environment.TryGetValue(NuGetPackagesVariable, out var nugetPackages);
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

        var profileRoot = Path.Combine(Path.GetTempPath(), HermeticProfileRootDirectoryName);
        Directory.CreateDirectory(profileRoot);
        var dotnetCliHome = string.IsNullOrWhiteSpace(buildEnvironmentRoot)
            ? profileRoot : Path.Combine(buildEnvironmentRoot, "dotnet-cli-home");
        Directory.CreateDirectory(dotnetCliHome);
        nugetPackages = string.IsNullOrWhiteSpace(nugetPackages)
            ? DefaultNuGetGlobalPackagesFolder(string.IsNullOrWhiteSpace(userProfile) ? profileRoot : userProfile)
            : nugetPackages;
        environment["HOME"] = profileRoot;
        environment["USERPROFILE"] = profileRoot;
        environment["DOTNET_CLI_HOME"] = dotnetCliHome;

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
        ConfigureHermeticGitConfiguration(environment, profileRoot);
        // A relocated DOTNET_CLI_HOME reads as a first use, and the SDK responds by PERSISTING
        // "$DOTNET_CLI_HOME\.dotnet\tools" into the operator's HKCU\Environment\Path. That is ambient
        // state written by a function whose entire purpose is to write none, so it must be suppressed
        // here rather than cleaned up afterwards. DOTNET_SKIP_FIRST_TIME_EXPERIENCE does NOT suppress
        // it - measured on .NET 10, the entry is still written; the SDK dropped that variable years ago.
        environment["DOTNET_ADD_GLOBAL_TOOLS_TO_PATH"] = "0";
        environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        environment["DOTNET_GENERATE_ASPNET_CERTIFICATE"] = "false";
        environment["DOTNET_NOLOGO"] = "1";
        // Acceptance tests recursively invoke dotnet/MSBuild. Reusing a daemon that was born outside this
        // hermetic process tree loses both the gate's environment and its Windows error-mode suppression,
        // allowing a nested test apphost startup failure to block the desktop with a modal error dialog.
        environment["MSBUILDDISABLENODEREUSE"] = "1";
        environment["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0";
        environment[NuGetPackagesVariable] = nugetPackages;
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
        name.Equals("MSBUILDDISABLENODEREUSE", StringComparison.OrdinalIgnoreCase) ||
        // The four deterministic git identity variables this function sets. They are DECLARED here rather
        // than inherited: the allow-list is what the hermetic environment is permitted to contain, so every
        // variable ConfigureHermeticVerificationEnvironment writes must be nameable here or the gate's own
        // hermeticity assertion fails on it.
        name.Equals("GIT_AUTHOR_NAME", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("GIT_AUTHOR_EMAIL", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("GIT_COMMITTER_NAME", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("GIT_COMMITTER_EMAIL", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("GIT_CONFIG_NOSYSTEM", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("GIT_CONFIG_GLOBAL", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("GIT_TERMINAL_PROMPT", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("GCM_INTERACTIVE", StringComparison.OrdinalIgnoreCase) ||
        name.Equals(AcceptanceAttemptTrxPrefixVariable, StringComparison.OrdinalIgnoreCase) || name.Equals(DotnetBuildEnvironmentManager.IsolatedRootOverrideVariable, StringComparison.OrdinalIgnoreCase) ||
        name.Equals(TempRootApparatusLossReceiptStore.GateInvocationIdVariable, StringComparison.OrdinalIgnoreCase) ||
        name.Equals(TempRootApparatusLossReceiptStore.ReceiptPathVariable, StringComparison.OrdinalIgnoreCase) ||
        name.Equals("MCG_ORCHESTRATOR_REPOSITORY_ROOT", StringComparison.OrdinalIgnoreCase);

    private static bool IsInheritedHermeticVerificationEnvironmentVariable(string name) =>
        IsHermeticSystemLocationVariable(name) ||
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

    private void EmitGateProgress(AcceptanceGateProgress progress) => EmitGateProgress(progress, null);

    private void EmitGateProgress(AcceptanceGateProgress progress, string? trailingTokens)
    {
        var line =
            $"PHASE_PROGRESS goal={FormatNullableToken(progress.GoalId, 8)} phase={progress.Phase} elapsed_ms={(long)progress.Elapsed.TotalMilliseconds} " +
            $"target={QuoteProgressToken(progress.CurrentTarget)} child_pid={progress.ChildProcessId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown"} " +
            $"output_bytes={progress.OutputBytes} heartbeat={QuoteProgressToken(progress.HeartbeatPath)} {GateLoadContextProbe.FormatProgressTokens(progress.LoadContext)}";
        Console.WriteLine($"{line}{(trailingTokens is null ? "" : " " + trailingTokens)} ts={progress.LastObservedAt:O}");
        Console.Out.Flush();
        _executionContext?.ReportProgress(progress);
    }

    internal static string FormatNullableToken(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "unknown";
        return value.Length <= maxLength ? value : value[..maxLength];
    }

    internal static string QuoteProgressToken(string value) =>
        value.IndexOfAny([' ', '\t', '\r', '\n', '"']) < 0
            ? value
            : $"\"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\"";

    private static string QuoteForDisplay(string value) =>
        value.Contains(' ', StringComparison.Ordinal) || value.Contains('"', StringComparison.Ordinal)
            ? $"\"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\""
            : value;

    internal static Task<string> ReadFileWithRetryAsync(string path, int? maximumBytes = null) =>
        GoalAcceptanceVerifierCaptureCustody.ReadFileWithRetryAsync(path, maximumBytes);

    internal static Task<string> ReadCapturedFileWithRetryAsync(string path, bool captureLimitReached) =>
        GoalAcceptanceVerifierCaptureCustody.ReadCapturedFileWithRetryAsync(path, captureLimitReached);

    internal static ProcessStartInfo BuildAcceptanceProcessStartInfo(
        string[] arguments,
        string workingDirectory,
        string? stdoutRedirectTarget = null,
        string? stderrRedirectTarget = null,
        bool forceUtf8ConsoleOutput = false) =>
        GoalAcceptanceVerifierCaptureCustody.BuildAcceptanceProcessStartInfo(
            arguments, workingDirectory, stdoutRedirectTarget, stderrRedirectTarget, forceUtf8ConsoleOutput);

    private static NamedPipeServerStream CreateCapturePipe(string pipeName) =>
        GoalAcceptanceVerifierCaptureCustody.CreateCapturePipe(pipeName);

    private static Task<CaptureLimitResult> ConnectAndDrainCappedCaptureAsync(
        NamedPipeServerStream source,
        Task connection,
        string path,
        long limitBytes,
        Func<DateTimeOffset> utcNow,
        Action? onLimitReached, CancellationToken cancellationToken, TimeSpan? capturePublicationInterval) =>
        GoalAcceptanceVerifierCaptureCustody.ConnectAndDrainCappedCaptureAsync(
            source, connection, path, limitBytes, utcNow, onLimitReached, cancellationToken, capturePublicationInterval);

    private static Task<IReadOnlyList<CaptureLimitResult>> CompleteCaptureDrainsAsync(
        RegisteredOwnedProcess process,
        Task<CaptureLimitResult>[] captureDrains,
        CancellationTokenSource captureDrainCts,
        IReadOnlyList<Stream> captureSources) =>
        GoalAcceptanceVerifierCaptureCustody.CompleteCaptureDrainsAsync(
            process, captureDrains, captureDrainCts, captureSources);

    private static Task CancelCaptureDrainsAsync(
        CancellationTokenSource captureDrainCts,
        Task<CaptureLimitResult>[]? captureDrains,
        IReadOnlyList<Stream>? captureSources) =>
        GoalAcceptanceVerifierCaptureCustody.CancelCaptureDrainsAsync(captureDrainCts, captureDrains, captureSources);

    private static void DisposeCaptureSources(IReadOnlyList<Stream>? captureSources) =>
        GoalAcceptanceVerifierCaptureCustody.DisposeCaptureSources(captureSources);

    private static void EmitCaptureLimitReached(
        GateHeartbeatContext? context,
        string? attemptResultsPrefix,
        string path,
        long capBytes) =>
        GoalAcceptanceVerifierCaptureCustody.EmitCaptureLimitReached(
            context?.GoalId,
            attemptResultsPrefix,
            path,
            capBytes,
            Console.Out);

    internal static void EmitCaptureLimitReached(
        string? goalId,
        string? attemptPrefix,
        string path,
        long capBytes,
        TextWriter writer) =>
        GoalAcceptanceVerifierCaptureCustody.EmitCaptureLimitReached(goalId, attemptPrefix, path, capBytes, writer);

    internal static string DecodeCapturedOutput(ReadOnlySpan<byte> bytes) =>
        GoalAcceptanceVerifierCaptureCustody.DecodeCapturedOutput(bytes);

    internal static void TryDeleteFile(string path)
    {
        try { File.Delete(path); } catch { /* best effort; lives under the temp dir */ }
    }

    internal static long TryGetFileLength(string path)
    {
        try { return File.Exists(path) ? new FileInfo(path).Length : 0L; }
        catch { return 0L; }
    }

    private sealed record EffectiveGatePlan(
        AcceptanceManifest Manifest,
        IReadOnlyList<AcceptanceManifestCheck> Checks,
        IReadOnlyList<AcceptanceTestLane> InfrastructureTestLanes,
        PolicyShardPlan PolicyShardPlan,
        IReadOnlyList<AcceptanceManifestCheck> PolicyRequiredChecks,
        IReadOnlyList<string> UndeclaredTestProjects,
        bool StructuralCoverageApplies,
        DotnetShardDisposition DotnetShardDisposition,
        IReadOnlyList<SemanticExecutionDeduplicationReceipt> SemanticDeduplications);

    internal sealed record SemanticExecutionDeduplicationReceipt(
        string PartitionId,
        string RetainedCheckName,
        string DroppedCheckName,
        int RetainedPlanIndex,
        int DroppedPlanIndex,
        string SemanticKeySha256);

    private sealed record SemanticExecutionDeduplicationPlan(
        IReadOnlyList<AcceptanceManifestCheck> Checks,
        IReadOnlyList<SemanticExecutionDeduplicationReceipt> Receipts);

    internal sealed record StructuralCoverageDeclarationPlan(
        IReadOnlyList<AcceptanceManifestCheck> Checks,
        IReadOnlyList<string> UndeclaredProjects);

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
                    : FromTestImpactPlan(
                        worktreePath,
                        RepositoryTestImpactPlanner.Plan(changedFiles, worktreePath));
            }

            return JsonSerializer.Deserialize<AcceptanceManifest>(
                File.ReadAllText(path),
                JsonOptions) ?? new AcceptanceManifest();
        }

        private static AcceptanceManifest FromTestImpactPlan(
            string worktreePath,
            RepositoryTestImpactPlan plan) =>
            new()
            {
                Checks = plan.Checks
                    .Select(check => ToAcceptanceCheck(worktreePath, check))
                    .SelectMany(check => ExpandBroadInfrastructureCheck(check))
                    .ToArray()
            };

        private static AcceptanceManifestCheck ToAcceptanceCheck(
            string worktreePath,
            RepositoryTestImpactCheck check)
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
                return DotnetCommandToManifestCheck(worktreePath, check.Name, [.. check.Command]);
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

    internal sealed partial class AcceptanceManifestCheck
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
        public bool IsFocusedEvidenceSelection { get; init; }
        public IReadOnlyList<FocusedEvidenceFilterToken> FocusedEvidenceTokens { get; init; } = [];
        public IReadOnlyList<IReadOnlyList<FocusedEvidenceFilterToken>> FocusedEvidenceSelections { get; init; } = [];
    }

    internal enum FocusedEvidenceTokenKind
    {
        Class,
        Method,
        ExcludedClass,
        ExcludedTrait
    }

    internal sealed record FocusedEvidenceFilterToken(
        string OriginalToken,
        string CanonicalToken,
        FocusedEvidenceTokenKind Kind,
        string Value,
        string ContainingClass);

    internal sealed record FocusedEvidenceFilter(
        string OriginalToken,
        string CanonicalText,
        IReadOnlyList<FocusedEvidenceFilterToken> Tokens);

    internal sealed record FocusedEvidencePlannedCheck(
        IReadOnlyList<string> Targets,
        string Project,
        FocusedEvidenceFilter? Filter,
        IReadOnlyList<FocusedEvidenceFilter> SelectionFilters);

    internal sealed record FocusedEvidenceSelectionCoverage(
        IReadOnlyList<string> UncoveredSelections,
        IReadOnlyList<string> UnreadableReceiptPaths)
    {
        public static FocusedEvidenceSelectionCoverage Empty { get; } = new([], []);
    }

    internal sealed record FocusedEvidenceIdentityExtraction(
        IReadOnlyList<string> Identities,
        IReadOnlyList<string> UnreadableReceiptPaths);

    internal sealed record DotnetTestTelemetry(IReadOnlyList<string> Paths, string[] Arguments);

    internal sealed record TrxCompletionEvidence(
        int? DiscoveredTestCount,
        int? ExecutedTestCount,
        int? NotExecutedTestCount,
        string Outcome,
        bool Passed,
        string? FailedPredicate,
        bool AssemblyCleanupOnly = false);

    private sealed record DotnetBaseBuildCachePlan(
        string MainSha,
        IReadOnlyList<string> RestoreProjects,
        IReadOnlyList<string> BuildProjects,
        IReadOnlyList<string> CacheableProjects);

    private sealed class DotnetTestBuildPhase(string[] buildArguments, DotnetBaseBuildCachePlan? cachePlan)
    {
        public string[] BuildArguments { get; } = buildArguments;
        public DotnetBaseBuildCachePlan? CachePlan { get; } = cachePlan;
        public DotnetBuildEnvironment? BuildEnvironment { get; set; }
        private (AcceptanceCheckResult Result, bool Retried)? _run;
        public TaskCompletionSource BuildCompleted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public (AcceptanceCheckResult Result, bool Retried)? Run
        {
            get => _run;
            set
            {
                _run = value;
                if (value is not null)
                    BuildCompleted.TrySetResult();
            }
        }
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

    internal readonly record struct TransientBuildLockWaitResult(long WaitedMilliseconds, bool Released);

    internal readonly record struct CaptureLimitResult(string Path, long WrittenBytes, bool LimitReached);

    private sealed partial record GateHeartbeatContext(
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
        private readonly Action<AcceptanceGateProgress>? _progressSink;
        private readonly TimeSpan _progressInterval;
        private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;
        private DateTimeOffset _lastProgressAt;
        private DateTimeOffset _lastProgressEmittedAt = DateTimeOffset.MinValue;
        private long _lastOutputBytes = -1;

        public GateHeartbeatRuntime(
            GateHeartbeatContext context,
            int processId,
            string stdoutPath,
            string stderrPath,
            TimeSpan timeout,
            Action<AcceptanceGateProgress>? progressSink = null,
            TimeSpan? progressInterval = null)
        {
            _context = context;
            _processId = processId;
            _stdoutPath = stdoutPath;
            _stderrPath = stderrPath;
            _timeout = timeout;
            _progressSink = progressSink;
            _progressInterval = progressInterval ?? TimeSpan.FromSeconds(30);
            _lastProgressAt = _startedAt;
        }

        public void WriteRunning(bool emitProgress)
        {
            var snapshot = BuildSnapshot("running", childPid: _processId);
            GateHeartbeatArtifacts.TryWrite(_context.HeartbeatPath, snapshot);
            MirrorToStableSlot(snapshot);
            if (emitProgress || snapshot.LastObservedAt - _lastProgressEmittedAt >= _progressInterval)
            {
                _lastProgressEmittedAt = snapshot.LastObservedAt;
                _progressSink?.Invoke(ToProgress(snapshot));
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
                _stderrPath, RunClass: _context.RunClass, RunId: _context.RunId);
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

}
