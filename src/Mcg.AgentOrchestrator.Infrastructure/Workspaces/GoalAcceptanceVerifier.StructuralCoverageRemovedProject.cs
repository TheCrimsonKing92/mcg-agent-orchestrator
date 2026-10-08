using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using static Mcg.AgentOrchestrator.Infrastructure.AcceptancePolicyShardPlanner;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed partial class GoalAcceptanceVerifier
{
    private sealed record RemovedTestProjectRecord(
        string Project, string? DeclaredKind, bool OwnerApproved,
        int MainCount = 0, bool LockRemediationApplied = false);

    private static RemovedTestProjectRecord? ClassifyRemovedTestProject(
        string candidateWorktreePath, string mainWorktreePath, string project,
        IReadOnlyList<AcceptanceManifestCheck> candidateManifestChecks,
        AcceptanceGateEngineSettings candidateSettings, bool ownerApprovalSatisfied)
    {
        var relativePath = project.Replace('/', Path.DirectorySeparatorChar);
        if (File.Exists(Path.Combine(candidateWorktreePath, relativePath)) ||
            !File.Exists(Path.Combine(mainWorktreePath, relativePath))) return null;

        var hasCheck = candidateManifestChecks.Any(check => string.Equals(
            NormalizePath(check.Project), NormalizePath(project), StringComparison.OrdinalIgnoreCase));
        var hasInvocation = candidateSettings.HasMtpInvocation(project);
        var declaredKind = hasCheck && hasInvocation ? "check and mtp invocation" :
            hasCheck ? "check" : hasInvocation ? "mtp invocation" : null;
        return new RemovedTestProjectRecord(project, declaredKind, ownerApprovalSatisfied);
    }

    private async Task<StructuralCoverageProject> PrepareRemovedTestProjectAsync(
        AcceptanceManifestCheck check, RemovedTestProjectRecord removal, string mainWorktreePath,
        DotnetBuildEnvironment environment, GoalId? goalId, int? stableSlotIndex,
        DotnetBuildEnvironmentLease? stableSlotLease, CancellationToken cancellationToken)
    {
        // Main-only projects must never resolve a candidate discovery invocation.
        var mainCheck = new AcceptanceManifestCheck
        {
            Name = check.Name, Type = check.Type, Project = removal.Project,
            Runner = ResolveDotnetTestRunner(mainWorktreePath, removal.Project),
            TimeoutMinutes = check.TimeoutMinutes
        };
        AcceptanceGateEngineSettings mainSettings;
        AcceptanceStructuralCoverageBaseline? baseline;
        try
        {
            mainSettings = AcceptanceGateEngineSettings.Load(mainWorktreePath);
            if (UsesMicrosoftTestingPlatform(mainCheck) && !mainSettings.HasMtpInvocation(removal.Project))
                throw new InvalidDataException(
                    $"Trusted main manifest has no MTP invocation for project '{removal.Project}'.");
            baseline = await PrepareStructuralCoverageBaselineAsync(mainCheck, mainSettings, environment,
                goalId, stableSlotIndex, stableSlotLease, mainWorktreePath, "main-coverage-baseline",
                "acceptance-main-coverage-baseline", cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is InvalidDataException or JsonException)
        {
            throw new AcceptanceInfrastructureDeferredException("trusted-main-discovery-failed", null, error.Message);
        }
        catch (Exception error) when (IsBuildArtifactIoException(error) &&
                                     error is not DotnetBuildSlotsBusyException)
        {
            throw new AcceptanceInfrastructureDeferredException("trusted-main-discovery-io", null, error.Message);
        }
        if (baseline is null)
            throw new AcceptanceInfrastructureDeferredException("trusted-main-discovery-failed", null,
                $"Trusted main project disappeared before discovery: {removal.Project}");

        (CommandResult? Discovery, Exception? IoException, TestDiscoverySnapshot? Snapshot) discovery;
        try
        {
            discovery = await _structuralCoverageEvaluator.DiscoverBaselineAsync(
                baseline, mainSettings.ResolveDiscoveryTimeout(), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is InvalidDataException or JsonException)
        {
            throw new AcceptanceInfrastructureDeferredException("trusted-main-discovery-failed", null, error.Message);
        }
        if (discovery.IoException is { } ioException)
            throw new AcceptanceInfrastructureDeferredException("trusted-main-discovery-io", null, ioException.Message);
        if (discovery.Discovery is { } result && (result.TimedOut || result.ExitCode != 0))
            throw new AcceptanceInfrastructureDeferredException(result.TimedOut
                ? "trusted-main-discovery-timeout" : "trusted-main-discovery-failed",
                result.ExitCode, TailOutput(result.Output));
        var snapshot = discovery.Snapshot ?? throw new AcceptanceInfrastructureDeferredException(
            "trusted-main-discovery-failed", null, $"Trusted main discovery produced no snapshot: {removal.Project}");
        return new StructuralCoverageProject(check, null, null, null,
            removal with { MainCount = snapshot.Tests.Count, LockRemediationApplied = baseline.LockRemediationApplied });
    }

    private static AcceptanceCheckResult EvaluateRemovedTestProject(
        AcceptanceManifestCheck check, RemovedTestProjectRecord removal)
    {
        var evidence = $"{removal.Project} ({removal.MainCount} tests on main)";
        if (removal.DeclaredKind is null && removal.OwnerApproved)
            return new AcceptanceCheckResult("structural test coverage", true, 0, null,
                LockRemediationApplied: removal.LockRemediationApplied,
                ResultSummary: $"removed test project: {evidence}");

        var message = $"test project removed without manifest removal and owner approval: {evidence}";
        if (removal.DeclaredKind is { } kind)
            message += $"; missing manifest removal: candidate manifest still declares {kind} for the project";
        if (!removal.OwnerApproved)
            message += "; missing owner approval: no owner approval (approve-policy-change) for this candidate satisfied the owner-protected configuration check";
        return new AcceptanceCheckResult($"structural test coverage: {check.Name}", false, 1, message,
            LockRemediationApplied: removal.LockRemediationApplied, ResultSummary: message,
            FailureClassification: AcceptanceFailureClassifications.StructuralCoverageFailed);
    }

    private static AcceptanceCheckResult CombineRemovedTestProjectFailures(
        IReadOnlyList<AcceptanceCheckResult> failures, IReadOnlyList<string> summaries, bool lockRemediationApplied) =>
        failures[0] with
        {
            OutputTail = string.Join(Environment.NewLine, failures.Select(result => result.OutputTail)),
            ResultSummary = string.Join("; ", summaries.Concat(failures.Select(result => result.ResultSummary!))),
            LockRemediationApplied = lockRemediationApplied
        };

    internal async Task<AcceptanceCheckResult> RunStructuralCoverageForTests(
        string worktreePath, GoalId? goalId, IReadOnlyList<string>? changedFiles,
        bool ownerApprovalSatisfied, IReadOnlyList<AcceptanceCheckResult> completedChecks,
        CancellationToken cancellationToken = default)
    {
        if (_executionContext is null)
        {
            var settings = AcceptanceGateEngineSettings.Load(worktreePath);
            await using var owner = AcceptanceExecutionOwners.CreateAttemptForVerifierCompatibility(
                worktreePath, goalId, null, cancellationToken, settings,
                new AcceptanceAttemptIdentityResolvers(
                    path => _testOverrides.ResolvePartitionVerdictCandidateTreeShaForTests?.Invoke(path),
                    path => _testOverrides.ResolvePartitionVerdictMainShaForTests?.Invoke(path),
                    path => _testOverrides.ResolvePartitionVerdictVerifyingCommitShaForTests?.Invoke(path)));
            var scopedVerifier = new GoalAcceptanceVerifier(this, (AcceptanceAttemptExecutionOwner)owner);
            return await scopedVerifier.RunStructuralCoverageForTests(worktreePath, goalId, changedFiles,
                ownerApprovalSatisfied, completedChecks, cancellationToken).ConfigureAwait(false);
        }
        var plan = CreateEffectiveGatePlan(worktreePath, changedFiles, EngineSettings,
            (_executionContext as AcceptanceRunExecutionOwner)?.ProjectHomeDirectory);
        var preparation = await PrepareStructuralCoverageCheckAsync(plan.Checks, plan.InfrastructureTestLanes,
            worktreePath, goalId, null, null, null, LoadSanctionedTestRemovals(worktreePath),
            plan.Manifest.Checks, EngineSettings, ownerApprovalSatisfied, cancellationToken).ConfigureAwait(false);
        return await EvaluateStructuralCoverageCheckAsync(preparation, completedChecks, cancellationToken).ConfigureAwait(false);
    }

    private async Task<AcceptanceStructuralCoverageBaseline?> PrepareStructuralCoverageBaselineAsync(
        AcceptanceManifestCheck baselineCheck,
        AcceptanceGateEngineSettings discoverySettings,
        DotnetBuildEnvironment environment,
        GoalId? goalId,
        int? stableSlotIndex,
        DotnetBuildEnvironmentLease? stableSlotLease,
        string baselineWorktreePath,
        string artifactsDirectoryName,
        string operationName,
        CancellationToken baselineCancellationToken)
    {
        var baselineProjectPath = Path.Combine(
            baselineWorktreePath,
            baselineCheck.Project!.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(baselineProjectPath))
        {
            return null;
        }

        var mainArtifactsPath = Path.Combine(environment.ArtifactsPath, artifactsDirectoryName);
        var mainEnvironment = environment.DeriveArtifactsPath(mainArtifactsPath);
        MainBaselineDiscoveryCacheWrite? cacheWrite = null;
        if (operationName == "acceptance-main-coverage-baseline" &&
            ResolvedMainBaselineDiscoveryCache is { } cache)
        {
            string[]? discoveryArguments = null;
            MainBaselineDiscoveryCacheKey? key = null;
            try
            {
                discoveryArguments = BuildUnattendedDiscoveryArguments(baselineCheck, discoverySettings, mainEnvironment);
                key = MainBaselineDiscoveryCache.TryCreateKey(baselineWorktreePath,
                    baselineCheck.Project!, "Debug", discoveryArguments, mainArtifactsPath);
            }
            catch (Exception error) when (error is InvalidDataException or JsonException or ArgumentException)
            {
                // Preserve the existing post-build argument-validation failure on a bypass.
            }
            var cachedDiscovery = key is null ? null : cache.TryRead(key);
            Console.WriteLine($"MAIN_BASELINE_DISCOVERY_CACHE status={(key is null ? "bypass" : cachedDiscovery is null ? "miss" : "hit")} project={baselineCheck.Project}");
            if (cachedDiscovery is not null)
                return new AcceptanceStructuralCoverageBaseline(discoveryArguments!, baselineWorktreePath,
                    baselineWorktreePath, UsesMicrosoftTestingPlatform(baselineCheck), false, cachedDiscovery);
            if (key is not null) cacheWrite = new MainBaselineDiscoveryCacheWrite(cache, key);
        }
        var mainBuildArguments = new[]
        {
            "dotnet",
            "build",
            baselineCheck.Project,
            "--verbosity",
            "minimal"
        };
        AcceptanceCheckResult mainBuild;
        var lockRemediationApplied = false;
        try
        {
            if (operationName == "acceptance-main-coverage-baseline")
                _testOverrides.OnTrustedMainBaselineBuildStartingForTests?.Invoke(baselineCheck.Project!);
            var managedBuild = await RunManagedDotnetCheckAsync(
                operationName == "acceptance-main-coverage-baseline"
                    ? WithTrustedBaselineBuildIdentity(baselineCheck)
                    : baselineCheck,
                mainBuildArguments,
                baselineWorktreePath,
                goalId,
                stableSlotIndex,
                stableSlotLease,
                operationName,
                baselineCancellationToken,
                executionEnvironment: mainEnvironment,
                waitForPermit: true)
                .ConfigureAwait(false);
            mainBuild = managedBuild.Result;
            lockRemediationApplied = managedBuild.Retried;
        }
        catch (BuildLockBlockedException ex)
        {
            throw new AcceptanceInfrastructureDeferredException(
                "trusted-main-build-lock",
                exitCode: null,
                outputTail: null,
                buildLockAttribution: ex.Attribution);
        }
        catch (Exception ex) when (
            IsBuildArtifactIoException(ex) &&
            ex is not DotnetBuildSlotsBusyException)
        {
            throw new AcceptanceInfrastructureDeferredException(
                "trusted-main-build-io",
                exitCode: null,
                outputTail: ex.Message);
        }

        if (!mainBuild.Passed)
        {
            throw new AcceptanceInfrastructureDeferredException(
                mainBuild.ResultSummary?.Contains("timed out", StringComparison.OrdinalIgnoreCase) == true
                    ? "trusted-main-build-timeout"
                    : "trusted-main-build-failed",
                mainBuild.ExitCode,
                mainBuild.OutputTail);
        }

        var mainDiscoveryArguments = BuildUnattendedDiscoveryArguments(
            baselineCheck,
            discoverySettings,
            mainEnvironment);
        return new AcceptanceStructuralCoverageBaseline(
            mainDiscoveryArguments,
            baselineWorktreePath,
            baselineWorktreePath,
            UsesMicrosoftTestingPlatform(baselineCheck),
            lockRemediationApplied,
            CacheWrite: cacheWrite);
    }
}
